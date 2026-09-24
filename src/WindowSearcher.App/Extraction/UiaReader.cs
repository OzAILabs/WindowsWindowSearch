using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Conditions;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;
using WindowSearcher.Core;

namespace WindowSearcher.App.Extraction;

internal sealed record TabSnapshot(string RawName, string Title, bool IsActive);

internal sealed record BrowserSnapshot(
    IReadOnlyList<TabSnapshot> Tabs,
    string? ActiveTitle,
    string? Url,
    IReadOnlyList<AutomationElement> ContentAreas);

/// <summary>
/// Reads window content through UI Automation. Each instance must be used from a single thread.
/// Timeouts keep a hung target app from blocking us for long.
/// </summary>
internal sealed partial class UiaReader : IDisposable
{
    private readonly UIA3Automation _automation;
    private readonly ConditionFactory _cf;

    // Browser UI (Chromium "views") class names look like "EdgeTab" or "Tab"; web page elements
    // carry HTML class attributes instead, which rarely look like this.
    [GeneratedRegex(@"^[A-Z][A-Za-z]*Tab$")]
    private static partial Regex BrowserTabClass();

    public UiaReader(TimeSpan timeout)
    {
        _automation = new UIA3Automation
        {
            TransactionTimeout = timeout,
            ConnectionTimeout = timeout,
        };
        _cf = _automation.ConditionFactory;
    }

    /// <summary>
    /// Reads the tab strip and address bar of a Chromium browser window without touching any web
    /// page. Reaching into a page makes Edge switch on accessibility for it, which some pages
    /// (e.g. admin portals) react to with layout flicker; that only happens in
    /// <see cref="GetPageDocument"/>, when the page's text is actually needed.
    /// </summary>
    public BrowserSnapshot ReadBrowser(IntPtr hwnd, string caption)
    {
        var ui = WalkBrowserUi(hwnd);
        var tabs = ui.Tabs.Select(t => new TabSnapshot(t.Name, TitleCleaner.CleanTabTitle(t.Name), t.Selected)).ToList();

        // Fall back to the window caption when no tab reports itself as selected.
        var captionTitle = TitleCleaner.CleanBrowserWindowTitle(caption);
        if (tabs.Count > 0 && !tabs.Any(t => t.IsActive))
        {
            var idx = tabs.FindIndex(t => TitleCleaner.TitlesMatch(t.Title, captionTitle));
            if (idx < 0) idx = tabs.FindIndex(t => captionTitle.StartsWith(t.Title, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0) tabs[idx] = tabs[idx] with { IsActive = true };
        }
        var activeTitle = tabs.FirstOrDefault(t => t.IsActive)?.Title ?? captionTitle;
        var url = NormalizeUrl(ui.Omnibox?.Patterns.Value.PatternOrDefault?.Value.ValueOrDefault);
        return new BrowserSnapshot(tabs, activeTitle, url, ui.ContentAreas);
    }

    /// <summary>
    /// The active page's document (RootWebArea). This reaches into the page, so Edge switches on
    /// accessibility for it; call only when the text is needed.
    /// </summary>
    public AutomationElement? GetPageDocument(BrowserSnapshot snapshot)
    {
        var walker = _automation.TreeWalkerFactory.GetRawViewWalker();
        var docs = new List<AutomationElement>();
        foreach (var area in snapshot.ContentAreas)
        {
            if (area.Properties.ControlType.ValueOrDefault == ControlType.Document)
            {
                docs.Add(area);
                continue;
            }
            // The main page lives in MultiContentsView -> View -> Document. Find that container first
            // (pre-order, so it's reached before any page), then step down a few levels to the document.
            var start = area.Properties.ClassName.ValueOrDefault == "MultiContentsView"
                ? area
                : area.FindFirstDescendant(_cf.ByClassName("MultiContentsView"));
            if (start is null)
            {
                // Other layouts (e.g. app windows): take the first document under this area.
                var doc = area.FindFirstDescendant(_cf.ByControlType(ControlType.Document));
                if (doc is not null) docs.Add(doc);
                continue;
            }
            var level = new List<AutomationElement> { start };
            for (var depth = 0; depth < 4 && level.Count > 0; depth++)
            {
                var next = new List<AutomationElement>();
                foreach (var el in level)
                {
                    for (var child = walker.GetFirstChild(el); child is not null; child = walker.GetNextSibling(child))
                    {
                        if (child.Properties.ControlType.ValueOrDefault == ControlType.Document) docs.Add(child);
                        else next.Add(child);
                    }
                }
                level = next;
            }
        }

        string Id(AutomationElement e) => e.Properties.AutomationId.ValueOrDefault ?? string.Empty;
        string Name(AutomationElement e) => e.Properties.Name.ValueOrDefault ?? string.Empty;
        return docs.FirstOrDefault(d => Id(d) == "RootWebArea" && TitleCleaner.TitlesMatch(Name(d), snapshot.ActiveTitle ?? string.Empty))
               ?? docs.FirstOrDefault(d => Id(d) == "RootWebArea")
               ?? docs.FirstOrDefault();
    }

    /// <summary>
    /// true (default): read tabs and the address bar without touching web pages.
    /// false: the 1.0.4 behavior, one search over the whole window including the page.
    /// Set from AppSettings.ReadTabsWithoutPages.
    /// </summary>
    public static volatile bool PageFreeBrowserReads = true;

    private sealed record BrowserUi(
        List<(AutomationElement El, string Name, bool Selected)> Tabs,
        AutomationElement? Omnibox,
        List<AutomationElement> ContentAreas);

    /// <summary>Is this Edge/Chrome view a container that hosts web pages (main page, side panes)?</summary>
    private static bool IsPageContainer(string cls) =>
        cls is "MultiContentsView" or "HubWebViewContainerView" || cls.Contains("WebView", StringComparison.Ordinal);

    private static bool IsMainPageArea(string cls) =>
        cls is "MultiContentsView" || cls.Contains("ContentsWebView", StringComparison.Ordinal);

    private CacheRequest NewBrowserCache()
    {
        var lib = _automation.PropertyLibrary;
        // Raw view: Edge marks its tabs as non-control elements.
        var cache = new CacheRequest
        {
            TreeScope = TreeScope.Element,
            AutomationElementMode = AutomationElementMode.Full,
            TreeFilter = TrueCondition.Default,
        };
        cache.Add(lib.Element.Name);
        cache.Add(lib.Element.ClassName);
        cache.Add(lib.Element.ControlType);
        cache.Add(lib.Element.AutomationId);
        cache.Add(lib.SelectionItem.IsSelected);
        return cache;
    }

    /// <summary>
    /// Finds the tabs, the address bar and the page containers of a browser window. Goes straight
    /// to Edge/Chrome's BrowserView and searches only its toolbar and tab bar (about 9 requests,
    /// ~40 ms), never entering a page. Other layouts fall back to a pruned walk of the whole window.
    /// </summary>
    private BrowserUi WalkBrowserUi(IntPtr hwnd)
    {
        if (!PageFreeBrowserReads) return SearchWholeWindow(hwnd);

        var root = _automation.FromHandle(hwnd);
        var lib = _automation.PropertyLibrary;
        var tabs = new List<(AutomationElement, string, bool)>();
        AutomationElement? omnibox = null;
        var content = new List<AutomationElement>();

        using (NewBrowserCache().Activate())
        {
            var browserView = FindBrowserView(root);
            if (browserView is null) return WalkPruned(root);

            void AddTabs(AutomationElement container)
            {
                foreach (var el in container.FindAllDescendants(_cf.ByControlType(ControlType.TabItem)))
                {
                    var cls = el.Properties.ClassName.ValueOrDefault ?? string.Empty;
                    var id = el.Properties.AutomationId.ValueOrDefault ?? string.Empty;
                    if (!BrowserTabClass().IsMatch(cls) || (id.Length > 0 && !id.StartsWith("view_", StringComparison.Ordinal)))
                        continue;
                    el.FrameworkAutomationElement.TryGetPropertyValue(lib.SelectionItem.IsSelected, out bool selected);
                    tabs.Add((el, el.Properties.Name.ValueOrDefault ?? string.Empty, selected));
                }
            }

            foreach (var child in browserView.FindAllChildren())
            {
                var cls = child.Properties.ClassName.ValueOrDefault ?? string.Empty;
                if (cls == "TopContainerView")
                {
                    // Toolbar (address bar); Chrome also keeps its tab strip here.
                    omnibox ??= child.FindFirstDescendant(_cf.ByClassName("OmniboxViewViews"));
                    AddTabs(child);
                }
                else if (cls.EndsWith("TabStripRegionView", StringComparison.Ordinal))
                {
                    AddTabs(child); // Edge's horizontal or vertical tab bar
                }
                else
                {
                    content.Add(child); // page area, side panes: only entered by GetPageDocument
                }
            }
        }
        return new BrowserUi(tabs, omnibox, content);
    }

    /// <summary>Breadth-first down to BrowserView through the frame views above it, never into pages.</summary>
    private static AutomationElement? FindBrowserView(AutomationElement root)
    {
        var level = new List<AutomationElement> { root };
        for (var depth = 0; depth < 6 && level.Count > 0; depth++)
        {
            var next = new List<AutomationElement>();
            foreach (var el in level)
            {
                foreach (var child in el.FindAllChildren())
                {
                    var cls = child.Properties.ClassName.ValueOrDefault ?? string.Empty;
                    if (cls == "BrowserView") return child;
                    if (child.Properties.ControlType.ValueOrDefault != ControlType.Document && !IsPageContainer(cls))
                        next.Add(child);
                }
            }
            level = next;
        }
        return null;
    }

    /// <summary>
    /// Fallback for unfamiliar layouts: depth-first over the window, one request per level,
    /// stopping at page containers and documents. Must be called with a cache request active.
    /// </summary>
    private BrowserUi WalkPruned(AutomationElement root)
    {
        var lib = _automation.PropertyLibrary;
        var tabs = new List<(AutomationElement, string, bool)>();
        AutomationElement? omnibox = null;
        var content = new List<AutomationElement>();
        var stack = new Stack<(AutomationElement El, int Depth)>();
        stack.Push((root, 0));
        var visited = 0;
        while (stack.Count > 0 && visited++ < 2000)
        {
            var (el, depth) = stack.Pop();
            if (depth > 0)
            {
                var type = el.Properties.ControlType.ValueOrDefault;
                var cls = el.Properties.ClassName.ValueOrDefault ?? string.Empty;
                if (type == ControlType.Document)
                {
                    content.Add(el); // app/popup windows expose their page directly
                    continue;
                }
                if (IsPageContainer(cls))
                {
                    if (IsMainPageArea(cls)) content.Add(el);
                    continue;
                }
                if (type == ControlType.TabItem)
                {
                    var id = el.Properties.AutomationId.ValueOrDefault ?? string.Empty;
                    if (BrowserTabClass().IsMatch(cls) && (id.Length == 0 || id.StartsWith("view_", StringComparison.Ordinal)))
                    {
                        el.FrameworkAutomationElement.TryGetPropertyValue(lib.SelectionItem.IsSelected, out bool selected);
                        tabs.Add((el, el.Properties.Name.ValueOrDefault ?? string.Empty, selected));
                    }
                    continue;
                }
                if (cls == "OmniboxViewViews")
                {
                    omnibox ??= el;
                    continue;
                }
            }
            if (depth >= 25) continue;
            var children = el.FindAllChildren();
            for (var i = children.Length - 1; i >= 0; i--) stack.Push((children[i], depth + 1));
        }
        return new BrowserUi(tabs, omnibox, content);
    }

    /// <summary>The 1.0.4 method: one search over the whole window, which also walks the page.</summary>
    private BrowserUi SearchWholeWindow(IntPtr hwnd)
    {
        var root = _automation.FromHandle(hwnd);
        var lib = _automation.PropertyLibrary;
        var tabs = new List<(AutomationElement, string, bool)>();
        AutomationElement? omnibox = null;
        var content = new List<AutomationElement>();
        using (NewBrowserCache().Activate())
        {
            var found = root.FindAllDescendants(
                _cf.ByControlType(ControlType.TabItem)
                    .Or(_cf.ByControlType(ControlType.Document))
                    .Or(_cf.ByClassName("OmniboxViewViews")));
            foreach (var el in found)
            {
                var type = el.Properties.ControlType.ValueOrDefault;
                var cls = el.Properties.ClassName.ValueOrDefault ?? string.Empty;
                var id = el.Properties.AutomationId.ValueOrDefault ?? string.Empty;
                if (type == ControlType.TabItem)
                {
                    if (!BrowserTabClass().IsMatch(cls) || (id.Length > 0 && !id.StartsWith("view_", StringComparison.Ordinal)))
                        continue;
                    el.FrameworkAutomationElement.TryGetPropertyValue(lib.SelectionItem.IsSelected, out bool selected);
                    tabs.Add((el, el.Properties.Name.ValueOrDefault ?? string.Empty, selected));
                }
                else if (type == ControlType.Document)
                {
                    content.Add(el);
                }
                else if (cls == "OmniboxViewViews")
                {
                    omnibox ??= el;
                }
            }
        }
        return new BrowserUi(tabs, omnibox, content);
    }
    /// <summary>Text of a web page element; walks into frames and embedded objects when the text pattern is sparse.</summary>
    public string ReadDocumentText(AutomationElement document, int maxChars, TimeSpan budget)
    {
        var sw = Stopwatch.StartNew();
        var text = string.Empty;
        var textPattern = document.Patterns.Text.PatternOrDefault;
        if (textPattern is not null) text = textPattern.DocumentRange.GetText(maxChars) ?? string.Empty;

        var placeholders = text.Count(c => c == '￼');
        var clean = Ranker.CleanText(text, maxChars);
        if (clean.Length >= 1500 && placeholders < 20) return clean;
        if (sw.Elapsed > budget) return clean;

        // Sparse or iframe-heavy page: collect the names of text-bearing elements instead.
        var names = CollectNames(document, maxChars, TextBearing());
        return names.Length > clean.Length ? names : clean;
    }

    /// <summary>Text of a non-browser window: documents/edits first, then visible labels and list items.</summary>
    public string ReadGenericText(IntPtr hwnd, int maxChars, TimeSpan budget)
    {
        var sw = Stopwatch.StartNew();
        var root = _automation.FromHandle(hwnd);
        var sb = new StringBuilder();

        var editors = root.FindAllDescendants(_cf.ByControlType(ControlType.Document).Or(_cf.ByControlType(ControlType.Edit)));
        foreach (var el in editors.Take(20))
        {
            if (sb.Length >= maxChars || sw.Elapsed > budget) break;
            if (el.Properties.IsPassword.ValueOrDefault) continue;
            string? t = null;
            var tp = el.Patterns.Text.PatternOrDefault;
            if (tp is not null) t = tp.DocumentRange.GetText(maxChars - sb.Length);
            else t = el.Patterns.Value.PatternOrDefault?.Value.ValueOrDefault;
            if (!string.IsNullOrWhiteSpace(t)) sb.Append(t).Append('\n');
        }

        var text = Ranker.CleanText(sb.ToString(), maxChars);
        if (text.Length >= 500 || sw.Elapsed > budget) return text;

        var names = CollectNames(root, maxChars - text.Length,
            TextBearing().Or(_cf.ByControlType(ControlType.ListItem)).Or(_cf.ByControlType(ControlType.TreeItem))
                .Or(_cf.ByControlType(ControlType.DataItem)).Or(_cf.ByControlType(ControlType.TabItem)));
        return text.Length == 0 ? names : $"{text} {names}";
    }

    /// <summary>
    /// Selects the tab with the given title in a browser window. Returns its position in the tab
    /// strip (same order as <see cref="ReadBrowser"/>), or -1 if no tab has that title.
    /// </summary>
    public int SelectTab(IntPtr hwnd, string title, int indexHint)
    {
        var candidates = WalkBrowserUi(hwnd).Tabs
            .Select(t => (El: t.El, Title: TitleCleaner.CleanTabTitle(t.Name)))
            .ToList();

        // Same title may appear twice (two "Inbox" tabs): prefer the one at the remembered position.
        var matches = candidates.Select((c, i) => (c.El, c.Title, Index: i))
            .Where(c => TitleCleaner.TitlesMatch(c.Title, title))
            .OrderBy(c => Math.Abs(c.Index - indexHint))
            .ToList();
        if (matches.Count == 0) return -1;
        var (tab, _, index) = matches[0];

        var selection = tab.Patterns.SelectionItem.PatternOrDefault;
        if (selection is not null)
        {
            selection.Select();
            return index;
        }
        var legacy = tab.Patterns.LegacyIAccessible.PatternOrDefault;
        if (legacy is not null)
        {
            legacy.DoDefaultAction();
            return index;
        }
        tab.Patterns.Invoke.PatternOrDefault?.Invoke();
        return index;
    }

    /// <summary>
    /// Cheap load check: number of visible characters at the start of the page (capped), used to
    /// tell when a waking tab has finished rendering.
    /// </summary>
    public int PeekTextLength(AutomationElement document)
    {
        var text = document.Patterns.Text.PatternOrDefault?.DocumentRange.GetText(20_000);
        return text is null ? 0 : text.Count(c => c != '￼' && !char.IsWhiteSpace(c));
    }

    private ConditionBase TextBearing() =>
        _cf.ByControlType(ControlType.Text)
            .Or(_cf.ByControlType(ControlType.Hyperlink))
            .Or(_cf.ByControlType(ControlType.Header))
            .Or(_cf.ByControlType(ControlType.HeaderItem));

    private string CollectNames(AutomationElement root, int maxChars, ConditionBase condition)
    {
        if (maxChars <= 0) return string.Empty;
        var cache = new CacheRequest { TreeScope = TreeScope.Element, AutomationElementMode = AutomationElementMode.None };
        cache.Add(_automation.PropertyLibrary.Element.Name);
        var sb = new StringBuilder();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        using (cache.Activate())
        {
            foreach (var el in root.FindAllDescendants(condition))
            {
                var name = el.Properties.Name.ValueOrDefault;
                if (string.IsNullOrWhiteSpace(name) || !seen.Add(name)) continue;
                sb.Append(name).Append(' ');
                if (sb.Length >= maxChars) break;
            }
        }
        return Ranker.CleanText(sb.ToString(), maxChars);
    }

    private static string? NormalizeUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var v = value.Trim();
        if (v.Contains(' ')) return null; // user is typing a search into the address bar
        if (!v.Contains("://", StringComparison.Ordinal) &&
            !v.StartsWith("edge:", StringComparison.OrdinalIgnoreCase) &&
            !v.StartsWith("chrome:", StringComparison.OrdinalIgnoreCase) &&
            !v.StartsWith("about:", StringComparison.OrdinalIgnoreCase))
            v = "https://" + v;
        return v;
    }

    public void Dispose() => _automation.Dispose();
}
