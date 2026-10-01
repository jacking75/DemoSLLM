using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace LocalMind.Backend;

public sealed class WindowsJob : IDisposable
{
    private readonly SafeFileHandle handle;
    public WindowsJob()
    {
        handle = CreateJobObjectW(IntPtr.Zero, null);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var info = new ExtendedLimit { Basic = new BasicLimit { LimitFlags = 0x2000 } };
        if (!SetInformationJobObject(handle, 9, ref info, (uint)Marshal.SizeOf<ExtendedLimit>()))
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error);
        }
    }

    public Process Start(string executable, IEnumerable<string> arguments, IReadOnlyDictionary<string, string>? environmentOverrides = null)
    {
        var startup = new StartupInfo { cb = (uint)Marshal.SizeOf<StartupInfo>() };
        var line = new StringBuilder(string.Join(" ", new[] { executable }.Concat(arguments).Select(Quote)));
        IntPtr environment = IntPtr.Zero;
        ProcessInfo info;
        try
        {
            if (environmentOverrides is not null)
            {
                var values = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
                    .ToDictionary(e => (string)e.Key, e => (string)e.Value!, StringComparer.OrdinalIgnoreCase);
                foreach (var pair in environmentOverrides) values[pair.Key] = pair.Value;
                var block = string.Join('\0', values.OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase).Select(e => e.Key + "=" + e.Value)) + "\0\0";
                environment = Marshal.StringToHGlobalUni(block);
            }
            if (!CreateProcessW(executable, line, IntPtr.Zero, IntPtr.Zero, false, 0x08000404,
                environment, Path.GetDirectoryName(executable), ref startup, out info))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally { if (environment != IntPtr.Zero) Marshal.FreeHGlobal(environment); }
        try
        {
            // Assign while suspended: the server cannot run before cleanup ownership exists.
            if (!AssignProcessToJobObject(handle, info.Process)) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (ResumeThread(info.Thread) == uint.MaxValue) throw new Win32Exception(Marshal.GetLastWin32Error());
            return Process.GetProcessById((int)info.ProcessId);
        }
        catch { TerminateProcess(info.Process, 1); throw; }
        finally { CloseHandle(info.Thread); CloseHandle(info.Process); }
    }
    public static string Quote(string value)
    {
        var result = new StringBuilder("\"");
        int slashes = 0;
        foreach (char ch in value)
        {
            if (ch == '\\') { slashes++; continue; }
            result.Append('\\', ch == '"' ? slashes * 2 + 1 : slashes);
            result.Append(ch); slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }
    public void Dispose() => handle.Dispose();

    [StructLayout(LayoutKind.Sequential)] private struct BasicLimit
    {
        public long ProcessTime, JobTime; public uint LimitFlags;
        public UIntPtr MinWorking, MaxWorking; public uint ActiveProcessLimit;
        public UIntPtr Affinity; public uint Priority, Scheduling;
    }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong A, B, C, D, E, F; }
    [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimit
    {
        public BasicLimit Basic; public IoCounters Io;
        public UIntPtr ProcessMemory, JobMemory, PeakProcess, PeakJob;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct StartupInfo
    {
        public uint cb; public string? Reserved, Desktop, Title;
        public uint X, Y, XSize, YSize, XChars, YChars, Fill, Flags;
        public ushort ShowWindow, Reserved2; public IntPtr ReservedPtr, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInfo
    { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateJobObjectW(IntPtr security, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(SafeFileHandle job, int type, ref ExtendedLimit info, uint length);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateProcessW(string app, StringBuilder command, IntPtr processSecurity, IntPtr threadSecurity, bool inherit, uint flags, IntPtr environment, string? directory, ref StartupInfo startup, out ProcessInfo info);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern bool TerminateProcess(IntPtr process, uint code);
}
