using WindowSearcher.Core;

namespace WindowSearcher.Tests;

public sealed class SettingsUpdateTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"wws-settings-{Guid.NewGuid():N}.json");

    public void Dispose() => File.Delete(_path);

    [Fact]
    public void New_settings_read_every_page()
    {
        Assert.Empty(new AppSettings().ScreenshotOnlyDomains);
        Assert.False(new AppSettings().IsScreenshotOnly("https://admin.example.com/#home"));
    }

    [Fact]
    public void File_from_an_older_release_is_updated_once()
    {
        File.WriteAllText(_path, """{ "Hotkey": "Ctrl+Alt+Space", "ScreenshotOnlyDomains": [ "admin.example.com" ] }""");
        var s = AppSettings.Load(_path);
        Assert.Equal(["admin.example.com"], s.ScreenshotOnlyDomains);
        Assert.Equal(AppSettings.CurrentVersion, s.SettingsVersion);
        Assert.Contains($"\"SettingsVersion\": {AppSettings.CurrentVersion}", File.ReadAllText(_path));
    }

    [Fact]
    public void List_the_user_edited_is_kept()
    {
        File.WriteAllText(_path, """{ "ScreenshotOnlyDomains": [ "admin.example.com", "example.com" ] }""");
        Assert.Equal(["admin.example.com", "example.com"], AppSettings.Load(_path).ScreenshotOnlyDomains);
    }

    [Fact]
    public void Other_settings_survive_the_update()
    {
        File.WriteAllText(_path, """{ "Hotkey": "Win+Shift+F", "OcrOnlyProcesses": [ "mmc" ], "ScreenshotOnlyDomains": [ "admin.example.com" ] }""");
        var s = AppSettings.Load(_path);
        Assert.Equal("Win+Shift+F", s.Hotkey);
        Assert.Equal(["mmc"], s.OcrOnlyProcesses);
    }

    [Theory]
    [InlineData("https://secure.chase.com/web/auth", true)]
    [InlineData("https://www.paypal.com/myaccount", true)]
    [InlineData("https://login.microsoftonline.com/common", true)]
    [InlineData("https://notchase.com/", false)]
    [InlineData("https://github.com/OzAILabs", false)]
    public void New_settings_exclude_sensitive_sites(string url, bool excluded) =>
        Assert.Equal(excluded, new AppSettings().IsUrlExcluded(url));

    [Fact]
    public void Older_files_get_the_starter_exclusions_and_keep_their_own()
    {
        File.WriteAllText(_path, """{ "SettingsVersion": 2, "ExcludedDomains": [ "example.com", "CHASE.com" ], "ExcludedProcesses": [ "mstsc" ] }""");
        var s = AppSettings.Load(_path);

        Assert.Contains("example.com", s.ExcludedDomains);
        Assert.True(s.IsUrlExcluded("https://paypal.com/"));
        Assert.Single(s.ExcludedDomains, d => d.Equals("chase.com", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("mstsc", s.ExcludedProcesses);
        Assert.True(s.IsProcessExcluded("Bitwarden"));
    }

    [Fact]
    public void A_starter_exclusion_the_user_removes_stays_removed()
    {
        File.WriteAllText(_path, """{ "SettingsVersion": 2 }""");
        var s = AppSettings.Load(_path);
        s.ExcludedDomains.Remove("paypal.com");
        s.Save(_path);

        Assert.False(AppSettings.Load(_path).IsUrlExcluded("https://paypal.com/"));
    }
}
