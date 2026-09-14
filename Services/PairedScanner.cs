// Paired-device enumeration via BTHPORT registry (no admin, no NuGet)
// + RFCOMM handshake probe to confirm a Nothing/CMF control channel.

using Microsoft.Win32;
using System.Text;

namespace NothingBuds.Services;

public sealed record ScannedDevice(string Mac, string Name);

public static class PairedScanner
{
    private const string DevicesKey = @"SYSTEM\CurrentControlSet\Services\BTHPORT\Parameters\Devices";

    public static List<ScannedDevice> GetPairedDevices()
    {
        var list = new List<ScannedDevice>();
        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(DevicesKey);
            if (root == null) return list;
            foreach (string sub in root.GetSubKeyNames())
            {
                string? mac = NormalizeSubkey(sub);
                if (mac == null) continue;
                string name = mac;
                try
                {
                    using var dev = root.OpenSubKey(sub);
                    object? v = dev?.GetValue("Name");
                    if (v is byte[] bytes && bytes.Length > 0)
                        name = DecodeName(bytes);
                    name = string.IsNullOrWhiteSpace(name) ? mac : name.Trim();
                }
                catch { }
                list.Add(new ScannedDevice(mac, name));
            }
        }
        catch { }
        return list;
    }

    private static string? NormalizeSubkey(string sub)
    {
        string h = sub.Trim().Replace(":", "").Replace("-", "").ToLowerInvariant();
        if (h.Length != 12 || !h.All(c => Uri.IsHexDigit(c))) return null;
        return string.Join(":", Enumerable.Range(0, 6).Select(i => h.Substring(i * 2, 2))).ToUpperInvariant();
    }

    private static string DecodeName(byte[] bytes)
    {
        int n = Array.IndexOf(bytes, (byte)0);
        if (n >= 0) bytes = bytes[..n];
        // BTHPORT stores ANSI/UTF-8; try UTF-8 then fallback Latin1
        try
        {
            string s = Encoding.UTF8.GetString(bytes);
            if (!string.IsNullOrWhiteSpace(s) && !s.Contains('\ufffd')) return s;
        }
        catch { }
        return Encoding.Latin1.GetString(bytes);
    }
}
