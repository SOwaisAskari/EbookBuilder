using System.Text.RegularExpressions;

namespace EbookBuilder.Services;

public static class ImageSupport
{
    public static readonly Regex StoredFileName = new(
        @"^[a-f0-9]{32}\.(png|jpg|gif|webp)$", RegexOptions.Compiled);

    /// <summary>Finds the "/api/ebooks/{id}/images/{file}" URLs we generate in chapter HTML.</summary>
    public static readonly Regex ApiUrl = new(
        @"/api/ebooks/[a-f0-9]{32}/images/([a-f0-9]{32}\.(?:png|jpg|jpeg|gif|webp))",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Detects the real image type from the first bytes (never trust the client's file name).</summary>
    public static string? DetectExtension(byte[] d)
    {
        if (d.Length < 12) return null;
        if (d[0] == 0x89 && d[1] == 0x50 && d[2] == 0x4E && d[3] == 0x47) return "png";
        if (d[0] == 0xFF && d[1] == 0xD8 && d[2] == 0xFF) return "jpg";
        if (d[0] == 'G' && d[1] == 'I' && d[2] == 'F' && d[3] == '8') return "gif";
        if (d[0] == 'R' && d[1] == 'I' && d[2] == 'F' && d[3] == 'F' &&
            d[8] == 'W' && d[9] == 'E' && d[10] == 'B' && d[11] == 'P') return "webp";
        return null;
    }

    public static string MimeType(string fileName) =>
        Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            _ => "application/octet-stream"
        };
}
