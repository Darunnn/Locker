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
/// Read All response format (from spec):
///   80 [board] [S1][S2][S3][S4][S5][S6][S7] 33 [BCC]  → 11 bytes
///   S1 = ch 1-8, S2 = ch 9-16, S3 = ch 17-24,
///   S4 = ch 25-32, S5 = ch 33-40, S6 = ch 41-48,
///   S7 = ch 49-50 (only bits 0-1 used)
///
/// NOTE on Read All status bits (no feedback wiring):
///   When feedback lines (pins 6/7) are NOT connected, board returns 0xFF by default.
///   bit=0 → Open (relay triggered), bit=1 → Closed (inverted)
/// </summary>
internal sealed class LockerController : IDisposable
{
    private readonly SerialPort _port;
    private readonly int _timeoutMs;

    // Max lock address per spec: 0x32 = 50
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
    // Unlock single channel
    // Send:    8A [board] [lock] 11 [BCC]
    // Reply:   8A [board] [lock] 00=Locked / 11=Unlocked [BCC]
    // lock: 0x01–0x32 (channel 1–50)
    // ----------------------------------------------------------
    public byte[]? UnlockSingle(byte boardAddr, byte lockAddr)
    {
        byte[] payload = { 0x8A, boardAddr, lockAddr, 0x11 };
        return SendAndReceive(AppendBCC(payload));
    }

    // ----------------------------------------------------------
    // Check door status (single channel)
    // Send:    80 [board] [lock] 33 [BCC]
    // Reply:   80 [board] [lock] 00=Closed / 11=Open [BCC]
    // lock: 0x01–0x32 (channel 1–50)
    // ----------------------------------------------------------
    public byte[]? CheckSingle(byte boardAddr, byte lockAddr)
    {
        byte[] payload = { 0x80, boardAddr, lockAddr, 0x33 };
        return SendAndReceive(AppendBCC(payload));
    }

    // ----------------------------------------------------------
    // Read all 50 channel statuses on one board
    // Send:    80 [board] 00 33 [BCC]
    // Reply:   80 [board] [S1][S2][S3][S4][S5][S6][S7] 33 [BCC]  → 11 bytes
    //   S1 = ch 1-8   S2 = ch 9-16  S3 = ch 17-24
    //   S4 = ch 25-32 S5 = ch 33-40 S6 = ch 41-48
    //   S7 = ch 49-50 (bits 0-1 only)
    // Without feedback wiring: bit=0 → Open, bit=1 → Closed
    // ----------------------------------------------------------
    public byte[]? ReadAllStatus(byte boardAddr)
    {
        byte[] payload = { 0x80, boardAddr, 0x00, 0x33 };
        return SendAndReceive(AppendBCC(payload));
    }

    // ----------------------------------------------------------
    // Unlock multiple channels at once (up to 50 channels via 7 bitmask bytes)
    // Send:    90 [board] [S1][S2][S3][S4][S5][S6][S7] [BCC]
    // ----------------------------------------------------------
    public byte[]? UnlockMultiple(byte boardAddr, byte s1, byte s2, byte s3,
                                                  byte s4, byte s5, byte s6, byte s7)
    {
        byte[] payload = { 0x90, boardAddr, s1, s2, s3, s4, s5, s6, s7 };
        return SendAndReceive(AppendBCC(payload));
    }

    // ----------------------------------------------------------
    // Convert channel list (1–50) → 7-byte bitmask tuple
    //   ch 1-8   → s1   ch 9-16  → s2   ch 17-24 → s3
    //   ch 25-32 → s4   ch 33-40 → s5   ch 41-48 → s6
    //   ch 49-50 → s7
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
    // Response: 80 [board] [S1..S7] 33 [BCC]  → 11 bytes
    //
    // Without feedback wiring: bit=0 → Open (board returns 0xFF default)
    // ----------------------------------------------------------
    public static List<int> ParseAllStatus(byte[] response)
    {
        var open = new List<int>();
        // Minimum 11 bytes: 80 [board] S1 S2 S3 S4 S5 S6 S7 33 [BCC]
        if (response == null || response.Length < 11) return open;

        byte[] s = new byte[7];
        for (int b = 0; b < 7; b++)
            s[b] = response[2 + b];  // S1–S7 at response[2]–response[8]

        // ch 1–48: full 8 bits each byte
        for (int byteIdx = 0; byteIdx < 6; byteIdx++)
        {
            for (int bit = 0; bit < 8; bit++)
            {
                int ch = byteIdx * 8 + bit + 1;
                // bit=0 → Open (inverted: no feedback wiring)
                if ((s[byteIdx] >> bit & 1) == 0)
                    open.Add(ch);
            }
        }
        // ch 49–50: only bits 0-1 of s7
        for (int bit = 0; bit < 2; bit++)
        {
            int ch = 49 + bit;
            if ((s[6] >> bit & 1) == 0)
                open.Add(ch);
        }

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