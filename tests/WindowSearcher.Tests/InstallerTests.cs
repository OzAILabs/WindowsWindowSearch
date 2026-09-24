using System.Text;

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
