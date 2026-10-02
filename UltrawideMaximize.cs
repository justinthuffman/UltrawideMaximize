// Ultrawide Maximize
// When a window is maximized on an ultrawide monitor (wider than ~2.2:1), it is
// turned into a centered 16:9 window instead. Other monitors maximize normally.
//   - Double-clicking the title bar (or holding Shift) gives a true full-width maximize.
//   - Maximizing an already-centered window puts it back to its previous size.
// Build: csc /target:winexe /out:UltrawideMaximize.exe UltrawideMaximize.cs

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

static class Program
{
    [STAThread]
    static void Main()
    {
        bool created;
        using (var mutex = new Mutex(true, "UltrawideMaximize_SingleInstance", out created))
        {
            if (!created) return;
            try { Native.SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch { }
            Application.EnableVisualStyles();
            Application.Run(new TrayApp());
        }
    }
}

class TrayApp : ApplicationContext
{
    const double UltrawideRatio = 2.2;
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunName = "UltrawideMaximize";

    struct Saved { public Native.RECT Applied; public Native.RECT Original; }

    readonly NotifyIcon tray;
    readonly Control invoker;
    readonly Native.WinEventDelegate proc;
    readonly IntPtr hook;
    readonly Native.LowLevelMouseProc mouseProc;
    readonly IntPtr mouseHook;
    bool enabled = true;

    // Last left-button press, used to spot a double-click (title bar double-click maximizes).
    uint lastDownTime;
    Native.POINT lastDownPt;
    uint lastDoubleClickTime;
    bool hasDoubleClick;

    // Windows the user wants fully maximized (Shift, or already maximized at startup).
    readonly HashSet<IntPtr> leaveFull = new HashSet<IntPtr>();
    readonly HashSet<IntPtr> pending = new HashSet<IntPtr>();
    readonly Dictionary<IntPtr, Saved> saved = new Dictionary<IntPtr, Saved>();

    public TrayApp()
    {
        invoker = new Control();
        invoker.CreateControl();
        var unused = invoker.Handle;

        Native.EnumWindows(delegate(IntPtr h, IntPtr l)
        {
            if (Native.IsZoomed(h)) leaveFull.Add(h);
            return true;
        }, IntPtr.Zero);

        proc = OnWinEvent;
        hook = Native.SetWinEventHook(Native.EVENT_OBJECT_LOCATIONCHANGE, Native.EVENT_OBJECT_LOCATIONCHANGE,
            IntPtr.Zero, proc, 0, 0, Native.WINEVENT_OUTOFCONTEXT | Native.WINEVENT_SKIPOWNPROCESS);

        mouseProc = OnMouse;
        mouseHook = Native.SetWindowsHookEx(14, mouseProc, Native.GetModuleHandle(null), 0); // WH_MOUSE_LL

        var menu = new ContextMenuStrip();
        var title = new ToolStripMenuItem("Ultrawide Maximize") { Enabled = false };
        var enabledItem = new ToolStripMenuItem("Enabled") { Checked = true, CheckOnClick = true };
        enabledItem.CheckedChanged += delegate { enabled = enabledItem.Checked; UpdateIcon(); };
        var startupItem = new ToolStripMenuItem("Start with Windows") { Checked = IsStartup(), CheckOnClick = true };
        startupItem.CheckedChanged += delegate { SetStartup(startupItem.Checked); };
        var exitItem = new ToolStripMenuItem("Exit");
        exitItem.Click += delegate { Quit(); };
        menu.Items.AddRange(new ToolStripItem[] { title, new ToolStripSeparator(), enabledItem, startupItem, new ToolStripSeparator(), exitItem });

        tray = new NotifyIcon { ContextMenuStrip = menu, Visible = true };
        UpdateIcon();
    }

    void UpdateIcon()
    {
        var old = tray.Icon;
        tray.Icon = MakeIcon(enabled);
        tray.Text = enabled ? "Ultrawide Maximize (double-click title = full width)" : "Ultrawide Maximize (paused)";
        if (old != null) { Native.DestroyIcon(old.Handle); old.Dispose(); }
    }

    static Icon MakeIcon(bool on)
    {
        using (var bmp = new Bitmap(16, 16))
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            var frame = on ? Color.FromArgb(220, 220, 220) : Color.FromArgb(120, 120, 120);
            var fill = on ? Color.FromArgb(70, 160, 255) : Color.FromArgb(110, 110, 110);
            using (var pen = new Pen(frame)) g.DrawRectangle(pen, 0, 4, 15, 8);
            using (var brush = new SolidBrush(fill)) g.FillRectangle(brush, 5, 6, 6, 5);
            return Icon.FromHandle(bmp.GetHicon());
        }
    }

    IntPtr OnMouse(int nCode, IntPtr wParam, ref Native.MSLLHOOKSTRUCT info)
    {
        if (nCode >= 0 && wParam.ToInt32() == 0x0201) // WM_LBUTTONDOWN
        {
            uint gap = info.time - lastDownTime;
            int dx = Math.Abs(info.pt.X - lastDownPt.X), dy = Math.Abs(info.pt.Y - lastDownPt.Y);
            if (lastDownTime != 0 && gap <= Native.GetDoubleClickTime() &&
                dx <= Native.GetSystemMetrics(36) && dy <= Native.GetSystemMetrics(37))
            {
                lastDoubleClickTime = info.time;
                hasDoubleClick = true;
                lastDownTime = 0; // a third click starts a new pair
            }
            else
            {
                lastDownTime = info.time;
                lastDownPt = info.pt;
            }
        }
        return Native.CallNextHookEx(IntPtr.Zero, nCode, wParam, ref info);
    }

    bool JustDoubleClicked()
    {
        return hasDoubleClick && (uint)Environment.TickCount - lastDoubleClickTime < 500;
    }

    void OnWinEvent(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != 0 || idChild != 0 || hwnd == IntPtr.Zero) return;

        if (!Native.IsZoomed(hwnd))
        {
            if (!Native.IsIconic(hwnd)) leaveFull.Remove(hwnd);
            return;
        }
        if (!enabled || leaveFull.Contains(hwnd) || pending.Contains(hwnd)) return;
        if ((Native.GetAsyncKeyState(0x10) & 0x8000) != 0 || JustDoubleClicked()) { leaveFull.Add(hwnd); return; }
        if (!IsEligible(hwnd)) { leaveFull.Add(hwnd); return; }

        Native.MONITORINFO mi;
        if (!GetUltrawide(hwnd, out mi)) return;

        pending.Add(hwnd);
        IntPtr target = hwnd;
        invoker.BeginInvoke((MethodInvoker)delegate { Center(target); });
    }

    static bool IsEligible(IntPtr hwnd)
    {
        if (Native.GetAncestor(hwnd, 2) != hwnd) return false;
        long style = Native.GetWindowLongPtr(hwnd, -16).ToInt64();
        long exStyle = Native.GetWindowLongPtr(hwnd, -20).ToInt64();
        if ((style & 0x40000) == 0 || (style & 0x10000) == 0) return false; // needs resize frame + maximize box
        if ((exStyle & 0x80) != 0) return false;                              // tool windows
        return true;
    }

    static bool GetUltrawide(IntPtr hwnd, out Native.MONITORINFO mi)
    {
        mi = new Native.MONITORINFO();
        mi.cbSize = Marshal.SizeOf(typeof(Native.MONITORINFO));
        IntPtr mon = Native.MonitorFromWindow(hwnd, 2);
        if (!Native.GetMonitorInfo(mon, ref mi)) return false;
        double w = mi.rcMonitor.Right - mi.rcMonitor.Left;
        double h = mi.rcMonitor.Bottom - mi.rcMonitor.Top;
        return h > 0 && w / h >= UltrawideRatio;
    }

    void Center(IntPtr hwnd)
    {
        pending.Remove(hwnd);
        if (!Native.IsWindow(hwnd) || !Native.IsZoomed(hwnd)) return;
        Native.MONITORINFO mi;
        if (!GetUltrawide(hwnd, out mi)) return;

        var wp = new Native.WINDOWPLACEMENT();
        wp.length = Marshal.SizeOf(typeof(Native.WINDOWPLACEMENT));
        if (!Native.GetWindowPlacement(hwnd, ref wp)) return;

        // Second maximize on a window we centered: put it back how it was.
        Saved s;
        if (saved.TryGetValue(hwnd, out s) && Near(wp.rcNormalPosition, s.Applied))
        {
            saved.Remove(hwnd);
            wp.rcNormalPosition = s.Original;
            wp.showCmd = 1;
            wp.flags = 0;
            Native.SetWindowPlacement(hwnd, ref wp);
            return;
        }

        // Centered 16:9 using the full monitor height, clipped to the work area (taskbar).
        var work = mi.rcWork;
        int monH = mi.rcMonitor.Bottom - mi.rcMonitor.Top;
        int workW = work.Right - work.Left;
        int width = Math.Min(workW, (int)Math.Round(monH * 16.0 / 9.0));
        var visual = new Native.RECT();
        visual.Left = work.Left + (workW - width) / 2;
        visual.Right = visual.Left + width;
        visual.Top = work.Top;
        visual.Bottom = work.Bottom;

        // Window placement uses workspace coordinates (relative to the primary work area).
        var primary = new Native.MONITORINFO();
        primary.cbSize = Marshal.SizeOf(typeof(Native.MONITORINFO));
        Native.GetMonitorInfo(Native.MonitorFromPoint(new Native.POINT(), 1), ref primary);
        int offX = primary.rcWork.Left, offY = primary.rcWork.Top;

        var original = wp.rcNormalPosition;
        wp.rcNormalPosition = Offset(visual, -offX, -offY);
        wp.showCmd = 1;
        wp.flags = 0;
        if (!Native.SetWindowPlacement(hwnd, ref wp)) return;

        // Compensate for the invisible resize borders so the visible frame lands exactly.
        Native.RECT wr, fr;
        if (Native.GetWindowRect(hwnd, out wr) &&
            Native.DwmGetWindowAttribute(hwnd, 9, out fr, Marshal.SizeOf(typeof(Native.RECT))) == 0)
        {
            int x = visual.Left - (fr.Left - wr.Left);
            int y = visual.Top - (fr.Top - wr.Top);
            int r = visual.Right + (wr.Right - fr.Right);
            int b = visual.Bottom + (wr.Bottom - fr.Bottom);
            if (x != wr.Left || y != wr.Top || r != wr.Right || b != wr.Bottom)
                Native.SetWindowPos(hwnd, IntPtr.Zero, x, y, r - x, b - y, 0x0004 | 0x0010 | 0x0200);
        }

        Native.GetWindowPlacement(hwnd, ref wp);
        PruneSaved();
        var entry = new Saved();
        entry.Applied = wp.rcNormalPosition;
        entry.Original = original;
        saved[hwnd] = entry;
    }

    void PruneSaved()
    {
        var dead = new List<IntPtr>();
        foreach (var h in saved.Keys) if (!Native.IsWindow(h)) dead.Add(h);
        foreach (var h in dead) saved.Remove(h);
        leaveFull.RemoveWhere(delegate(IntPtr h) { return !Native.IsWindow(h); });
    }

    static bool Near(Native.RECT a, Native.RECT b)
    {
        return Math.Abs(a.Left - b.Left) <= 4 && Math.Abs(a.Top - b.Top) <= 4 &&
               Math.Abs(a.Right - b.Right) <= 4 && Math.Abs(a.Bottom - b.Bottom) <= 4;
    }

    static Native.RECT Offset(Native.RECT r, int dx, int dy)
    {
        r.Left += dx; r.Right += dx; r.Top += dy; r.Bottom += dy;
        return r;
    }

    static bool IsStartup()
    {
        using (var key = Registry.CurrentUser.OpenSubKey(RunKey))
            return key != null && key.GetValue(RunName) != null;
    }

    static void SetStartup(bool on)
    {
        using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
        {
            if (on) key.SetValue(RunName, "\"" + Application.ExecutablePath + "\"");
            else key.DeleteValue(RunName, false);
        }
    }

    void Quit()
    {
        Native.UnhookWinEvent(hook);
        Native.UnhookWindowsHookEx(mouseHook);
        tray.Visible = false;
        tray.Dispose();
        ExitThread();
    }
}

static class Native
{
    public const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
    public const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    public const uint WINEVENT_SKIPOWNPROCESS = 0x0002;

    public delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
    public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
    public delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, ref MSLLHOOKSTRUCT info);

    [StructLayout(LayoutKind.Sequential)]
    public struct MSLLHOOKSTRUCT { public POINT pt; public uint mouseData, flags, time; public UIntPtr extraInfo; }

    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

    [StructLayout(LayoutKind.Sequential)]
    public struct WINDOWPLACEMENT
    {
        public int length, flags, showCmd;
        public POINT ptMinPosition, ptMaxPosition;
        public RECT rcNormalPosition;
    }

    [DllImport("user32.dll")] public static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr hmod, WinEventDelegate proc, uint pid, uint tid, uint flags);
    [DllImport("user32.dll")] public static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] public static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] public static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
    [DllImport("user32.dll")] public static extern bool GetMonitorInfo(IntPtr mon, ref MONITORINFO mi);
    [DllImport("user32.dll")] public static extern bool GetWindowPlacement(IntPtr hwnd, ref WINDOWPLACEMENT wp);
    [DllImport("user32.dll")] public static extern bool SetWindowPlacement(IntPtr hwnd, ref WINDOWPLACEMENT wp);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll")] public static extern IntPtr SetWindowsHookEx(int id, LowLevelMouseProc proc, IntPtr hmod, uint tid);
    [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr hook, int nCode, IntPtr wParam, ref MSLLHOOKSTRUCT info);
    [DllImport("user32.dll")] public static extern uint GetDoubleClickTime();
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandle(string name);
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT rect, int size);
}
