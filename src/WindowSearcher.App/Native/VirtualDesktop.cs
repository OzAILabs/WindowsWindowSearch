using System.Runtime.InteropServices;

namespace WindowSearcher.App.Native;

/// <summary>
/// Windows on other virtual desktops are cloaked by the shell; this lets us keep them in the
/// list while still hiding truly invisible (cloaked) windows.
/// </summary>
internal static class VirtualDesktop
{
    [ComImport]
    [Guid("a5cd92ff-29be-454c-8d04-d82879fb3f1b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IVirtualDesktopManager
    {
        [PreserveSig]
        int IsWindowOnCurrentVirtualDesktop(IntPtr topLevelWindow, [MarshalAs(UnmanagedType.Bool)] out bool onCurrentDesktop);

        [PreserveSig]
        int GetWindowDesktopId(IntPtr topLevelWindow, out Guid desktopId);

        [PreserveSig]
        int MoveWindowToDesktop(IntPtr topLevelWindow, ref Guid desktopId);
    }

    [ComImport]
    [Guid("aa509086-5ca9-4c25-8f95-589d3c07b48a")]
    private class VirtualDesktopManagerClass;

    private static readonly IVirtualDesktopManager? Manager = Create();

    private static IVirtualDesktopManager? Create()
    {
        try
        {
            return (IVirtualDesktopManager)new VirtualDesktopManagerClass();
        }
        catch (COMException)
        {
            return null;
        }
    }

    /// <summary>True when the window belongs to a virtual desktop other than the current one.</summary>
    public static bool IsOnOtherDesktop(IntPtr hwnd)
    {
        if (Manager is null) return false;
        try
        {
            if (Manager.IsWindowOnCurrentVirtualDesktop(hwnd, out var onCurrent) != 0 || onCurrent) return false;
            return Manager.GetWindowDesktopId(hwnd, out var id) == 0 && id != Guid.Empty;
        }
        catch (COMException)
        {
            return false;
        }
    }
}
