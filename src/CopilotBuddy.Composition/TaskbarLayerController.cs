using System.Runtime.InteropServices;
using System.Text;

namespace CopilotBuddy.Composition;

internal sealed class TaskbarLayerController : IDisposable
{
    private const uint ForegroundEvent = 0x0003;
    private const uint LocationChangeEvent = 0x800B;
    private const uint OutOfContext = 0;
    private const int WindowObjectId = 0;
    private const uint NoActivate = 0x0010;
    private const uint NoMove = 0x0002;
    private const uint NoOwnerZOrder = 0x0200;
    private const uint NoSize = 0x0001;
    private const int HideWindow = 0;
    private const int ShowWithoutActivation = 4;
    private static readonly nint TopMost = new(-1);
    private static readonly nint NotTopMost = new(-2);
    private readonly nint window;
    private readonly SynchronizationContext synchronizationContext;
    private readonly WinEventCallback winEventCallback;
    private readonly System.Windows.Forms.Timer recoveryTimer = new() { Interval = 1000 };
    private nint taskbar;
    private nint foregroundHook;
    private nint locationHook;
    private bool? elevated;
    private int refreshQueued;
    private bool disposed;

    internal TaskbarLayerController(nint window)
    {
        this.window = window;
        synchronizationContext = SynchronizationContext.Current
            ?? throw new InvalidOperationException("Taskbar layer control must be created on the UI thread.");
        winEventCallback = OnWinEvent;
        recoveryTimer.Tick += (_, _) => Refresh();
    }

    internal void Start()
    {
        foregroundHook = RegisterHook(ForegroundEvent);
        try
        {
            locationHook = RegisterHook(LocationChangeEvent);
        }
        catch
        {
            UnhookWinEvent(foregroundHook);
            foregroundHook = 0;
            throw;
        }
        Refresh();
        recoveryTimer.Start();
    }

    internal void Reattach()
    {
        taskbar = 0;
        Refresh();
    }

    private void Refresh()
    {
        nint currentTaskbar = TaskbarPlacement.GetPrimaryTaskbarWindow();
        bool shouldElevate = currentTaskbar != 0 && !FullscreenWindowCoversPrimaryTaskbar(currentTaskbar);
        if (elevated == shouldElevate && taskbar == currentTaskbar)
        {
            return;
        }

        if (shouldElevate)
        {
            taskbar = TaskbarPlacement.AttachToPrimaryTaskbar(window);
            ShowWindow(window, ShowWithoutActivation);
        }
        else
        {
            ShowWindow(window, HideWindow);
            TaskbarPlacement.DetachFromTaskbar(window);
            taskbar = currentTaskbar;
        }

        if (!SetWindowPos(window, shouldElevate ? TopMost : NotTopMost, 0, 0, 0, 0,
                NoActivate | NoMove | NoOwnerZOrder | NoSize))
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
        elevated = shouldElevate;
    }

    private nint RegisterHook(uint eventType)
    {
        nint hook = SetWinEventHook(eventType, eventType, 0, winEventCallback, 0, 0, OutOfContext);
        return hook != 0
            ? hook
            : throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }

    private void OnWinEvent(nint hook, uint eventType, nint eventWindow, int objectId,
        int childId, uint eventThread, uint eventTime)
    {
        if (eventType == LocationChangeEvent &&
            (objectId != WindowObjectId || eventWindow != GetForegroundWindow()))
        {
            return;
        }
        QueueRefresh();
    }

    private void QueueRefresh()
    {
        if (disposed || Interlocked.Exchange(ref refreshQueued, 1) != 0)
        {
            return;
        }
        synchronizationContext.Post(_ =>
        {
            Interlocked.Exchange(ref refreshQueued, 0);
            if (!disposed)
            {
                Refresh();
            }
        }, null);
    }

    private bool FullscreenWindowCoversPrimaryTaskbar(nint currentTaskbar)
    {
        nint foreground = GetForegroundWindow();
        if (foreground == 0 || foreground == window || foreground == currentTaskbar ||
            !IsWindowVisible(foreground) || IsIconic(foreground))
        {
            return false;
        }

        StringBuilder className = new(64);
        GetClassName(foreground, className, className.Capacity);
        if (className.ToString() is "Progman" or "WorkerW")
        {
            return false;
        }

        if (!GetWindowRect(foreground, out NativeRect foregroundBounds))
        {
            return false;
        }

        Rectangle screen = Screen.PrimaryScreen!.Bounds;
        return foregroundBounds.Left <= screen.Left &&
            foregroundBounds.Top <= screen.Top &&
            foregroundBounds.Right >= screen.Right &&
            foregroundBounds.Bottom >= screen.Bottom;
    }

    public void Dispose()
    {
        disposed = true;
        recoveryTimer.Dispose();
        if (foregroundHook != 0)
        {
            UnhookWinEvent(foregroundHook);
            foregroundHook = 0;
        }
        if (locationHook != 0)
        {
            UnhookWinEvent(locationHook);
            locationHook = 0;
        }
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint window, StringBuilder className, int maximumCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint window, out NativeRect rectangle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint window);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint window, int command);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWinEventHook(uint eventMinimum, uint eventMaximum, nint eventHookModule,
        WinEventCallback callback, uint processId, uint threadId, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(nint hook);

    private delegate void WinEventCallback(nint hook, uint eventType, nint eventWindow, int objectId,
        int childId, uint eventThread, uint eventTime);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
