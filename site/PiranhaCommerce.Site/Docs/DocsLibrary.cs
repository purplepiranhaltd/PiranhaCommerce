using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Extensions.AutoIdentifiers;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace PiranhaCommerce.Site.Docs;

/// <summary>One documentation page, rendered once at startup.</summary>
/// <param name="Slug">Path under the docs root without ".md" ("guides/pricing"); empty for the front page.</param>
public sealed record DocPage(string Slug, string Title, string Section, int Order, string Description, string Html, IReadOnlyList<DocHeading> Headings, string PlainText);

public sealed record DocHeading(int Level, string Id, string Text);

public sealed record DocSection(string Folder, string Title, IReadOnlyList<DocPage> Pages);

public sealed record DocSearchHit(DocPage Page, string? HeadingId, string? HeadingText, string Snippet, int Score);

/// <summary>
/// The Markdown documentation, read from a folder once at startup. Pages are plain Markdown with a small front matter block
/// (<c>title</c>, <c>order</c>, <c>description</c>); folders are sections (named in <c>sections.json</c>). Links between pages are
/// written as relative <c>.md</c> paths and are checked here, so a broken one is found by <see cref="Problems"/> (and by a test) rather than by a reader.
/// Internal links are rendered with the <see cref="LinkToken"/> marker and given the right address for the host at request time.
/// </summary>
public sealed class DocsLibrary
{
    /// <summary>Stands in for the docs address (empty on the docs subdomain, "/docs" elsewhere) in rendered links.</summary>
    public const string LinkToken = "~docs~";

    private readonly Dictionary<string, DocPage> _bySlug = new(StringComparer.Ordinal);
    private readonly List<DocSection> _sections = [];
    private readonly List<string> _problems = [];

    public IReadOnlyList<DocSection> Sections => _sections;
    public IReadOnlyCollection<DocPage> Pages => _bySlug.Values;

    /// <summary>Broken links, missing anchors and malformed pages found while loading. Empty when the docs are sound.</summary>
    public IReadOnlyList<string> Problems => _problems;

    public DocPage? Home => _bySlug.GetValueOrDefault("");

    public DocPage? Find(string? slug) => _bySlug.GetValueOrDefault((slug ?? "").Trim('/').ToLowerInvariant());

    private record RawLink(string FromSlug, string TargetSlug, string? Anchor, string Original);

    public static DocsLibrary Load(string root)
    {
        var lib = new DocsLibrary();
        if (!Directory.Exists(root))
        {
            lib._problems.Add($"The docs folder was not found: {root}");
            return lib;
        }

        var pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().UseAutoIdentifiers(AutoIdentifierOptions.GitHub).Build();
        var links = new List<RawLink>();
        var headingIds = new Dictionary<string, HashSet<string>>();

        foreach (var file in Directory.EnumerateFiles(root, "*.md", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (relative.Split('/').Any(part => part.StartsWith('_'))) continue;
            var slug = relative[..^3];
            if (slug.Equals("index", StringComparison.OrdinalIgnoreCase)) slug = "";
            else if (slug.EndsWith("/index", StringComparison.OrdinalIgnoreCase)) slug = slug[..^6];
            slug = slug.ToLowerInvariant();

            var (meta, body) = SplitFrontMatter(File.ReadAllText(file, Encoding.UTF8));
            if (!meta.TryGetValue("title", out var title) || string.IsNullOrWhiteSpace(title)) { lib._problems.Add($"{relative}: no title in the front matter."); title = slug; }
            if (!meta.ContainsKey("description")) lib._problems.Add($"{relative}: no description in the front matter.");
            var order = int.TryParse(meta.GetValueOrDefault("order"), out var o) ? o : 1000;

            var document = Markdown.Parse(body, pipeline);
            // The page title is shown by the page itself, so a leading H1 in the Markdown is dropped.
            if (document.FirstOrDefault() is HeadingBlock { Level: 1 } h1) document.Remove(h1);
            var headings = new List<DocHeading>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var h in document.Descendants<HeadingBlock>())
            {
                var id = h.GetAttributes().Id;
                var text = HeadingText(h);
                if (id is not null) ids.Add(id);
                if (id is not null && h.Level is >= 2 and <= 3) headings.Add(new DocHeading(h.Level, id, text));
            }
            headingIds[slug] = ids;

            var folder = slug.Contains('/') ? slug[..slug.IndexOf('/')] : "";
            var baseDir = slug.Contains('/') ? slug[..slug.LastIndexOf('/')] : "";
            foreach (var link in document.Descendants<LinkInline>().Where(l => !l.IsImage))
            {
                var url = link.Url ?? "";
                if (url.Length == 0 || Regex.IsMatch(url, @"^[a-zA-Z][a-zA-Z0-9+.-]*:") || url.StartsWith("//")) continue; // external, mailto, etc.
                if (url.StartsWith('#')) { links.Add(new RawLink(slug, slug, url[1..], url)); continue; }
                var hash = url.IndexOf('#');
                var path = hash < 0 ? url : url[..hash];
                var anchor = hash < 0 ? null : url[(hash + 1)..];
                if (!path.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) { lib._problems.Add($"{relative}: the link \"{url}\" should point to a .md page."); continue; }
                var target = Resolve(baseDir, path[..^3]);
                if (target.EndsWith("/index")) target = target[..^6]; else if (target == "index") target = "";
                links.Add(new RawLink(slug, target, anchor, url));
                link.Url = $"{LinkToken}/{target}" + (anchor is null ? "" : "#" + anchor);
            }

            var html = Markdown.ToHtml(document, pipeline);
            var plain = Markdown.ToPlainText(body, pipeline);
            lib._bySlug[slug] = new DocPage(slug, title, folder, order, meta.GetValueOrDefault("description") ?? "", html, headings, Regex.Replace(plain, @"\s+", " ").Trim());
        }

        foreach (var l in links)
        {
            if (!lib._bySlug.ContainsKey(l.TargetSlug)) { lib._problems.Add($"{(l.FromSlug.Length == 0 ? "index" : l.FromSlug)}: the link \"{l.Original}\" goes to a page that does not exist."); continue; }
            if (!string.IsNullOrEmpty(l.Anchor) && !headingIds[l.TargetSlug].Contains(l.Anchor))
                lib._problems.Add($"{(l.FromSlug.Length == 0 ? "index" : l.FromSlug)}: the link \"{l.Original}\" goes to a heading that does not exist.");
        }

        var sectionTitles = lib.ReadSections(root);
        foreach (var (folder, title) in sectionTitles)
        {
            var pages = lib._bySlug.Values.Where(p => p.Section == folder).OrderBy(p => p.Order).ThenBy(p => p.Title, StringComparer.OrdinalIgnoreCase).ToList();
            if (pages.Count > 0) lib._sections.Add(new DocSection(folder, title, pages));
        }
        foreach (var orphan in lib._bySlug.Values.Where(p => p.Section.Length > 0 && sectionTitles.All(s => s.Folder != p.Section)).Select(p => p.Section).Distinct())
            lib._problems.Add($"The folder \"{orphan}\" is not listed in sections.json.");
        return lib;
    }

    private List<(string Folder, string Title)> ReadSections(string root)
    {
        var path = Path.Combine(root, "sections.json");
        if (!File.Exists(path)) { _problems.Add("sections.json was not found."); return []; }
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.EnumerateArray().Select(e => (e.GetProperty("folder").GetString()!, e.GetProperty("title").GetString()!)).ToList();
    }

    /// <summary>The page before and after this one, in reading order (the front page, then each section in turn).</summary>
    public (DocPage? Previous, DocPage? Next) Neighbours(DocPage page)
    {
        var order = new List<DocPage>();
        if (Home is { } home) order.Add(home);
        order.AddRange(_sections.SelectMany(s => s.Pages));
        var i = order.FindIndex(p => p.Slug == page.Slug);
        return i < 0 ? (null, null) : (i > 0 ? order[i - 1] : null, i < order.Count - 1 ? order[i + 1] : null);
    }

    public IReadOnlyList<DocSearchHit> Search(string? query, int max = 20)
    {
        var words = Regex.Split((query ?? "").ToLowerInvariant(), @"[^\p{L}\p{N}]+").Where(w => w.Length >= 2).Distinct().ToList();
        if (words.Count == 0) return [];
        var hits = new List<DocSearchHit>();
        foreach (var page in _bySlug.Values)
        {
            var text = page.PlainText.ToLowerInvariant();
            var title = page.Title.ToLowerInvariant();
            if (!words.All(w => text.Contains(w) || title.Contains(w))) continue;
            var score = words.Sum(w => (title.Contains(w) ? 50 : 0) + Math.Min(Occurrences(text, w), 20));
            var heading = page.Headings.FirstOrDefault(h => words.All(w => h.Text.Contains(w, StringComparison.OrdinalIgnoreCase)));
            if (heading is not null) score += 30;
            hits.Add(new DocSearchHit(page, heading?.Id, heading?.Text, Snippet(page.PlainText, words), score));
        }
        return hits.OrderByDescending(h => h.Score).ThenBy(h => h.Page.Title, StringComparer.OrdinalIgnoreCase).Take(max).ToList();
    }

    private static int Occurrences(string text, string word)
    {
        var n = 0;
        for (var i = text.IndexOf(word, StringComparison.Ordinal); i >= 0; i = text.IndexOf(word, i + word.Length, StringComparison.Ordinal)) n++;
        return n;
    }

    private static string Snippet(string text, List<string> words)
    {
        var at = words.Select(w => text.IndexOf(w, StringComparison.OrdinalIgnoreCase)).Where(i => i >= 0).DefaultIfEmpty(0).Min();
        var start = Math.Max(0, at - 60);
        var end = Math.Min(text.Length, at + 160);
        return (start > 0 ? "…" : "") + text[start..end].Trim() + (end < text.Length ? "…" : "");
    }

    /// <summary>Gives a rendered page its links for the host: <paramref name="prefix"/> is empty on the docs subdomain and "/docs" elsewhere.</summary>
    public static string WithPrefix(string html, string prefix) => html.Replace($"href=\"{LinkToken}/", $"href=\"{prefix}/");

    private static string Resolve(string baseDir, string relativePath)
    {
        var parts = new List<string>(baseDir.Length == 0 ? [] : baseDir.Split('/'));
        foreach (var part in relativePath.Replace('\\', '/').Split('/'))
        {
            if (part is "" or ".") continue;
            if (part == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); }
            else parts.Add(part);
        }
        return string.Join('/', parts).ToLowerInvariant();
    }

    private static string HeadingText(HeadingBlock h)
    {
        var sb = new StringBuilder();
        void Walk(Markdig.Syntax.Inlines.Inline? inline)
        {
            for (; inline is not null; inline = inline.NextSibling)
            {
                switch (inline)
                {
                    case LiteralInline lit: sb.Append(lit.Content.ToString()); break;
                    case CodeInline code: sb.Append(code.Content); break;
                    case ContainerInline c: Walk(c.FirstChild); break;
                }
            }
        }
        Walk(h.Inline?.FirstChild);
        return sb.ToString();
    }

    private static (Dictionary<string, string> Meta, string Body) SplitFrontMatter(string text)
    {
        var meta = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        text = text.Replace("\r\n", "\n");
        if (!text.StartsWith("---\n")) return (meta, text);
        var end = text.IndexOf("\n---", 4, StringComparison.Ordinal);
        if (end < 0) return (meta, text);
        foreach (var line in text[4..end].Split('\n'))
        {
            var colon = line.IndexOf(':');
            if (colon > 0) meta[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }
        var bodyStart = text.IndexOf('\n', end + 1);
        return (meta, bodyStart < 0 ? "" : text[(bodyStart + 1)..]);
    }
}
