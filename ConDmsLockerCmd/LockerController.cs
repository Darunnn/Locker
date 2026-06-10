using System.IO.Ports;

namespace ConDmsLockerCmd;

/// <summary>
/// Low-level RS-485 locker controller.
/// Protocol: 9600 baud 8N1, BCC = XOR of all payload bytes.
///
/// Commands:
///   Unlock single  : 8A [board] [lock] 11 [BCC]                    lock = 0x01–0x32
///   Check single   : 80 [board] [lock] 33 [BCC]                    lock = 0x01–0x32
///   Read all       : 80 [board] 00 33 [BCC]                        lock = 0x00 (ALL)
///   Unlock multi   : 90 [board] S1 S2 S3 S4 S5 S6 S7 [BCC]
///
/// Read all send  : 80 01 00 33 B2  (board=0x01)
/// Read all reply : 80 [board] S1 S2 S3 S4 S5 S6 S7 33 [BCC]  = 11 bytes
///   bit = 0 → channel OPEN (unlocked)
///   bit = 1 → channel CLOSED (locked)
///
/// Single channel status (response[3]):
///   0x11 = Locked  (ปิด)   ← confirmed จากการทดสอบจริง
///   0x00 = Unlocked (เปิด) ← confirmed จากการทดสอบจริง
///
/// NOTE: Board may prepend garbage bytes before the real response.
///   Real response always starts with the command head byte (0x80 or 0x8A or 0x90).
///   ParseResponse() strips any leading garbage before the expected header.
/// </summary>
internal sealed class LockerController : IDisposable
{
    private readonly SerialPort _port;
    private readonly int _timeoutMs;

    public const byte MaxLockAddr = 0x32; // channel 1–50

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
    //
    // Board บางตัวส่ง garbage bytes นำหน้า response จริง
    // e.g. FA FD 99 99 ก่อน 80 01 ...
    // วิธี: scan จนเจอ expectedHeader ที่เหลือ >= minLen bytes
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

        return null; // ไม่เจอ header
    }

    // ----------------------------------------------------------
    // Unlock single channel
    //
    // Send  : 8A [board] [lock] 11 [BCC]
    // Reply : 8A [board] [lock] 11 [BCC]   → response[3] = 0x11 = success
    // ----------------------------------------------------------
    public byte[]? UnlockSingle(byte boardAddr, byte lockAddr)
    {
        byte[] payload = { 0x8A, boardAddr, lockAddr, 0x11 };
        byte[]? raw = SendAndReceive(AppendBCC(payload));
        return ParseResponse(raw, 0x8A, 5);
    }

    // ----------------------------------------------------------
    // Check door status — single channel
    //
    // Send  : 80 [board] [lock] 33 [BCC]     lock = 0x01–0x32
    // Reply : 80 [board] [lock] [status] [BCC]
    //   response[3] = 0x11 → Locked (ปิด)
    //   response[3] = 0x00 → Unlocked (เปิด)
    // ----------------------------------------------------------
    public byte[]? CheckSingle(byte boardAddr, byte lockAddr)
    {
        byte[] payload = { 0x80, boardAddr, lockAddr, 0x33 };
        byte[]? raw = SendAndReceive(AppendBCC(payload));
        return ParseResponse(raw, 0x80, 5);
    }

    // ----------------------------------------------------------
    // Read all 50-channel status
    //
    // Send  : 80 [board] 00 33 [BCC]
    //   e.g. board=0x01 → 80 01 00 33 B2
    //
    // Reply : 80 [board] S1 S2 S3 S4 S5 S6 S7 33 [BCC]  = 11 bytes
    //   S1 = CH 1–8,  S2 = CH 9–16,  S3 = CH 17–24
    //   S4 = CH 25–32, S5 = CH 33–40, S6 = CH 41–48
    //   S7 = CH 49–50 (bit 0 = CH49, bit 1 = CH50)
    //   bit = 0 → Open (เปิด)   bit = 1 → Closed (ปิด)
    // ----------------------------------------------------------
    public byte[]? ReadAllStatus(byte boardAddr)
    {
        // lock addr = 0x00 หมายถึง "read all" ตาม spec
        byte[] payload = { 0x80, boardAddr, 0x00, 0x33 };
        byte[]? raw = SendAndReceive(AppendBCC(payload));
        return ParseResponse(raw, 0x80, 11);
    }

    // ----------------------------------------------------------
    // Unlock multiple channels พร้อมกัน (bitmask S1–S7)
    //
    // Send  : 90 [board] S1 S2 S3 S4 S5 S6 S7 [BCC]  = 10 bytes
    //   S1 = CH 1–8,  bit=1 → unlock channel นั้น
    //   S7 = CH 49–50 (ใช้แค่ bit 0,1)
    //
    // Unlock ALL 50: S1=0xFF S2=0xFF S3=0xFF S4=0xFF S5=0xFF S6=0xFF S7=0x03
    // Unlock CH1 only: S1=0x01 S2–S7=0x00
    // ----------------------------------------------------------
    public byte[]? UnlockMultiple(byte boardAddr,
                                   byte s1, byte s2, byte s3,
                                   byte s4, byte s5, byte s6, byte s7)
    {
        byte[] payload = { 0x90, boardAddr, s1, s2, s3, s4, s5, s6, s7 };
        byte[]? raw = SendAndReceive(AppendBCC(payload));
        return ParseResponse(raw, 0x90, 5);
    }

    // ----------------------------------------------------------
    // Helper: แปลงรายชื่อ channel (1–50) → bitmask S1–S7
    // ใช้กับ UnlockMultiple
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
    // Parse ReadAllStatus response → list ของ channel ที่ "เปิดอยู่"
    // input: response หลัง strip garbage แล้ว (11 bytes)
    //   index: 0=0x80, 1=board, 2–8=S1–S7, 9=0x33, 10=BCC
    //   bit=0 → Open, bit=1 → Closed
    // ----------------------------------------------------------
    public static List<int> ParseAllStatus(byte[] response)
    {
        var open = new List<int>();
        if (response == null || response.Length < 11) return open;

        byte[] s = new byte[7];
        for (int b = 0; b < 7; b++)
            s[b] = response[2 + b];

        for (int ch = 1; ch <= 50; ch++)
        {
            int byteIdx = (ch - 1) / 8;
            int bit = (ch - 1) % 8;

            // LSB first ทุก byte (CH26 = byteIdx=3, bit=1 → 0x02 >> 1 & 1 = 1 = closed)
            bool closed = (s[byteIdx] >> bit & 1) == 1;

            if (!closed) open.Add(ch);
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