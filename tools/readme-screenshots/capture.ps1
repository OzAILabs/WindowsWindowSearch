param(
    [string]$Exe = (Join-Path $PSScriptRoot '..\..\src\WindowSearcher.App\bin\Debug\net10.0-windows10.0.22621.0\WindowSearcher.exe'),
    [string]$OutDir = (Join-Path $PSScriptRoot '..\..\docs\screenshots'),
    # Also saves one browser window at full size here, to check that no account or warning shows in it.
    [string]$CheckDir
)
# Takes the README screenshots from the real app, using only fake websites (pages\*.html on *.example addresses).
# Needs the Debug build and Python, and the normal copy of the app closed. It opens a throwaway Edge profile, and the
# Debug-only WWS_DEMO_* variables make the app see only that browser and keep its data in a temporary folder, so no
# real window, tab or saved page can appear. Leave the PC alone while it runs: it switches between its own windows.
$ErrorActionPreference = 'Stop'
if (Get-Process -Name WindowSearcher -ErrorAction SilentlyContinue) { throw 'Close Windows Window Search first.' }
$Exe = (Resolve-Path $Exe).Path
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$OutDir = (Resolve-Path $OutDir).Path
Add-Type -AssemblyName System.Drawing, System.Windows.Forms, UIAutomationClient, UIAutomationTypes
Add-Type -TypeDefinition @"
using System; using System.Collections.Generic; using System.Runtime.InteropServices; using System.Text;
public static class Demo {
  delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32")] static extern bool EnumWindows(EnumProc p, IntPtr l);
  [DllImport("user32")] static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32", CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32", CharSet=CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [DllImport("user32")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32")] public static extern void SwitchToThisWindow(IntPtr h, bool alt);
  [DllImport("user32")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint f);
  [DllImport("user32")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
  [DllImport("user32")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("dwmapi")] public static extern int DwmGetWindowAttribute(IntPtr h, int a, out RECT r, int size);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  public static string Title(IntPtr h) { var s = new StringBuilder(300); GetWindowText(h, s, 300); return s.ToString(); }
  public static int Owner(IntPtr h) { uint p; GetWindowThreadProcessId(h, out p); return (int)p; }
  public static IntPtr[] Browser(string startsWith) {
    var list = new List<IntPtr>();
    EnumWindows((h, l) => {
      if (!IsWindowVisible(h)) return true;
      var c = new StringBuilder(64); GetClassName(h, c, 64);
      if (c.ToString() == "Chrome_WidgetWin_1" && Title(h).StartsWith(startsWith)) list.Add(h);
      return true;
    }, IntPtr.Zero);
    return list.ToArray();
  }
  public static IntPtr[] Of(int pid) {
    var list = new List<IntPtr>();
    EnumWindows((h, l) => { if (Owner(h) == pid && IsWindowVisible(h)) list.Add(h); return true; }, IntPtr.Zero);
    return list.ToArray();
  }
}
"@

function Wait-For([scriptblock]$Condition, [int]$Seconds) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.Elapsed.TotalSeconds -lt $Seconds) { $r = & $Condition; if ($r) { return $r }; Start-Sleep -Milliseconds 250 }
    return $null
}
function Save-Window([IntPtr]$H, [string]$Name, [string]$Folder = $OutDir) {
    # The visible frame only: a normal window's rectangle includes an invisible border.
    $whole = New-Object Demo+RECT; [void][Demo]::GetWindowRect($H, [ref]$whole)
    $seen = New-Object Demo+RECT; [void][Demo]::DwmGetWindowAttribute($H, 9, [ref]$seen, 16)
    $bmp = New-Object Drawing.Bitmap(($whole.R - $whole.L), ($whole.B - $whole.T))
    $g = [Drawing.Graphics]::FromImage($bmp); $dc = $g.GetHdc(); [void][Demo]::PrintWindow($H, $dc, 2); $g.ReleaseHdc($dc); $g.Dispose()
    $crop = New-Object Drawing.Rectangle(($seen.L - $whole.L), ($seen.T - $whole.T), ($seen.R - $seen.L), ($seen.B - $seen.T))
    $final = $bmp.Clone($crop, [Drawing.Imaging.PixelFormat]::Format24bppRgb); $bmp.Dispose()
    $final.Save((Join-Path $Folder $Name), [Drawing.Imaging.ImageFormat]::Png); $final.Dispose()
    "  saved $Name"
}
function Read-Name($Root, [string]$Id) {
    $c = New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::AutomationIdProperty, $Id)
    $e = $Root.FindFirst([Windows.Automation.TreeScope]::Descendants, $c)
    if ($e) { return $e.Current.Name }
    return ''
}
function Open-Search([string[]]$Arguments) {
    Start-Process $Exe -ArgumentList $Arguments
    $h = Wait-For { @([Demo]::Of($script:app.Id)) | Select-Object -First 1 } 10
    if (-not $h) { throw 'The search window did not open' }
    Start-Sleep -Milliseconds 1300
    return [IntPtr]$h
}
function Select-Result([IntPtr]$H, [string]$TitlePattern) {
    $root = [Windows.Automation.AutomationElement]::FromHandle($H)
    for ($i = 0; $i -lt 15 -and (Read-Name $root 'PreviewTitle') -notmatch $TitlePattern; $i++) {
        if ([Demo]::GetForegroundWindow() -ne $H) { throw 'The search window lost focus; leave the PC alone while this runs.' }
        [Windows.Forms.SendKeys]::SendWait('{DOWN}'); Start-Sleep -Milliseconds 300
    }
    Start-Sleep -Milliseconds 900
    "  '$(Read-Name $root 'SearchBox')' selected '$(Read-Name $root 'PreviewTitle')': $(Read-Name $root 'StatusText')"
}
function Hide-Search([IntPtr]$H) {
    if ([Demo]::GetForegroundWindow() -eq $H) { [Windows.Forms.SendKeys]::SendWait('{ESC}') }
    [void](Wait-For { @([Demo]::Of($script:app.Id)).Count -eq 0 } 3)
}

# Least recently viewed first, because the search window lists the latest first. The last two are closed or searched.
$sites = @(
    @{ Host = 'skycast.example';    Title = 'Portland weather' },
    @{ Host = 'crumbandco.example'; Title = 'Sourdough basics' },
    @{ Host = 'wayfarer.example';   Title = 'Lisbon in spring' },
    @{ Host = 'kanbanly.example';   Title = 'Sprint 42 board' },
    @{ Host = 'stackwise.example';  Title = 'How we cut our LLM bill' },
    @{ Host = 'gridly.example';     Title = 'Assistant rollout budget' },
    @{ Host = 'fernmail.example';   Title = 'RE: Pricing for the assistant rollout' },
    @{ Host = 'meridian-api.example'; Title = 'Pricing' },
    @{ Host = 'costwise.example';   Title = 'Token cost calculator' },
    @{ Host = 'dailycircuit.example'; Title = 'AI token costs are falling' }
)

$work = Join-Path ([IO.Path]::GetTempPath()) "wws-readme-$PID"
$profile = Join-Path $work 'edge'
$server = $null; $script:app = $null
$port = 18443
try {
    New-Item -ItemType Directory -Force -Path $work | Out-Null
    $rsa = [Security.Cryptography.RSA]::Create(2048)
    $request = [Security.Cryptography.X509Certificates.CertificateRequest]::new('CN=fake.example', $rsa, [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)
    $cert = $request.CreateSelfSigned([DateTimeOffset]::Now.AddDays(-1), [DateTimeOffset]::Now.AddDays(2))
    Set-Content (Join-Path $work 'cert.pem') $cert.ExportCertificatePem() -Encoding ascii
    Set-Content (Join-Path $work 'key.pem') $rsa.ExportPkcs8PrivateKeyPem() -Encoding ascii
    $server = Start-Process python -ArgumentList '-I', "`"$PSScriptRoot\serve.py`"", $port, "`"$work\cert.pem`"", "`"$work\key.pem`"" -WindowStyle Hidden -PassThru

    "--- opening the fake sites ---"
    # Guest mode: on a work PC, a normal new profile signs in to the Windows account and would show it in the screenshots.
    $certKey = [Convert]::ToBase64String([Security.Cryptography.SHA256]::HashData($cert.PublicKey.ExportSubjectPublicKeyInfo()))
    $edgeFlags = @("--user-data-dir=`"$profile`"", '--guest', '--disable-sync', '--no-first-run', '--no-default-browser-check', '--test-type',
        "--ignore-certificate-errors-spki-list=$certKey", "--host-resolver-rules=`"MAP *.example 127.0.0.1:$port`"",
        '--window-size=1500,1000', '--window-position=60,40', '--new-window')
    $windows = @{}
    foreach ($site in $sites) {
        Start-Process msedge -ArgumentList ($edgeFlags + "https://$($site.Host)/")
        $h = Wait-For { @([Demo]::Browser($site.Title)) | Select-Object -First 1 } 40
        if (-not $h) { throw "Edge did not show $($site.Host)" }
        $windows[$site.Host] = [IntPtr]$h
        "  $($site.Host)"
    }
    $browserPid = [Demo]::Owner($windows[$sites[0].Host])
    if (@($windows.Values | Where-Object { [Demo]::Owner($_) -ne $browserPid }).Count) { throw 'The fake sites opened in more than one browser.' }

    "--- starting the app, which can only see that browser ---"
    $env:WWS_DEMO_PID = "$browserPid"
    $env:WWS_DEMO_DATA = Join-Path $work 'data'
    New-Item -ItemType Directory -Force -Path $env:WWS_DEMO_DATA | Out-Null
    # Reads and captures a window every time it's viewed, so a read that fails the first time is made up for.
    Set-Content (Join-Path $env:WWS_DEMO_DATA 'settings.json') '{ "SettingsVersion": 3, "ReindexAfterSeconds": 1, "RecaptureAfterSeconds": 1 }' -Encoding utf8
    $script:app = Start-Process $Exe -ArgumentList '--background' -PassThru
    $log = Join-Path $env:WWS_DEMO_DATA 'log.txt'
    if (-not (Wait-For { (Test-Path $log) -and (Get-Content $log | Select-String 'Startup pass done') } 60)) { throw 'The app did not finish reading the windows.' }

    "--- viewing each site, so it gets a real preview ---"
    foreach ($pass in 1, 2) {
        foreach ($site in $sites) {
            [Demo]::SwitchToThisWindow($windows[$site.Host], $true)
            $seen = @(Get-Content $log | Select-String 'Foreground msedge').Count
            $read = Wait-For { @(Get-Content $log | Select-String 'Foreground msedge').Count -gt $seen } 8
            "  pass $pass $($site.Host): in front: $([Demo]::GetForegroundWindow() -eq $windows[$site.Host]); read: $([bool]$read)"
            Start-Sleep -Milliseconds 500
        }
    }
    if ($CheckDir) { Start-Sleep -Milliseconds 500; Save-Window $windows['dailycircuit.example'] 'edge-check.png' $CheckDir }

    "--- closing one, so it becomes a closed tab ---"
    [void][Demo]::PostMessage($windows['costwise.example'], 0x10, [IntPtr]::Zero, [IntPtr]::Zero)
    [void](Wait-For { @([Demo]::Browser('Token cost calculator')).Count -eq 0 } 10)
    Start-Sleep -Seconds 1

    "--- screenshots ---"
    $h = Open-Search @('--query', 'token', 'cost')
    Select-Result $h '^AI token costs'
    Save-Window $h 'search.png'
    Select-Result $h '^Token cost calculator'
    Save-Window $h 'closed-tab.png'
    Hide-Search $h

    # Taller for this one, so all three rows of the grid fit.
    Set-Content (Join-Path $env:WWS_DEMO_DATA 'launcher.json') '{ "Left": 120, "Top": 60, "Width": 1770, "Height": 1340 }' -Encoding ascii
    $h = Open-Search @('--grid')
    Start-Sleep -Milliseconds 1200
    "  grid: $(Read-Name ([Windows.Automation.AutomationElement]::FromHandle($h)) 'StatusText')"
    Save-Window $h 'grid.png'
    Hide-Search $h

    Start-Process $Exe -ArgumentList '--settings'
    $s = Wait-For { @([Demo]::Of($script:app.Id) | Where-Object { [Demo]::Title($_) -match 'Settings' }) | Select-Object -First 1 } 10
    if ($s) { Start-Sleep -Milliseconds 900; Save-Window ([IntPtr]$s) 'settings.png'; [void][Demo]::PostMessage([IntPtr]$s, 0x10, [IntPtr]::Zero, [IntPtr]::Zero) }
    else { '  the settings window did not open' }

    $errors = @(Get-Content $log | Select-String ' ERROR ')
    "errors in the demo log: $($errors.Count)"
    $errors | ForEach-Object { $_.Line -replace '^\S+ \S+ ERROR ', '' } | Group-Object | ForEach-Object { "  $($_.Count) x $($_.Name)" }
}
finally {
    Remove-Item Env:WWS_DEMO_PID, Env:WWS_DEMO_DATA -ErrorAction SilentlyContinue
    if ($script:app) { Stop-Process -Id $script:app.Id -Force -ErrorAction SilentlyContinue }
    # Only the processes started with this temporary profile; any other Edge stays open.
    Get-CimInstance Win32_Process -Filter "Name='msedge.exe'" | Where-Object { $_.CommandLine -like "*$profile*" } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    if ($server) { Stop-Process -Id $server.Id -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Seconds 2
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
    "temporary folder removed: $(-not (Test-Path $work))"
}
