using System.IO.Ports;

namespace ConDmsLockerCmd;

/// <summary>
/// Low-level RS-485 locker controller.
/// Protocol: 9600 baud 8N1, BCC = XOR of all payload bytes.
///
/// Commands:
///   Unlock single  : 8A [board] [lock] 11 [BCC]
///   Check single   : 80 [board] [lock] 33 [BCC]
///   Read all       : 80 [board] 00 33 [BCC]
///   Unlock multi   : 90 [board] [s1] [s2] [s3] [BCC]
/// </summary>
internal sealed class LockerController : IDisposable
{
    private readonly SerialPort _port;

    private readonly int _timeoutMs;

    public LockerController(string portName)
    {
        var cfg = LockerConfig.Instance;
        _timeoutMs = cfg.TimeoutMs;

        _port = new SerialPort(portName, cfg.BaudRate, Parity.None, 8, StopBits.One)
        {
            ReadTimeout  = _timeoutMs,
            WriteTimeout = 2000
        };
        _port.Open();
    }

    // ----------------------------------------------------------
    // BCC = XOR of every byte in the command (before BCC byte)
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
    // Reply:   8A [board] [lock] 00/11 [BCC]
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
    // ----------------------------------------------------------
    public byte[]? CheckSingle(byte boardAddr, byte lockAddr)
    {
        byte[] payload = { 0x80, boardAddr, lockAddr, 0x33 };
        return SendAndReceive(AppendBCC(payload));
    }

    // ----------------------------------------------------------
    // Read all channel status on one board
    // Send:    80 [board] 00 33 [BCC]
    // Reply:   80 [board] [S1] [S2] [S3] 33 [BCC]
    //          S1 = ch 1-8, S2 = ch 9-16, S3 = ch 17-24  (bit=1 → Open)
    // ----------------------------------------------------------
    public byte[]? ReadAllStatus(byte boardAddr)
    {
        byte[] payload = { 0x80, boardAddr, 0x00, 0x33 };
        return SendAndReceive(AppendBCC(payload));
    }

    // ----------------------------------------------------------
    // Unlock multiple channels at once
    // Send:    90 [board] [S1] [S2] [S3] [BCC]
    // ----------------------------------------------------------
    public byte[]? UnlockMultiple(byte boardAddr, byte s1, byte s2, byte s3)
    {
        byte[] payload = { 0x90, boardAddr, s1, s2, s3 };
        return SendAndReceive(AppendBCC(payload));
    }

    // ----------------------------------------------------------
    // Convert channel list → bitmask tuple (s1, s2, s3)
    // ch 1-8 → s1, ch 9-16 → s2, ch 17-24 → s3
    // ----------------------------------------------------------
    public static (byte s1, byte s2, byte s3) ChannelsToBitmask(int[] channels)
    {
        byte s1 = 0, s2 = 0, s3 = 0;
        foreach (int ch in channels)
        {
            if      (ch >= 1  && ch <= 8)  s1 |= (byte)(1 << (ch - 1));
            else if (ch >= 9  && ch <= 16) s2 |= (byte)(1 << (ch - 9));
            else if (ch >= 17 && ch <= 24) s3 |= (byte)(1 << (ch - 17));
        }
        return (s1, s2, s3);
    }

    // ----------------------------------------------------------
    // Parse ReadAllStatus response into open channel list
    // ----------------------------------------------------------
    public static List<int> ParseAllStatus(byte[] response)
    {
        // response: 80 [board] [S1] [S2] [S3] 33 [BCC]  → 7 bytes
        var open = new List<int>();
        if (response == null || response.Length < 7) return open;

        byte s1 = response[2], s2 = response[3], s3 = response[4];
        for (int i = 0; i < 8; i++)
        {
            if ((s1 >> i & 1) == 1) open.Add(i + 1);
            if ((s2 >> i & 1) == 1) open.Add(i + 9);
            if ((s3 >> i & 1) == 1) open.Add(i + 17);
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
        // Flush stale bytes
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
