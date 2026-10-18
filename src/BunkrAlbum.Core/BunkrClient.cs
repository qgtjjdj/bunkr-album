using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace BunkrAlbum;

public sealed class BunkrClient : IDisposable
{
    public const string UserAgent =
        "Mozilla/5.0 (Linux; Android 14) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Mobile Safari/537.36";

    readonly HttpClient _http;

    public BunkrClient(HttpMessageHandler? handler = null)
    {
        _http = handler is null
            ? new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
            : new HttpClient(handler);
        _http.Timeout = Timeout.InfiniteTimeSpan;
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "en");
    }

    public async Task<AlbumInfo> OpenAsync(string input, CancellationToken cancellationToken = default)
    {
        var link = BunkrUrls.Parse(input);
        if (link.Kind == BunkrLinkKind.Album)
        {
            var url = $"{link.Origin}/a/{Uri.EscapeDataString(link.IdOrSlug)}?advanced=1";
            var html = await GetTextAsync(url, link.Origin + "/", cancellationToken);
            return AlbumParser.ParseAlbum(input.Trim(), html);
        }

        var fileUrl = $"{link.Origin}/{link.Section}/{Uri.EscapeDataString(link.IdOrSlug)}";
        var page = await GetTextAsync(fileUrl, link.Origin + "/", cancellationToken);
        return AlbumParser.ParseSingleFile(input.Trim(), page);
    }

    public async Task<string> ResolveDownloadUrlAsync(BunkrFile file, CancellationToken cancellationToken = default)
    {
        using var body = new StringContent("{\"id\":\"" + file.Id + "\"}", Encoding.UTF8, "application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, BunkrUrls.DownloadApi) { Content = body };
        request.Headers.TryAddWithoutValidation("Origin", "https://dl.bunkr.cr");
        request.Headers.TryAddWithoutValidation("Referer", "https://dl.bunkr.cr/");
        request.Headers.TryAddWithoutValidation("Accept", "application/json");

        var json = await SendTextAsync(request, cancellationToken);
        var mediaUrl = AlbumParser.ReadResolvedUrl(json);
        if (!mediaUrl.Contains("token=", StringComparison.Ordinal))
            mediaUrl = await SignAsync(mediaUrl, cancellationToken);
        return mediaUrl;
    }

    public async Task<long> DownloadAsync(
        BunkrFile file,
        Stream destination,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default,
        long offset = 0)
    {
        var url = await ResolveWithRetryAsync(file, cancellationToken);
        return await CopyUrlAsync(url, destination, progress, offset, cancellationToken);
    }

    async Task<string> ResolveWithRetryAsync(BunkrFile file, CancellationToken cancellationToken)
    {
        Exception? last = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                return await ResolveDownloadUrlAsync(file, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (attempt < 3)
            {
                last = ex;
                await Task.Delay(TimeSpan.FromMilliseconds(700 * attempt), cancellationToken);
            }
        }

        throw new BunkrException("Could not create the download link.", last!);
    }

    public void Dispose() => _http.Dispose();

    async Task<string> SignAsync(string mediaUrl, CancellationToken cancellationToken)
    {
        var path = new Uri(mediaUrl).AbsolutePath;
        var signUrl = BunkrUrls.SignApi + "?path=" + BunkrUrls.EncodeUriComponent(Uri.UnescapeDataString(path));
        using var request = new HttpRequestMessage(HttpMethod.Get, signUrl);
        request.Headers.TryAddWithoutValidation("Referer", "https://bunkr.cr/");
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        var json = await SendTextAsync(request, cancellationToken);
        var (token, expires) = AlbumParser.ReadSignature(json);
        return BunkrUrls.WithSignature(mediaUrl, token, expires);
    }

    async Task<long> CopyUrlAsync(
        string url,
        Stream destination,
        IProgress<long>? progress,
        long offset,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Referer", "https://bunkr.cr/");
        request.Headers.TryAddWithoutValidation("Accept", "*/*");
        if (offset > 0)
            request.Headers.Range = new RangeHeaderValue(offset, null);

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && offset > 0)
        {
            progress?.Report(offset);
            return offset;
        }

        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
            throw new BunkrException("The download link was rejected.");

        var decision = BunkrUrls.DecideRange(offset, (int)response.StatusCode, destination.CanSeek);
        if (offset > 0 && response.StatusCode == HttpStatusCode.OK && !decision.Truncate)
            throw new BunkrException("The file has to be downloaded again.");
        if (!decision.AlreadyComplete && response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.PartialContent))
            throw new BunkrException("The download server returned HTTP " + (int)response.StatusCode + ".");

        var mediaType = response.Content.Headers.ContentType?.MediaType ?? "";
        if (mediaType.Contains("text/html", StringComparison.OrdinalIgnoreCase))
            throw new BunkrException("The server did not return a file. Check the signature or try again later.");

        if (decision.AlreadyComplete)
        {
            progress?.Report(offset);
            return offset;
        }

        if (decision.Truncate)
        {
            destination.SetLength(0);
            destination.Position = 0;
        }

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        var buffer = new byte[80 * 1024];
        long total = decision.StartAt;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0)
                break;
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            total += read;
            progress?.Report(total);
        }

        return total;
    }

    async Task<string> GetTextAsync(string url, string referer, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Referer", referer);
        request.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml");
        return await SendTextAsync(request, cancellationToken);
    }

    async Task<string> SendTextAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var text = await response.Content.ReadAsStringAsync(timeout.Token);
            if (!response.IsSuccessStatusCode)
                throw new BunkrException("Bunkr returned HTTP " + (int)response.StatusCode + ".");
            if (text.Contains("Just a moment", StringComparison.OrdinalIgnoreCase) &&
                text.Contains("cloudflare", StringComparison.OrdinalIgnoreCase))
                throw new BunkrException("Cloudflare blocked the request. Try again later.");
            return text;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new BunkrException("The request to Bunkr timed out.");
        }
    }
}
