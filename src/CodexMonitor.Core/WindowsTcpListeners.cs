using System.Buffers.Binary;
using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;

namespace CodexMonitor.Core;

public sealed record OwnedTcpListener(IPAddress Address, int Port, int ProcessId);

public static class WindowsTcpListeners
{
    public static IReadOnlyList<OwnedTcpListener> Read()
    {
        if (!OperatingSystem.IsWindows()) return [];
        return ReadFamily(2, false).Concat(ReadFamily(23, true)).ToArray();
    }
    private static IReadOnlyList<OwnedTcpListener> ReadFamily(int family, bool ipv6)
    {
        uint size = 0;
        var status = GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, 3, 0);
        if (status is 50 or 232) return []; // Unsupported address family or empty table.
        if (status != 122 && status != 0) throw new Win32Exception((int)status);
        for (int retry = 0; retry < 4 && size > 0 && size <= 16 * 1024 * 1024; retry++)
        {
            var buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                status = GetExtendedTcpTable(buffer, ref size, false, family, 3, 0);
                if (status == 122) continue;
                if (status != 0) throw new Win32Exception((int)status);
                var bytes = new byte[size]; Marshal.Copy(buffer, bytes, 0, bytes.Length);
                return Decode(bytes, ipv6);
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        throw new IOException("Unable to read the local TCP listener table");
    }
    public static IReadOnlyList<OwnedTcpListener> Decode(ReadOnlySpan<byte> table, bool ipv6)
    {
        if (table.Length < 4) throw new InvalidDataException("TCP table header is incomplete");
        var count = BinaryPrimitives.ReadUInt32LittleEndian(table);
        var rowSize = ipv6 ? 56 : 24;
        if (count > (table.Length - 4) / rowSize) throw new InvalidDataException("TCP table is truncated");
        var result = new List<OwnedTcpListener>();
        for (int i = 0; i < count; i++)
        {
            var row = table.Slice(4 + i * rowSize, rowSize);
            if (BinaryPrimitives.ReadUInt32LittleEndian(row.Slice(ipv6 ? 48 : 0, 4)) != 2) continue;
            var address = new IPAddress(row.Slice(ipv6 ? 0 : 4, ipv6 ? 16 : 4));
            var port = BinaryPrimitives.ReadUInt16BigEndian(row.Slice(ipv6 ? 20 : 8, 2));
            var pid = BinaryPrimitives.ReadUInt32LittleEndian(row.Slice(ipv6 ? 52 : 20, 4));
            if (pid <= int.MaxValue) result.Add(new(address, port, (int)pid));
        }
        return result;
    }
    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref uint size, [MarshalAs(UnmanagedType.Bool)] bool order, int addressFamily, int tableClass, uint reserved);
}
