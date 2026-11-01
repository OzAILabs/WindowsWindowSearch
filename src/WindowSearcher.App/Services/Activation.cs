using System.Collections.Concurrent;
using System.Diagnostics;
using WindowSearcher.App.Extraction;
using WindowSearcher.App.Native;

namespace WindowSearcher.App.Services;

/// <summary>
/// Dedicated UI Automation thread for user-initiated actions (tab switching), so they never
/// wait behind background indexing.
/// </summary>
internal sealed class InteractiveUia : IDisposable
{
    private readonly BlockingCollection<Action<UiaReader>> _queue = new();
    private readonly Thread _thread;

    public InteractiveUia()
    {
        _thread = new Thread(() =>
        {
            using var reader = new UiaReader(TimeSpan.FromMilliseconds(1000));
            foreach (var work in _queue.GetConsumingEnumerable())
            {
                try { work(reader); }
                catch (Exception ex) { Log.Error("Interactive UIA action failed", ex); }
            }
        }) { IsBackground = true, Name = "InteractiveUia" };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    public Task<T> Run<T>(Func<UiaReader, T> func)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Add(reader =>
        {
            try { tcs.SetResult(func(reader)); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        return tcs.Task;
    }

    public void Dispose() => _queue.CompleteAdding();
}

internal static class WindowActivator
{
    /// <summary>Restores (if minimized) and focuses a window. Call while our launcher is in the foreground.</summary>
    public static bool Activate(IntPtr hwnd)
    {
        if (!Win32.IsWindow(hwnd)) return false;
        if (Win32.IsIconic(hwnd)) Win32.ShowWindow(hwnd, Win32.SW_RESTORE);
        if (Win32.SetForegroundWindow(hwnd) && Win32.GetForegroundWindow() == hwnd) return true;

        // Foreground lock fallback: attach to the current foreground thread's input queue.
        var fg = Win32.GetForegroundWindow();
        var fgThread = Win32.GetWindowThreadProcessId(fg, out _);
        var ours = Win32.GetCurrentThreadId();
        var attached = fgThread != ours && Win32.AttachThreadInput(ours, fgThread, true);
        try
        {
            Win32.BringWindowToTop(hwnd);
            Win32.ShowWindow(hwnd, Win32.IsIconic(hwnd) ? Win32.SW_RESTORE : Win32.SW_SHOW);
            return Win32.SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attached) Win32.AttachThreadInput(ours, fgThread, false);
        }
    }

    public static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error("Could not reopen a closed tab", ex);
        }
    }
}
