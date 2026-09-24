# Run by the installer just before InstallValidate (upgrade and uninstall).
# Ends Windows Window Search, then waits until none of its files has been open by anything for 2 s
# (at least 5 s after closing, at most 60 s). Right after the app exits, security software re-opens
# the exe for about 3 s; if InstallValidate runs then, Windows Installer's fallback files-in-use check
# (used where policy disables Restart Manager) walks every process's memory map and can take 15+ min.
# Checks use read access with no sharing, so this also works for a standard user.
$dir = Join-Path $env:ProgramFiles 'Windows Window Search'
Get-Process -Name WindowSearcher -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
if (-not (Test-Path -LiteralPath $dir)) { exit 0 }
$files = @(Get-ChildItem -LiteralPath $dir -File -ErrorAction SilentlyContinue)
$sw = [Diagnostics.Stopwatch]::StartNew()
$free = 0
while ($sw.Elapsed.TotalSeconds -lt 60) {
    $locked = $false
    foreach ($f in $files) {
        try { $s = [IO.File]::Open($f.FullName, 'Open', 'Read', 'None'); $s.Close() }
        catch [IO.IOException] { $locked = $true; break }
        catch { }
    }
    if ($locked) { $free = 0 } else { $free++ }
    if ($free -ge 8 -and $sw.Elapsed.TotalSeconds -ge 5) { break }
    Start-Sleep -Milliseconds 250
}
exit 0
