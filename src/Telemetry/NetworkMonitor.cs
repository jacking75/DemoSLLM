using System.Net;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace LocalMind.Telemetry;

[SupportedOSPlatform("windows")]
public static class NetworkMonitor
{
    // Owner PID tables include IPv4 and IPv6. UDP has no remote endpoint, so
    // non-loopback local bindings are conservatively counted, not bytes sent.
    public static int CountExternalSockets(int pid) => ReadTable(pid, false, false) + ReadTable(pid, false, true)
        + ReadTable(pid, true, false) + ReadTable(pid, true, true);
    private static int ReadTable(int pid, bool udp, bool ipv6)
    {
        uint size = 0; int family = ipv6 ? 23 : 2;
        uint result = udp ? GetExtendedUdpTable(IntPtr.Zero, ref size, false, family, 1, 0)
            : GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, 5, 0);
        if (result != 122 && result != 0) throw new IOException($"소켓 표 조회 실패 {result}");
        for (int attempt = 0; attempt < 3; attempt++)
        {
            IntPtr buffer = Marshal.AllocHGlobal(checked((int)size));
            try
            {
                result = udp ? GetExtendedUdpTable(buffer, ref size, false, family, 1, 0)
                    : GetExtendedTcpTable(buffer, ref size, false, family, 5, 0);
                if (result == 122) continue;
                if (result != 0) throw new IOException($"소켓 표 조회 실패 {result}");
                int rows = Marshal.ReadInt32(buffer), count = 0;
                int rowSize = udp ? (ipv6 ? 28 : 12) : (ipv6 ? 56 : 24);
                if (rows < 0 || 4L + (long)rows * rowSize > size) throw new InvalidDataException("소켓 표 크기 불일치이다.");
                for (int i = 0; i < rows; i++)
                {
                    var row = IntPtr.Add(buffer, 4 + i * rowSize);
                    if (Marshal.ReadInt32(row, rowSize - 4) != pid) continue;
                    int localOffset = !udp && !ipv6 ? 4 : 0;
                    byte[] address = new byte[ipv6 ? 16 : 4];
                    Marshal.Copy(IntPtr.Add(row, localOffset), address, 0, address.Length);
                    if (!IPAddress.IsLoopback(new IPAddress(address))) { count++; continue; }
                    if (!udp)
                    {
                        Marshal.Copy(IntPtr.Add(row, ipv6 ? 24 : 12), address, 0, address.Length);
                        if (!address.All(b => b == 0) && !IPAddress.IsLoopback(new IPAddress(address))) count++;
                    }
                }
                return count;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        throw new IOException("소켓 표가 계속 변경되어 미확인이다.");
    }
    public static string InternetStatus()
    {
        object? manager = null;
        try
        {
            var type = Type.GetTypeFromCLSID(new Guid("DCB00C01-570F-4A9B-8D69-199FDBA5723B"));
            manager = Activator.CreateInstance(type!);
            return ((dynamic)manager!).IsConnectedToInternet ? "연결 (Windows 판정)" : "끊김 (Windows 판정)";
        }
        catch { return "미확인"; }
        finally { if (manager is not null && Marshal.IsComObject(manager)) Marshal.ReleaseComObject(manager); }
    }
    [DllImport("iphlpapi.dll")] private static extern uint GetExtendedTcpTable(IntPtr table, ref uint size, bool order, int family, int tableClass, uint reserved);
    [DllImport("iphlpapi.dll")] private static extern uint GetExtendedUdpTable(IntPtr table, ref uint size, bool order, int family, int tableClass, uint reserved);
}
