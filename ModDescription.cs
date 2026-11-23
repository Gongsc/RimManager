using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using System.Windows.Media;

namespace RimManager;

internal sealed record DescriptionStyle(bool Bold = false, bool Italic = false, bool Underline = false,
    bool Strike = false, double? Size = null, Color? Color = null, Uri? Link = null);
internal sealed record DescriptionRun(string Text, DescriptionStyle Style);
internal sealed record ModDescription(IReadOnlyList<DescriptionRun> Runs, IReadOnlyList<string> Images)
{
    private static readonly Regex Markup = new(
        """\[img(?:=[^\]\r\n]*)?\]\s*(?<image>[^\[\r\n]+?)\s*\[/img\]|<img\b[^>]*?\bsrc\s*=\s*(?:"(?<image>[^"]+)"|'(?<image>[^']+)'|(?<image>[^\s>]+))[^>]*>|(?:<|\[)\s*(?<close>/)?(?<tag>[a-z][a-z0-9]*|\*)(?<args>[^<>\[\]\r\n]*)(?:>|\])""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex MarkdownBold = new("""(?<![\\*])\*\*(?=\S)([^*\r\n]+?)(?<=\S)\*\*(?!\*)""");

    internal static ModDescription Parse(string description)
    {
        var text = MarkdownBold.Replace(description.Trim().Replace("\r\n", "\n").Replace('\r', '\n'), "<b>$1</b>");
        var runs = new List<DescriptionRun>();
        var images = new List<string>();
        var styles = new List<(string Tag, string Argument)>();
        var current = new DescriptionStyle();

        void Add(string value)
        {
            if (value.Length > 0) runs.Add(new(WebUtility.HtmlDecode(value), current));
        }

        var offset = 0;
        foreach (Match match in Markup.Matches(text))
        {
            Add(text[offset..match.Index]);
            offset = match.Index + match.Length;
            if (match.Groups["image"].Success)
            {
                images.Add(WebUtility.HtmlDecode(match.Groups["image"].Value).Trim());
                continue;
            }
            var tag = match.Groups["tag"].Value.ToLowerInvariant();
            var closing = match.Groups["close"].Success;
            switch (tag)
            {
                case "br": case "p": case "hr": case "quote": case "code":
                    Add("\n");
                    break;
                case "list": case "ul": case "ol":
                    if (closing) Add("\n");
                    break;
                case "*": case "li":
                    if (!closing) Add((runs.Count == 0 || runs[^1].Text.EndsWith('\n') ? "" : "\n") + "• ");
                    break;
                case "b": case "strong": case "i": case "em": case "u": case "s": case "strike":
                case "h1": case "h2": case "h3": case "color": case "size": case "url": case "a":
                    var argument = match.Groups["args"].Value.Trim().TrimStart('=').Trim().Trim('"', '\'');
                    if (tag is "h1" or "h2" or "h3")
                        if (runs.Count > 0 && !runs[^1].Text.EndsWith('\n')) Add("\n");
                    if (closing)
                    {
                        var start = styles.FindLastIndex(style => style.Tag == tag);
                        if (start >= 0) styles.RemoveAt(start);
                    }
                    else
                    {
                        if (tag == "url" && argument.Length == 0)
                        {
                            var end = text.IndexOf("[/url]", offset, StringComparison.OrdinalIgnoreCase);
                            if (end >= offset) argument = text[offset..end].Trim();
                        }
                        if (tag == "a")
                        {
                            var link = Regex.Match(argument, """\bhref\s*=\s*(?:"([^"]+)"|'([^']+)'|([^\s]+))""", RegexOptions.IgnoreCase);
                            argument = link.Groups.Cast<Group>().Skip(1).FirstOrDefault(g => g.Success)?.Value ?? "";
                        }
                        styles.Add((tag, argument));
                    }
                    current = new DescriptionStyle();
                    foreach (var style in styles) current = Apply(current, style.Tag, WebUtility.HtmlDecode(style.Argument));
                    break;
                default:
                    if (tag is not ("img" or "spoiler" or "table" or "tr" or "th" or "td" or "noparse" or "align" or "indent" or "sprite"))
                        Add(match.Value);
                    break;
            }
        }
        Add(text[offset..]);
        return new(runs, images.Distinct(StringComparer.Ordinal).ToArray());
    }

    private static DescriptionStyle Apply(DescriptionStyle style, string tag, string argument) => tag switch
    {
        "b" or "strong" => style with { Bold = true },
        "i" or "em" => style with { Italic = true },
        "u" => style with { Underline = true },
        "s" or "strike" => style with { Strike = true },
        "h1" => style with { Bold = true, Size = 19 },
        "h2" => style with { Bold = true, Size = 17 },
        "h3" => style with { Bold = true, Size = 15 },
        "color" when TryColor(argument) is Color color => style with { Color = color },
        "size" when double.TryParse(argument, NumberStyles.Number, CultureInfo.InvariantCulture, out var size)
            => style with { Size = Math.Clamp(size, 10, 24) },
        "a" or "url" when Uri.TryCreate(argument, UriKind.Absolute, out var link) && link.Scheme is "https" or "http"
            => style with { Link = link },
        _ => style
    };

    private static Color? TryColor(string value)
    {
        // Unity writes eight-digit colors as RRGGBBAA. WPF expects AARRGGBB.
        if (value.StartsWith('#') && value.Length == 9) value = "#" + value[7..] + value[1..7];
        try { return ColorConverter.ConvertFromString(value) as Color?; }
        catch (FormatException) { return null; }
    }
}
