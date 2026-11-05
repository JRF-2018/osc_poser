// Windows の機能: ファイルのドラッグアンドドロップ、ファイルを開くダイアログ、いつも手前。(VrmPoseViewer のものに手前を足した)
// ドロップは、Unity の窓に DragAcceptFiles を付け、メインスレッドのメッセージを WH_GETMESSAGE のフックで見て
// WM_DROPFILES を拾う。
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
    public static void SetTopmost(bool on)
    {
        var h = UnityWindow();
        if (h != IntPtr.Zero) SetWindowPos(h, new IntPtr(on ? -1 : -2), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);   // NOSIZE | NOMOVE | NOACTIVATE
    }
#else
    public static void SetTopmost(bool on) { }
    public static void InstallDrop() { }
    public static void UninstallDrop() { }
    public static List<string> TakeDropped() => new List<string>();
    public static string OpenFile(string title, string label, string ext, string initialDir) => null;
#endif
}
