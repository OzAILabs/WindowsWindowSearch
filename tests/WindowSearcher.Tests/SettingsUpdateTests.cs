using WindowSearcher.Core;

namespace WindowSearcher.Tests;

public sealed class SettingsUpdateTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"wws-settings-{Guid.NewGuid():N}.json");

    public void Dispose() => File.Delete(_path);

    [Fact]
    public void New_settings_read_admin_portal_pages()
    {
        Assert.Empty(new AppSettings().ScreenshotOnlyDomains);
        Assert.False(new AppSettings().IsScreenshotOnly("https://admin.example.com/#home"));
    }

    [Fact]
    public void File_from_an_older_release_with_the_old_default_is_updated_once()
    {
        File.WriteAllText(_path, """{ "Hotkey": "Ctrl+Alt+Space", "ScreenshotOnlyDomains": [ "admin.example.com" ] }""");
        var s = AppSettings.Load(_path);
        Assert.Empty(s.ScreenshotOnlyDomains);
        Assert.Equal(AppSettings.CurrentVersion, s.SettingsVersion);

        // Written back, so a site the user adds afterwards stays.
        var saved = File.ReadAllText(_path);
        Assert.Contains("\"SettingsVersion\": 2", saved);
        s.ScreenshotOnlyDomains = ["admin.example.com"];
        s.Save(_path);
        Assert.Equal(["admin.example.com"], AppSettings.Load(_path).ScreenshotOnlyDomains);
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
}
