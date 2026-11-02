using System.Text;
using System.Xml.Linq;

namespace WindowSearcher.Tests;

public class InstallerTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "WindowSearcher.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Repository root not found");
    }

    [Fact]
    public void Embedded_images_are_not_also_content_files()
    {
        // WPF looks for a Content file on disk instead of the embedded Resource, so the image silently goes blank.
        var project = XDocument.Load(Path.Combine(RepoRoot(), "src", "WindowSearcher.App", "WindowSearcher.App.csproj"));
        string[] Includes(string item) => project.Descendants(item).Select(e => (string?)e.Attribute("Include")).OfType<string>().ToArray();

        Assert.NotEmpty(Includes("Resource"));
        Assert.Empty(Includes("Resource").Intersect(Includes("Content"), StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void Embedded_close_app_script_matches_close_app_ps1()
    {
        var root = RepoRoot();
        var script = File.ReadAllText(Path.Combine(root, "installer", "close-app.ps1"));
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var package = File.ReadAllText(Path.Combine(root, "installer", "Package.wxs"));

        Assert.True(package.Contains($"-EncodedCommand {encoded}", StringComparison.Ordinal),
            "installer\\Package.wxs embeds an outdated copy of installer\\close-app.ps1. Re-encode it: " +
            "[Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes((Get-Content installer\\close-app.ps1 -Raw)))");
    }
}
