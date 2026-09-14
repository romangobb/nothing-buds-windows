// Raw RFCOMM client over Winsock AF_BTH via P/Invoke (no NuGet needed).
// Live-verified against CMF Buds Pro 2: MAC 2C:BE:EE:70:3F:A7, channel 15.
// (.NET Socket.Connect rejects AF_BTH SocketAddress with WSAEINVAL, so we
//  call ws2_32 directly — same calls CPython's socket module makes.)

using System.Runtime.InteropServices;
using System.IO;

namespace NothingBuds.Bluetooth;

public sealed class BluetoothEndPoint : System.Net.EndPoint
{
    public string Mac { get; }
    public int Channel { get; }

    public BluetoothEndPoint(string mac, int channel)
    {
        Mac = mac; Channel = channel;
    }

    public override System.Net.Sockets.AddressFamily AddressFamily =>
        (System.Net.Sockets.AddressFamily)32; // AF_BTH

    public override System.Net.EndPoint Create(System.Net.SocketAddress socketAddress) => this;

    public override System.Net.SocketAddress Serialize()
    {
        var sa = new System.Net.SocketAddress(AddressFamily, 30);
        byte[] macBytes = ParseMac(Mac);
        Array.Reverse(macBytes); // btAddr is little-endian
        for (int i = 0; i < 6; i++) sa[2 + i] = macBytes[i];
        sa[2 + 6] = 0; sa[2 + 7] = 0;
        uint port = (uint)Channel;
        sa[2 + 24] = (byte)(port & 0xFF);
        sa[2 + 25] = (byte)((port >> 8) & 0xFF);
        sa[2 + 26] = (byte)((port >> 16) & 0xFF);
        sa[2 + 27] = (byte)((port >> 24) & 0xFF);
        return sa;
    }

    public static byte[] ParseMac(string mac)
    {
        string h = mac.Replace(":", "").Replace("-", "");
        if (h.Length != 12) throw new ArgumentException("MAC must be 12 hex digits", nameof(mac));
        byte[] b = new byte[6];
        for (int i = 0; i < 6; i++) b[i] = Convert.ToByte(h.Substring(i * 2, 2), 16);
        return b;
    }

    public static string Normalize(string mac)
    {
        byte[] b = ParseMac(mac);
        return string.Join(":", b.Select(x => x.ToString("X2")));
    }

    /// <summary>30-byte SOCKADDR_BTH: family(2) + btAddr-LE(8) + serviceGuid(16: zero) + port(4 LE).</summary>
    public static byte[] ToSockaddr(string mac, int channel)
    {
        byte[] sa = new byte[30];
        sa[0] = 32; sa[1] = 0;
        byte[] m = ParseMac(mac);
        Array.Reverse(m);
        Array.Copy(m, 0, sa, 2, 6);
        uint port = (uint)channel;
        sa[26] = (byte)(port & 0xFF);
        sa[27] = (byte)((port >> 8) & 0xFF);
        sa[28] = (byte)((port >> 16) & 0xFF);
        sa[29] = (byte)((port >> 24) & 0xFF);
        return sa;
    }
}

/// <summary>Blocking RFCOMM socket with background reader, frame reassembly and opId sequencing.</summary>
public sealed class RfcommClient : IDisposable
{
    private static readonly IntPtr InvalidSocket = new(-1);
    private static bool _wsaUp;
    private static readonly object _wsaLock = new();

    private IntPtr _s = InvalidSocket;
    private Thread? _reader;
    private volatile bool _running;
    private readonly List<byte> _rx = new();
    private readonly object _rxLock = new();
    private readonly object _txLock = new();
    private byte _opId;

    public event Action<ushort, byte[]>? FrameReceived;
    public event Action? Disconnected;

    public bool Connected => _s != InvalidSocket;
    public string? RemoteMac { get; private set; }
    public int Channel { get; private set; } = 15;

    private static void EnsureWsa()
    {
        lock (_wsaLock)
        {
            if (_wsaUp) return;
            byte[] data = new byte[512];
            int rc = Native.WSAStartup(0x0202, data);
            if (rc != 0) throw new IOException($"WSAStartup failed: {rc}");
            _wsaUp = true;
        }
    }

    public void Connect(string mac, int channel = 15, int timeoutMs = 8000)
    {
        Disconnect();
        EnsureWsa();
        mac = BluetoothEndPoint.Normalize(mac);

        IntPtr s = Native.socket(32, 1, 3); // AF_BTH, SOCK_STREAM, BTHPROTO_RFCOMM
        if (s == InvalidSocket)
            throw new IOException($"socket() failed: {Native.WSAGetLastError()}");

        // Non-blocking connect with timeout via select()
        int nb = 1;
        Native.ioctlsocket(s, -2147195266 /*FIONBIO*/, ref nb); // 0x8004667E as int
        byte[] sa = BluetoothEndPoint.ToSockaddr(mac, channel);
        int rc = Native.connect(s, sa, sa.Length);
        if (rc != 0)
        {
            int err = Native.WSAGetLastError();
            const int WouldBlock = 10035;
            if (err != WouldBlock)
            {
                Native.closesocket(s);
                throw new IOException($"RFCOMM connect to {mac}:{channel} failed (WSA {err})");
            }
            // Wait writable
            var w = new Native.fd_set(); w.fd_count = 1; w.fd_array = new IntPtr[64]; w.fd_array[0] = s;
            var tv = new Native.timeval { tv_sec = timeoutMs / 1000, tv_usec = (timeoutMs % 1000) * 1000 };
            int sel = Native.select(0, IntPtr.Zero, ref w, IntPtr.Zero, ref tv);
            if (sel <= 0)
            {
                Native.closesocket(s);
                throw new TimeoutException($"RFCOMM connect to {mac}:{channel} timed out");
            }
            int optErr = 0; int optLen = 4;
            Native.getsockopt(s, 0xFFFF /*SOL_SOCKET*/, 0x1007 /*SO_ERROR*/, ref optErr, ref optLen);
            if (optErr != 0)
            {
                Native.closesocket(s);
                throw new IOException($"RFCOMM connect to {mac}:{channel} failed (SO_ERROR {optErr})");
            }
        }
        nb = 0;
        Native.ioctlsocket(s, -2147195266, ref nb); // back to blocking
        // 1s recv timeout so the reader can notice Disconnect promptly
        int recvTimeout = 1000;
        Native.setsockopt(s, 0xFFFF, 0x1006 /*SO_RCVTIMEO*/, ref recvTimeout, 4);
        Native.setsockopt(s, 0xFFFF, 0x1005 /*SO_SNDTIMEO*/, ref recvTimeout, 4);

        _s = s;
        RemoteMac = mac; Channel = channel;
        _running = true;
        _reader = new Thread(ReadLoop) { IsBackground = true, Name = "rfcomm-reader" };
        _reader.Start();
    }

    public void Send(ushort command, ReadOnlySpan<byte> payload = default)
    {
        if (_s == InvalidSocket) throw new InvalidOperationException("Not connected");
        byte[] frame;
        lock (_txLock)
        {
            _opId++;
            if (_opId == 0 || _opId >= 250) _opId = 1;
            frame = NothingPacket.Build(command, payload, _opId);
        }
        lock (_txLock)
        {
            int off = 0;
            while (off < frame.Length)
            {
                byte[] chunk = frame.AsSpan(off).ToArray();
                int n = Native.send(_s, chunk, chunk.Length, 0);
                if (n <= 0) throw new IOException($"send failed: {Native.WSAGetLastError()}");
                off += n;
            }
        }
    }

    private void ReadLoop()
    {
        var buf = new byte[1024];
        try
        {
            while (_running && _s != InvalidSocket)
            {
                int n = Native.recv(_s, buf, buf.Length, 0);
                if (n > 0)
                {
                    List<(ushort, byte[])> frames = new();
                    lock (_rxLock)
                    {
                        for (int i = 0; i < n; i++) _rx.Add(buf[i]);
                        while (true)
                        {
                            while (_rx.Count > 0 && _rx[0] != 0x55) _rx.RemoveAt(0);
                            if (_rx.Count < 10) break;
                            int len = _rx[5];
                            int total = 8 + len + 2;
                            if (_rx.Count < total) break;
                            byte[] candidate = _rx.GetRange(0, total).ToArray();
                            _rx.RemoveRange(0, total);
                            if (NothingPacket.TryParse(candidate, out ushort cmd, out _, out byte[] pl))
                                frames.Add((cmd, pl));
                        }
                        if (_rx.Count > 4096) _rx.Clear();
                    }
                    foreach (var (cmd, pl) in frames)
                    {
                        try { FrameReceived?.Invoke(cmd, pl); } catch { }
                    }
                }
                else if (n == 0) break; // orderly shutdown
                else
                {
                    int err = Native.WSAGetLastError();
                    if (err == 10035 || err == 10060) continue; // timeout, keep waiting
                    break;
                }
            }
        }
        finally
        {
            _running = false;
            try { Disconnected?.Invoke(); } catch { }
        }
    }

    public void Disconnect()
    {
        _running = false;
        IntPtr s = Interlocked.Exchange(ref _s, InvalidSocket);
        if (s != InvalidSocket)
        {
            try { Native.shutdown(s, 2); } catch { }
            try { Native.closesocket(s); } catch { }
        }
        lock (_rxLock) _rx.Clear();
    }

    public void Dispose() => Disconnect();

    private static class Native
    {
        [DllImport("ws2_32.dll", SetLastError = true)]
        internal static extern int WSAStartup(ushort version, byte[] data);
        [DllImport("ws2_32.dll", SetLastError = true)]
        internal static extern IntPtr socket(int af, int type, int protocol);
        [DllImport("ws2_32.dll", SetLastError = true)]
        internal static extern int connect(IntPtr s, byte[] addr, int namelen);
        [DllImport("ws2_32.dll", SetLastError = true)]
        internal static extern int send(IntPtr s, byte[] buf, int len, int flags);
        [DllImport("ws2_32.dll", SetLastError = true)]
        internal static extern int recv(IntPtr s, byte[] buf, int len, int flags);
        [DllImport("ws2_32.dll", SetLastError = true)]
        internal static extern int closesocket(IntPtr s);
        [DllImport("ws2_32.dll", SetLastError = true)]
        internal static extern int shutdown(IntPtr s, int how);
        [DllImport("ws2_32.dll", SetLastError = true)]
        internal static extern int ioctlsocket(IntPtr s, int cmd, ref int argp);
        [DllImport("ws2_32.dll", SetLastError = true)]
        internal static extern int select(int nfds, IntPtr readfds, ref fd_set writefds, IntPtr exceptfds, ref timeval timeout);
        [DllImport("ws2_32.dll", SetLastError = true)]
        internal static extern int setsockopt(IntPtr s, int level, int optname, ref int optval, int optlen);
        [DllImport("ws2_32.dll", SetLastError = true)]
        internal static extern int getsockopt(IntPtr s, int level, int optname, ref int optval, ref int optlen);
        [DllImport("ws2_32.dll")]
        internal static extern int WSAGetLastError();

        [StructLayout(LayoutKind.Sequential)]
        internal struct timeval { public int tv_sec; public int tv_usec; }

        [StructLayout(LayoutKind.Sequential)]
        internal struct fd_set
        {
            public uint fd_count;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)]
            public IntPtr[] fd_array;
        }
    }
}
