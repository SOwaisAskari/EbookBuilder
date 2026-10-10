using System.Text.Json;
using System.Text.RegularExpressions;
using EbookBuilder.Models;

namespace EbookBuilder.Services;

public interface IEbookRepository
{
    Task<IReadOnlyList<EbookSummary>> ListAsync();
    Task<Ebook?> GetAsync(string id);
    Task<Ebook> CreateAsync(string? title);
    Task<Ebook?> SaveAsync(string id, Ebook incoming);
    Task<bool> DeleteAsync(string id);
    Task<string?> SaveImageAsync(string id, byte[] data, string extension);
    string? GetImagePath(string id, string fileName);
}

/// <summary>
/// Stores each book as App_Data/books/{id}/book.json plus an images/ folder.
/// Swap this class for an EF Core / SQL implementation without touching anything else.
/// </summary>
public class JsonEbookRepository : IEbookRepository
{
    private static readonly Regex IdPattern = new("^[a-f0-9]{32}$", RegexOptions.Compiled);
    private static readonly Regex ChapterIdPattern = new("^[a-zA-Z0-9]{1,32}$", RegexOptions.Compiled);
    private static readonly Regex ColorPattern = new("^#[0-9a-fA-F]{6}$", RegexOptions.Compiled);
    private static readonly Regex LangPattern = new("^[a-zA-Z]{2,3}(-[a-zA-Z0-9]{2,8})*$", RegexOptions.Compiled);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string _root;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public JsonEbookRepository(IWebHostEnvironment env, IConfiguration config)
    {
        var configured = config["Storage:Path"] ?? "App_Data";
        var basePath = Path.IsPathRooted(configured) ? configured : Path.Combine(env.ContentRootPath, configured);
        _root = Path.Combine(basePath, "books");
        Directory.CreateDirectory(_root);
    }

    private string BookDir(string id) => Path.Combine(_root, id);
    private string BookFile(string id) => Path.Combine(BookDir(id), "book.json");
    private string ImageDir(string id) => Path.Combine(BookDir(id), "images");

    public async Task<IReadOnlyList<EbookSummary>> ListAsync()
    {
        var list = new List<EbookSummary>();
        foreach (var dir in Directory.EnumerateDirectories(_root))
        {
            var id = Path.GetFileName(dir);
            if (!IdPattern.IsMatch(id)) continue;
            var book = await ReadAsync(id);
            if (book is null) continue;
            list.Add(new EbookSummary(book.Id, book.Title, book.Author, book.Chapters.Count, book.CoverImage, book.UpdatedUtc));
        }
        return list.OrderByDescending(b => b.UpdatedUtc).ToList();
    }

    public Task<Ebook?> GetAsync(string id) => IdPattern.IsMatch(id) ? ReadAsync(id) : Task.FromResult<Ebook?>(null);

    public async Task<Ebook> CreateAsync(string? title)
    {
        var book = new Ebook
        {
            Title = string.IsNullOrWhiteSpace(title) ? "Untitled Book" : Clip(title.Trim(), 200),
            Chapters =
            {
                new Chapter { Title = "Chapter 1", Html = "<p>Start writing here…</p>" }
            }
        };

        await _lock.WaitAsync();
        try
        {
            Directory.CreateDirectory(ImageDir(book.Id));
            await WriteAsync(book);
        }
        finally { _lock.Release(); }
        return book;
    }

    public async Task<Ebook?> SaveAsync(string id, Ebook incoming)
    {
        if (!IdPattern.IsMatch(id)) return null;

        await _lock.WaitAsync();
        try
        {
            var book = await ReadAsync(id);
            if (book is null) return null;

            book.Title = Clip(incoming.Title?.Trim() ?? "", 200);
            if (book.Title.Length == 0) book.Title = "Untitled Book";
            book.Author = Clip(incoming.Author?.Trim() ?? "", 200);
            book.Description = Clip(incoming.Description?.Trim() ?? "", 2000);
            book.Language = incoming.Language is { } l && LangPattern.IsMatch(l) ? l : "en";

            book.CoverImage = incoming.CoverImage is { } c
                              && ImageSupport.StoredFileName.IsMatch(c)
                              && File.Exists(Path.Combine(ImageDir(id), c))
                ? c : null;

            book.Theme = new ReaderTheme
            {
                Mode = incoming.Theme?.Mode is "light" or "sepia" or "dark" ? incoming.Theme.Mode : "light",
                Font = incoming.Theme?.Font is "serif" or "sans" ? incoming.Theme.Font : "serif",
                Accent = incoming.Theme?.Accent is { } a && ColorPattern.IsMatch(a) ? a : "#263F7A"
            };

            var usedIds = new HashSet<string>();
            book.Chapters = (incoming.Chapters ?? new()).Take(500).Select(ch =>
            {
                var cid = ch.Id is { } x && ChapterIdPattern.IsMatch(x) && usedIds.Add(x)
                    ? x
                    : NewChapterId(usedIds);
                var t = Clip(ch.Title?.Trim() ?? "", 200);
                return new Chapter
                {
                    Id = cid,
                    Title = t.Length == 0 ? "Untitled chapter" : t,
                    Html = ContentSanitizer.Sanitize(ch.Html)
                };
            }).ToList();

            book.UpdatedUtc = DateTime.UtcNow;
            await WriteAsync(book);
            return book;
        }
        finally { _lock.Release(); }
    }

    public async Task<bool> DeleteAsync(string id)
    {
        if (!IdPattern.IsMatch(id)) return false;
        await _lock.WaitAsync();
        try
        {
            var dir = BookDir(id);
            if (!Directory.Exists(dir)) return false;
            Directory.Delete(dir, recursive: true);
            return true;
        }
        finally { _lock.Release(); }
    }

    public async Task<string?> SaveImageAsync(string id, byte[] data, string extension)
    {
        if (!IdPattern.IsMatch(id) || !Directory.Exists(BookDir(id))) return null;
        var name = $"{Guid.NewGuid():N}.{extension}";
        Directory.CreateDirectory(ImageDir(id));
        await File.WriteAllBytesAsync(Path.Combine(ImageDir(id), name), data);
        return name;
    }

    public string? GetImagePath(string id, string fileName)
    {
        if (!IdPattern.IsMatch(id) || !ImageSupport.StoredFileName.IsMatch(fileName)) return null;
        var path = Path.Combine(ImageDir(id), fileName);
        return File.Exists(path) ? path : null;
    }

    // ---- helpers ----------------------------------------------------------

    private async Task<Ebook?> ReadAsync(string id)
    {
        var file = BookFile(id);
        if (!File.Exists(file)) return null;
        await using var fs = File.OpenRead(file);
        return await JsonSerializer.DeserializeAsync<Ebook>(fs, Json);
    }

    private async Task WriteAsync(Ebook book)
    {
        Directory.CreateDirectory(BookDir(book.Id));
        var tmp = BookFile(book.Id) + ".tmp";
        await using (var fs = File.Create(tmp))
            await JsonSerializer.SerializeAsync(fs, book, Json);
        File.Move(tmp, BookFile(book.Id), overwrite: true);
    }

    private static string NewChapterId(HashSet<string> used)
    {
        string id;
        do { id = Guid.NewGuid().ToString("N")[..8]; } while (!used.Add(id));
        return id;
    }

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max];
}
