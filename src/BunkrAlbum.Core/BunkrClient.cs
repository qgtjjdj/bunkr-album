using System.Net;
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
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "de,en;q=0.8");
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
        CancellationToken cancellationToken = default)
    {
        var url = await ResolveWithRetryAsync(file, cancellationToken);
        return await CopyUrlAsync(url, destination, progress, cancellationToken);
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

        throw new BunkrException("Download-Link konnte nicht erzeugt werden.", last!);
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

    async Task<long> CopyUrlAsync(string url, Stream destination, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Referer", "https://bunkr.cr/");
        request.Headers.TryAddWithoutValidation("Accept", "*/*");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
            throw new BunkrException("Der Download-Link wurde abgelehnt.");
        if (!response.IsSuccessStatusCode)
            throw new BunkrException("Download-Server antwortet mit " + (int)response.StatusCode + ".");

        var mediaType = response.Content.Headers.ContentType?.MediaType ?? "";
        if (mediaType.Contains("text/html", StringComparison.OrdinalIgnoreCase))
            throw new BunkrException("Der Server hat keine Datei geliefert. Signatur oder Wartung prüfen.");

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        var buffer = new byte[80 * 1024];
        long total = 0;
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
                throw new BunkrException("Bunkr antwortet mit HTTP " + (int)response.StatusCode + ".");
            if (text.Contains("Just a moment", StringComparison.OrdinalIgnoreCase) &&
                text.Contains("cloudflare", StringComparison.OrdinalIgnoreCase))
                throw new BunkrException("Cloudflare hat die Anfrage blockiert. Später erneut versuchen.");
            return text;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new BunkrException("Zeitüberschreitung bei der Anfrage an Bunkr.");
        }
    }
}
