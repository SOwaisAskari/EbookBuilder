namespace EbookBuilder.Services;

/// <summary>Loads the reader templates once from the Templates folder next to the binary.</summary>
public class TemplateStore
{
    public string ReaderCss { get; }
    public string ReaderJs { get; }
    public string EpubCss { get; }

    public TemplateStore()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "Templates");
        ReaderCss = File.ReadAllText(Path.Combine(dir, "reader.css"));
        ReaderJs = File.ReadAllText(Path.Combine(dir, "reader.js"));
        EpubCss = File.ReadAllText(Path.Combine(dir, "epub.css"));
    }
}
