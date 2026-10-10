using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace EbookBuilder.Services;

/// <summary>
/// Whitelist-based sanitizer for chapter HTML. It:
///  - keeps only a small set of book-friendly tags and attributes,
///  - rewrites the output so every tag is balanced and void tags are self-closed
///    (which makes the result valid XHTML for EPUB as well as HTML5),
///  - blocks scripts, event handlers and javascript: URLs.
/// For a public, multi-tenant deployment consider adding the maintained
/// HtmlSanitizer NuGet package as a second layer.
/// </summary>
public static class ContentSanitizer
{
    private static readonly HashSet<string> AllowedTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "p", "br", "hr", "h1", "h2", "h3", "h4", "strong", "em", "u", "s", "sub", "sup",
        "blockquote", "ul", "ol", "li", "a", "img", "pre", "code", "figure", "figcaption",
        "div", "span", "table", "thead", "tbody", "tr", "th", "td"
    };

    private static readonly HashSet<string> VoidTags = new(StringComparer.OrdinalIgnoreCase) { "br", "hr", "img" };

    private static readonly HashSet<string> AlignableTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "p", "h1", "h2", "h3", "h4", "div", "li", "blockquote", "figure", "figcaption"
    };

    private static readonly Regex Comments = new(@"<!--[\s\S]*?-->", RegexOptions.Compiled);

    private static readonly Regex DangerousBlocks = new(
        @"<(script|style|iframe|object|embed|noscript|template|svg|math)\b[^>]*>[\s\S]*?</\1\s*>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ControlChars = new(@"[\x00-\x08\x0B\x0C\x0E-\x1F]", RegexOptions.Compiled);

    private static readonly Regex TagRegex = new(
        @"<(/?)([a-zA-Z][a-zA-Z0-9]*)((?:""[^""]*""|'[^']*'|[^'"">])*)>",
        RegexOptions.Compiled);

    private static readonly Regex AttrRegex = new(
        @"([a-zA-Z_:][-a-zA-Z0-9_:.]*)(?:\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s""'>]+)))?",
        RegexOptions.Compiled);

    private static readonly Regex BareAmpersand = new(
        @"&(?!(?:[a-zA-Z][a-zA-Z0-9]*|#[0-9]+|#x[0-9a-fA-F]+);)", RegexOptions.Compiled);

    private static readonly Regex LocalImage = new(
        @"^/api/ebooks/[a-f0-9]{32}/images/[a-f0-9]{32}\.(png|jpe?g|gif|webp)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex TextAlign = new(
        @"text-align\s*:\s*(left|right|center|justify)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static string Sanitize(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return "";

        html = ControlChars.Replace(html, "");
        html = Comments.Replace(html, "");
        html = DangerousBlocks.Replace(html, "");

        var sb = new StringBuilder(html.Length);
        var open = new List<string>();
        var pos = 0;

        foreach (Match m in TagRegex.Matches(html))
        {
            AppendText(sb, html.Substring(pos, m.Index - pos));
            pos = m.Index + m.Length;

            var closing = m.Groups[1].Value == "/";
            var name = NormalizeName(m.Groups[2].Value.ToLowerInvariant());
            if (!AllowedTags.Contains(name)) continue;

            if (closing)
            {
                var idx = open.LastIndexOf(name);
                if (idx < 0) continue;
                for (var i = open.Count - 1; i >= idx; i--)
                    sb.Append("</").Append(open[i]).Append('>');
                open.RemoveRange(idx, open.Count - idx);
                continue;
            }

            var attrs = BuildAttributes(name, m.Groups[3].Value);
            if (name == "img" && !attrs.Contains(" src=\"")) continue;   // unsafe/missing source: drop the image
            if (name == "a" && !attrs.Contains(" href=\"")) continue;    // unsafe/missing link: keep the text only

            if (VoidTags.Contains(name))
            {
                sb.Append('<').Append(name).Append(attrs).Append(" />");
            }
            else
            {
                sb.Append('<').Append(name).Append(attrs).Append('>');
                open.Add(name);
            }
        }

        AppendText(sb, html.Substring(pos));
        for (var i = open.Count - 1; i >= 0; i--)
            sb.Append("</").Append(open[i]).Append('>');

        return sb.ToString().Trim();
    }

    private static string NormalizeName(string name) => name switch
    {
        "b" => "strong",
        "i" => "em",
        "strike" => "s",
        "del" => "s",
        _ => name
    };

    private static void AppendText(StringBuilder sb, string text)
    {
        if (text.Length == 0) return;
        text = text.Replace("<", "&lt;").Replace(">", "&gt;");
        sb.Append(BareAmpersand.Replace(text, "&amp;"));
    }

    private static string BuildAttributes(string tag, string raw)
    {
        var sb = new StringBuilder();
        var seen = new HashSet<string>();

        foreach (Match a in AttrRegex.Matches(raw))
        {
            var key = a.Groups[1].Value.ToLowerInvariant();
            var val = a.Groups[2].Success ? a.Groups[2].Value
                    : a.Groups[3].Success ? a.Groups[3].Value
                    : a.Groups[4].Value;
            val = WebUtility.HtmlDecode(val);

            if (!seen.Add(key)) continue;

            switch (key)
            {
                case "href" when tag == "a":
                    if (IsSafeLink(val)) sb.Append(" href=\"").Append(WebUtility.HtmlEncode(val.Trim())).Append('"');
                    break;

                case "src" when tag == "img":
                    if (LocalImage.IsMatch(val.Trim()) || IsHttp(val))
                        sb.Append(" src=\"").Append(WebUtility.HtmlEncode(val.Trim())).Append('"');
                    break;

                case "alt" when tag == "img":
                    sb.Append(" alt=\"").Append(WebUtility.HtmlEncode(val)).Append('"');
                    break;

                case "title" when tag is "a" or "img":
                    sb.Append(" title=\"").Append(WebUtility.HtmlEncode(val)).Append('"');
                    break;

                case "style" when AlignableTags.Contains(tag):
                    var align = TextAlign.Match(val);
                    if (align.Success)
                        sb.Append(" style=\"text-align:").Append(align.Groups[1].Value.ToLowerInvariant()).Append('"');
                    break;

                case "colspan" or "rowspan" when tag is "td" or "th":
                    if (int.TryParse(val, out var n) && n is > 0 and < 50)
                        sb.Append(' ').Append(key).Append("=\"").Append(n).Append('"');
                    break;
            }
        }

        if (tag == "a" && sb.Length > 0) sb.Append(" rel=\"noopener noreferrer\"");
        if (tag == "img" && !seen.Contains("alt")) sb.Append(" alt=\"\"");
        return sb.ToString();
    }

    private static string Compact(string url) => Regex.Replace(url, @"[\s\x00-\x1f]", "");

    private static bool IsHttp(string url)
    {
        var u = Compact(url);
        return u.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || u.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSafeLink(string url)
    {
        var u = Compact(url);
        return IsHttp(u)
            || u.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)
            || u.StartsWith("#", StringComparison.Ordinal);
    }
}
