using WindowSearcher.Core;

namespace WindowSearcher.Tests;

public class TitleCleanerTests
{
    [Theory]
    [InlineData("Log in | Helpdesk - Sleeping - Memory usage - 131 MB", "Log in | Helpdesk")]
    [InlineData("Access Portal - Approvals - Memory usage - 397 MB", "Access Portal - Approvals")]
    [InlineData("YouTube - Audio playing", "YouTube")]
    [InlineData("Devices - Example admin center - High memory usage - 1.2 GB", "Devices - Example admin center")]
    [InlineData("Devices - Example admin center - High memory usage", "Devices - Example admin center")]
    [InlineData("Helpdesk Developers", "Helpdesk Developers")]
    [InlineData("  spaced   out  ", "spaced out")]
    public void CleanTabTitle_strips_status_suffixes(string raw, string expected) =>
        Assert.Equal(expected, TitleCleaner.CleanTabTitle(raw));

    [Theory]
    [InlineData("Access Portal - Approvals and 5 more pages - Work - Microsoft​ Edge", "Access Portal - Approvals")]
    [InlineData("AI token costs explained | TechCrunch - Microsoft​ Edge", "AI token costs explained | TechCrunch")]
    [InlineData("Inbox and 1 more page - Personal - Microsoft Edge", "Inbox")]
    [InlineData("Notepad", "Notepad")]
    public void CleanBrowserWindowTitle_extracts_page_title(string caption, string expected) =>
        Assert.Equal(expected, TitleCleaner.CleanBrowserWindowTitle(caption));

    [Theory]
    [InlineData("(30) YouTube", "(31) YouTube")]
    [InlineData("(2) Inbox - Outlook", "Inbox - Outlook")]
    [InlineData("(99+) Feed", "Feed")]
    public void Unread_counters_are_ignored_when_matching(string a, string b)
    {
        Assert.True(TitleCleaner.TitlesMatch(a, b));
        Assert.Equal(TitleCleaner.Key(a), TitleCleaner.Key(b));
    }

    [Fact]
    public void Titles_that_are_only_a_number_still_match_exactly()
    {
        Assert.True(TitleCleaner.TitlesMatch("(3)", "(3)"));
        Assert.False(TitleCleaner.TitlesMatch("(3)", "(4)"));
        Assert.False(TitleCleaner.TitlesMatch("Report (2) draft", "Report draft"));
    }

    [Fact]
    public void Private_windows_are_detected() =>
        Assert.True(TitleCleaner.IsPrivateBrowserCaption("New tab - [InPrivate] - Microsoft​ Edge"));
}

public class AppSettingsTests
{
    [Theory]
    [InlineData("https://admin.example.com/#view/devices/apps", true)]
    [InlineData("https://endpoint.admin.example.com/", true)]
    [InlineData("https://notadmin.example.com/", false)]
    [InlineData("https://portal.azure.com/", false)]
    [InlineData(null, false)]
    public void admin portal_is_screenshot_only_by_default(string? url, bool expected) =>
        Assert.Equal(expected, new AppSettings().IsScreenshotOnly(url));
}

public class FtsQueryTests
{
    [Fact]
    public void Build_quotes_and_prefixes_every_word() =>
        Assert.Equal("\"token\"* \"cost\"*", FtsQuery.Build("Token cost"));

    [Fact]
    public void Build_neutralizes_fts_syntax() =>
        Assert.Equal("\"a\"* \"or\"* \"b\"*", FtsQuery.Build("a\" OR b*"));

    [Fact]
    public void Build_returns_null_without_words() =>
        Assert.Null(FtsQuery.Build("  -- ** "));
}

public class FuzzyMatcherTests
{
    [Fact]
    public void Word_start_beats_mid_word_beats_subsequence()
    {
        var wordStart = FuzzyMatcher.Score("crunch", "Tech Crunch news");
        var midWord = FuzzyMatcher.Score("crunch", "TechCrunch news");
        var subsequence = FuzzyMatcher.Score("tcrnch", "TechCrunch news");
        Assert.True(wordStart > midWord);
        Assert.True(midWord > subsequence);
        Assert.True(subsequence > 0);
    }

    [Fact]
    public void All_words_must_match() =>
        Assert.Equal(0, FuzzyMatcher.Score("token banana", "AI token costs"));

    [Fact]
    public void Scattered_letters_do_not_match() =>
        Assert.Equal(0, FuzzyMatcher.Score("abc", "a long sentence with b somewhere and c at the end"));

    [Fact]
    public void Words_can_span_title_process_and_url() =>
        Assert.True(FuzzyMatcher.ScoreFields("edge techcrunch", "AI token costs", "msedge", "https://techcrunch.com/x") > 0);
}

public class RankerTests
{
    [Fact]
    public void Recent_items_win_ties()
    {
        var recent = Ranker.Combine(0.8, 0, TimeSpan.FromMinutes(1));
        var old = Ranker.Combine(0.8, 0, TimeSpan.FromDays(2));
        Assert.True(recent > old);
    }

    [Fact]
    public void Relevance_outweighs_recency()
    {
        var strongOld = Ranker.Combine(1.0, 0, TimeSpan.FromDays(3));
        var weakNew = Ranker.Combine(0.3, 0, TimeSpan.Zero);
        Assert.True(strongOld > weakNew);
    }

    [Fact]
    public void CleanText_removes_placeholders_and_collapses_whitespace() =>
        Assert.Equal("Hello world again", Ranker.CleanText("￼ Hello\n\n  world￼\tagain ", 100));
}

public sealed class SearchIndexTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"ws-test-{Guid.NewGuid():N}.db");
    private readonly SearchIndex _index;

    public SearchIndexTests() => _index = new SearchIndex(_path);

    private static DocRecord Doc(string key, string title, string? body, string? thumb = null, DateTimeOffset? viewed = null) =>
        new(key, DocKind.Tab, 1, 2, "msedge", title, "https://techcrunch.com/" + key, body, thumb, viewed ?? DateTimeOffset.UtcNow);

    [Fact]
    public void Finds_stemmed_words_and_highlights_them()
    {
        _index.Upsert(Doc("a", "AI news", "The rising token costs of large language models"));
        _index.Upsert(Doc("b", "Recipes", "How to cook pasta"));

        var hits = _index.Search(FtsQuery.Build("token cost")!);

        var hit = Assert.Single(hits);
        Assert.Equal("a", hit.Key);
        Assert.Contains($"{SearchIndex.HighlightStart}token{SearchIndex.HighlightEnd}", hit.Snippet);
    }

    [Fact]
    public void Unchanged_text_is_not_reindexed_and_thumb_is_kept()
    {
        Assert.True(_index.Upsert(Doc("a", "T", "body", thumb: "one.jpg")).ContentChanged);
        var second = _index.Upsert(Doc("a", "T", "body"));
        Assert.False(second.ContentChanged);
        Assert.Equal("one.jpg", _index.GetSummaries().Single().Thumb);

        var third = _index.Upsert(Doc("a", "T", "body", thumb: "two.jpg"));
        Assert.Equal("one.jpg", third.PreviousThumb);
    }

    [Fact]
    public void Null_body_and_background_reads_keep_text_and_recency()
    {
        var viewed = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000);
        _index.Upsert(Doc("a", "T", "alpha", viewed: viewed));

        var r = _index.Upsert(Doc("a", "T", body: null) with { ViewedAt = null });

        Assert.False(r.ContentChanged);
        Assert.Equal(viewed, r.Summary.LastSeen);
        Assert.Single(_index.Search(FtsQuery.Build("alpha")!));
    }

    [Fact]
    public void Updated_text_replaces_old_text()
    {
        _index.Upsert(Doc("a", "T", "alpha"));
        _index.Upsert(Doc("a", "T", "beta"));
        Assert.Empty(_index.Search(FtsQuery.Build("alpha")!));
        Assert.Single(_index.Search(FtsQuery.Build("beta")!));
    }

    [Fact]
    public void Delete_removes_from_search()
    {
        _index.Upsert(Doc("a", "T", "alpha", thumb: "a.jpg"));
        var thumbs = _index.Delete(["a"]);
        Assert.Equal(["a.jpg"], thumbs);
        Assert.Empty(_index.Search(FtsQuery.Build("alpha")!));
        Assert.Equal(0, _index.Count());
    }

    public void Dispose()
    {
        _index.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _path, _path + "-wal", _path + "-shm" })
            try { File.Delete(f); } catch (IOException) { }
    }
}
