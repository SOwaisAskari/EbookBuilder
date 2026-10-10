using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using EbookBuilder.Models;

namespace EbookBuilder.Services;

/// <summary>
/// Turns an <see cref="Ebook"/> into:
///   • a static website (.zip: index.html + assets/css + assets/js + assets/images)
///   • one self-contained .html file (CSS, JS and images inlined)
///   • an .epub file
///   • a live preview page (images served by the API)
/// </summary>
public class ExportService
{
    private static readonly UTF8Encoding Utf8 = new(false);
    private static readonly HashSet<string> RtlLanguages = new(StringComparer.OrdinalIgnoreCase) { "ar", "ur", "fa", "he", "ps", "sd", "ug", "yi" };

    private readonly IEbookRepository _repo;
    private readonly TemplateStore _templates;

    public ExportService(IEbookRepository repo, TemplateStore templates)
    {
        _repo = repo;
        _templates = templates;
    }

    // =====================================================================
    //  Public API
    // =====================================================================

    public string BuildPreview(Ebook book) =>
        RenderReader(book, inlineAssets: true, img: f => $"/api/ebooks/{book.Id}/images/{f}");

    public string BuildSingleFile(Ebook book) =>
        RenderReader(book, inlineAssets: true, img: f => DataUri(book.Id, f));

    public byte[] BuildWebsiteZip(Ebook book)
    {
        var html = RenderReader(book, inlineAssets: false, img: f => "assets/images/" + f);

        return Zip(zip =>
        {
            AddText(zip, "index.html", html);
            AddText(zip, "assets/css/reader.css", _templates.ReaderCss);
            AddText(zip, "assets/js/reader.js", _templates.ReaderJs);
            foreach (var file in ReferencedImages(book))
            {
                var path = _repo.GetImagePath(book.Id, file);
                if (path is not null) AddBytes(zip, "assets/images/" + file, File.ReadAllBytes(path));
            }
            AddText(zip, "README.txt",
                $"{book.Title}\r\n\r\nOpen index.html in any browser - no server needed.\r\n" +
                "To publish: upload this whole folder to any static host (GitHub Pages, Netlify, S3, IIS...).\r\n");
        });
    }

    public byte[] BuildEpub(Ebook book)
    {
        var lang = SafeLang(book.Language);
        var uuid = Guid.TryParse(book.Id, out var g) ? g : Guid.NewGuid();
        var images = ReferencedImages(book).Where(f => _repo.GetImagePath(book.Id, f) is not null).ToList();
        var coverFile = book.CoverImage is { } c && images.Contains(c) ? c : null;

        return Zip(zip =>
        {
            // The mimetype entry must be first and stored without compression.
            var mime = zip.CreateEntry("mimetype", CompressionLevel.NoCompression);
            using (var s = mime.Open()) s.Write(Utf8.GetBytes("application/epub+zip"));

            AddText(zip, "META-INF/container.xml",
                """
                <?xml version="1.0" encoding="UTF-8"?>
                <container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
                  <rootfiles>
                    <rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/>
                  </rootfiles>
                </container>
                """);

            AddText(zip, "OEBPS/style.css", _templates.EpubCss);

            // cover / title page
            var cover = new StringBuilder();
            cover.Append("<section class=\"cover\" epub:type=\"cover\">");
            if (coverFile is not null)
                cover.Append($"<img src=\"images/{coverFile}\" alt=\"{E("Cover of " + book.Title)}\" />");
            else
            {
                cover.Append($"<h1>{E(book.Title)}</h1>");
                if (book.Author.Length > 0) cover.Append($"<p class=\"author\">{E(book.Author)}</p>");
            }
            cover.Append("</section>");
            AddText(zip, "OEBPS/cover.xhtml", XhtmlPage(book.Title, lang, cover.ToString()));

            // chapters
            for (var i = 0; i < book.Chapters.Count; i++)
            {
                var ch = book.Chapters[i];
                var inner = PrepareHtml(ch.Html, f => "images/" + f).Replace("&nbsp;", "&#160;");
                AddText(zip, $"OEBPS/chapter-{i + 1}.xhtml",
                    XhtmlPage(ch.Title, lang, $"<h1>{E(ch.Title)}</h1>\n{inner}"));
            }

            foreach (var file in images)
                AddBytes(zip, "OEBPS/images/" + file, File.ReadAllBytes(_repo.GetImagePath(book.Id, file)!));

            // navigation (EPUB 3) + NCX (EPUB 2 readers)
            var nav = new StringBuilder("<nav epub:type=\"toc\" id=\"toc\"><h1>Contents</h1><ol>");
            var ncx = new StringBuilder();
            for (var i = 0; i < book.Chapters.Count; i++)
            {
                nav.Append($"<li><a href=\"chapter-{i + 1}.xhtml\">{E(book.Chapters[i].Title)}</a></li>");
                ncx.Append($"<navPoint id=\"np{i + 1}\" playOrder=\"{i + 1}\"><navLabel><text>{E(book.Chapters[i].Title)}</text></navLabel><content src=\"chapter-{i + 1}.xhtml\"/></navPoint>");
            }
            nav.Append("</ol></nav>");
            AddText(zip, "OEBPS/nav.xhtml", XhtmlPage("Contents", lang, nav.ToString()));

            AddText(zip, "OEBPS/toc.ncx",
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
                "<ncx xmlns=\"http://www.daisy.org/z3986/2005/ncx/\" version=\"2005-1\">" +
                $"<head><meta name=\"dtb:uid\" content=\"urn:uuid:{uuid}\"/></head>" +
                $"<docTitle><text>{E(book.Title)}</text></docTitle><navMap>{ncx}</navMap></ncx>");

            // package document
            var opf = new StringBuilder();
            opf.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
            opf.Append("<package xmlns=\"http://www.idpf.org/2007/opf\" version=\"3.0\" unique-identifier=\"bookid\">");
            opf.Append("<metadata xmlns:dc=\"http://purl.org/dc/elements/1.1/\">");
            opf.Append($"<dc:identifier id=\"bookid\">urn:uuid:{uuid}</dc:identifier>");
            opf.Append($"<dc:title>{E(book.Title)}</dc:title>");
            if (book.Author.Length > 0) opf.Append($"<dc:creator>{E(book.Author)}</dc:creator>");
            opf.Append($"<dc:language>{lang}</dc:language>");
            if (book.Description.Length > 0) opf.Append($"<dc:description>{E(book.Description)}</dc:description>");
            opf.Append($"<meta property=\"dcterms:modified\">{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ}</meta>");
            if (coverFile is not null) opf.Append("<meta name=\"cover\" content=\"img-cover\"/>");
            opf.Append("</metadata><manifest>");
            opf.Append("<item id=\"nav\" href=\"nav.xhtml\" media-type=\"application/xhtml+xml\" properties=\"nav\"/>");
            opf.Append("<item id=\"ncx\" href=\"toc.ncx\" media-type=\"application/x-dtbncx+xml\"/>");
            opf.Append("<item id=\"css\" href=\"style.css\" media-type=\"text/css\"/>");
            opf.Append("<item id=\"cover-page\" href=\"cover.xhtml\" media-type=\"application/xhtml+xml\"/>");
            for (var i = 0; i < book.Chapters.Count; i++)
                opf.Append($"<item id=\"ch{i + 1}\" href=\"chapter-{i + 1}.xhtml\" media-type=\"application/xhtml+xml\"/>");
            for (var i = 0; i < images.Count; i++)
            {
                var isCover = images[i] == coverFile;
                opf.Append($"<item id=\"{(isCover ? "img-cover" : "img" + i)}\" href=\"images/{images[i]}\" media-type=\"{ImageSupport.MimeType(images[i])}\"{(isCover ? " properties=\"cover-image\"" : "")}/>");
            }
            opf.Append("</manifest><spine toc=\"ncx\"><itemref idref=\"cover-page\"/>");
            for (var i = 0; i < book.Chapters.Count; i++) opf.Append($"<itemref idref=\"ch{i + 1}\"/>");
            opf.Append("</spine></package>");
            AddText(zip, "OEBPS/content.opf", opf.ToString());
        });
    }

    // =====================================================================
    //  Reader page
    // =====================================================================

    private string RenderReader(Ebook book, bool inlineAssets, Func<string, string> img)
    {
        var lang = SafeLang(book.Language);
        var dir = RtlLanguages.Contains(lang.Split('-')[0]) ? "rtl" : "ltr";
        var accent = Regex.IsMatch(book.Theme.Accent, "^#[0-9a-fA-F]{6}$") ? book.Theme.Accent : "#263F7A";

        var sb = new StringBuilder(64 * 1024);
        sb.Append("<!DOCTYPE html>\n");
        sb.Append($"<html lang=\"{lang}\" dir=\"{dir}\" data-theme=\"{book.Theme.Mode}\" data-font=\"{book.Theme.Font}\" style=\"--accent:{accent}\">\n<head>\n");
        sb.Append("<meta charset=\"utf-8\">\n<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n");
        sb.Append($"<title>{E(book.Title)}</title>\n");
        if (book.Author.Length > 0) sb.Append($"<meta name=\"author\" content=\"{E(book.Author)}\">\n");
        if (book.Description.Length > 0) sb.Append($"<meta name=\"description\" content=\"{E(book.Description)}\">\n");
        sb.Append("<script>document.documentElement.classList.add('js')</script>\n");
        if (inlineAssets) sb.Append("<style>\n").Append(_templates.ReaderCss).Append("\n</style>\n");
        else sb.Append("<link rel=\"stylesheet\" href=\"assets/css/reader.css\">\n");
        sb.Append("</head>\n");

        sb.Append($"<body data-book-id=\"{book.Id}\">\n<div id=\"progress\"></div>\n");
        sb.Append("<header class=\"topbar\">\n");
        sb.Append("  <button id=\"toc-toggle\" type=\"button\" aria-controls=\"toc\" aria-expanded=\"true\">Contents</button>\n");
        sb.Append($"  <div class=\"topbar-title\">{E(book.Title)}</div>\n");
        sb.Append("  <div class=\"tools\">\n");
        sb.Append("    <button id=\"size-down\" type=\"button\" aria-label=\"Smaller text\">A&minus;</button>\n");
        sb.Append("    <button id=\"size-up\" type=\"button\" aria-label=\"Larger text\">A+</button>\n");
        sb.Append("    <button id=\"font-btn\" type=\"button\" aria-label=\"Switch typeface\">Serif/Sans</button>\n");
        sb.Append("    <button id=\"theme-btn\" type=\"button\" aria-label=\"Switch theme\">Theme</button>\n");
        sb.Append("  </div>\n</header>\n");

        sb.Append("<div class=\"shell\">\n<nav id=\"toc\" class=\"toc\" aria-label=\"Table of contents\">\n");
        sb.Append("  <div class=\"search\"><input id=\"search-input\" type=\"search\" placeholder=\"Search this book\" aria-label=\"Search this book\"></div>\n");
        sb.Append("  <ol id=\"toc-list\">\n");
        sb.Append($"    <li><a href=\"#cover\">{E(book.Title)}</a></li>\n");
        foreach (var ch in book.Chapters)
            sb.Append($"    <li><a href=\"#ch-{ch.Id}\">{E(ch.Title)}</a></li>\n");
        sb.Append("  </ol>\n  <ol id=\"search-results\" hidden></ol>\n</nav>\n");

        sb.Append("<main class=\"content\" id=\"content\">\n");

        // cover
        sb.Append("<section class=\"chapter cover\" id=\"cover\" data-title=\"Cover\">\n");
        if (book.CoverImage is { } cover)
            sb.Append($"  <img class=\"cover-img\" src=\"{E(img(cover))}\" alt=\"{E("Cover of " + book.Title)}\">\n");
        sb.Append("  <div class=\"cover-plate\">\n");
        sb.Append($"    <h1>{E(book.Title)}</h1>\n");
        if (book.Author.Length > 0) sb.Append($"    <p class=\"author\">{E(book.Author)}</p>\n");
        sb.Append("  </div>\n");
        if (book.Description.Length > 0) sb.Append($"  <p class=\"blurb\">{E(book.Description).Replace("\n", "<br>")}</p>\n");
        if (book.Chapters.Count > 0) sb.Append($"  <a class=\"start\" href=\"#ch-{book.Chapters[0].Id}\">Start reading</a>\n");
        sb.Append("</section>\n");

        foreach (var ch in book.Chapters)
        {
            sb.Append($"<section class=\"chapter\" id=\"ch-{ch.Id}\" data-title=\"{E(ch.Title)}\">\n");
            sb.Append($"  <h1 class=\"chapter-title\">{E(ch.Title)}</h1>\n");
            sb.Append("  <div class=\"prose\">\n").Append(PrepareHtml(ch.Html, img)).Append("\n  </div>\n</section>\n");
        }

        sb.Append("<div class=\"pager\"><button id=\"prev\" type=\"button\">Previous</button><span id=\"pos\"></span><button id=\"next\" type=\"button\">Next</button></div>\n");
        sb.Append("</main>\n</div>\n");

        if (inlineAssets) sb.Append("<script>\n").Append(_templates.ReaderJs.Replace("</script", "<\\/script")).Append("\n</script>\n");
        else sb.Append("<script src=\"assets/js/reader.js\"></script>\n");
        sb.Append("</body>\n</html>\n");
        return sb.ToString();
    }

    // =====================================================================
    //  Helpers
    // =====================================================================

    private static string E(string s) => WebUtility.HtmlEncode(s);

    private static string SafeLang(string? lang) =>
        lang is not null && Regex.IsMatch(lang, "^[a-zA-Z]{2,3}(-[a-zA-Z0-9]{2,8})*$") ? lang : "en";

    /// <summary>Re-sanitize (defence in depth) and rewrite API image URLs for the target format.</summary>
    private static string PrepareHtml(string html, Func<string, string> img) =>
        ImageSupport.ApiUrl.Replace(ContentSanitizer.Sanitize(html), m => img(m.Groups[1].Value.ToLowerInvariant()));

    private static IEnumerable<string> ReferencedImages(Ebook book)
    {
        var files = new HashSet<string>();
        if (book.CoverImage is { } c) files.Add(c);
        foreach (var ch in book.Chapters)
            foreach (Match m in ImageSupport.ApiUrl.Matches(ch.Html ?? ""))
                files.Add(m.Groups[1].Value.ToLowerInvariant());
        return files.Where(f => ImageSupport.StoredFileName.IsMatch(f));
    }

    private string DataUri(string bookId, string file)
    {
        var path = _repo.GetImagePath(bookId, file);
        if (path is null) return "";
        return $"data:{ImageSupport.MimeType(file)};base64,{Convert.ToBase64String(File.ReadAllBytes(path))}";
    }

    private static string XhtmlPage(string title, string lang, string bodyHtml) =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<!DOCTYPE html>\n" +
        $"<html xmlns=\"http://www.w3.org/1999/xhtml\" xmlns:epub=\"http://www.idpf.org/2007/ops\" lang=\"{lang}\" xml:lang=\"{lang}\">\n" +
        $"<head><meta charset=\"utf-8\"/><title>{E(title)}</title><link rel=\"stylesheet\" type=\"text/css\" href=\"style.css\"/></head>\n" +
        $"<body>\n{bodyHtml}\n</body>\n</html>";

    private static byte[] Zip(Action<ZipArchive> fill)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            fill(zip);
        return ms.ToArray();
    }

    private static void AddText(ZipArchive zip, string name, string content) =>
        AddBytes(zip, name, Utf8.GetBytes(content));

    private static void AddBytes(ZipArchive zip, string name, byte[] data)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var s = entry.Open();
        s.Write(data);
    }
}
