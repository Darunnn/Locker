using System.IO.Ports;

namespace ConDmsLockerCmd;

/// <summary>
/// Low-level RS-485 locker controller.
/// Protocol: 9600 baud 8N1, BCC = XOR of all payload bytes.
///
/// Commands (ตาม spec PDF):
///   Unlock single  : 8A [board] [lock] 11 [BCC]       lock = 0x01–0x18 (CH1–24)
///   Check single   : 80 [board] [lock] 33 [BCC]       lock = 0x01–0x18
///   Read all       : 80 [board] 00 33 [BCC]           lock = 0x00 (ALL)
///   Unlock multi   : 90 [board] S1 S2 S3 [BCC]        ← 3 bytes เท่านั้น! (CH1–24)
///
/// Unlock single reply:
///   8A [board] [lock] 00 [BCC] = Locked (ยังปิดอยู่)
///   8A [board] [lock] 11 [BCC] = Unlocked (เปิดสำเร็จ)
///
/// Check single reply:
///   80 [board] [lock] 00 [BCC] = Closed/Locked
///   80 [board] [lock] 11 [BCC] = Open/Unlocked
///
/// Read all reply: 80 [board] S1 S2 S3 33 [BCC] = 7 bytes
///   S1 = CH1–8, S2 = CH9–16, S3 = CH17–24
///   bit = 1 → channel Closed (locked)  bit = 0 → Open (unlocked)
///
/// Unlock multi (0x90):
///   Send : 90 [board] S1 S2 S3 [BCC]   = 6 bytes total
///   S1   = CH1–8  (bit=1 → unlock นั้น)
///   S2   = CH9–16
///   S3   = CH17–24
///   Example: unlock CH2, CH10, CH18 → S1=02 S2=02 S3=02 → send: 90 01 02 02 02 93
///
/// NOTE: Board may prepend garbage bytes before the real response.
///   ParseResponse() strips any leading garbage before the expected header.
/// </summary>
internal sealed class LockerController : IDisposable
{
    private readonly SerialPort _port;
    private readonly int _timeoutMs;

    public const byte MaxLockAddr = 0x18; // channel 1–24 ตาม spec

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
    // BCC = XOR ของทุก byte ใน payload (ก่อน BCC byte)
    // ----------------------------------------------------------
    private static byte ComputeBCC(byte[] data)
    {
        byte bcc = 0;
        foreach (var b in data) bcc ^= b;
        return bcc;
    }

    // ----------------------------------------------------------
    // Strip leading garbage — หา header ที่ถูกต้องก่อน parse
    // ----------------------------------------------------------
    private static byte[]? ParseResponse(byte[]? buf, byte expectedHeader, int minLen)
    {
        if (buf == null || buf.Length == 0) return null;

        for (int i = 0; i <= buf.Length - minLen; i++)
        {
            if (buf[i] == expectedHeader)
            {
                byte[] result = new byte[buf.Length - i];
                Array.Copy(buf, i, result, 0, result.Length);
                return result;
            }
        }

        return null;
    }

    // ----------------------------------------------------------
    // Unlock single channel
    // Send  : 8A [board] [lock] 11 [BCC]
    // Reply : 8A [board] [lock] 11 xx = Unlocked
    //         8A [board] [lock] 00 xx = Locked (ยังปิด)
    // ----------------------------------------------------------
    public byte[]? UnlockSingle(byte boardAddr, byte lockAddr)
    {
        byte[] payload = { 0x8A, boardAddr, lockAddr, 0x11 };
        byte[]? raw = SendAndReceive(AppendBCC(payload));
        return ParseResponse(raw, 0x8A, 5);
    }

    // ----------------------------------------------------------
    // Check door status — single channel
    // Send  : 80 [board] [lock] 33 [BCC]
    // Reply : 80 [board] [lock] 00 xx = Closed/Locked
    //         80 [board] [lock] 11 xx = Open/Unlocked
    // ----------------------------------------------------------
    public byte[]? CheckSingle(byte boardAddr, byte lockAddr)
    {
        byte[] payload = { 0x80, boardAddr, lockAddr, 0x33 };
        byte[]? raw = SendAndReceive(AppendBCC(payload));
        return ParseResponse(raw, 0x80, 5);
    }

    // ----------------------------------------------------------
    // Read all status (CH1–24)
    // Send  : 80 [board] 00 33 [BCC]
    //   board=0x01 → 80 01 00 33 B2
    // Reply : 80 [board] S1 S2 S3 33 [BCC]  = 7 bytes
    //   S1=CH1–8, S2=CH9–16, S3=CH17–24
    //   bit=1 → Closed, bit=0 → Open
    // ----------------------------------------------------------
    public byte[]? ReadAllStatus(byte boardAddr)
    {
        byte[] payload = { 0x80, boardAddr, 0x00, 0x33 };
        byte[]? raw = SendAndReceive(AppendBCC(payload));
        return ParseResponse(raw, 0x80, 7); // 7 bytes: 80 board S1 S2 S3 33 BCC
    }

    // ----------------------------------------------------------
    // Unlock multiple channels พร้อมกัน
    // Send  : 90 [board] S1 S2 S3 [BCC]  = 6 bytes total  ← 3 status bytes เท่านั้น!
    //   S1 = CH1–8,  bit=1 → unlock
    //   S2 = CH9–16, bit=1 → unlock
    //   S3 = CH17–24, bit=1 → unlock
    //
    // Example จาก spec: unlock CH2/CH10/CH18
    //   → 90 01 02 02 02 93
    //
    // Board ไม่ส่ง response กลับสำหรับ command นี้ตาม spec
    // ----------------------------------------------------------
    public byte[]? UnlockMultiple(byte boardAddr, byte s1, byte s2, byte s3)
    {
        byte[] payload = { 0x90, boardAddr, s1, s2, s3 };
        byte[]? raw = SendAndReceive(AppendBCC(payload));
        return raw; // คืน raw ทั้งหมด (board อาจไม่ส่งกลับ)
    }

    // ----------------------------------------------------------
    // Helper: แปลง channel list (1–24) → bitmask S1–S3
    // ตาม spec: S1=CH1–8, S2=CH9–16, S3=CH17–24
    // ----------------------------------------------------------
    public static (byte s1, byte s2, byte s3) ChannelsToBitmask(int[] channels)
    {
        byte s1 = 0, s2 = 0, s3 = 0;
        foreach (int ch in channels)
        {
            if (ch >= 1 && ch <= 8) s1 |= (byte)(1 << (ch - 1));
            else if (ch >= 9 && ch <= 16) s2 |= (byte)(1 << (ch - 9));
            else if (ch >= 17 && ch <= 24) s3 |= (byte)(1 << (ch - 17));
        }
        return (s1, s2, s3);
    }

    // ----------------------------------------------------------
    // Parse ReadAllStatus response → list ของ channel ที่ "เปิดอยู่"
    // input: response หลัง strip garbage (7 bytes)
    //   index: 0=0x80, 1=board, 2=S1, 3=S2, 4=S3, 5=0x33, 6=BCC
    //   bit=1 → Closed, bit=0 → Open
    // ----------------------------------------------------------
    public static List<int> ParseAllStatus(byte[] response)
    {
        var open = new List<int>();
        if (response == null || response.Length < 7) return open;

        byte s1 = response[2]; // CH1–8
        byte s2 = response[3]; // CH9–16
        byte s3 = response[4]; // CH17–24

        for (int ch = 1; ch <= 8; ch++) if ((s1 >> (ch - 1) & 1) == 0) open.Add(ch);
        for (int ch = 9; ch <= 16; ch++) if ((s2 >> (ch - 9) & 1) == 0) open.Add(ch);
        for (int ch = 17; ch <= 24; ch++) if ((s3 >> (ch - 17) & 1) == 0) open.Add(ch);

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