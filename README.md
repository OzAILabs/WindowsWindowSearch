# Windows Window Search

A Windows background app for finding the right window or browser tab when you have dozens open.
It searches window titles, tab titles **and the text inside** windows and Edge tabs, shows a saved
preview of each one, and switches straight to it. No browser extension is needed.

## Use

- **Ctrl+Alt+Space** opens the launcher. With no text, it lists windows and tabs, most recent first.
  Type to search, for example `token cost`.
- **↑/↓** select, **Enter** switch to it (restores the window and selects the tab), **Tab** toggles the
  thumbnail grid, **Esc** closes.
- Clicking the tray icon toggles the launcher. The tray menu has pause (the app then doesn't touch other
  apps at all), start with Windows, settings, log and clear index.
- Launching the exe again opens the launcher of the running instance. `--grid` opens the grid view and
  `--query <text>` opens it pre-filled.

## How it works

| Piece | What it does |
|---|---|
| `Tracking/WindowTracker` | Keeps the Alt-Tab window list current. Window events for focus, minimize and title change; a cheap `EnumWindows` pass every 3 s catches windows opening and closing. |
| `Extraction/ExtractionService` | A single background thread. Waits ~700 ms after you focus a window or switch tabs, then reads its text and saves a screenshot. A one-time pass at startup covers windows that were already open. |
| `Extraction/UiaReader` | UI Automation reads the Edge/Chrome tab strip (`EdgeTab`) and address bar (`OmniboxViewViews`) by walking only the browser's own interface; it reaches into the active page (`RootWebArea`) only when that page's text is due, because that switches on Edge's accessibility support for the page. For other apps it reads documents, edit fields and labels. |
| `Extraction/OcrService` | Built-in Windows OCR on the screenshot when an app exposes little text. Apps with slow UI Automation are switched to OCR only and remembered in settings. |
| `Core/SearchIndex` | SQLite FTS5 (word stems plus prefixes) with highlighted snippets. |
| `Search/SearchService` | Merges live windows and tabs (fuzzy title match) with full-text hits and ranks them by relevance, then recency. |

**Limit:** Edge only exposes the page text of each window's *active* tab. A tab is searchable by its
content once you have viewed it; tabs you haven't viewed since the app started match by title only.
Text and previews persist, so a tab stays searchable after Edge puts it to sleep, and closed tabs
remain findable (Enter reopens the URL) for `RetentionDays`.

**Scan tabs** (tray menu, or the link in the launcher's status bar) closes that gap on demand.
It switches through every Edge tab that has no saved text or preview yet, reads and captures each one,
and then returns each window to the tab you had open. Minimized windows are shown briefly without taking
focus so their tabs get a preview, then minimized again (`SeedRestoreMinimized`). Up to 4 windows run at once (`SeedMaxWindows`). Within each window
the next few tabs are woken ahead of time, so sleeping tabs reload while the current one is being read,
with at most 8 tabs loading at once (`SeedMaxLoadingTabs`). Any keyboard or mouse input stops the scan
and restores your tabs. Sleeping tabs it wakes use more memory until Edge puts them back to sleep.

## Settings

`%LOCALAPPDATA%\WindowSearcher\settings.json` (tray → Settings). Changes apply as soon as you save the file.

- `Hotkey`: for example `"Ctrl+Alt+Space"` or `"Win+Shift+F"`.
- `ExcludedProcesses`: listed by title only, never captured or read. Password managers are excluded by default.
- `ExcludedDomains`: pages on these domains (and their subdomains) are never captured or indexed.
- `OcrOnlyProcesses`: apps read only through screenshots and OCR (filled in automatically for slow apps).
- `IndexPrivateWindows`: also capture and index InPrivate windows (tray → **Include InPrivate windows**).
- `ReadTabsWithoutPages`: read Edge's tabs and address bar without touching pages (default `true`; `false` = the 1.0.4 method).
- `ScreenshotOnlyDomains`: sites indexed from a screenshot + OCR instead of reading the page. Default: `admin.example.com`, whose app list flickers at some window sizes while Edge's accessibility support is on (reading a page switches it on).
- `SeedMaxWindows`, `SeedMaxLoadingTabs`, `SeedTabTimeoutMs`, `SeedRestoreMinimized`: tab scan speed, load limits and minimized windows.
- `RetentionDays`, `OcrEnabled`, `CaptureDelayMs`, `ReindexAfterSeconds`, `RecaptureAfterSeconds`, `IndexingPaused`.

## Privacy

Everything stays on this machine in `%LOCALAPPDATA%\WindowSearcher` (index, previews, log), outside
OneDrive. Password fields are skipped, text from closed windows is deleted, and closed tabs expire after
`RetentionDays`. InPrivate windows are listed by title only unless you turn on **Include InPrivate
windows**. That's useful when InPrivate is a second sign-in, but it keeps a local record of those pages.
**Tray → Clear index** wipes the index and all previews.

## Install

```powershell
dotnet build installer -c Release
# -> installer\bin\Release\WindowsWindowSearch-<version>-x64.msi
```

The MSI installs per-machine to `C:\Program Files\Windows Window Search` (it asks for administrator
approval), adds a Start menu shortcut and an Add/Remove Programs entry, and closes a running copy
before upgrading or uninstalling. The app is self-contained, so no .NET install is needed.

- **New release:** bump `Version` in `Directory.Build.props` and rebuild; the new MSI upgrades in place.
- **Uninstall:** use Add/Remove Programs. Your index and settings in `%LOCALAPPDATA%\WindowSearcher` are
  kept. Delete that folder to remove them.
- Every released MSI is kept in `releases\` (not in git; they're 70 MB each).
- The MSI isn't code-signed, so Windows SmartScreen may warn before running it.
- The installer uses WiX 5.0.2 (MS-RL). WiX 6+ is under a maintenance-fee EULA, so don't upgrade it without checking.

## Going back to an earlier version

- **Without reinstalling:** each behavior change since 1.0.4 can be switched off in settings.
  - `"ReadTabsWithoutPages": false` restores the 1.0.4 way of reading Edge windows.
  - `"ScreenshotOnlyDomains": []` indexes admin portal by reading the page again.
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

## Measured on the development machine (about 35 windows)

- Idle CPU: not measurable over 30 s.
- Private memory: about 125 MB (219 MB working set, including shared system DLLs).
- Search: 5–36 ms.
- Opening the launcher: 144–346 ms in a freshly started process, including one-time startup compilation.
- Work per window: Edge 40–700 ms, most apps 100–600 ms, OCR 70–750 ms.
- The startup pass over all windows takes about 20 s in the background.

Per-window timings are written to `log.txt`.
