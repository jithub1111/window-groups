// Window Groups - a small Windows tray app.
// Put windows into groups; minimize one and the whole group minimizes, restore one
// and they all come back (optionally maximize too).
//
// Targets .NET Framework 4.x, which is built into Windows 10 and 11, so the exe
// runs with nothing to install.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Win32;

[assembly: AssemblyTitle("Window Groups")]
[assembly: AssemblyProduct("Window Groups")]
[assembly: AssemblyVersion(WindowGroups.Updater.Version)]
[assembly: AssemblyFileVersion(WindowGroups.Updater.Version)]

namespace WindowGroups
{
    static class Native
    {
        public delegate void WinEventDelegate(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
        public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

        [DllImport("user32.dll")] public static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr hmod, WinEventDelegate proc, uint pid, uint tid, uint flags);
        [DllImport("user32.dll")] public static extern bool UnhookWinEvent(IntPtr hook);
        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
        [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr h);
        [DllImport("user32.dll")] public static extern bool ShowWindowAsync(IntPtr h, int cmd);
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder sb, int max);
        public static string ClassOf(IntPtr h) { var sb = new StringBuilder(256); GetClassName(h, sb, 256); return sb.ToString(); }
        [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr h, uint cmd);
        [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr h, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder sb, int max);
        [DllImport("user32.dll")] public static extern int GetWindowTextLength(IntPtr h);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll", EntryPoint = "GetWindowLong")] public static extern int GetWindowLong(IntPtr h, int idx);
        [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int val, int size);
        [DllImport("kernel32.dll")] public static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder sb, ref int size);
        [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);

        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; public int W { get { return Right - Left; } } public int H { get { return Bottom - Top; } } }
        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] public struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }
        public delegate bool MonitorEnumProc(IntPtr hMon, IntPtr hdc, ref RECT r, IntPtr data);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int hgt, uint flags);
        [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")] public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc cb, IntPtr data);
        [DllImport("user32.dll")] public static extern bool GetMonitorInfo(IntPtr hMon, ref MONITORINFO mi);
        [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
        [DllImport("user32.dll")] static extern IntPtr SetThreadDpiAwarenessContext(IntPtr ctx);
        [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT val, int size);
        public const uint EVENT_SYSTEM_MOVESIZESTART = 0x0A, EVENT_SYSTEM_MOVESIZEEND = 0x0B;
        public const uint SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10, SWP_ASYNCWINDOWPOS = 0x4000;

        // Work in real pixels while arranging windows (the UI itself stays DPI-scaled by Windows).
        public static IntPtr EnterDpiAware() { try { return SetThreadDpiAwarenessContext(new IntPtr(-4)); } catch { return IntPtr.Zero; } }
        public static void LeaveDpiAware(IntPtr old) { if (old != IntPtr.Zero) try { SetThreadDpiAwarenessContext(old); } catch { } }

        // The visible part of a window (without the invisible resize border Windows 10/11 adds).
        public static RECT VisibleRect(IntPtr h)
        {
            RECT r; GetWindowRect(h, out r);
            RECT f;
            try { if (DwmGetWindowAttribute(h, 9 /*EXTENDED_FRAME_BOUNDS*/, out f, 16) == 0) return f; } catch { }
            return r;
        }

        // Place a window so its visible part fills r exactly.
        public static void PlaceVisible(IntPtr h, RECT r)
        {
            RECT wr; GetWindowRect(h, out wr);
            RECT vr = VisibleRect(h);
            int l = vr.Left - wr.Left, t = vr.Top - wr.Top, rr = wr.Right - vr.Right, b = wr.Bottom - vr.Bottom;
            SetWindowPos(h, IntPtr.Zero, r.Left - l, r.Top - t, r.W + l + rr, r.H + t + b, SWP_NOZORDER | SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS);
        }

        public static List<RECT> MonitorWorkAreas()
        {
            var list = new List<RECT>();
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr m, IntPtr dc, ref RECT rc, IntPtr d) =>
            {
                var mi = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
                if (GetMonitorInfo(m, ref mi)) list.Add(mi.rcWork);
                return true;
            }, IntPtr.Zero);
            return list.OrderBy(r => r.Left).ThenBy(r => r.Top).ToList();
        }

        public static RECT WorkAreaOf(IntPtr h)
        {
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
            GetMonitorInfo(MonitorFromWindow(h, 2 /*NEAREST*/), ref mi);
            return mi.rcWork;
        }

        public const uint EVENT_SYSTEM_MINIMIZESTART = 0x16, EVENT_SYSTEM_MINIMIZEEND = 0x17, EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
        public const uint WINEVENT_OUTOFCONTEXT = 0, WINEVENT_SKIPOWNPROCESS = 2;
        public const int SW_MAXIMIZE = 3, SW_SHOWMINNOACTIVE = 7, SW_RESTORE = 9;

        public static string Title(IntPtr h)
        {
            int n = GetWindowTextLength(h);
            var sb = new StringBuilder(n + 1);
            GetWindowText(h, sb, n + 1);
            return sb.ToString();
        }

        public static uint Pid(IntPtr h) { uint pid; GetWindowThreadProcessId(h, out pid); return pid; }

        public static string ExePath(uint pid)
        {
            IntPtr p = OpenProcess(0x1000, false, pid);
            if (p == IntPtr.Zero) return "";
            try
            {
                var sb = new StringBuilder(1024); int size = sb.Capacity;
                return QueryFullProcessImageName(p, 0, sb, ref size) ? sb.ToString() : "";
            }
            finally { CloseHandle(p); }
        }

        public static string State(IntPtr h) { return IsIconic(h) ? "min" : IsZoomed(h) ? "max" : "normal"; }

        static readonly uint OwnPid = (uint)Process.GetCurrentProcess().Id;

        // A window that would appear on the taskbar.
        public static bool IsAppWindow(IntPtr h)
        {
            if (!IsWindowVisible(h) || GetWindow(h, 4 /*GW_OWNER*/) != IntPtr.Zero) return false;
            int ex = GetWindowLong(h, -20);
            if ((ex & 0x80) != 0 || (ex & 0x08000000) != 0) return false; // toolwindow / noactivate
            if (GetWindowTextLength(h) == 0 || Pid(h) == OwnPid) return false;
            int cloaked;
            try { if (DwmGetWindowAttribute(h, 14, out cloaked, 4) == 0 && cloaked != 0) return false; } catch { }
            return true;
        }

        public static List<IntPtr> AppWindows()
        {
            var list = new List<IntPtr>();
            EnumWindows((h, l) => { if (IsAppWindow(h)) list.Add(h); return true; }, IntPtr.Zero);
            return list;
        }
    }

    class Member
    {
        public string ExePath = "", Title = "", ClassName = "";
        public IntPtr Hwnd = IntPtr.Zero;
        public int L, T, R, B;  // last normal position on screen (real pixels); R == L means unknown
        public bool HasRect { get { return R > L && B > T; } }
        public string ExeName { get { return ExePath.Length > 0 ? Path.GetFileName(ExePath) : "?"; } }
        public bool Open { get { return Hwnd != IntPtr.Zero; } }
    }

    class Group
    {
        public string Name;
        public List<Member> Members = new List<Member>();
        public bool Grid;            // tile this group's windows into a grid
        public int Columns;          // 0 = automatic
        public bool SpanLast;        // short last row: stretch its last window into the empty space
        public int BoundsX, BoundsY, BoundsW, BoundsH; // the grid's own rectangle (0 size = not set yet)
        public double[] ColEdges, RowEdges; // grid lines as 0..1 fractions (null = even)
    }

    // ------------------------------------------------------------------
    // Groups, saving, and the sync logic
    // ------------------------------------------------------------------
    class Engine
    {
        public List<Group> Groups = new List<Group>();
        public bool Enabled = true;
        public bool SyncMaximize = true;
        public event Action<string> Synced;

        readonly Dictionary<IntPtr, string> lastState = new Dictionary<IntPtr, string>();
        readonly Dictionary<IntPtr, int> suppressUntil = new Dictionary<IntPtr, int>();
        readonly Native.WinEventDelegate proc;
        IntPtr hook1, hook2, hook3;
        static readonly string ConfigPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WindowGroups", "groups.txt");

        public Engine() { proc = OnWinEvent; }

        public void Start()
        {
            uint f = Native.WINEVENT_OUTOFCONTEXT | Native.WINEVENT_SKIPOWNPROCESS;
            hook1 = Native.SetWinEventHook(Native.EVENT_SYSTEM_MINIMIZESTART, Native.EVENT_SYSTEM_MINIMIZEEND, IntPtr.Zero, proc, 0, 0, f);
            hook3 = Native.SetWinEventHook(Native.EVENT_SYSTEM_MOVESIZESTART, Native.EVENT_SYSTEM_MOVESIZEEND, IntPtr.Zero, proc, 0, 0, f);
            hook2 = Native.SetWinEventHook(Native.EVENT_OBJECT_LOCATIONCHANGE, Native.EVENT_OBJECT_LOCATIONCHANGE, IntPtr.Zero, proc, 0, 0, f);
        }

        public void Stop()
        {
            if (hook1 != IntPtr.Zero) Native.UnhookWinEvent(hook1);
            if (hook2 != IntPtr.Zero) Native.UnhookWinEvent(hook2);
            if (hook3 != IntPtr.Zero) Native.UnhookWinEvent(hook3);
        }

        public Group GroupOf(IntPtr h)
        {
            foreach (var g in Groups) foreach (var m in g.Members) if (m.Hwnd == h) return g;
            return null;
        }

        public string NextName()
        {
            for (char c = 'A'; c <= 'Z'; c++) if (!Groups.Any(g => g.Name == c.ToString())) return c.ToString();
            int i = 1; while (Groups.Any(g => g.Name == "Group " + i)) i++;
            return "Group " + i;
        }

        public void AddWindow(Group g, IntPtr h)
        {
            RemoveWindow(h);
            var m = new Member { Hwnd = h, Title = Native.Title(h), ExePath = Native.ExePath(Native.Pid(h)), ClassName = Native.ClassOf(h) };
            RememberRect(m);
            g.Members.Add(m);
            lastState[h] = Native.State(h);
            Save();
        }

        public void RemoveWindow(IntPtr h)
        {
            foreach (var g in Groups) g.Members.RemoveAll(m => m.Hwnd == h);
        }

        // Remember where a window sits (only while it's a normal window, not minimized/maximized).
        bool RememberRect(Member m)
        {
            if (!m.Open || Native.IsIconic(m.Hwnd) || Native.IsZoomed(m.Hwnd)) return false;
            IntPtr ctx = Native.EnterDpiAware();
            try
            {
                var r = Native.VisibleRect(m.Hwnd);
                if (r.W <= 0 || r.H <= 0 || (r.Left == m.L && r.Top == m.T && r.Right == m.R && r.Bottom == m.B)) return false;
                m.L = r.Left; m.T = r.Top; m.R = r.Right; m.B = r.Bottom;
                return true;
            }
            finally { Native.LeaveDpiAware(ctx); }
        }

        // Put a re-found window back where it was last time (if that spot is still on a screen).
        void PutBack(Member m)
        {
            if (!m.HasRect || Native.IsIconic(m.Hwnd) || Native.IsZoomed(m.Hwnd)) return;
            IntPtr ctx = Native.EnterDpiAware();
            try
            {
                int cx = (m.L + m.R) / 2, cy = (m.T + m.B) / 2;
                if (!Native.MonitorWorkAreas().Any(w => cx >= w.Left && cx < w.Right && cy >= w.Top && cy < w.Bottom)) return;
                suppressUntil[m.Hwnd] = Environment.TickCount + 800;
                Native.PlaceVisible(m.Hwnd, new Native.RECT { Left = m.L, Top = m.T, Right = m.R, Bottom = m.B });
            }
            finally { Native.LeaveDpiAware(ctx); }
        }

        static HashSet<string> Words(string t)
        {
            return new HashSet<string>((t ?? "").ToLowerInvariant().Split(new[] { ' ', '-', '–', '—', '|', '·', ':', '(', ')' }, StringSplitOptions.RemoveEmptyEntries));
        }

        // How well an open window matches a remembered one (higher is better, < 0 = no).
        static int MatchScore(Member m, string title, string cls, bool onlyCandidate)
        {
            int score = 0;
            if (title == m.Title) score += 100;
            if (m.ClassName.Length > 0 && cls == m.ClassName) score += 20;
            // ignore the shared ending (usually the app name, e.g. " - Google Chrome")
            string t1 = m.Title ?? "", t2 = title ?? "";
            int k = 0;
            while (k < t1.Length && k < t2.Length && t1[t1.Length - 1 - k] == t2[t2.Length - 1 - k]) k++;
            if (k < t1.Length && k < t2.Length) { t1 = t1.Substring(0, t1.Length - k); t2 = t2.Substring(0, t2.Length - k); }
            var a = Words(t1); var b = Words(t2);
            if (a.Count > 0 && b.Count > 0) score += 50 * a.Count(w => b.Contains(w)) / Math.Max(a.Count, b.Count);
            if (score >= 25 || onlyCandidate) return score;
            return -1;
        }

        public bool PositionsChanged;
        readonly Dictionary<IntPtr, int> firstSeen = new Dictionary<IntPtr, int>();

        class Cand { public IntPtr Hwnd; public string Title, Exe, Class; public Native.RECT Rect; public bool Settled; }

        // Apps usually reopen windows where they were, so a window in the remembered spot is a strong hint.
        static int PositionScore(Member m, Native.RECT r)
        {
            if (!m.HasRect || r.W <= 0) return 0;
            int d = Math.Abs(r.Left - m.L) + Math.Abs(r.Top - m.T) + Math.Abs(r.Right - m.R) + Math.Abs(r.Bottom - m.B);
            if (d <= 12) return 80;
            return d < 300 ? 60 * (300 - d) / 300 : 0;
        }

        // Point a remembered entry at a specific window (used when the user re-links by hand).
        public void Relink(Group g, Member m, IntPtr h)
        {
            foreach (var gg in Groups) foreach (var mm in gg.Members) if (mm != m && mm.Hwnd == h) mm.Hwnd = IntPtr.Zero;
            m.Hwnd = h; m.Title = Native.Title(h); m.ClassName = Native.ClassOf(h);
            var exe = Native.ExePath(Native.Pid(h)); if (exe.Length > 0) m.ExePath = exe;
            RememberRect(m);
            lastState[h] = Native.State(h);
            Save();
        }

        // Drop closed windows; attach saved entries to matching open windows
        // (after a restart the windows are new, so they're found again by program, title and window type).
        public List<Group> Rebind()
        {
            var changed = new HashSet<Group>();
            foreach (var g in Groups)
                foreach (var m in g.Members)
                {
                    if (m.Open && !Native.IsWindow(m.Hwnd)) { m.Hwnd = IntPtr.Zero; changed.Add(g); }
                    else if (m.Open)
                    {
                        var t = Native.Title(m.Hwnd); if (t.Length > 0) m.Title = t;
                        if (RememberRect(m)) PositionsChanged = true;
                    }
                }
            var missing = Groups.SelectMany(g => g.Members.Where(x => !x.Open).Select(x => Tuple.Create(g, x))).ToList();
            if (missing.Count > 0)
            {
                var taken = new HashSet<IntPtr>(Groups.SelectMany(g => g.Members).Where(m => m.Open).Select(m => m.Hwnd));
                int nowTick = Environment.TickCount;
                var wins = new List<Cand>();
                IntPtr ctx = Native.EnterDpiAware();
                try
                {
                    foreach (var h in Native.AppWindows())
                    {
                        if (taken.Contains(h)) continue;
                        int seen;
                        if (!firstSeen.TryGetValue(h, out seen)) { seen = nowTick; firstSeen[h] = seen; }
                        wins.Add(new Cand
                        {
                            Hwnd = h, Title = Native.Title(h), Exe = Path.GetFileName(Native.ExePath(Native.Pid(h))), Class = Native.ClassOf(h),
                            Rect = Native.VisibleRect(h), Settled = nowTick - seen > 4000
                        });
                    }
                }
                finally { Native.LeaveDpiAware(ctx); }
                if (firstSeen.Count > 500) { var alive = new HashSet<IntPtr>(wins.Select(w => w.Hwnd)); foreach (var k in firstSeen.Keys.Where(k => !alive.Contains(k)).ToList()) firstSeen.Remove(k); }

                // Score every (remembered entry, open window) pair of the same program, then hand out best first.
                var pairs = new List<Tuple<int, Tuple<Group, Member>, Cand>>();
                foreach (var gm in missing)
                {
                    var m = gm.Item2;
                    var same = wins.Where(w => string.Equals(w.Exe, m.ExeName, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (same.Count == 0) continue;
                    // Windows of the same program and type, compared with how many entries of that program are waiting.
                    int waiting = missing.Count(x => string.Equals(x.Item2.ExeName, m.ExeName, StringComparison.OrdinalIgnoreCase));
                    foreach (var w in same)
                    {
                        bool sameType = m.ClassName.Length == 0 || w.Class == m.ClassName;
                        int score = MatchScore(m, w.Title, w.Class, false);
                        int pos = PositionScore(m, w.Rect);
                        if (score < 0) score = 0;
                        score += pos;
                        int sameTypeCount = same.Count(x => m.ClassName.Length == 0 || x.Class == m.ClassName);
                        bool strong = score >= 45;                                    // title and/or position clearly agree
                        bool onlyChoices = sameType && w.Settled && sameTypeCount <= waiting; // every such window is one of ours
                        if (strong || onlyChoices) pairs.Add(Tuple.Create(score + (sameType ? 20 : 0), gm, w));
                    }
                }
                var used = new HashSet<Member>();
                foreach (var p in pairs.OrderByDescending(x => x.Item1))
                {
                    var g = p.Item2.Item1; var m = p.Item2.Item2; var w = p.Item3;
                    if (used.Contains(m) || taken.Contains(w.Hwnd)) continue;
                    used.Add(m); taken.Add(w.Hwnd);
                    m.Hwnd = w.Hwnd;
                    if (m.ClassName.Length == 0) m.ClassName = w.Class;
                    lastState[w.Hwnd] = Native.State(w.Hwnd);
                    changed.Add(g);
                    if (!g.Grid) PutBack(m);
                }
            }
            return changed.Where(g => g.Grid).ToList();
        }

        void OnWinEvent(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
        {
            if (idObject != 0 || idChild != 0 || hwnd == IntPtr.Zero) return;
            try
            {
                if (ev == Native.EVENT_SYSTEM_MOVESIZESTART) GridMoveSizeStart(hwnd);
                else if (ev == Native.EVENT_SYSTEM_MOVESIZEEND) GridMoveSizeEnd(hwnd);
                else
                {
                    if (ev == Native.EVENT_OBJECT_LOCATIONCHANGE && hwnd == dragHwnd) GridDragging(hwnd);
                    Handle(hwnd);
                }
            }
            catch { }
        }

        void Handle(IntPtr h)
        {
            var g = GroupOf(h);
            if (g == null) return;
            string now = Native.State(h), before;
            lastState.TryGetValue(h, out before);
            lastState[h] = now;
            if (before == null || before == now || !Enabled) return;
            int until;
            if (suppressUntil.TryGetValue(h, out until) && Environment.TickCount - until < 0) return; // our own echo

            var others = g.Members.Where(m => m.Open && m.Hwnd != h && Native.IsWindow(m.Hwnd)).Select(m => m.Hwnd).ToList();
            if (others.Count == 0) return;
            if (now == "min") { Apply(others, "min", IntPtr.Zero); Notify(g, "minimized"); }
            else if (before == "min") { Apply(others, "restore", h); Notify(g, "restored"); if (g.Grid) Later(400, () => Tile(g)); }
            else if (g.Grid) { if (before == "max" && now == "normal") Later(200, () => Tile(g)); } // grids: maximize just zooms that one window
            else if (now == "max" && SyncMaximize) { Apply(others, "max", h); Notify(g, "maximized"); }
            else if (before == "max" && now == "normal" && SyncMaximize) { Apply(others, "unmax", h); Notify(g, "un-maximized"); }
        }

        void Notify(Group g, string what) { if (Synced != null) Synced("Group " + g.Name + " " + what + " together"); }

        public void Apply(IEnumerable<IntPtr> hwnds, string action, IntPtr refocus)
        {
            bool moved = false;
            foreach (var h in hwnds)
            {
                string st = Native.State(h); int cmd;
                if (action == "min" && st != "min") cmd = Native.SW_SHOWMINNOACTIVE;
                else if (action == "restore" && st == "min") cmd = Native.SW_RESTORE;
                else if (action == "max" && st != "max") cmd = Native.SW_MAXIMIZE;
                else if (action == "unmax" && st == "max") cmd = Native.SW_RESTORE;
                else continue;
                suppressUntil[h] = Environment.TickCount + 1000;
                Native.ShowWindowAsync(h, cmd); // async so a frozen app can't freeze us
                moved = true;
            }
            if (moved && refocus != IntPtr.Zero)
            {
                var t = new System.Windows.Forms.Timer { Interval = 150 };
                t.Tick += (s, e) => { t.Stop(); t.Dispose(); Native.SetForegroundWindow(refocus); };
                t.Start();
            }
        }

        public static void Later(int ms, Action a)
        {
            var t = new System.Windows.Forms.Timer { Interval = ms };
            t.Tick += (s, e) => { t.Stop(); t.Dispose(); try { a(); } catch { } };
            t.Start();
        }

        // ---- grids ----
        // A grid lives inside its own rectangle (g.Bounds*), which the user moves by dragging
        // any window in it and resizes by dragging the grid's outer edges.
        Native.RECT dragStart;
        IntPtr dragHwnd = IntPtr.Zero;
        Dictionary<IntPtr, Native.RECT> dragOthers = new Dictionary<IntPtr, Native.RECT>();
        const int MinCellPx = 120;

        // Windows that take part in the grid right now, in grid order.
        List<Member> GridMembers(Group g)
        {
            return g.Members.Where(m => m.Open && Native.IsWindow(m.Hwnd) && !Native.IsIconic(m.Hwnd)).ToList();
        }

        public static void Dims(Group g, int n, out int cols, out int rows)
        {
            cols = g.Columns > 0 ? Math.Min(g.Columns, Math.Max(1, n)) : (int)Math.Ceiling(Math.Sqrt(Math.Max(1, n)));
            rows = Math.Max(1, (n + cols - 1) / cols);
        }

        static double[] Even(int n) { var e = new double[n + 1]; for (int i = 0; i <= n; i++) e[i] = (double)i / n; return e; }

        static Native.RECT Union(IEnumerable<Native.RECT> rs)
        {
            var u = new Native.RECT { Left = int.MaxValue, Top = int.MaxValue, Right = int.MinValue, Bottom = int.MinValue };
            foreach (var r in rs) { u.Left = Math.Min(u.Left, r.Left); u.Top = Math.Min(u.Top, r.Top); u.Right = Math.Max(u.Right, r.Right); u.Bottom = Math.Max(u.Bottom, r.Bottom); }
            return u;
        }

        // Keep the grid on a screen and not smaller than its cells allow.
        static Native.RECT Sanitize(Native.RECT b, int cols, int rows, Native.RECT work)
        {
            int minW = cols * MinCellPx, minH = rows * (MinCellPx * 3 / 4);
            if (b.W < minW) b.Right = b.Left + minW;
            if (b.H < minH) b.Bottom = b.Top + minH;
            if (b.W > work.W) { b.Left = work.Left; b.Right = work.Right; }
            if (b.H > work.H) { b.Top = work.Top; b.Bottom = work.Bottom; }
            if (b.Left < work.Left) { b.Right += work.Left - b.Left; b.Left = work.Left; }
            if (b.Top < work.Top) { b.Bottom += work.Top - b.Top; b.Top = work.Top; }
            if (b.Right > work.Right) { b.Left -= b.Right - work.Right; b.Right = work.Right; }
            if (b.Bottom > work.Bottom) { b.Top -= b.Bottom - work.Bottom; b.Bottom = work.Bottom; }
            return b;
        }

        Native.RECT AreaFor(Group g, List<Member> ms)
        {
            int cols, rows; Dims(g, ms.Count, out cols, out rows);
            Native.RECT b;
            if (g.BoundsW > 0 && g.BoundsH > 0)
                b = new Native.RECT { Left = g.BoundsX, Top = g.BoundsY, Right = g.BoundsX + g.BoundsW, Bottom = g.BoundsY + g.BoundsH };
            else
                b = Union(ms.Select(m => Native.VisibleRect(m.Hwnd))); // start from where the windows already are
            // keep it on the screen its centre is on
            var mons = Native.MonitorWorkAreas();
            int cx = (b.Left + b.Right) / 2, cy = (b.Top + b.Bottom) / 2;
            var work = mons.FirstOrDefault(w => cx >= w.Left && cx < w.Right && cy >= w.Top && cy < w.Bottom);
            if (work.W == 0) work = Native.WorkAreaOf(ms[0].Hwnd);
            b = Sanitize(b, cols, rows, work);
            SetBounds(g, b);
            return b;
        }

        static void SetBounds(Group g, Native.RECT b) { g.BoundsX = b.Left; g.BoundsY = b.Top; g.BoundsW = b.W; g.BoundsH = b.H; }

        List<Native.RECT> Cells(Group g, int n, Native.RECT a)
        {
            int cols, rows; Dims(g, n, out cols, out rows);
            if (g.ColEdges == null || g.ColEdges.Length != cols + 1) g.ColEdges = Even(cols);
            if (g.RowEdges == null || g.RowEdges.Length != rows + 1) g.RowEdges = Even(rows);
            int lastRow = n - (rows - 1) * cols;
            var cells = new List<Native.RECT>();
            for (int i = 0; i < n; i++)
            {
                int r = i / cols, c = i % cols;
                double x0, x1;
                x0 = g.ColEdges[c]; x1 = g.ColEdges[c + 1];
                // short last row keeps normal cell sizes (leaving a gap) unless its last window was stretched
                if (r == rows - 1 && lastRow < cols && c == lastRow - 1 && g.SpanLast) x1 = 1.0;
                cells.Add(new Native.RECT
                {
                    Left = a.Left + (int)Math.Round(x0 * a.W), Right = a.Left + (int)Math.Round(x1 * a.W),
                    Top = a.Top + (int)Math.Round(g.RowEdges[r] * a.H), Bottom = a.Top + (int)Math.Round(g.RowEdges[r + 1] * a.H)
                });
            }
            return cells;
        }

        public void Tile(Group g) { Tile(g, true); }

        void Tile(Group g, bool retry)
        {
            if (!g.Grid) return;
            var ms = GridMembers(g);
            if (ms.Count == 0) return;
            IntPtr ctx = Native.EnterDpiAware();
            bool wasZoomed = false;
            try
            {
                var cells = Cells(g, ms.Count, AreaFor(g, ms));
                for (int i = 0; i < ms.Count; i++)
                {
                    var h = ms[i].Hwnd;
                    suppressUntil[h] = Environment.TickCount + 800;
                    if (Native.IsZoomed(h)) { Native.ShowWindowAsync(h, Native.SW_RESTORE); lastState[h] = "normal"; wasZoomed = true; }
                    Native.PlaceVisible(h, cells[i]);
                }
            }
            finally { Native.LeaveDpiAware(ctx); }
            if (wasZoomed && retry) Later(250, () => Tile(g, false)); // place again once un-maximize finishes
        }

        // Even cell sizes; keeps the grid where it is.
        public void ResetGrid(Group g) { g.ColEdges = null; g.RowEdges = null; g.SpanLast = false; }

        // Forget the grid's rectangle so it is rebuilt around the windows' current spots.
        public void ResetBounds(Group g) { g.BoundsW = g.BoundsH = 0; }

        // Make the grid fill the screen it's on.
        public void FitToScreen(Group g)
        {
            var ms = GridMembers(g);
            if (ms.Count == 0) return;
            IntPtr ctx = Native.EnterDpiAware();
            try { SetBounds(g, Native.WorkAreaOf(ms[0].Hwnd)); } finally { Native.LeaveDpiAware(ctx); }
            Tile(g);
        }

        void GridMoveSizeStart(IntPtr h)
        {
            var g = GroupOf(h);
            if (g == null || !g.Grid || !Enabled) { dragHwnd = IntPtr.Zero; return; }
            IntPtr ctx = Native.EnterDpiAware();
            try
            {
                dragHwnd = h;
                dragStart = Native.VisibleRect(h);
                dragOthers.Clear();
                foreach (var m in GridMembers(g))
                    if (m.Hwnd != h) { Native.RECT r; Native.GetWindowRect(m.Hwnd, out r); dragOthers[m.Hwnd] = r; }
            }
            finally { Native.LeaveDpiAware(ctx); }
        }

        // While a grid window is being dragged, carry the rest of the grid along.
        void GridDragging(IntPtr h)
        {
            if (h != dragHwnd || IsShiftDown()) return;
            IntPtr ctx = Native.EnterDpiAware();
            try
            {
                var now = Native.VisibleRect(h);
                if (Math.Abs(now.W - dragStart.W) > 2 || Math.Abs(now.H - dragStart.H) > 2) return; // resizing, not moving
                int dx = now.Left - dragStart.Left, dy = now.Top - dragStart.Top;
                foreach (var kv in dragOthers)
                {
                    suppressUntil[kv.Key] = Environment.TickCount + 800;
                    Native.SetWindowPos(kv.Key, IntPtr.Zero, kv.Value.Left + dx, kv.Value.Top + dy, 0, 0,
                        0x1 /*NOSIZE*/ | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_ASYNCWINDOWPOS);
                }
            }
            finally { Native.LeaveDpiAware(ctx); }
        }

        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
        static bool IsShiftDown() { return (GetAsyncKeyState(0x10) & 0x8000) != 0; }

        // The user finished dragging or resizing a grid window.
        void GridMoveSizeEnd(IntPtr h)
        {
            if (h != dragHwnd) return;
            dragHwnd = IntPtr.Zero;
            var g = GroupOf(h);
            if (g == null || !g.Grid || !Enabled) return;
            var ms = GridMembers(g);
            int i = ms.FindIndex(m => m.Hwnd == h);
            if (i < 0) return;
            IntPtr ctx = Native.EnterDpiAware();
            try
            {
                var area = AreaFor(g, ms);
                var cells = Cells(g, ms.Count, area);
                var now = Native.VisibleRect(h);
                bool sameSize = Math.Abs(now.W - dragStart.W) < 12 && Math.Abs(now.H - dragStart.H) < 12;
                if (sameSize && IsShiftDown())
                {
                    // Shift + drop on another window: swap places.
                    Native.POINT p; Native.GetCursorPos(out p);
                    int j = cells.FindIndex(c => p.X >= c.Left && p.X < c.Right && p.Y >= c.Top && p.Y < c.Bottom);
                    if (j >= 0 && j != i)
                    {
                        int a = g.Members.IndexOf(ms[i]), b = g.Members.IndexOf(ms[j]);
                        var tmp = g.Members[a]; g.Members[a] = g.Members[b]; g.Members[b] = tmp;
                        Notify(g, "swapped");
                    }
                }
                else if (sameSize)
                {
                    // Moved: the whole grid moves by the same amount.
                    int dx = now.Left - dragStart.Left, dy = now.Top - dragStart.Top;
                    g.BoundsX = area.Left + dx; g.BoundsY = area.Top + dy;
                }
                else
                {
                    int cols, rows; Dims(g, ms.Count, out cols, out rows);
                    int r = i / cols, c = i % cols, lastRow = ms.Count - (rows - 1) * cols;
                    bool shortLast = r == rows - 1 && lastRow < cols && c == lastRow - 1; // last window, gap to its right
                    var cell = cells[i];
                    // absolute grid lines in pixels
                    double[] xs = g.ColEdges.Select(e => area.Left + e * area.W).ToArray();
                    double[] ys = g.RowEdges.Select(e => area.Top + e * area.H).ToArray();
                    var b = area;
                    bool movedL = Math.Abs(now.Left - cell.Left) > 4, movedR = Math.Abs(now.Right - cell.Right) > 4;
                    bool movedT = Math.Abs(now.Top - cell.Top) > 4, movedB = Math.Abs(now.Bottom - cell.Bottom) > 4;
                    int minH = MinCellPx * 3 / 4;
                    // outer edges resize the whole grid, inner edges move the shared line
                    if (movedL) { if (c == 0) b.Left = Math.Min(now.Left, (int)xs[1] - MinCellPx); else xs[c] = Clamp(now.Left, xs[c - 1] + MinCellPx, xs[c + 1] - MinCellPx); }
                    if (movedR)
                    {
                        if (shortLast)
                        {
                            // stretch into the gap (past the middle of the next empty cell) or shrink back
                            if (g.SpanLast && now.Right > xs[cols] + 4) b.Right = now.Right;
                            else g.SpanLast = now.Right > (xs[c + 1] + xs[c + 2]) / 2;
                        }
                        else if (c == cols - 1) b.Right = Math.Max(now.Right, (int)xs[cols - 1] + MinCellPx);
                        else xs[c + 1] = Clamp(now.Right, xs[c] + MinCellPx, xs[c + 2] - MinCellPx);
                    }
                    if (movedT) { if (r == 0) b.Top = Math.Min(now.Top, (int)ys[1] - minH); else ys[r] = Clamp(now.Top, ys[r - 1] + minH, ys[r + 1] - minH); }
                    if (movedB) { if (r == rows - 1) b.Bottom = Math.Max(now.Bottom, (int)ys[rows - 1] + minH); else ys[r + 1] = Clamp(now.Bottom, ys[r] + minH, ys[r + 2] - minH); }
                    xs[0] = b.Left; xs[cols] = b.Right; ys[0] = b.Top; ys[rows] = b.Bottom;
                    // back to fractions of the (possibly new) grid rectangle
                    for (int k = 0; k <= cols; k++) g.ColEdges[k] = (xs[k] - b.Left) / b.W;
                    for (int k = 0; k <= rows; k++) g.RowEdges[k] = (ys[k] - b.Top) / b.H;
                    SetBounds(g, b);
                }
            }
            finally { Native.LeaveDpiAware(ctx); }
            Tile(g);
            Save();
            if (GridChanged != null) GridChanged();
        }

        public event Action GridChanged;

        static double Clamp(double v, double lo, double hi) { return lo > hi ? (lo + hi) / 2 : Math.Max(lo, Math.Min(hi, v)); }

        static string Edges(double[] e) { return e == null ? "" : string.Join(",", e.Select(x => x.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture))); }
        static double[] ParseEdges(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            try { return s.Split(',').Select(x => double.Parse(x, System.Globalization.CultureInfo.InvariantCulture)).ToArray(); } catch { return null; }
        }

        // ---- saving: simple tab-separated lines ----
        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath));
                var sb = new StringBuilder();
                sb.AppendLine("option\tsyncmax\t" + (SyncMaximize ? "1" : "0"));
                sb.AppendLine("option\treopen\t" + (ReopenApps ? "1" : "0"));
                sb.AppendLine("option\tautoupdate\t" + (AutoUpdate ? "1" : "0"));
                sb.AppendLine("option\tlastcheck\t" + Clean(LastUpdateCheck));
                foreach (var g in Groups)
                {
                    sb.AppendLine("group\t" + Clean(g.Name) + "\t" + (g.Grid ? "1" : "0") + "\t" + g.Columns + "\t-1\t" + Edges(g.ColEdges) + "\t" + Edges(g.RowEdges) + "\t" + g.BoundsX + "\t" + g.BoundsY + "\t" + g.BoundsW + "\t" + g.BoundsH + "\t" + (g.SpanLast ? "1" : "0"));
                    foreach (var m in g.Members)
                        sb.AppendLine("win\t" + Clean(m.ExePath) + "\t" + Clean(m.Title) + "\t" + Clean(m.ClassName) + "\t" + m.L + "\t" + m.T + "\t" + m.R + "\t" + m.B);
                }
                File.WriteAllText(ConfigPath, sb.ToString(), Encoding.UTF8);
            }
            catch { }
        }

        static string Clean(string s) { return (s ?? "").Replace("\t", " ").Replace("\r", " ").Replace("\n", " "); }

        public bool ReopenApps = true;
        public bool AutoUpdate = true;
        public string LastUpdateCheck = "";
        public bool FirstRun;

        // Start programs from the groups that aren't running (e.g. after the PC was restarted).
        public int LaunchMissing()
        {
            int started = 0;
            var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ApplicationFrameHost.exe", "explorer.exe", "?" };
            var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in Groups.SelectMany(g => g.Members).Where(x => !x.Open))
            {
                if (skip.Contains(m.ExeName) || !done.Add(m.ExePath) || !File.Exists(m.ExePath)) continue;
                try
                {
                    if (Process.GetProcessesByName(Path.GetFileNameWithoutExtension(m.ExePath)).Length > 0) continue; // already running
                    Process.Start(new ProcessStartInfo(m.ExePath) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(m.ExePath) });
                    started++;
                }
                catch { }
            }
            return started;
        }

        public void Load()
        {
            try
            {
                if (!File.Exists(ConfigPath)) { FirstRun = true; return; }
                Group cur = null;
                foreach (var line in File.ReadAllLines(ConfigPath, Encoding.UTF8))
                {
                    var p = line.Split('\t');
                    if (p[0] == "option" && p.Length > 2 && p[1] == "syncmax") SyncMaximize = p[2] == "1";
                    else if (p[0] == "option" && p.Length > 2 && p[1] == "reopen") ReopenApps = p[2] == "1";
                    else if (p[0] == "option" && p.Length > 2 && p[1] == "autoupdate") AutoUpdate = p[2] == "1";
                    else if (p[0] == "option" && p.Length > 2 && p[1] == "lastcheck") LastUpdateCheck = p[2];
                    else if (p[0] == "group" && p.Length > 1)
                    {
                        cur = new Group { Name = p[1] }; Groups.Add(cur);
                        int n;
                        if (p.Length > 2) cur.Grid = p[2] == "1";
                        if (p.Length > 3 && int.TryParse(p[3], out n)) cur.Columns = n;
                        if (p.Length > 5) cur.ColEdges = ParseEdges(p[5]);
                        if (p.Length > 6) cur.RowEdges = ParseEdges(p[6]);
                        if (p.Length > 10) { int.TryParse(p[7], out cur.BoundsX); int.TryParse(p[8], out cur.BoundsY); int.TryParse(p[9], out cur.BoundsW); int.TryParse(p[10], out cur.BoundsH); }
                        if (p.Length > 11) cur.SpanLast = p[11] == "1";
                    }
                    else if (p[0] == "win" && p.Length > 2 && cur != null)
                    {
                        var m = new Member { ExePath = p[1], Title = p[2] };
                        if (p.Length > 3) m.ClassName = p[3];
                        if (p.Length > 7) { int.TryParse(p[4], out m.L); int.TryParse(p[5], out m.T); int.TryParse(p[6], out m.R); int.TryParse(p[7], out m.B); }
                        cur.Members.Add(m);
                    }
                }
            }
            catch { }
        }
    }

    // ------------------------------------------------------------------
    // Look and feel
    // ------------------------------------------------------------------
    static class Theme
    {
        public static readonly Color Bg = Color.FromArgb(243, 243, 243);
        public static readonly Color Card = Color.White;
        public static readonly Color Border = Color.FromArgb(225, 225, 225);
        public static readonly Color Text = Color.FromArgb(32, 32, 32);
        public static readonly Color Muted = Color.FromArgb(110, 110, 110);
        public static readonly Color Accent = Color.FromArgb(0, 103, 192);
        public static readonly Color[] GroupColors =
        {
            Color.FromArgb(0, 120, 212), Color.FromArgb(232, 113, 26), Color.FromArgb(16, 137, 62),
            Color.FromArgb(136, 23, 152), Color.FromArgb(196, 43, 28), Color.FromArgb(0, 153, 153),
            Color.FromArgb(202, 80, 16), Color.FromArgb(92, 46, 145)
        };
        public static Color ColorFor(int i) { return GroupColors[i % GroupColors.Length]; }

        public static GraphicsPath Rounded(Rectangle r, int radius)
        {
            var p = new GraphicsPath(); int d = radius * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        // App / tray icon: three linked squares.
        public static Icon MakeIcon(int size)
        {
            var bmp = new Bitmap(size, size);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                float s = size / 16f;
                Color[] cols = { GroupColors[0], GroupColors[1], GroupColors[2] };
                for (int i = 0; i < 3; i++)
                {
                    var r = new Rectangle((int)((1 + i * 4) * s), (int)((1 + i * 4) * s), (int)(7 * s), (int)(7 * s));
                    using (var b = new SolidBrush(cols[i])) using (var path = Rounded(r, Math.Max(1, (int)(1.5 * s)))) g.FillPath(b, path);
                    using (var pen = new Pen(Color.White, Math.Max(1, s))) using (var path = Rounded(r, Math.Max(1, (int)(1.5 * s)))) g.DrawPath(pen, path);
                }
            }
            return Icon.FromHandle(bmp.GetHicon());
        }
    }

    class FlatButton : Button
    {
        public FlatButton(string text, bool primary)
        {
            Text = text; FlatStyle = FlatStyle.Flat; Cursor = Cursors.Hand; AutoSize = false;
            Font = new Font("Segoe UI", 9f, primary ? FontStyle.Bold : FontStyle.Regular);
            FlatAppearance.BorderSize = primary ? 0 : 1;
            FlatAppearance.BorderColor = Theme.Border;
            BackColor = primary ? Theme.Accent : Color.White;
            ForeColor = primary ? Color.White : Theme.Text;
            FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(0, 90, 170) : Color.FromArgb(245, 245, 245);
            UseVisualStyleBackColor = false;
        }
    }

    // One group, drawn as a card.
    class GroupCard : Panel
    {
        public readonly Group Group;
        readonly MainForm form;
        readonly Color color;
        bool picking;
        const int Pad = 12, HeaderH = 44, RowH = 26, FooterH = 34;

        public GroupCard(MainForm f, Group g, Color c, bool picking)
        {
            form = f; Group = g; color = c; this.picking = picking;
            DoubleBuffered = true; BackColor = Theme.Bg;
            // Click a "not open" row (or right-click any row) to point it at a window by clicking that window.
            var rowMenu = new ContextMenuStrip();
            MouseMove += (s, e) => { var m = RowAt(e.Y); Cursor = m != null && !m.Open ? Cursors.Hand : Cursors.Default; };
            MouseClick += (s, e) =>
            {
                var m = RowAt(e.Y);
                if (m == null) return;
                if (e.Button == MouseButtons.Left && !m.Open) form.StartRelink(Group, m);
                else if (e.Button == MouseButtons.Right)
                {
                    rowMenu.Items.Clear();
                    rowMenu.Items.Add("Link to a different window…", null, (s2, e2) => form.StartRelink(Group, m));
                    rowMenu.Items.Add("Remove from group", null, (s2, e2) => form.RemoveMember(Group, m));
                    rowMenu.Show(this, e.Location);
                }
            };
            Margin = new Padding(0, 0, 0, 10);
            Height = HeaderH + Pad + Math.Max(1, g.Members.Count) * RowH + FooterH + Pad;

            var gridBox = new CheckBox
            {
                Text = "Enable grid", Checked = g.Grid, AutoSize = true, BackColor = Theme.Card, ForeColor = Theme.Text,
                Cursor = Cursors.Hand, Location = new Point(Pad, Height - Pad - 22)
            };
            new ToolTip().SetToolTip(gridBox, "Tile this group's windows side by side, like charts on a trading screen");
            gridBox.CheckedChanged += (s, e) => form.SetGrid(Group, gridBox.Checked);
            Controls.Add(gridBox);

            // one-click minimize / restore for the whole group
            var minBtn = new FlatButton("Minimize", false) { Size = new Size(74, 26) };
            var resBtn = new FlatButton("Restore", false) { Size = new Size(70, 26) };
            new ToolTip().SetToolTip(minBtn, "Minimize every window in this group");
            new ToolTip().SetToolTip(resBtn, "Bring every window in this group back");
            minBtn.Click += (s, e) => form.GroupAction(Group, "min");
            resBtn.Click += (s, e) => form.GroupAction(Group, "restore");
            minBtn.Enabled = resBtn.Enabled = g.Members.Count > 0;
            Controls.Add(minBtn); Controls.Add(resBtn);

            var add = new FlatButton(picking ? "Cancel" : "+ Add window", !picking) { Size = new Size(104, 28) };
            add.Click += (s, e) => { if (picking) form.CancelPick(); else form.StartPick(Group); };
            Controls.Add(add);

            var more = new FlatButton("•••", false) { Size = new Size(34, 28), Font = new Font("Segoe UI", 7f) };
            var menu = new ContextMenuStrip();
            menu.Opening += (s, e) =>
            {
                menu.Items.Clear();
                if (Group.Grid)
                {
                    menu.Items.Add("Even out sizes", null, (s2, e2) => form.ResetGrid(Group));
                    var cols = new ToolStripMenuItem("Columns");
                    for (int n = 0; n <= 6; n++)
                    {
                        int nn = n;
                        cols.DropDownItems.Add(new ToolStripMenuItem(n == 0 ? "Automatic" : n.ToString(), null, (s2, e2) => form.SetColumns(Group, nn)) { Checked = Group.Columns == n });
                    }
                    menu.Items.Add(cols);
                    menu.Items.Add("Fill the screen", null, (s2, e2) => form.FitToScreen(Group));
                    menu.Items.Add("Re-fit around windows", null, (s2, e2) => form.RefitGrid(Group));
                }
                if (Group.Grid) menu.Items.Add(new ToolStripSeparator());
                menu.Items.Add("Rename…", null, (s2, e2) => form.RenameGroup(Group));
                menu.Items.Add("Delete group", null, (s2, e2) => form.DeleteGroup(Group));
                e.Cancel = false;
            };
            menu.Items.Add("…");
            more.Click += (s, e) => menu.Show(more, new Point(0, more.Height));
            Controls.Add(more);

            Resize += (s, e) =>
            {
                more.Location = new Point(Width - Pad - more.Width, Pad + 2);
                add.Location = new Point(more.Left - 6 - add.Width, Pad + 2);
                resBtn.Location = new Point(Width - Pad - resBtn.Width, Height - Pad - 25);
                minBtn.Location = new Point(resBtn.Left - 6 - minBtn.Width, Height - Pad - 25);
                LayoutRows();
            };

            int y = HeaderH + Pad;
            for (int i = 0; i < g.Members.Count; i++)
            {
                var mm = g.Members[i];
                Controls.Add(RowButton("✕", "Remove from group", y, 26, () => form.RemoveMember(Group, mm)));
                if (g.Grid)
                {
                    // move this window earlier/later in the grid (swaps places with its neighbour)
                    if (i > 0) Controls.Add(RowButton("▲", "Move up in the grid (swap with the one above)", y, 52, () => form.MoveMember(Group, mm, -1)));
                    if (i < g.Members.Count - 1) Controls.Add(RowButton("▼", "Move down in the grid (swap with the one below)", y, 76, () => form.MoveMember(Group, mm, +1)));
                }
                y += RowH;
            }
        }

        // small clickable symbol at the right end of a window row; xFromRight says how far from the edge
        Label RowButton(string text, string tip, int y, int xFromRight, Action click)
        {
            var l = new Label
            {
                Text = text, AutoSize = false, Size = new Size(22, 22), TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = Theme.Muted, Cursor = Cursors.Hand, Tag = new Point(xFromRight, y), BackColor = Theme.Card,
                Font = new Font("Segoe UI Symbol", text == "✕" ? 9f : 8f)
            };
            new ToolTip().SetToolTip(l, tip);
            l.Click += (s, e) => click();
            l.MouseEnter += (s, e) => l.ForeColor = Theme.Accent;
            l.MouseLeave += (s, e) => l.ForeColor = Theme.Muted;
            return l;
        }

        Member RowAt(int y)
        {
            int i = (y - (HeaderH + Pad)) / RowH;
            if (y < HeaderH + Pad || i < 0 || i >= Group.Members.Count) return null;
            return Group.Members[i];
        }

        void LayoutRows()
        {
            foreach (Control c in Controls)
                if (c is Label && c.Tag is Point) { var t = (Point)c.Tag; c.Location = new Point(Width - Pad - t.X, t.Y + 2); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var path = Theme.Rounded(r, 8))
            {
                using (var b = new SolidBrush(Theme.Card)) g.FillPath(b, path);
                using (var p = new Pen(picking ? color : Theme.Border, picking ? 2 : 1)) g.DrawPath(p, path);
            }
            // badge
            var badge = new Rectangle(Pad, Pad + 2, 28, 28);
            using (var b = new SolidBrush(color)) g.FillEllipse(b, badge);
            string initial = Group.Name.Length <= 2 ? Group.Name : Group.Name.Substring(0, 1);
            using (var f = new Font("Segoe UI", 10.5f, FontStyle.Bold))
            {
                var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString(initial, f, Brushes.White, badge, sf);
            }
            string title = Group.Name.Length <= 2 ? "Group " + Group.Name : Group.Name;
            using (var f = new Font("Segoe UI", 10f, FontStyle.Bold)) using (var b = new SolidBrush(Theme.Text))
                g.DrawString(title, f, b, Pad + 36, Pad);
            int open = Group.Members.Count(m => m.Open);
            string sub = Group.Members.Count == 0 ? "No windows yet" :
                Group.Members.Count + (Group.Members.Count == 1 ? " window" : " windows") + (open < Group.Members.Count ? ", " + open + " open" : "");
            if (Group.Grid && open > 0) { int gc, gr; Engine.Dims(Group, open, out gc, out gr); sub += " · " + gc + "×" + gr + " grid"; }
            using (var f = new Font("Segoe UI", 8.5f))
                TextRenderer.DrawText(g, sub, f, new Rectangle(Pad + 34, Pad + 19, Math.Max(40, Width - 156 - (Pad + 34) - 4), 18), Theme.Muted,
                    TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

            using (var p = new Pen(Theme.Border)) g.DrawLine(p, Pad, HeaderH + 4, Width - Pad, HeaderH + 4);

            // footer: grid checkbox lives here
            int fy = Height - Pad - FooterH + 4;
            using (var p = new Pen(Theme.Border)) g.DrawLine(p, Pad, fy, Width - Pad, fy);

            int y = HeaderH + Pad;
            if (Group.Members.Count == 0)
            {
                using (var f = new Font("Segoe UI", 8.5f, FontStyle.Italic)) using (var b = new SolidBrush(Theme.Muted))
                    g.DrawString(picking ? "Now click the window you want to add…" : "Click “+ Add window”, then click any window.", f, b, Pad, y + 4);
                return;
            }
            int slot = 0;
            foreach (var m in Group.Members)
            {
                int x0 = Pad;
                if (Group.Grid)
                {
                    // grid spot number (open windows fill the grid in list order)
                    string num = m.Open ? (++slot).ToString() : "–";
                    using (var f = new Font("Segoe UI", 8f, FontStyle.Bold))
                        TextRenderer.DrawText(g, num, f, new Rectangle(x0, y + 4, 16, 18), color, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                    x0 += 20;
                }
                var icon = form.IconFor(m.ExePath);
                if (icon != null) g.DrawIcon(icon, new Rectangle(x0, y + 4, 16, 16));
                int rightSpace = Group.Grid ? 82 : 30;
                var textRect = new Rectangle(x0 + 24, y + 4, Width - Pad - (x0 + 24) - rightSpace, 18);
                using (var f = new Font("Segoe UI", 9f)) using (var b = new SolidBrush(m.Open ? Theme.Text : Theme.Muted))
                    TextRenderer.DrawText(g, m.Open ? m.Title : m.Title + "  (not open · click to link)", f, textRect, m.Open ? Theme.Text : Theme.Muted,
                        TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter);
                y += RowH;
            }
        }
    }

    // ------------------------------------------------------------------
    // Main window + tray icon
    // ------------------------------------------------------------------
    class MainForm : Form
    {
        readonly Engine engine = new Engine();
        readonly FlowLayoutPanel list;
        readonly Label status;
        readonly NotifyIcon tray;
        readonly FlatButton gear;
        ToolStripMenuItem updateItem, autoUpdateItem;
        Updater.Release pendingUpdate;
        bool checkingUpdate;
        readonly CheckBox onSwitch;
        readonly ToolStripMenuItem pauseItem, startupItem, maxItem, reopenItem;
        readonly System.Windows.Forms.Timer refreshTimer, pickTimer;
        readonly Dictionary<string, Icon> icons = new Dictionary<string, Icon>(StringComparer.OrdinalIgnoreCase);
        Group pickGroup; Member pickMember; int pickDeadline;
        string lastSignature = "";
        bool reallyExit, toldAboutTray, allowVisible = true;

        protected override void SetVisibleCore(bool value)
        {
            if (!allowVisible) { value = false; if (!IsHandleCreated) CreateHandle(); }
            base.SetVisibleCore(value);
        }
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        public MainForm(bool startHidden, bool justUpdated)
        {
            Text = "Window Groups";
            Font = new Font("Segoe UI", 9f);
            BackColor = Theme.Bg;
            ClientSize = new Size(360, 500);
            MinimumSize = new Size(320, 360);
            StartPosition = FormStartPosition.CenterScreen;
            Icon = Theme.MakeIcon(32);

            // header
            var header = new Panel { Dock = DockStyle.Top, Height = 64, BackColor = Theme.Bg, Padding = new Padding(16, 12, 16, 0) };
            var title = new Label { Text = "Window Groups", Font = new Font("Segoe UI", 14f, FontStyle.Bold), ForeColor = Theme.Text, AutoSize = true, Location = new Point(14, 10) };
            var tag = new Label { Text = "Minimize one window, the whole group follows.", ForeColor = Theme.Muted, AutoSize = true, Location = new Point(16, 38) };
            onSwitch = new CheckBox
            {
                Appearance = Appearance.Button, Checked = true, Text = "On", Size = new Size(52, 26), FlatStyle = FlatStyle.Flat,
                TextAlign = ContentAlignment.MiddleCenter, Font = new Font("Segoe UI", 9f, FontStyle.Bold), Cursor = Cursors.Hand,
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            onSwitch.FlatAppearance.BorderSize = 0;
            onSwitch.CheckedChanged += (s, e) => SetEnabled(onSwitch.Checked);
            gear = new FlatButton("⚙", false) { Size = new Size(30, 26), Font = new Font("Segoe UI Symbol", 10f), Anchor = AnchorStyles.Top | AnchorStyles.Right };
            new ToolTip().SetToolTip(gear, "Settings and updates");
            header.Controls.AddRange(new Control[] { title, tag, onSwitch, gear });
            header.Resize += (s, e) =>
            {
                onSwitch.Location = new Point(header.Width - 16 - onSwitch.Width, 16);
                gear.Location = new Point(onSwitch.Left - 6 - gear.Width, 16);
            };

            // footer
            var footer = new Panel { Dock = DockStyle.Bottom, Height = 70, BackColor = Theme.Bg };
            var newGroup = new FlatButton("+ New group", false) { Height = 32 };
            newGroup.Click += (s, e) => NewGroup();
            status = new Label { ForeColor = Theme.Muted, AutoSize = false, Height = 20, TextAlign = ContentAlignment.MiddleLeft };
            footer.Controls.AddRange(new Control[] { newGroup, status });
            footer.Resize += (s, e) =>
            {
                newGroup.SetBounds(16, 4, footer.Width - 32, 32);
                status.SetBounds(16, 42, footer.Width - 32, 20);
            };

            list = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true,
                Padding = new Padding(16, 4, 16, 4), BackColor = Theme.Bg
            };
            list.Resize += (s, e) => SizeCards();

            Controls.Add(list); Controls.Add(footer); Controls.Add(header);

            // tray
            var menu = new ContextMenuStrip();
            menu.Items.Add("Open Window Groups", null, (s, e) => ShowMain()).Font = new Font("Segoe UI", 9f, FontStyle.Bold);
            pauseItem = new ToolStripMenuItem("Pause syncing", null, (s, e) => onSwitch.Checked = !onSwitch.Checked);
            maxItem = new ToolStripMenuItem("Also link maximize", null, (s, e) => { engine.SyncMaximize = !engine.SyncMaximize; maxItem.Checked = engine.SyncMaximize; engine.Save(); });
            startupItem = new ToolStripMenuItem("Start with Windows", null, (s, e) => ToggleStartup());
            reopenItem = new ToolStripMenuItem("Reopen my apps after restart", null, (s, e) => { engine.ReopenApps = !engine.ReopenApps; reopenItem.Checked = engine.ReopenApps; engine.Save(); });
            var reopenNow = new ToolStripMenuItem("Open missing apps now", null, (s, e) =>
            {
                int n = engine.LaunchMissing();
                SetStatus(n == 0 ? "Nothing to open: all grouped apps are running." : "Opening " + n + (n == 1 ? " app…" : " apps…"));
            });
            menu.Items.AddRange(new ToolStripItem[] { pauseItem, maxItem, startupItem, reopenItem, reopenNow, new ToolStripSeparator() });
            updateItem = new ToolStripMenuItem("Check for updates…", null, (s, e) => CheckForUpdates(true));
            autoUpdateItem = new ToolStripMenuItem("Check for updates automatically", null, (s, e) => { engine.AutoUpdate = !engine.AutoUpdate; autoUpdateItem.Checked = engine.AutoUpdate; engine.Save(); });
            var versionItem = new ToolStripMenuItem("Version " + Updater.Version) { Enabled = false };
            menu.Items.AddRange(new ToolStripItem[] { updateItem, autoUpdateItem, versionItem, new ToolStripSeparator() });
            menu.Items.Add("Exit", null, (s, e) => ExitApp());
            tray = new NotifyIcon { Icon = Theme.MakeIcon(16), Text = "Window Groups", ContextMenuStrip = menu, Visible = true };
            tray.DoubleClick += (s, e) => ShowMain();

            // engine
            engine.Load();
            if (engine.Groups.Count == 0) engine.Groups.Add(new Group { Name = "A" });
            engine.Rebind();
            engine.Synced += msg => SetStatus(msg);
            engine.GridChanged += () => RebuildIfChanged();
            engine.Start();
            maxItem.Checked = engine.SyncMaximize;
            if (engine.FirstRun && !IsStartupOn()) ToggleStartup();   // remember groups across restarts out of the box
            else if (IsStartupOn()) SetStartup(true);                   // keep the path right if the exe was moved
            startupItem.Checked = IsStartupOn();
            reopenItem.Checked = engine.ReopenApps;
            autoUpdateItem.Checked = engine.AutoUpdate;
            gear.Click += (s, e) => menu.Show(gear, new Point(0, gear.Height));
            tray.BalloonTipClicked += (s, e) => { if (pendingUpdate != null) OfferUpdate(pendingUpdate); };
            if (justUpdated)
            {
                Engine.Later(1500, () => tray.ShowBalloonTip(4000, "Window Groups updated", "You're now on version " + Updater.Version + ".", ToolTipIcon.Info));
                SetStatus("Updated to version " + Updater.Version + ".");
            }
            if (engine.AutoUpdate && engine.LastUpdateCheck != DateTime.Today.ToString("yyyy-MM-dd"))
                Engine.Later(30000, () => CheckForUpdates(false));
            SetEnabled(true);
            engine.Save();
            if (engine.ReopenApps && startHidden)
            {
                // Started with Windows: give programs that start by themselves a moment, then open the rest.
                Engine.Later(8000, () =>
                {
                    engine.Rebind();
                    int n = engine.LaunchMissing();
                    if (n > 0) SetStatus("Reopened " + n + (n == 1 ? " app" : " apps") + " from your groups.");
                });
            }

            refreshTimer = new System.Windows.Forms.Timer { Interval = 2000 };
            refreshTimer.Tick += (s, e) =>
            {
                foreach (var g in engine.Rebind()) engine.Tile(g);
                if (engine.PositionsChanged) { engine.PositionsChanged = false; engine.Save(); }
                RebuildIfChanged();
            };
            refreshTimer.Start();
            pickTimer = new System.Windows.Forms.Timer { Interval = 120 };
            pickTimer.Tick += (s, e) => PollPick();

            Rebuild();
            SetStatus("Tip: closing this window keeps it running in the tray.");
            allowVisible = !startHidden;
        }

        // ---- UI building ----
        string Signature()
        {
            var sb = new StringBuilder(pickGroup == null ? "-" : pickGroup.Name);
            foreach (var g in engine.Groups)
            {
                sb.Append('|').Append(g.Name).Append(g.Grid).Append(g.Columns);
                foreach (var m in g.Members) sb.Append(';').Append(m.Open).Append(m.Title);
            }
            return sb.ToString();
        }

        void RebuildIfChanged() { if (Signature() != lastSignature) Rebuild(); }

        void Rebuild()
        {
            lastSignature = Signature();
            list.SuspendLayout();
            var old = list.Controls.Cast<Control>().ToList();
            list.Controls.Clear();
            foreach (var c in old) c.Dispose();
            for (int i = 0; i < engine.Groups.Count; i++)
                list.Controls.Add(new GroupCard(this, engine.Groups[i], Theme.ColorFor(i), engine.Groups[i] == pickGroup));
            SizeCards();
            list.ResumeLayout();
        }

        void SizeCards()
        {
            int w = list.ClientSize.Width - list.Padding.Horizontal - (list.VerticalScroll.Visible ? 0 : 0);
            foreach (Control c in list.Controls) c.Width = Math.Max(200, w);
        }

        public Icon IconFor(string exe)
        {
            if (string.IsNullOrEmpty(exe)) return null;
            Icon ic;
            if (!icons.TryGetValue(exe, out ic))
            {
                try { ic = File.Exists(exe) ? Icon.ExtractAssociatedIcon(exe) : null; } catch { ic = null; }
                icons[exe] = ic;
            }
            return ic;
        }

        void SetStatus(string s) { status.Text = s; }

        void SetEnabled(bool on)
        {
            engine.Enabled = on;
            onSwitch.Text = on ? "On" : "Off";
            onSwitch.BackColor = on ? Color.FromArgb(16, 137, 62) : Color.FromArgb(160, 160, 160);
            onSwitch.ForeColor = Color.White;
            pauseItem.Checked = !on;
            tray.Text = on ? "Window Groups" : "Window Groups (paused)";
            SetStatus(on ? "Syncing is on." : "Syncing is paused.");
        }

        // ---- picking a window by clicking it ----
        public void StartRelink(Group g, Member m)
        {
            StartPick(g);
            pickMember = m;
            SetStatus("Click the window that is “" + m.Title + "”…");
        }

        public void StartPick(Group g)
        {
            pickMember = null;
            pickGroup = g;
            pickDeadline = Environment.TickCount + 15000;
            Rebuild();
            SetStatus("Click the window to add to " + Label(g) + "…");
            pickTimer.Start();
        }

        public void CancelPick()
        {
            pickTimer.Stop(); pickGroup = null; pickMember = null; Rebuild();
            SetStatus("Cancelled.");
        }

        void PollPick()
        {
            if (pickGroup == null) { pickTimer.Stop(); return; }
            if (Environment.TickCount - pickDeadline > 0) { CancelPick(); SetStatus("Nothing picked. Try again."); return; }
            IntPtr fg = Native.GetForegroundWindow();
            if (fg == IntPtr.Zero || fg == Handle) return;
            IntPtr root = Native.GetAncestor(fg, 3 /*GA_ROOTOWNER*/);
            if (root != IntPtr.Zero) fg = root;
            if (!Native.IsAppWindow(fg)) return;
            var g = pickGroup; var relink = pickMember;
            pickTimer.Stop(); pickGroup = null; pickMember = null;
            if (relink != null && g.Members.Contains(relink))
            {
                engine.Relink(g, relink, fg);
                engine.Tile(g);
                Rebuild();
                SetStatus("Linked “" + Native.Title(fg) + "” in " + Label(g) + ".");
                Native.SetForegroundWindow(Handle);
                return;
            }
            engine.AddWindow(g, fg);
            engine.Tile(g);
            Rebuild();
            SetStatus("Added “" + Native.Title(fg) + "” to " + Label(g) + ".");
            // come back so the user can add more
            Native.SetForegroundWindow(Handle);
        }

        static string Label(Group g) { return g.Name.Length <= 2 ? "Group " + g.Name : g.Name; }

        // ---- group actions ----
        void NewGroup()
        {
            var name = Prompt("New group", "Name for the new group:", engine.NextName());
            if (string.IsNullOrWhiteSpace(name)) return;
            name = name.Trim();
            if (engine.Groups.Any(g => g.Name == name)) { MessageBox.Show(this, "There's already a group called " + name + "."); return; }
            var grp = new Group { Name = name };
            engine.Groups.Add(grp); engine.Save(); Rebuild();
            list.ScrollControlIntoView(list.Controls[list.Controls.Count - 1]);
        }

        public void RenameGroup(Group g)
        {
            var name = Prompt("Rename group", "New name:", g.Name);
            if (string.IsNullOrWhiteSpace(name) || name.Trim() == g.Name) return;
            if (engine.Groups.Any(x => x.Name == name.Trim())) { MessageBox.Show(this, "That name is taken."); return; }
            g.Name = name.Trim(); engine.Save(); Rebuild();
        }

        public void DeleteGroup(Group g)
        {
            if (g.Members.Count > 0 && MessageBox.Show(this, "Delete " + Label(g) + "? The windows stay open.", "Window Groups",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            engine.Groups.Remove(g); engine.Save(); Rebuild();
        }

        public void MoveMember(Group g, Member m, int delta)
        {
            int i = g.Members.IndexOf(m), j = i + delta;
            if (i < 0 || j < 0 || j >= g.Members.Count) return;
            g.Members[i] = g.Members[j]; g.Members[j] = m;
            engine.Tile(g); engine.Save(); Rebuild();
            SetStatus("Swapped places in " + Label(g) + ".");
        }

        public void RemoveMember(Group g, Member m) { g.Members.Remove(m); engine.Tile(g); engine.Save(); Rebuild(); }

        // ---- grids ----
        public void SetGrid(Group g, bool on)
        {
            if (g.Grid == on) return;
            g.Grid = on;
            if (on) { engine.ResetGrid(g); engine.ResetBounds(g); engine.Tile(g); }
            engine.Save(); Rebuild();
            SetStatus(on ? Label(g) + " is now a grid." : Label(g) + " grid turned off.");
        }

        public void ResetGrid(Group g) { engine.ResetGrid(g); engine.Tile(g); engine.Save(); }
        public void SetColumns(Group g, int n) { g.Columns = n; engine.ResetGrid(g); engine.Tile(g); engine.Save(); Rebuild(); }
        public void FitToScreen(Group g) { engine.FitToScreen(g); engine.Save(); }
        public void RefitGrid(Group g) { engine.ResetBounds(g); engine.Tile(g); engine.Save(); }

        public void GroupAction(Group g, string action)
        {
            engine.Apply(g.Members.Where(m => m.Open).Select(m => m.Hwnd).ToList(), action, IntPtr.Zero);
            SetStatus(Label(g) + (action == "min" ? " minimized." : " restored."));
        }

        string Prompt(string caption, string text, string value)
        {
            using (var f = new Form
            {
                Text = caption, FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent,
                ClientSize = new Size(300, 120), MaximizeBox = false, MinimizeBox = false, Font = Font, BackColor = Color.White, ShowInTaskbar = false
            })
            {
                var lbl = new Label { Text = text, Location = new Point(14, 14), AutoSize = true };
                var box = new TextBox { Text = value, Location = new Point(16, 38), Width = 268 };
                var ok = new FlatButton("OK", true) { DialogResult = DialogResult.OK, Location = new Point(124, 76), Size = new Size(76, 30) };
                var cancel = new FlatButton("Cancel", false) { DialogResult = DialogResult.Cancel, Location = new Point(208, 76), Size = new Size(76, 30) };
                f.Controls.AddRange(new Control[] { lbl, box, ok, cancel });
                f.AcceptButton = ok; f.CancelButton = cancel;
                return f.ShowDialog(this) == DialogResult.OK ? box.Text : null;
            }
        }

        // ---- tray / startup ----
        void ShowMain()
        {
            allowVisible = true; Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
        }

        // ---- updates ----
        void CheckForUpdates(bool userAsked)
        {
            if (checkingUpdate) return;
            checkingUpdate = true;
            if (userAsked) SetStatus("Checking for updates…");
            ThreadPool.QueueUserWorkItem(_ =>
            {
                Updater.Release r = null; string error = null;
                try { r = Updater.Latest(); }
                catch (WebException ex)
                {
                    var resp = ex.Response as HttpWebResponse;
                    error = resp != null && resp.StatusCode == HttpStatusCode.NotFound ? "No updates have been published yet." : "Couldn't reach the update server. Check your internet connection.";
                }
                catch (Exception ex) { error = ex.Message; }
                BeginInvoke((Action)(() =>
                {
                    checkingUpdate = false;
                    engine.LastUpdateCheck = DateTime.Today.ToString("yyyy-MM-dd"); engine.Save();
                    if (r != null && r.Ver > Updater.Current)
                    {
                        pendingUpdate = r;
                        updateItem.Text = "Update to version " + r.Ver + "…";
                        updateItem.Font = new Font(updateItem.Font, FontStyle.Bold);
                        gear.Text = "⚙•"; gear.ForeColor = Theme.Accent;
                        if (userAsked) OfferUpdate(r);
                        else tray.ShowBalloonTip(6000, "Update available", "Window Groups " + r.Ver + " is ready. Click here to install it.", ToolTipIcon.Info);
                        SetStatus("Version " + r.Ver + " is available. Open ⚙ to update.");
                    }
                    else if (userAsked)
                    {
                        string msg = error ?? "You have the latest version (" + Updater.Version + ").";
                        SetStatus(msg);
                        MessageBox.Show(this, msg, "Window Groups", MessageBoxButtons.OK, error == null ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
                    }
                }));
            });
        }

        void OfferUpdate(Updater.Release r)
        {
            string notes = string.IsNullOrWhiteSpace(r.Notes) ? "" : "\n\nWhat's new:\n" + (r.Notes.Length > 600 ? r.Notes.Substring(0, 600) + "…" : r.Notes);
            if (MessageBox.Show(this, "Window Groups " + r.Ver + " is available (you have " + Updater.Version + ")." + notes +
                "\n\nUpdate now? The app restarts by itself and keeps your groups.", "Update Window Groups",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            SetStatus("Downloading update…");
            UseWaitCursor = true;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                string error = null;
                try { engine.Save(); Updater.Install(r); }
                catch (UnauthorizedAccessException) { error = "Windows didn't allow replacing the app in this folder. Move the WindowGroups folder to Documents (or your Desktop) and try again."; }
                catch (Exception ex) { error = "The update didn't finish: " + ex.Message; }
                BeginInvoke((Action)(() =>
                {
                    UseWaitCursor = false;
                    if (error != null) { SetStatus("Update failed."); MessageBox.Show(this, error, "Window Groups", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
                    ExitApp(); // the new version is starting
                }));
            });
        }

        void ExitApp()
        {
            reallyExit = true; engine.Save(); engine.Stop(); tray.Visible = false; Application.Exit();
        }

        bool IsStartupOn()
        {
            try { using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) return k != null && k.GetValue("WindowGroups") != null; }
            catch { return false; }
        }

        void ToggleStartup() { SetStartup(!IsStartupOn()); }

        void SetStartup(bool on)
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (!on) k.DeleteValue("WindowGroups", false);
                    else k.SetValue("WindowGroups", "\"" + Application.ExecutablePath + "\" --tray");
                }
            }
            catch { }
            if (startupItem != null) startupItem.Checked = IsStartupOn();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!reallyExit && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true; Hide();
                if (!toldAboutTray)
                {
                    toldAboutTray = true;
                    tray.ShowBalloonTip(3000, "Still running", "Window Groups keeps your groups linked from the tray. Right-click the icon to exit.", ToolTipIcon.Info);
                }
                return;
            }
            engine.Save(); engine.Stop(); tray.Visible = false;
            base.OnFormClosing(e);
        }
    }

    // ------------------------------------------------------------------
    // Self-update from the GitHub releases of the app's repository
    // ------------------------------------------------------------------
    static class Updater
    {
        public const string Version = "1.6.0";
        public const string Repo = "jithub1111/window-groups";

        public class Release { public Version Ver; public string Tag, ExeUrl, Notes; }

        public static Version Current { get { return new Version(Version); } }

        // Ask GitHub for the newest release. Throws on network/HTTP errors.
        public static Release Latest()
        {
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; // TLS 1.2
            string json;
            using (var wc = new WebClient())
            {
                wc.Headers["User-Agent"] = "WindowGroups-Updater";
                wc.Headers["Accept"] = "application/vnd.github+json";
                wc.Encoding = Encoding.UTF8;
                json = wc.DownloadString("https://api.github.com/repos/" + Repo + "/releases/latest");
            }
            var tag = Regex.Match(json, "\"tag_name\"\\s*:\\s*\"v?([0-9]+(?:\\.[0-9]+){1,3})\"");
            var url = Regex.Match(json, "\"browser_download_url\"\\s*:\\s*\"(https://[^\"]+/WindowGroups\\.exe)\"");
            if (!tag.Success || !url.Success) return null;
            var body = Regex.Match(json, "\"body\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
            string notes = body.Success ? Regex.Unescape(body.Groups[1].Value) : "";
            return new Release { Ver = new Version(tag.Groups[1].Value), Tag = tag.Groups[0].Value, ExeUrl = url.Groups[1].Value, Notes = notes };
        }

        // Download the new exe next to the current one, swap them, and start the new one.
        public static void Install(Release r)
        {
            string exe = Application.ExecutablePath, dir = Path.GetDirectoryName(exe);
            string tmp = Path.Combine(dir, "WindowGroups.update.tmp"), old = exe + ".old";
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
            using (var wc = new WebClient())
            {
                wc.Headers["User-Agent"] = "WindowGroups-Updater";
                wc.DownloadFile(r.ExeUrl, tmp);
            }
            var bytes = File.ReadAllBytes(tmp);
            if (bytes.Length < 20000 || bytes[0] != 'M' || bytes[1] != 'Z') { File.Delete(tmp); throw new Exception("The download didn't look like the app. Nothing was changed."); }
            if (File.Exists(old)) File.Delete(old);
            File.Move(exe, old);          // Windows lets a running exe be renamed
            try { File.Move(tmp, exe); }
            catch { File.Move(old, exe); throw; }
            Process.Start(new ProcessStartInfo(exe, "--updated") { UseShellExecute = false, WorkingDirectory = dir });
        }

        // Remove the previous version left behind by an update.
        public static void Cleanup()
        {
            try { var old = Application.ExecutablePath + ".old"; if (File.Exists(old)) File.Delete(old); } catch { }
            try { var tmp = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "WindowGroups.update.tmp"); if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }

    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            bool created;
            using (var mutex = new Mutex(true, "WindowGroups.SingleInstance", out created))
            {
                if (!created && args.Contains("--updated"))
                {
                    // We were started by the updater: wait for the old version to close.
                    try { created = mutex.WaitOne(15000); } catch (AbandonedMutexException) { created = true; }
                }
                if (!created)
                {
                    MessageBox.Show("Window Groups is already running. Look for its icon in the tray.", "Window Groups");
                    return;
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Updater.Cleanup();
                Application.Run(new MainForm(args.Contains("--tray") || args.Contains("--updated"), args.Contains("--updated")));
            }
        }

    }
}
