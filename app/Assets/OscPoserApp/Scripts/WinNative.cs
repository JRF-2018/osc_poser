// Windows の機能: ファイルのドラッグアンドドロップ、ファイルを開くダイアログ、いつも手前。(VrmPoseViewer のものに手前を足した)
// ドロップは、Unity の窓に DragAcceptFiles を付け、メインスレッドのメッセージを WH_GETMESSAGE のフックで見て
// WM_DROPFILES を拾う。
// 0.3.0: VRChat の窓を前面に出す(持つのに要る)。VRChat はアンチチートでコマンドラインが読めないので、
// OSC の受信ポートを開いているプロセスを UDP の表(GetExtendedUdpTable)から探し、その窓を前面に出す。
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;

public static class WinNative
{
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
    delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
    delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam; public uint time; public int x, y; }

    [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int idHook, HookProc fn, IntPtr hMod, uint threadId);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool EnumThreadWindows(uint threadId, EnumWindowsProc fn, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hWnd, StringBuilder sb, int n);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("shell32.dll")] static extern void DragAcceptFiles(IntPtr hWnd, bool accept);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern uint DragQueryFile(IntPtr hDrop, uint i, StringBuilder sb, int n);
    [DllImport("shell32.dll")] static extern void DragFinish(IntPtr hDrop);

    const int WH_GETMESSAGE = 3;
    const uint WM_DROPFILES = 0x0233;

    static HookProc hookProc;   // ガベージコレクションで消えないように持っておく
    static IntPtr hook;
    static readonly List<string> dropped = new List<string>();

    public static void InstallDrop()
    {
        if (hook != IntPtr.Zero) return;
        uint tid = GetCurrentThreadId();
        EnumThreadWindows(tid, (h, _) =>
        {
            var sb = new StringBuilder(64);
            GetClassName(h, sb, sb.Capacity);
            if (sb.ToString() == "UnityWndClass") DragAcceptFiles(h, true);
            return true;
        }, IntPtr.Zero);
        hookProc = OnMessage;
        hook = SetWindowsHookEx(WH_GETMESSAGE, hookProc, IntPtr.Zero, tid);
    }

    public static void UninstallDrop()
    {
        if (hook != IntPtr.Zero) UnhookWindowsHookEx(hook);
        hook = IntPtr.Zero;
    }

    static IntPtr OnMessage(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code == 0)
        {
            var msg = Marshal.PtrToStructure<MSG>(lParam);
            if (msg.message == WM_DROPFILES)
            {
                uint n = DragQueryFile(msg.wParam, 0xFFFFFFFF, null, 0);
                for (uint i = 0; i < n; i++)
                {
                    var sb = new StringBuilder(1024);
                    DragQueryFile(msg.wParam, i, sb, sb.Capacity);
                    lock (dropped) dropped.Add(sb.ToString());
                }
                DragFinish(msg.wParam);
            }
        }
        return CallNextHookEx(hook, code, wParam, lParam);
    }

    public static List<string> TakeDropped()
    {
        lock (dropped)
        {
            var l = new List<string>(dropped);
            dropped.Clear();
            return l;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    class OpenFileName
    {
        public int structSize;
        public IntPtr dlgOwner = IntPtr.Zero;
        public IntPtr instance = IntPtr.Zero;
        public string filter;
        public string customFilter;
        public int maxCustFilter;
        public int filterIndex;
        public string file;
        public int maxFile;
        public string fileTitle;
        public int maxFileTitle;
        public string initialDir;
        public string title;
        public int flags;
        public short fileOffset;
        public short fileExtension;
        public string defExt;
        public IntPtr custData = IntPtr.Zero;
        public IntPtr hook = IntPtr.Zero;
        public string templateName;
        public IntPtr reservedPtr = IntPtr.Zero;
        public int reservedInt;
        public int flagsEx;
    }

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode)] static extern bool GetOpenFileName([In, Out] OpenFileName ofn);

    // 開くファイルを選ぶ。やめたら null。ext は "*.vrm" など。
    public static string OpenFile(string title, string label, string ext, string initialDir)
    {
        var ofn = new OpenFileName();
        ofn.structSize = Marshal.SizeOf(ofn);
        ofn.filter = $"{label} ({ext})\0{ext}\0すべてのファイル (*.*)\0*.*\0\0";
        ofn.filterIndex = 1;
        ofn.file = new string('\0', 1024);
        ofn.maxFile = ofn.file.Length;
        ofn.initialDir = initialDir;
        ofn.title = title;
        ofn.flags = 0x00080000 | 0x00001000 | 0x00000800 | 0x00000008;   // EXPLORER | FILEMUSTEXIST | PATHMUSTEXIST | NOCHANGEDIR
        return GetOpenFileName(ofn) ? ofn.file.TrimEnd('\0') : null;
    }

    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    static IntPtr UnityWindow()
    {
        IntPtr found = IntPtr.Zero;
        EnumThreadWindows(GetCurrentThreadId(), (h, _) =>
        {
            var sb = new StringBuilder(64);
            GetClassName(h, sb, sb.Capacity);
            if (sb.ToString() == "UnityWndClass") { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    // 窓をいつも手前に(VRChat の上に出しておく)
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
    public static void MessageBox(string caption, string text) => MessageBoxW(IntPtr.Zero, text, caption, 0x40);   // MB_ICONINFORMATION

    public static void SetTopmost(bool on)
    {
        var h = UnityWindow();
        if (h != IntPtr.Zero) SetWindowPos(h, new IntPtr(on ? -1 : -2), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);   // NOSIZE | NOMOVE | NOACTIVATE
    }

    // ---- VRChat の窓を前面に出す ----

    [DllImport("iphlpapi.dll")] static extern uint GetExtendedUdpTable(IntPtr table, ref int size, bool sort, int af, int tableClass, uint reserved);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc fn, IntPtr lParam);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint a, uint b, bool attach);
    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("kernel32.dll")] static extern uint GetCurrentProcessId();

    // UDP の port を開いているプロセス(IPv4 と IPv6。見つからなければ 0)
    static uint PidOfUdpPort(int port)
    {
        foreach (var (af, rowSize, portOff) in new[] { (2, 12, 4), (23, 28, 20) })   // AF_INET: addr, port, pid / AF_INET6: addr16, scope, port, pid
        {
            int size = 0;
            GetExtendedUdpTable(IntPtr.Zero, ref size, false, af, 1, 0);   // UDP_TABLE_OWNER_PID
            if (size <= 0) continue;
            var buf = Marshal.AllocHGlobal(size);
            try
            {
                if (GetExtendedUdpTable(buf, ref size, false, af, 1, 0) != 0) continue;
                int n = Marshal.ReadInt32(buf);
                for (int i = 0; i < n; i++)
                {
                    var row = buf + 4 + i * rowSize;
                    int raw = Marshal.ReadInt32(row + portOff);
                    int p = ((raw & 0xff) << 8) | ((raw >> 8) & 0xff);   // ネットワークの順
                    uint pid = (uint)Marshal.ReadInt32(row + portOff + 4);
                    if (p == port && pid != GetCurrentProcessId()) return pid;
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        return 0;
    }

    // OSC の受信ポート port を開いている VRChat の、見えている一番上の窓(なければ 0)
    public static IntPtr FindWindowByUdpPort(int port)
    {
        uint pid = PidOfUdpPort(port);
        if (pid == 0) return IntPtr.Zero;
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out var p);
            if (p == pid && IsWindowVisible(h) && GetWindow(h, 4) == IntPtr.Zero) { found = h; return false; }   // 4 = GW_OWNER
            return true;
        }, IntPtr.Zero);
        return found;
    }

    // 窓を前面に出す。前面の窓のスレッドに入力をつないでから(Windows の制限を避ける)。だめなら Alt を一度押して離してから
    public static bool BringToFront(IntPtr h)
    {
        if (h == IntPtr.Zero || !IsWindow(h)) return false;
        if (IsIconic(h)) ShowWindow(h, 9);   // SW_RESTORE
        var fg = GetForegroundWindow();
        uint me = GetCurrentThreadId();
        uint other = fg != IntPtr.Zero ? GetWindowThreadProcessId(fg, out _) : 0;
        if (other != 0 && other != me) AttachThreadInput(me, other, true);
        bool ok;
        try { ok = SetForegroundWindow(h); BringWindowToTop(h); }
        finally { if (other != 0 && other != me) AttachThreadInput(me, other, false); }
        if (!ok || GetForegroundWindow() != h)
        {
            keybd_event(0x12, 0, 0, UIntPtr.Zero);   // VK_MENU
            keybd_event(0x12, 0, 2, UIntPtr.Zero);   // KEYEVENTF_KEYUP
            ok = SetForegroundWindow(h);
            BringWindowToTop(h);
        }
        return ok;
    }

    // この窓(メインスレッドで呼ぶ)
    public static IntPtr MyWindow() => UnityWindow();
#else
    public static IntPtr FindWindowByUdpPort(int port) => IntPtr.Zero;
    public static bool BringToFront(IntPtr h) => false;
    public static IntPtr MyWindow() => IntPtr.Zero;
    public static void SetTopmost(bool on) { }
    public static void MessageBox(string caption, string text) { Debug.Log(caption + "\n" + text); }
    public static void InstallDrop() { }
    public static void UninstallDrop() { }
    public static List<string> TakeDropped() => new List<string>();
    public static string OpenFile(string title, string label, string ext, string initialDir) => null;
#endif
}
