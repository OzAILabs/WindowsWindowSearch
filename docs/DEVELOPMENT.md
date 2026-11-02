# Developer notes

Everything about how Windows Window Search works, its settings, and how it's built and released. For what the app does, see the [README](../README.md).

## Use

- **Ctrl+Alt+Space** opens the launcher. With no text, it lists windows and tabs, most recent first.
  Type to search, for example `token cost`.
- **↑/↓** select, **Enter** switch to it (restores the window and selects the tab), **Tab** or the title bar's
  **Show grid** / **Show list** button toggles the thumbnail grid, **F1** opens the user guide, **Esc** closes.
- Drag the shaded title bar (or the status bar) to move the launcher and its edges to resize it; × closes it. It reopens where it was left
  (`launcher.json` in the data folder), shrunk to fit if that screen is smaller. It falls back to the pointer's
  screen if that one is gone. **Settings → Reset size and position** forgets it.
- Clicking the tray icon toggles the launcher. The tray menu has pause (the app then doesn't touch other
  apps at all), start with Windows, **Settings…** (a settings window), **Help** (the user guide), **About**, log and clear index.
- Launching the exe again opens the launcher of the running instance. `--grid` opens the grid view,
  `--query <text>` opens it pre-filled, `--settings` opens settings, `--about` the About window and `--help` the user guide.

The user guide is [src/WindowSearcher.App/Help/index.html](../src/WindowSearcher.App/Help/index.html). It's installed next to the exe
(`Help\index.html`, with the logo and branding images) and opens in the default browser from the tray menu, F1 in the
launcher, or the **Windows Window Search Help** Start menu shortcut. Update it when behavior or settings change.

## How it works

| Piece | What it does |
|---|---|
| `Tracking/WindowTracker` | Keeps the Alt-Tab window list current. Window events for focus, minimize and title change; a cheap `EnumWindows` pass every 3 s catches windows opening and closing. |
| `Extraction/ExtractionService` | A single background thread. Waits ~700 ms after you focus a window or switch tabs, then reads its text and saves a screenshot. A one-time pass at startup covers windows that were already open: Edge tab lists first (a few seconds), then each window in full. The launcher shows its progress (`Reading open windows N of M…`), refreshes itself meanwhile, and holds back the tab-scan link until it's done. |
| `Extraction/UiaReader` | UI Automation reads the Edge/Chrome tab strip (`EdgeTab`) and address bar (`OmniboxViewViews`) by walking only the browser's own interface; it reaches into the active page (`RootWebArea`) only when that page's text is due, because that switches on Edge's accessibility support for the page. For other apps it reads documents, edit fields and labels. |
| `Extraction/OcrService` | Built-in Windows OCR on the screenshot when an app exposes little text. Apps with slow UI Automation are switched to OCR only and remembered in settings. |
| `Core/SearchIndex` | SQLite FTS5 (word stems plus prefixes) with highlighted snippets, in an encrypted database. |
| `Services/SecureStore` | Sets up the encryption at startup and seals preview files. Anything the current key can't read, or that isn't encrypted, is deleted and rebuilt, because the index is only a cache. |
| `Search/SearchService` | Merges live windows and tabs (fuzzy title match) with full-text hits and ranks them by relevance, then recency. |

**Limit:** Edge only exposes the page text of each window's *active* tab. A tab is searchable by its
content once you have viewed it; tabs you haven't viewed since the app started match by title only.
Text and previews persist, so a tab stays searchable after Edge puts it to sleep, and closed tabs
remain findable for `RetentionDays`. Closed tabs are marked **CLOSED** with a dimmed preview, because they're
a saved copy: Enter only opens the address again, which may show something different or ask you to sign in.

**Scan tabs** (tray menu, or the link in the launcher's status bar) closes that gap on demand.
It switches through every Edge tab that has no saved text or preview yet, reads and captures each one,
and then returns each window to the tab you had open. Minimized windows are shown briefly without taking
focus so their tabs get a preview, then minimized again (`SeedRestoreMinimized`). Up to 4 windows run at once (`SeedMaxWindows`). Within each window
the next few tabs are woken ahead of time, so sleeping tabs reload while the current one is being read,
with at most 8 tabs loading at once (`SeedMaxLoadingTabs`). Any keyboard or mouse input stops the scan
and restores your tabs. Sleeping tabs it wakes use more memory until Edge puts them back to sleep.

## Settings

Tray → **Settings…** opens a settings window. Everything is stored in `%LOCALAPPDATA%\WindowSearcher\settings.json`, which can also be edited directly (**Open settings file** in the window); changes apply as soon as the file is saved.

- `Hotkey`: for example `"Ctrl+Alt+Space"` or `"Win+Shift+F"`.
- `ExcludedProcesses`: listed by title only, never captured or read. Password managers are excluded by default.
- `ExcludedDomains`: pages on these domains (and their subdomains) are never captured or indexed. Starts with common banking, investing, payment, tax, credit, health and sign-in sites (`AppSettings.StarterExcludedDomains`). Removing one keeps it removed.
- `OcrOnlyProcesses`: apps read only through screenshots and OCR (filled in automatically for slow apps).
- `IndexPrivateWindows`: also capture and index InPrivate windows (tray → **Include InPrivate windows**).
- `ReadTabsWithoutPages`: read Edge's tabs and address bar without touching pages (default `true`; `false` = the 1.0.4 method).
- `ScreenshotOnlyDomains`: sites indexed from a screenshot + OCR instead of reading the page, for sites that react badly to Edge's accessibility support (reading a page switches it on). Empty by default.
- `SettingsVersion`: settings format; older files are updated once when loaded.
- `SeedMaxWindows`, `SeedMaxLoadingTabs`, `SeedTabTimeoutMs`, `SeedRestoreMinimized`: tab scan speed, load limits and minimized windows.
- `RetentionDays`, `OcrEnabled`, `CaptureDelayMs`, `ReindexAfterSeconds`, `RecaptureAfterSeconds`, `IndexingPaused`.

## Privacy

Everything stays on this machine in `%LOCALAPPDATA%\WindowSearcher` (index, previews, log), outside
OneDrive, and the app makes no network connections. Password fields are skipped, text from closed windows is
deleted, and closed tabs expire after `RetentionDays`. InPrivate windows are listed by title only unless you
turn on **Include InPrivate windows**. That's useful when InPrivate is a second sign-in, but it keeps a local
record of those pages. **Tray → Clear index** wipes the index and all previews.

- **Encrypted at rest:** the index (SQLite3 Multiple Ciphers) and previews (AES-GCM) use a random key that
  Windows protects for your account (`index.key`, DPAPI). The files are useless if copied to another PC or read
  by another account.
- **What encryption doesn't cover:** anything running as you while you're signed in can ask Windows for the
  key, as the app itself does. Settings, the window position and the log aren't encrypted; the log leaves out
  window titles and addresses.
- **Excluded by default:** password managers, plus the starter list of sensitive sites. A tab whose address
  can't be read isn't captured, because it could be on an excluded site. Adding a site or app to the exclusions
  also deletes what's already saved for it.
- **Upgrading from before 1.0.14:** the old unencrypted index and previews are deleted at first start and
  rebuilt. That removes the files, but doesn't guarantee the old data is gone from the disk itself.

## Install

```powershell
dotnet build installer -c Release
# -> installer\bin\Release\WindowsWindowSearch-<version>-x64.msi
```

The MSI installs per-machine to `C:\Program Files\Windows Window Search` (it asks for administrator
approval), adds a Start menu shortcut and an Add/Remove Programs entry, and closes a running copy
before upgrading or uninstalling. The app is self-contained, so no .NET install is needed.

- **New release:** bump `Version` in `Directory.Build.props` and commit, then push a matching tag (`git tag v1.0.14`, `git push origin v1.0.14`). GitHub tests it, builds the MSI and publishes it with its SHA-256 on the repo's Releases page. A tag with a suffix (`v1.0.14-rc.1`) publishes a prerelease. The new MSI upgrades in place.
- **Every push** to any branch is built and tested by GitHub (`.github/workflows`).
- **Uninstall:** use Add/Remove Programs. Your index and settings in `%LOCALAPPDATA%\WindowSearcher` are
  kept. Delete that folder to remove them.
- Releases from 1.0.16 on are on the GitHub Releases page. Older MSIs aren't published (they're 70 MB each, so `releases\` isn't in git).
- The MSI isn't code-signed, so Windows SmartScreen may warn before running it.
- The installer uses WiX 5.0.2 (MS-RL). WiX 6+ is under a maintenance-fee EULA, so don't upgrade it without checking.

## Going back to an earlier version

- **Without reinstalling:** each behavior change since 1.0.4 can be switched off in settings.
  - `"ReadTabsWithoutPages": false` restores the 1.0.4 way of reading Edge windows.
- **Installing an older release:** from 1.0.6 on, any MSI in `releases\` can be run directly to go
  back to that version. To go back to 1.0.5 or earlier, uninstall first (Add/Remove Programs), because
  those installers refuse to replace a newer version.
- **Source:** the project is in git with a tag per release (`git tag`). For example,
  `git checkout v1.0.5 -- .` restores that release's files, and rebuilding gives its installer.

## Build and test

```powershell
dotnet build WindowSearcher.slnx
dotnet test tests/WindowSearcher.Tests
src/WindowSearcher.App/bin/Debug/net10.0-windows10.0.22621.0/WindowSearcher.exe
```

Requires the .NET 10 SDK on Windows 10 19041 or later.

## README screenshots

`tools\readme-screenshots\capture.ps1` retakes `docs\screenshots` from the real app, using only the fake websites in its `pages` folder on `.example` addresses. Close the app, build Debug, run the script, and leave the PC alone for about 3 minutes.

- **Nothing real can appear:** the Debug-only `WWS_DEMO_PID` and `WWS_DEMO_DATA` variables make the app see only the demo browser and keep its data in a temporary folder. They're compiled out of Release builds, so they never ship.
- **Edge runs in Guest mode:** a normal new profile on a work PC signs in to the Windows account, which would show in the previews. Use `-CheckDir <folder>` to save a browser window at full size and check it.
- **Look at every image before committing it.**

## Measured on the development machine (about 35 windows)

- Idle CPU: not measurable over 30 s.
- Private memory: about 125 MB (219 MB working set, including shared system DLLs).
- Search: 5–36 ms.
- Opening the launcher: 144–346 ms in a freshly started process, including one-time startup compilation.
- Work per window: Edge 40–700 ms, most apps 100–600 ms, OCR 70–750 ms.
- The startup pass over all windows takes about 20 s in the background.

Per-window timings are written to `log.txt`.
