using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Navigation;
using WindowSearcher.App.Services;

namespace WindowSearcher.App.UI;

internal partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        var asm = Assembly.GetExecutingAssembly();
        var version = asm.GetName().Version;
        VersionText.Text = $"Version {version?.ToString(3)}";
        var company = asm.GetCustomAttribute<AssemblyCompanyAttribute>()?.Company;
        var copyright = asm.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright;
        CopyrightText.Text = string.Join("  ·  ", new[] { company, copyright }.Where(s => !string.IsNullOrEmpty(s)));
    }

    private void OnLinkClick(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error("Could not open the link", ex);
        }
        e.Handled = true;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
