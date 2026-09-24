using System.Windows.Input;
using System.Windows.Interop;
using WindowSearcher.App.Native;

namespace WindowSearcher.App.Services;

/// <summary>Global hotkey through RegisterHotKey on a hidden message-only window.</summary>
internal sealed class HotkeyManager : IDisposable
{
    private const int HotkeyId = 0x5753;
    private static readonly IntPtr HwndMessage = new(-3);

    private readonly HwndSource _source;
    private readonly Action _onPressed;
    private bool _registered;

    public HotkeyManager(Action onPressed)
    {
        _onPressed = onPressed;
        _source = new HwndSource(new HwndSourceParameters("WindowSearcherHotkey")
        {
            ParentWindow = HwndMessage,
            WindowStyle = 0,
        });
        _source.AddHook(WndProc);
    }

    /// <summary>Registers (or re-registers) the hotkey. Returns false if the text is invalid or the combination is taken.</summary>
    public bool Register(string text)
    {
        Unregister();
        if (!TryParse(text, out var modifiers, out var vk)) return false;
        _registered = Win32.RegisterHotKey(_source.Handle, HotkeyId, modifiers | Win32.MOD_NOREPEAT, vk);
        return _registered;
    }

    public static bool TryParse(string text, out uint modifiers, out uint vk)
    {
        modifiers = 0;
        vk = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2) return false;
        foreach (var part in parts[..^1])
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control": modifiers |= Win32.MOD_CONTROL; break;
                case "alt": modifiers |= Win32.MOD_ALT; break;
                case "shift": modifiers |= Win32.MOD_SHIFT; break;
                case "win" or "windows": modifiers |= Win32.MOD_WIN; break;
                default: return false;
            }
        }
        if (!Enum.TryParse<Key>(parts[^1], ignoreCase: true, out var key)) return false;
        vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        return vk != 0 && modifiers != 0;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Win32.WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            handled = true;
            _onPressed();
        }
        return IntPtr.Zero;
    }

    private void Unregister()
    {
        if (!_registered) return;
        Win32.UnregisterHotKey(_source.Handle, HotkeyId);
        _registered = false;
    }

    public void Dispose()
    {
        Unregister();
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }
}
