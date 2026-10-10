namespace EbookBuilder.Models;

public class Ebook
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "Untitled Book";
    public string Author { get; set; } = "";
    public string Description { get; set; } = "";
    public string Language { get; set; } = "en";

    /// <summary>File name of the cover inside the book's image folder (e.g. "3f2a...c1.png").</summary>
    public string? CoverImage { get; set; }

    public ReaderTheme Theme { get; set; } = new();
    public List<Chapter> Chapters { get; set; } = new();

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}

public class Chapter
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Title { get; set; } = "Untitled chapter";

    /// <summary>Sanitized HTML fragment (body of the chapter, without the title).</summary>
    public string Html { get; set; } = "";
}

public class ReaderTheme
{
    /// <summary>light | sepia | dark</summary>
    public string Mode { get; set; } = "light";

    /// <summary>serif | sans</summary>
    public string Font { get; set; } = "serif";

    /// <summary>Accent colour, #RRGGBB.</summary>
    public string Accent { get; set; } = "#263F7A";
}

public record EbookSummary(
    string Id,
    string Title,
    string Author,
    int ChapterCount,
    string? CoverImage,
    DateTime UpdatedUtc);
