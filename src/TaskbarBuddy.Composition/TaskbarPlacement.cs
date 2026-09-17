using System.ComponentModel;
using System.Runtime.InteropServices;

namespace TaskbarBuddy.Composition;

internal static class TaskbarPlacement
{
    private const int OwnerWindowIndex = -8;

    internal static Rectangle GetPrimaryOverlayBounds()
    {
        Screen screen = Screen.PrimaryScreen!;
        AppBarData data = new() { Size = (uint)Marshal.SizeOf<AppBarData>() };
        bool autoHide = (SHAppBarMessage(4, ref data).ToInt64() & 1) != 0;
        int bottom = autoHide ? screen.Bounds.Bottom : screen.WorkingArea.Bottom;
        return new Rectangle(screen.Bounds.Left, screen.Bounds.Top, screen.Bounds.Width, Math.Max(1, bottom - screen.Bounds.Top));
    }

    internal static nint AttachToPrimaryTaskbar(nint window)
    {
        nint taskbar = FindWindow("Shell_TrayWnd", null);
        if (taskbar == 0)
        {
            return 0;
        }

        SetOwner(window, taskbar);
        return taskbar;
    }

    internal static void DetachFromTaskbar(nint window) => SetOwner(window, 0);

    internal static nint GetPrimaryTaskbarWindow() => FindWindow("Shell_TrayWnd", null);

    private static void SetOwner(nint window, nint owner)
    {
        Marshal.SetLastPInvokeError(0);
        nint previousOwner = SetOwnerValue(window, owner);
        int error = Marshal.GetLastPInvokeError();
        if (previousOwner == 0 && error != 0)
        {
            throw new Win32Exception(error);
        }
    }

    private static nint SetOwnerValue(nint window, nint owner) => IntPtr.Size == 8
        ? SetWindowLongPtr64(window, OwnerWindowIndex, owner)
        : new nint(SetWindowLong32(window, OwnerWindowIndex, owner.ToInt32()));

    [DllImport("shell32.dll")]
    private static extern nint SHAppBarMessage(uint message, ref AppBarData data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint FindWindow(string className, string? windowName);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(nint window, int index, int value);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr64(nint window, int index, nint value);

    [StructLayout(LayoutKind.Sequential)]
    private struct AppBarData
    {
        public uint Size;
        public nint Window;
        public uint CallbackMessage;
        public uint Edge;
        public NativeRect Rectangle;
        public nint Parameter;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}