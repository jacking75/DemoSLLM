using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;

namespace LocalMind.Wpf;

internal static class ShortcutClipboard
{
    public static DataObject Snapshot()
    {
        var snapshot = new DataObject(); var original = Clipboard.GetDataObject();
        if (original is not null)
            foreach (var format in original.GetFormats(false))
            {
                var value = original.GetData(format, false);
                if (value is null) throw new IOException($"클립보드 형식 {format}의 원본을 읽지 못해 복사를 시작하지 않는다.");
                snapshot.SetData(format, value, false);
            }
        return snapshot;
    }
    public static void Restore(DataObject snapshot)
    {
        if (snapshot.GetFormats(false).Length == 0) Clipboard.Clear();
        else Clipboard.SetDataObject(snapshot, true);
    }
    public static async Task<string> CopySelection(IntPtr source, CancellationToken ct)
    {
        // Do not release keys held by the user. Wait for the hotkey's modifiers to be released.
        var until = DateTime.UtcNow.AddSeconds(2);
        while ((GetAsyncKeyState(0x11) & 0x8000) != 0 || (GetAsyncKeyState(0x12) & 0x8000) != 0 || (GetAsyncKeyState(0x20) & 0x8000) != 0)
        {
            ct.ThrowIfCancellationRequested(); if (DateTime.UtcNow >= until) throw new IOException("단축키를 놓은 뒤 다시 실행해야 한다."); await Task.Delay(30, ct);
        }
        if (source == IntPtr.Zero || GetForegroundWindow() != source) throw new IOException("원래 입력 창의 포커스가 바뀌어 읽기를 중단했다.");
        var saved = Snapshot(); var before = GetClipboardSequenceNumber(); uint copied = before;
        bool injected = false;
        try
        {
            var keys = new[] { Key(0x11), Key(0x43), Key(0x43, true), Key(0x11, true) };
            var sent = SendInput((uint)keys.Length, keys, Marshal.SizeOf<Input>());
            if (sent != keys.Length)
            {
                var error = Marshal.GetLastWin32Error();
                var release = new[] { Key(0x43, true), Key(0x11, true) }; SendInput(2, release, Marshal.SizeOf<Input>());
                throw new Win32Exception(error, "Ctrl+C 입력 전달이 실패했다. 권한이나 데스크톱 상태를 확인해야 한다.");
            }
            injected = true; until = DateTime.UtcNow.AddSeconds(2);
            while ((copied = GetClipboardSequenceNumber()) == before)
            {
                ct.ThrowIfCancellationRequested(); if (DateTime.UtcNow >= until) throw new IOException("선택 텍스트를 가져오지 못했다. 텍스트 선택과 앱 권한을 확인해야 한다."); await Task.Delay(30, ct);
            }
            if (GetForegroundWindow() != source) throw new IOException("복사 도중 입력 창이 바뀌어 결과를 사용하지 않는다.");
            var text = Clipboard.ContainsText(TextDataFormat.UnicodeText) ? Clipboard.GetText(TextDataFormat.UnicodeText) : "";
            if (GetClipboardSequenceNumber() != copied) throw new IOException("클립보드가 다른 작업으로 변경돼 읽기를 중단했다.");
            if (string.IsNullOrWhiteSpace(text)) throw new IOException("선택된 텍스트가 없다.");
            if (text.Length > 2000) throw new IOException("선택 텍스트 상한은 2000자이다. 짧은 문단을 선택해야 한다.");
            return text;
        }
        finally
        {
            if (injected)
            {
                var current = GetClipboardSequenceNumber();
                if (current == copied || copied == before && current == before) Restore(saved);
                else throw new IOException("클립보드가 다른 작업으로 변경돼 덮어쓰지 않았다. 기존 클립보드 복원은 미완료이다.");
            }
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public InputUnion Value; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion { [FieldOffset(0)] public Keyboard Keyboard; [FieldOffset(0)] public Mouse Mouse; }
    [StructLayout(LayoutKind.Sequential)] private struct Keyboard { public ushort Key, Scan; public uint Flags, Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct Mouse { public int X, Y; public uint Data, Flags, Time; public UIntPtr Extra; }
    private static Input Key(ushort key, bool up = false) => new() { Type = 1, Value = new() { Keyboard = new() { Key = key, Flags = up ? 2u : 0u } } };
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] input, int size);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
    public static uint Sequence => GetClipboardSequenceNumber();
}
