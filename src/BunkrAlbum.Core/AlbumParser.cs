using System.Text.Json;
using System.Text.RegularExpressions;

namespace BunkrAlbum;

public static partial class AlbumParser
{
    public static AlbumInfo ParseAlbum(string sourceUrl, string html)
    {
        if (string.IsNullOrWhiteSpace(html))
            throw new BunkrException("Die Albumseite ist leer.");
        if (html.Contains("Server under maintenance", StringComparison.OrdinalIgnoreCase))
            throw new BunkrException("Bunkr meldet Wartung. Bitte später erneut versuchen.");

        var files = ParseAlbumFiles(html);
        if (files.Count == 0)
            throw new BunkrException("In diesem Album sind keine Dateien.");

        return new AlbumInfo(ExtractTitle(html, "Album"), sourceUrl, files);
    }

    public static AlbumInfo ParseSingleFile(string sourceUrl, string html)
    {
        if (string.IsNullOrWhiteSpace(html))
            throw new BunkrException("Die Dateiseite ist leer.");
        if (html.Contains("Server under maintenance", StringComparison.OrdinalIgnoreCase))
            throw new BunkrException("Bunkr meldet Wartung. Bitte später erneut versuchen.");

        var idMatch = FileIdRegex().Match(html);
        if (!idMatch.Success)
            throw new BunkrException("Die Datei-ID wurde nicht gefunden.");

        var title = ExtractTitle(html, "Datei");
        var original = title.EndsWith("| Bunkr", StringComparison.OrdinalIgnoreCase)
            ? title[..title.LastIndexOf('|')].Trim()
            : title;
        if (string.IsNullOrWhiteSpace(original))
            original = "datei";

        var file = new BunkrFile(
            idMatch.Groups[1].Value,
            original,
            original,
            "",
            "",
            "",
            -1,
            "",
            "");

        return new AlbumInfo(original, sourceUrl, [file]);
    }

    public static IReadOnlyList<BunkrFile> ParseAlbumFiles(string html)
    {
        const string marker = "window.albumFiles";
        var start = html.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
            throw new BunkrException("Keine Dateiliste gefunden. Advanced-Ansicht fehlt auf der Albumseite.");

        var bracket = html.IndexOf('[', start);
        if (bracket < 0)
            throw new BunkrException("Die Dateiliste ist unvollständig.");

        var end = FindMatchingBracket(html, bracket);
        var body = html[bracket..(end + 1)];

        try
        {
            var json = QuoteKeys(body);
            using var document = JsonDocument.Parse(json);
            var files = new List<BunkrFile>();
            foreach (var item in document.RootElement.EnumerateArray())
                files.Add(ReadFile(item));
            return files;
        }
        catch (JsonException)
        {
            return ParseLoose(body);
        }
    }

    public static string ReadResolvedUrl(string apiJson)
    {
        using var document = JsonDocument.Parse(apiJson);
        var root = document.RootElement;

        if (root.TryGetProperty("mediafiles", out var media) && root.TryGetProperty("path", out var path))
        {
            var mediaText = media.GetString();
            var pathText = path.GetString();
            if (!string.IsNullOrWhiteSpace(mediaText) && !string.IsNullOrWhiteSpace(pathText))
                return BunkrUrls.CombineMedia(mediaText, pathText);
        }

        if (root.TryGetProperty("url", out var urlNode))
        {
            var url = urlNode.GetString() ?? "";
            var encrypted = root.TryGetProperty("encrypted", out var enc) && enc.ValueKind == JsonValueKind.True;
            if (encrypted)
            {
                if (!root.TryGetProperty("timestamp", out var stamp) || !stamp.TryGetInt64(out var timestamp))
                    throw new BunkrException("Verschlüsselte Antwort ohne Zeitstempel.");
                return BunkrUrls.DecryptLegacyUrl(url, timestamp);
            }

            if (url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                return url;
        }

        throw new BunkrException("Bunkr hat keine Download-Adresse geliefert.");
    }

    public static (string Token, string Expires) ReadSignature(string signJson)
    {
        using var document = JsonDocument.Parse(signJson);
        var root = document.RootElement;
        var token = root.GetProperty("token").GetString();
        if (string.IsNullOrWhiteSpace(token))
            throw new BunkrException("Signatur ohne Token.");

        if (!root.TryGetProperty("ex", out var ex))
            throw new BunkrException("Signatur ohne Ablaufzeit.");

        var expires = ex.ValueKind == JsonValueKind.String ? ex.GetString() : ex.GetRawText();
        if (string.IsNullOrWhiteSpace(expires))
            throw new BunkrException("Signatur ohne Ablaufzeit.");
        return (token, expires);
    }

    static BunkrFile ReadFile(JsonElement item)
    {
        var id = item.TryGetProperty("id", out var idNode) ? idNode.ToString() : "";
        var original = GetString(item, "original");
        var name = GetString(item, "name");
        if (string.IsNullOrWhiteSpace(original))
            original = name;
        if (string.IsNullOrWhiteSpace(id))
            throw new BunkrException("Eine Datei hat keine ID.");

        long size = 0;
        if (item.TryGetProperty("size", out var sizeNode) && sizeNode.TryGetInt64(out var parsed))
            size = parsed;

        return new BunkrFile(
            id.Trim(),
            string.IsNullOrWhiteSpace(name) ? original : name,
            original,
            GetString(item, "slug"),
            GetString(item, "type"),
            GetString(item, "extension"),
            size,
            GetString(item, "timestamp"),
            GetString(item, "thumbnail"));
    }

    static string GetString(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var node))
            return "";
        return node.ValueKind == JsonValueKind.String ? node.GetString() ?? "" : node.ToString();
    }

    static List<BunkrFile> ParseLoose(string body)
    {
        var files = new List<BunkrFile>();
        foreach (Match match in LooseIdRegex().Matches(body))
        {
            var windowStart = Math.Max(0, match.Index - 40);
            var windowEnd = Math.Min(body.Length, match.Index + 1200);
            var window = body[windowStart..windowEnd];
            var original = LooseString(window, "original");
            var name = LooseString(window, "name");
            if (string.IsNullOrWhiteSpace(original))
                original = name;
            if (string.IsNullOrWhiteSpace(original))
                continue;

            long size = 0;
            var sizeMatch = Regex.Match(window, @"size\s*:\s*(-?\d+)");
            if (sizeMatch.Success)
                long.TryParse(sizeMatch.Groups[1].Value, out size);

            files.Add(new BunkrFile(
                match.Groups[1].Value,
                string.IsNullOrWhiteSpace(name) ? original : name,
                original,
                LooseString(window, "slug"),
                LooseString(window, "type"),
                LooseString(window, "extension"),
                size,
                LooseString(window, "timestamp"),
                LooseString(window, "thumbnail")));
        }

        if (files.Count == 0)
            throw new BunkrException("Die Dateiliste konnte nicht gelesen werden.");
        return files;
    }

    static string LooseString(string block, string field)
    {
        var match = Regex.Match(block, field + @"\s*:\s*""((?:\\.|[^""])*)""", RegexOptions.Singleline);
        if (!match.Success)
            return "";
        return JsonSerializer.Deserialize<string>("\"" + match.Groups[1].Value + "\"") ?? "";
    }

    static string ExtractTitle(string html, string fallback)
    {
        var match = OgTitleRegex().Match(html);
        if (!match.Success)
            match = TitleRegex().Match(html);
        if (!match.Success)
            return fallback;
        var title = HtmlDecode(match.Groups[1].Value).Trim();
        return title.Length == 0 ? fallback : title;
    }

    static string HtmlDecode(string value)
    {
        return value
            .Replace("&" + "amp;", "&", StringComparison.Ordinal)
            .Replace("&" + "quot;", "\"", StringComparison.Ordinal)
            .Replace("&" + "#39;", "'", StringComparison.Ordinal)
            .Replace("&" + "apos;", "'", StringComparison.Ordinal)
            .Replace("&" + "lt;", "<", StringComparison.Ordinal)
            .Replace("&" + "gt;", ">", StringComparison.Ordinal);
    }

    public static string QuoteKeys(string jsArray)
    {
        var builder = new System.Text.StringBuilder(jsArray.Length + 32);
        var inString = false;
        var escape = false;
        for (var i = 0; i < jsArray.Length; i++)
        {
            var c = jsArray[i];
            if (inString)
            {
                builder.Append(c);
                if (escape)
                    escape = false;
                else if (c == '\\')
                    escape = true;
                else if (c == '"')
                    inString = false;
                continue;
            }

            if (c == '"')
            {
                inString = true;
                builder.Append(c);
                continue;
            }

            if (char.IsLetter(c) || c == '_')
            {
                var j = i + 1;
                while (j < jsArray.Length && (char.IsLetterOrDigit(jsArray[j]) || jsArray[j] == '_'))
                    j++;
                var k = j;
                while (k < jsArray.Length && char.IsWhiteSpace(jsArray[k]))
                    k++;
                if (k < jsArray.Length && jsArray[k] == ':')
                {
                    builder.Append('"');
                    builder.Append(jsArray, i, j - i);
                    builder.Append('"');
                    i = j - 1;
                    continue;
                }
            }

            builder.Append(c);
        }

        return builder.ToString();
    }

    static int FindMatchingBracket(string text, int open)
    {
        var depth = 0;
        var inString = false;
        var escape = false;
        for (var i = open; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (escape)
                    escape = false;
                else if (c == '\\')
                    escape = true;
                else if (c == '"')
                    inString = false;
                continue;
            }

            if (c == '"')
            {
                inString = true;
                continue;
            }

            if (c == '[')
                depth++;
            else if (c == ']')
            {
                depth--;
                if (depth == 0)
                    return i;
            }
        }

        throw new BunkrException("Die Dateiliste ist unvollständig.");
    }

    [GeneratedRegex(@"(?:https?://dl\.bunkr\.\w+)?/file/(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex FileIdRegex();

    [GeneratedRegex(@"\bid\s*:\s*(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex LooseIdRegex();

    [GeneratedRegex("property=\"og:title\"\\s+content=\"([^\"]*)\"", RegexOptions.IgnoreCase)]
    private static partial Regex OgTitleRegex();

    [GeneratedRegex("<title>([^<]+)</title>", RegexOptions.IgnoreCase)]
    private static partial Regex TitleRegex();
}
