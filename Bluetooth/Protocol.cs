// Nothing/CMF earbuds wire protocol.
// Reverse-engineered from radiance-project/ear-web (GPLv3) + live-verified
// against CMF Buds Pro 2 (B172, fw 1.0.1.74) over RFCOMM channel 15.
//
// Framing: header [0x55,0x60,0x01,cmdLo,cmdHi,len,0x00,opId] + payload + CRC16-Modbus LE.
// SET=0xF0xx, GET=0xC0xx, RESP=0x40xx (GET-0x8000), EVENT=0xE0xx.

namespace NothingBuds.Bluetooth;

public static class Crc16
{
    public static ushort Modbus(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++)
                crc = (crc & 1) != 0 ? (ushort)((crc >> 1) ^ 0xA001) : (ushort)(crc >> 1);
        }
        return crc;
    }
}

public static class NothingPacket
{
    public static byte[] Build(ushort command, ReadOnlySpan<byte> payload, byte opId)
    {
        byte[] buf = new byte[8 + payload.Length + 2];
        buf[0] = 0x55; buf[1] = 0x60; buf[2] = 0x01;
        buf[3] = (byte)(command & 0xFF);
        buf[4] = (byte)((command >> 8) & 0xFF);
        buf[5] = (byte)payload.Length;
        buf[6] = 0x00; buf[7] = opId;
        payload.CopyTo(buf.AsSpan(8));
        ushort crc = Crc16.Modbus(buf.AsSpan(0, 8 + payload.Length));
        buf[8 + payload.Length] = (byte)(crc & 0xFF);
        buf[8 + payload.Length + 1] = (byte)((crc >> 8) & 0xFF);
        return buf;
    }

    public static bool TryParse(ReadOnlySpan<byte> frame, out ushort command, out byte opId, out byte[] payload)
    {
        command = 0; opId = 0; payload = [];
        if (frame.Length < 10 || frame[0] != 0x55) return false;
        int len = frame[5];
        if (frame.Length < 8 + len + 2) return false;
        // Asymmetry (live-verified): requests CRC-cover header+payload,
        // but buds' responses CRC-cover the payload only (empty => 0xFFFF).
        ushort want = (ushort)(frame[8 + len] | (frame[8 + len + 1] << 8));
        if (Crc16.Modbus(frame.Slice(8, len)) != want) return false;
        command = (ushort)(frame[3] | (frame[4] << 8));
        opId = frame[7];
        payload = frame.Slice(8, len).ToArray();
        return true;
    }
}

/// <summary>Command IDs (decimal in comments for ear-web cross-ref).</summary>
public static class Cmd
{
    // GET
    public const ushort Battery = 0xC007;      // 49159
    public const ushort InEarRead = 0xC00E;    // 49166
    public const ushort GetGesture = 0xC018;   // 49176
    public const ushort AncRead = 0xC01E;      // 49182
    public const ushort LegacyEqRead = 0xC01F; // 49183 (legacy presets)
    public const ushort PersonalAncRead = 0xC020; // 49184 (Ear 2 only)
    public const ushort LatencyRead = 0xC041;  // 49217
    public const ushort Firmware = 0xC042;     // 49218
    public const ushort AdvancedEqRead = 0xC04C; // 49228
    public const ushort BassRead = 0xC04E;     // 49230
    public const ushort ListeningRead = 0xC050; // 49232 (B172/B168)
    // SET
    public const ushort Ring = 0xF002;         // 61442
    public const ushort SetGesture = 0xF003;   // 61443
    public const ushort SetInEar = 0xF004;     // 61444
    public const ushort SetAnc = 0xF00F;       // 61455
    public const ushort SetLegacyEq = 0xF010;  // 61456 (legacy presets)
    public const ushort SetPersonalAnc = 0xF011; // 61457 (Ear 2 only)
    public const ushort EarFitTest = 0xF014;   // 61460
    public const ushort SetListening = 0xF01D; // 61469 (B172/B168)
    public const ushort SetLatency = 0xF040;   // 61504
    public const ushort SetBass = 0xF051;      // 61521
    // RESP / EVENT
    public const ushort RespBattery = 0x4007;  // 16391
    public const ushort EventBattery = 0xE001; // 57345
    public const ushort RespInEar = 0x400E;    // 16398
    public const ushort RespPersonalAnc = 0x4020; // 16416
    public const ushort RespAnc = 0x401E;      // 16414
    public const ushort EventAnc = 0xE003;     // 57347
    public const ushort RespGesture = 0x4018;  // 16408
    public const ushort RespLatency = 0x4041;  // 16449
    public const ushort RespFirmware = 0x4042; // 16450
    public const ushort RespBass = 0x404E;     // 16462
    public const ushort RespListening = 0x4050; // 16464 (B172)
    public const ushort RespLegacyEq = 0x401F; // 16415
    public const ushort RespAdvancedEq = 0x404C; // 16460
    public const ushort EventEarFit = 0xE00D;  // 57357
}

/// <summary>B172 (CMF Buds Pro 2) ANC wire values. L1..L6 per ear-web.</summary>
public enum AncMode { Off = 1, Transparency = 2, Low = 3, High = 4, Mid = 5, Adaptive = 6 }

public static class AncWire
{
    public static byte ToWire(AncMode m) => m switch
    {
        AncMode.Off => 0x05,
        AncMode.Transparency => 0x07,
        AncMode.Low => 0x03,
        AncMode.High => 0x01,
        AncMode.Mid => 0x02,
        AncMode.Adaptive => 0x04,
        _ => 0x01,
    };
    public static AncMode FromWire(byte raw) => raw switch
    {
        0x05 => AncMode.Off,
        0x07 => AncMode.Transparency,
        0x03 => AncMode.Low,
        0x01 => AncMode.High,
        0x02 => AncMode.Mid,
        0x04 => AncMode.Adaptive,
        _ => AncMode.High,
    };
}

/// <summary>B172 listening-mode / EQ preset (espeon.js).</summary>
public enum ListeningPreset
{
    Dirac = 0, Rock = 1, Electronic = 2, Pop = 3, Vocals = 4, Classical = 5, Custom = 6,
}

public static class ListeningLabels
{
    public static string Label(ListeningPreset p) => p switch
    {
        ListeningPreset.Dirac => "Dirac OPTEO",
        ListeningPreset.Rock => "Rock",
        ListeningPreset.Electronic => "Electronic",
        ListeningPreset.Pop => "Pop",
        ListeningPreset.Vocals => "Enhance vocals",
        ListeningPreset.Classical => "Classical",
        ListeningPreset.Custom => "Custom",
        _ => p.ToString(),
    };
}
