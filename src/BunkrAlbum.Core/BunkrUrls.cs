using System.Text;

namespace BunkrAlbum;

public static class BunkrUrls
{
    public const string DownloadApi = "https://dl.bunkr.cr/api/_001_v2";
    public const string SignApi = "https://glb-apisign.cdn.cr/sign";

    public static BunkrLink Parse(string input)
    {
        var text = (input ?? "").Trim();
        if (text.Length == 0)
            throw new BunkrException("Paste a Bunkr link.");

        if (!text.Contains("://", StringComparison.Ordinal))
            text = "https://" + text;

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
            throw new BunkrException("The URL is invalid.");

        var host = uri.Host.ToLowerInvariant();
        if (!host.Contains("bunkr", StringComparison.Ordinal) && !host.Contains("bunkrr", StringComparison.Ordinal))
            throw new BunkrException("This is not a Bunkr link.");

        var parts = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            throw new BunkrException("Use an album link (/a/…) or a file link (/f/…).");

        var section = parts[0].ToLowerInvariant();
        if (section is not ("a" or "f" or "v" or "d" or "i"))
            throw new BunkrException("Use an album link (/a/…) or a file link (/f/…).");

        var origin = uri.GetLeftPart(UriPartial.Authority);
        return new BunkrLink(origin, section, Uri.UnescapeDataString(parts[1]));
    }

    public static string CombineMedia(string mediafiles, string path)
    {
        if (string.IsNullOrWhiteSpace(mediafiles) || string.IsNullOrWhiteSpace(path))
            throw new BunkrException("The download address is incomplete.");
        return mediafiles.TrimEnd('/') + "/" + path.TrimStart('/');
    }

    public static string WithSignature(string mediaUrl, string token, string expires)
    {
        var separator = mediaUrl.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        return mediaUrl + separator + "token=" + EncodeUriComponent(token) + "&ex=" + EncodeUriComponent(expires);
    }

    public static string EncodeUriComponent(string value)
    {
        return Uri.EscapeDataString(value)
            .Replace("%21", "!", StringComparison.Ordinal)
            .Replace("%27", "'", StringComparison.Ordinal)
            .Replace("%28", "(", StringComparison.Ordinal)
            .Replace("%29", ")", StringComparison.Ordinal)
            .Replace("%2A", "*", StringComparison.Ordinal);
    }

    public static string DecryptLegacyUrl(string base64Url, long timestamp)
    {
        byte[] data;
        try
        {
            data = Convert.FromBase64String(base64Url);
        }
        catch (FormatException ex)
        {
            throw new BunkrException("The encrypted download URL is invalid.", ex);
        }

        var key = Encoding.UTF8.GetBytes("SECRET_KEY_" + (timestamp / 3600));
        for (var i = 0; i < data.Length; i++)
            data[i] = (byte)(data[i] ^ key[i % key.Length]);

        var url = Encoding.UTF8.GetString(data);
        if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            throw new BunkrException("The encrypted download URL could not be read.");
        return url;
    }

    public static string? ExtractUrl(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var match = System.Text.RegularExpressions.Regex.Match(text, @"https?://[^\s<>""']+", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var candidate = (match.Success ? match.Value : text).Trim().TrimEnd('.', ',', ';', ')');
        try
        {
            Parse(candidate);
            return candidate;
        }
        catch (BunkrException)
        {
            return null;
        }
    }

    public static bool IsImage(BunkrFile file) => GuessMime(file).StartsWith("image/", StringComparison.Ordinal);

    public static bool IsVideo(BunkrFile file) => GuessMime(file).StartsWith("video/", StringComparison.Ordinal);

    public readonly record struct RangeDecision(long StartAt, bool Truncate, bool AlreadyComplete);

    public static RangeDecision DecideRange(long offset, int statusCode, bool canSeek)
    {
        if (offset <= 0 || statusCode == 200 && offset <= 0)
            return new RangeDecision(0, false, false);
        if (statusCode == 206)
            return new RangeDecision(offset, false, false);
        if (statusCode == 416)
            return new RangeDecision(offset, false, true);
        if (statusCode == 200 && canSeek)
            return new RangeDecision(0, true, false);
        return new RangeDecision(0, false, false);
    }

    public static string SanitizeFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "file";

        var invalid = Path.GetInvalidFileNameChars();
        var buffer = new char[name.Length];
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            buffer[i] = c is '/' or '\\' || Array.IndexOf(invalid, c) >= 0 ? '_' : c;
        }

        var cleaned = new string(buffer).Trim().Trim('.');
        if (cleaned.Length == 0)
            return "file";
        return cleaned.Length <= 180 ? cleaned : cleaned[^180..];
    }

    public static string FormatSize(long bytes)
    {
        if (bytes < 0)
            return "?";
        string[] units = ["B", "KB", "MB", "GB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} B" : $"{value:0.#} {units[unit]}";
    }

    public static string GuessMime(BunkrFile file)
    {
        if (!string.IsNullOrWhiteSpace(file.Type) && file.Type.Contains('/', StringComparison.Ordinal))
            return file.Type;

        var name = file.Original.ToLowerInvariant();
        if (name.EndsWith(".png", StringComparison.Ordinal)) return "image/png";
        if (name.EndsWith(".webp", StringComparison.Ordinal)) return "image/webp";
        if (name.EndsWith(".gif", StringComparison.Ordinal)) return "image/gif";
        if (name.EndsWith(".mp4", StringComparison.Ordinal)) return "video/mp4";
        if (name.EndsWith(".webm", StringComparison.Ordinal)) return "video/webm";
        if (name.EndsWith(".mov", StringComparison.Ordinal)) return "video/quicktime";
        if (name.EndsWith(".mkv", StringComparison.Ordinal)) return "video/x-matroska";
        if (name.EndsWith(".mp3", StringComparison.Ordinal)) return "audio/mpeg";
        if (name.EndsWith(".m4a", StringComparison.Ordinal)) return "audio/mp4";
        if (name.EndsWith(".zip", StringComparison.Ordinal)) return "application/zip";
        if (name.EndsWith(".jpg", StringComparison.Ordinal) || name.EndsWith(".jpeg", StringComparison.Ordinal))
            return "image/jpeg";
        return "application/octet-stream";
    }
}
