param(
    [string]$Executable = "$PSScriptRoot\..\artifacts\click-through\TaskbarBuddy.Composition.exe"
)

$ErrorActionPreference = 'Stop'
$source = @'
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

public static class ClickThroughCheck
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string className, string title);
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr word, IntPtr data);
    [DllImport("user32.dll")] static extern void mouse_event(uint flags, uint horizontal, uint vertical, uint data, UIntPtr extra);
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

    public static string Run(string executable)
    {
        SetProcessDPIAware();
        Point originalCursor = Cursor.Position;
        Rectangle workArea = Screen.PrimaryScreen.WorkingArea;
        string failure = null;
        int clicks = 0;
        int phase = 0;
        Point opaquePoint = Point.Empty;
        Point outsidePoint = new Point(workArea.Left + 20, workArea.Bottom - 120);
        Process buddy = null;
        using (Form underlay = new Form())
        using (Timer timer = new Timer())
        {
            underlay.Text = "Taskbar Buddy click-through check";
            underlay.FormBorderStyle = FormBorderStyle.None;
            underlay.StartPosition = FormStartPosition.Manual;
            underlay.Bounds = new Rectangle(workArea.Left, workArea.Bottom - 160, workArea.Width, 160);
            underlay.BackColor = Color.Magenta;
            underlay.MouseDown += delegate { clicks++; };
            timer.Interval = 500;
            underlay.Shown += delegate
            {
                Cursor.Position = outsidePoint;
                buddy = Process.Start(executable);
                timer.Start();
            };
            timer.Tick += delegate
            {
                try
                {
                    buddy.Refresh();
                    if (buddy.HasExited) throw new Exception("Overlay exited before the check.");
                    IntPtr overlay = FindWindow(null, "Taskbar Buddy - Composition drag trial");
                    if (overlay == IntPtr.Zero) throw new Exception("Overlay has no window.");
                    if (phase == 0)
                    {
                        if (WindowFromPoint(outsidePoint) != underlay.Handle)
                            throw new Exception("Empty overlay area blocks the underlying window.");
                        mouse_event(0x2, 0, 0, 0, UIntPtr.Zero);
                        mouse_event(0x4, 0, 0, 0, UIntPtr.Zero);
                    }
                    else if (phase == 1)
                    {
                        if (clicks != 1) throw new Exception("Underlying window did not receive the click.");
                        using (Bitmap image = new Bitmap(underlay.Width, underlay.Height))
                        using (Graphics graphics = Graphics.FromImage(image))
                        {
                            graphics.CopyFromScreen(underlay.Location, Point.Empty, image.Size);
                            image.Save(Path.Combine(Path.GetDirectoryName(executable), "click-through.png"));
                            int background = 0;
                            int foreground = 0;
                            for (int vertical = 0; vertical < image.Height; vertical++)
                            for (int horizontal = 0; horizontal < image.Width; horizontal++)
                            {
                                Color pixel = image.GetPixel(horizontal, vertical);
                                if (pixel.ToArgb() == Color.Magenta.ToArgb()) { background++; continue; }
                                Point point = new Point(underlay.Left + horizontal, underlay.Top + vertical);
                                long packed = (uint)(point.X & 0xffff) | ((long)(uint)(point.Y & 0xffff) << 16);
                                if (SendMessage(overlay, 0x84, IntPtr.Zero, new IntPtr(packed)).ToInt64() == 1)
                                {
                                    foreground++;
                                    opaquePoint = point;
                                }
                            }
                            if (background < image.Width * image.Height * 0.9)
                                throw new Exception("Overlay background is not visually transparent.");
                            if (foreground < 20) throw new Exception("Sprite is not visibly rendered at its hit-test position.");
                        }
                        Cursor.Position = opaquePoint;
                    }
                    else if (phase == 2)
                    {
                        if (WindowFromPoint(Cursor.Position) != overlay)
                            throw new Exception("Opaque sprite does not receive input.");
                        Cursor.Position = outsidePoint;
                    }
                    else
                    {
                        if (WindowFromPoint(outsidePoint) != underlay.Handle)
                            throw new Exception("Overlay remains interactive after leaving the sprite.");
                        underlay.Close();
                    }
                    phase++;
                }
                catch (Exception exception)
                {
                    failure = exception.Message;
                    underlay.Close();
                }
            };
            try { Application.Run(underlay); }
            finally
            {
                timer.Stop();
                Cursor.Position = originalCursor;
                if (buddy != null)
                {
                    if (!buddy.HasExited)
                    {
                        SendMessage(FindWindow(null, "Taskbar Buddy - Composition drag trial"), 0x10, IntPtr.Zero, IntPtr.Zero);
                        if (!buddy.WaitForExit(3000)) buddy.Kill();
                    }
                    buddy.Dispose();
                }
            }
        }
        if (failure != null) throw new Exception(failure);
        return "PASS: outside click delivered, sprite rendered and interactive, click-through restored after leaving.";
    }
}
'@

$checkType = Add-Type -ReferencedAssemblies System.Windows.Forms,System.Drawing -PassThru -TypeDefinition (
    $source.Replace('ClickThroughCheck', 'ClickThroughCheck' + [Guid]::NewGuid().ToString('N')))
$checkType = $checkType | Where-Object { $_.IsPublic }
$checkType::Run((Resolve-Path $Executable).Path)