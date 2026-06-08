using System.IO.Ports;

namespace ConDmsLockerCmd;

/// <summary>
/// Low-level RS-485 locker controller.
/// Protocol: 9600 baud 8N1, BCC = XOR of all payload bytes.
///
/// Commands:
///   Unlock single  : 8A [board] [lock] 11 [BCC]          lock = 0x01–0x32 (1–50)
///   Check single   : 80 [board] [lock] 33 [BCC]          lock = 0x01–0x32 (1–50)
///   Read all       : 80 [board] 00 33 [BCC]
///   Unlock multi   : 90 [board] [S1][S2][S3][S4][S5][S6][S7] [BCC]
///
/// NOTE: Board may prepend garbage bytes before the real response.
///   Real response always starts with the command head byte (0x80 or 0x8A).
///   ParseResponse() strips any leading garbage before the expected header.
/// </summary>
internal sealed class LockerController : IDisposable
{
    private readonly SerialPort _port;
    private readonly int _timeoutMs;

    public const byte MaxLockAddr = 0x32;

    public LockerController(string portName)
    {
        var cfg = LockerConfig.Instance;
        _timeoutMs = cfg.TimeoutMs;

        _port = new SerialPort(portName, cfg.BaudRate, Parity.None, 8, StopBits.One)
        {
            ReadTimeout = _timeoutMs,
            WriteTimeout = 2000
        };
        _port.Open();
    }

    // ----------------------------------------------------------
    // BCC = XOR of every byte in the payload (before BCC byte)
    // ----------------------------------------------------------
    private static byte ComputeBCC(byte[] data)
    {
        byte bcc = 0;
        foreach (var b in data) bcc ^= b;
        return bcc;
    }

    // ----------------------------------------------------------
    // Strip leading garbage bytes — find real response by header
    //
    // Board observed to prepend N garbage bytes (e.g. FA FD 99 99)
    // before the actual response. Real response always starts with
    // the expected header byte (0x80 for status/check, 0x8A for unlock).
    //
    // Strategy: scan forward until we find expectedHeader at position i
    //           where remaining bytes (buf.Length - i) >= minLen.
    // ----------------------------------------------------------
    private static byte[]? ParseResponse(byte[]? buf, byte expectedHeader, int minLen)
    {
        if (buf == null || buf.Length == 0) return null;

        for (int i = 0; i <= buf.Length - minLen; i++)
        {
            if (buf[i] == expectedHeader)
            {
                // Found header at position i — slice from here
                byte[] result = new byte[buf.Length - i];
                Array.Copy(buf, i, result, 0, result.Length);
                return result;
            }
        }

        return null; // header not found
    }

    // ----------------------------------------------------------
    // Unlock single channel
    // Send:    8A [board] [lock] 11 [BCC]
    // Reply:   8A [board] [lock] 00=Locked / 11=Unlocked [BCC]
    // ----------------------------------------------------------
    public byte[]? UnlockSingle(byte boardAddr, byte lockAddr)
    {
        byte[] payload = { 0x8A, boardAddr, lockAddr, 0x11 };
        byte[]? raw = SendAndReceive(AppendBCC(payload));
        return ParseResponse(raw, 0x8A, 5);
    }

    // ----------------------------------------------------------
    // Check door status (single channel)
    // Send:    80 [board] [lock] 33 [BCC]
    // Reply:   80 [board] [lock] 00=Closed / 11=Open [BCC]
    // ----------------------------------------------------------
    public byte[]? CheckSingle(byte boardAddr, byte lockAddr)
    {
        byte[] payload = { 0x80, boardAddr, lockAddr, 0x33 };
        byte[]? raw = SendAndReceive(AppendBCC(payload));
        return ParseResponse(raw, 0x80, 5);
    }

    // ----------------------------------------------------------
    // Read all 50 channel statuses on one board
    // Send:    80 [board] 00 33 [BCC]
    // Reply:   80 [board] [S1][S2][S3][S4][S5][S6][S7] 33 [BCC]  → 11 bytes
    // ----------------------------------------------------------
    public byte[]? ReadAllStatus(byte boardAddr)
    {
        byte[] payload = { 0x80, boardAddr, 0x00, 0x33 };
        byte[]? raw = SendAndReceive(AppendBCC(payload));
        return ParseResponse(raw, 0x80, 11);
    }

    // ----------------------------------------------------------
    // Unlock multiple channels at once (up to 50 channels via 7 bitmask bytes)
    // Send:    90 [board] [S1][S2][S3][S4][S5][S6][S7] [BCC]
    // ----------------------------------------------------------
    public byte[]? UnlockMultiple(byte boardAddr, byte s1, byte s2, byte s3,
                                                  byte s4, byte s5, byte s6, byte s7)
    {
        byte[] payload = { 0x90, boardAddr, s1, s2, s3, s4, s5, s6, s7 };
        byte[]? raw = SendAndReceive(AppendBCC(payload));
        return ParseResponse(raw, 0x90, 5);
    }

    // ----------------------------------------------------------
    // Convert channel list (1–50) → 7-byte bitmask tuple
    // ----------------------------------------------------------
    public static (byte s1, byte s2, byte s3, byte s4, byte s5, byte s6, byte s7)
        ChannelsToBitmask(int[] channels)
    {
        byte s1 = 0, s2 = 0, s3 = 0, s4 = 0, s5 = 0, s6 = 0, s7 = 0;
        foreach (int ch in channels)
        {
            if (ch >= 1 && ch <= 8) s1 |= (byte)(1 << (ch - 1));
            else if (ch >= 9 && ch <= 16) s2 |= (byte)(1 << (ch - 9));
            else if (ch >= 17 && ch <= 24) s3 |= (byte)(1 << (ch - 17));
            else if (ch >= 25 && ch <= 32) s4 |= (byte)(1 << (ch - 25));
            else if (ch >= 33 && ch <= 40) s5 |= (byte)(1 << (ch - 33));
            else if (ch >= 41 && ch <= 48) s6 |= (byte)(1 << (ch - 41));
            else if (ch >= 49 && ch <= 50) s7 |= (byte)(1 << (ch - 49));
        }
        return (s1, s2, s3, s4, s5, s6, s7);
    }

    // ----------------------------------------------------------
    // Parse ReadAllStatus response → list of OPEN channel numbers
    // Response after stripping garbage: 80 [board] S1..S7 33 [BCC] = 11 bytes
    // bit=0 → Open, bit=1 → Closed (with feedback wiring)
    // ----------------------------------------------------------
    public static List<int> ParseAllStatus(byte[] response)
    {
        var open = new List<int>();
        if (response == null || response.Length < 11) return open;

        byte[] s = new byte[7];
        for (int b = 0; b < 7; b++)
            s[b] = response[2 + b];

        for (int byteIdx = 0; byteIdx < 6; byteIdx++)
            for (int bit = 0; bit < 8; bit++)
                if ((s[byteIdx] >> bit & 1) == 0)
                    open.Add(byteIdx * 8 + bit + 1);

        for (int bit = 0; bit < 2; bit++)
            if ((s[6] >> bit & 1) == 0)
                open.Add(49 + bit);

        return open;
    }

    // ----------------------------------------------------------
    // Internal helpers
    // ----------------------------------------------------------
    private static byte[] AppendBCC(byte[] payload)
    {
        byte bcc = ComputeBCC(payload);
        var packet = new byte[payload.Length + 1];
        payload.CopyTo(packet, 0);
        packet[^1] = bcc;
        return packet;
    }

    private byte[]? SendAndReceive(byte[] packet, int timeoutMs = 0)
    {
        int wait = timeoutMs > 0 ? timeoutMs : _timeoutMs;
        _port.DiscardInBuffer();
        _port.Write(packet, 0, packet.Length);
        Thread.Sleep(wait);

        int available = _port.BytesToRead;
        if (available == 0) return null;

        byte[] buf = new byte[available];
        _port.Read(buf, 0, available);
        return buf;
    }

    public void Dispose()
    {
        if (_port.IsOpen) _port.Close();
        _port.Dispose();
    }
}