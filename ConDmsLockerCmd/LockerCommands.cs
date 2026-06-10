using System.IO.Ports;

namespace ConDmsLockerCmd;

public static class LockerCommands
{
    private static LockerController? _ctrl;
    private static readonly object _lock = new();

    public static bool CmdConnectPort(string portName)
    {
        if (string.IsNullOrWhiteSpace(portName))
            portName = LockerConfig.Instance.Port;

        lock (_lock)
        {
            try
            {
                _ctrl?.Dispose();
                _ctrl = new LockerController(portName);
                return true;
            }
            catch
            {
                _ctrl = null;
                return false;
            }
        }
    }

    /// <summary>
    /// Check สถานะล็อคช่องเดียว
    /// Return: true = ล็อคอยู่ (Closed), false = เปิดอยู่ (Open)
    ///
    /// response[3] = 0x11 → Locked  ← confirmed จากการทดสอบจริง
    /// response[3] = 0x00 → Unlocked ← confirmed จากการทดสอบจริง
    /// </summary>
    public static bool CmdCheckLocked(byte boardAddr, byte lockAddr)
    {
        lock (_lock)
        {
            if (_ctrl == null)
                throw new InvalidOperationException("Port not connected.");

            byte[]? response = _ctrl.CheckSingle(boardAddr, lockAddr);

            if (response == null || response.Length < 4)
                throw new IOException("No response from board.");

            return response[3] == 0x11; // 0x11 = Locked
        }
    }

    /// <summary>
    /// Check สถานะล็อคแบบ raw — คืนค่า response[3] โดยตรง
    /// 0x11 = Locked, 0x00 = Unlocked
    /// </summary>
    public static byte CmdCheckLockedRaw(byte boardAddr, byte lockAddr)
    {
        lock (_lock)
        {
            if (_ctrl == null)
                throw new InvalidOperationException("Port not connected.");

            byte[]? response = _ctrl.CheckSingle(boardAddr, lockAddr);

            if (response == null || response.Length < 4)
                throw new IOException("No response from board.");

            return response[3];
        }
    }

    /// <summary>
    /// ดึง raw bytes ทั้งหมดจาก CheckSingle — ใช้สำหรับ debug
    /// </summary>
    public static byte[]? ReadRaw(byte boardAddr, byte lockAddr)
    {
        lock (_lock)
        {
            if (_ctrl == null) return null;
            return _ctrl.CheckSingle(boardAddr, lockAddr);
        }
    }

    /// <summary>
    /// Debug: แสดงผล raw bytes เป็น hex string
    /// </summary>
    public static string ReadRawHex(byte boardAddr, byte lockAddr)
    {
        byte[]? raw = ReadRaw(boardAddr, lockAddr);
        if (raw == null || raw.Length == 0)
            return "(no response)";
        return string.Join(" ", raw.Select(b => $"{b:X2}"));
    }

    /// <summary>
    /// Unlock ช่องเดียว
    /// Return: "ok" = สำเร็จ, "ex-error: ..." = ล้มเหลว
    /// response[3] = 0x11 หลัง unlock = board ตอบรับ = success
    /// </summary>
    public static string CmdUnlock(byte boardAddr, byte lockAddr)
    {
        lock (_lock)
        {
            try
            {
                if (_ctrl == null)
                    return "ex-error: Port not connected.";

                byte[]? response = _ctrl.UnlockSingle(boardAddr, lockAddr);

                if (response == null || response.Length < 4)
                    return "ex-error: No response from board.";

                // board ส่ง 0x11 กลับมา = acknowledge unlock สำเร็จ
                return response[3] == 0x11 ? "ok"
                    : $"ex-error: Unexpected response byte 0x{response[3]:X2}";
            }
            catch (Exception ex)
            {
                return $"ex-error: {ex.Message}";
            }
        }
    }

    /// <summary>
    /// Read All Status (CMD: 80 [board] 00 33 BCC)
    /// คืน raw response 11 bytes:
    ///   [0]=0x80 [1]=board [2–8]=S1–S7 [9]=0x33 [10]=BCC
    /// </summary>
    public static byte[]? ReadAllStatus(byte boardAddr)
    {
        lock (_lock)
        {
            if (_ctrl == null) return null;
            return _ctrl.ReadAllStatus(boardAddr);
        }
    }

    /// <summary>
    /// Unlock หลายช่องพร้อมกัน (CMD: 90 [board] S1–S7 BCC)
    ///
    /// แต่ละ bit ใน S1–S7 ตรงกับ channel:
    ///   S1 bit0=CH1, bit1=CH2, ..., bit7=CH8
    ///   S2 bit0=CH9, ..., bit7=CH16
    ///   S3 bit0=CH17, ..., bit7=CH24
    ///   S4 bit0=CH25, ..., bit7=CH32
    ///   S5 bit0=CH33, ..., bit7=CH40
    ///   S6 bit0=CH41, ..., bit7=CH48
    ///   S7 bit0=CH49, bit1=CH50 (ใช้แค่ 2 bits)
    ///
    /// Unlock ALL 50 channels: s1=0xFF s2=0xFF s3=0xFF s4=0xFF s5=0xFF s6=0xFF s7=0x03
    /// Return: "ok" หรือ "ex-error: ..."
    /// </summary>
    public static string CmdUnlockMultiple(byte boardAddr,
                                            byte s1, byte s2, byte s3,
                                            byte s4, byte s5, byte s6, byte s7)
    {
        lock (_lock)
        {
            try
            {
                if (_ctrl == null)
                    return "ex-error: Port not connected.";

                byte[]? response = _ctrl.UnlockMultiple(boardAddr, s1, s2, s3, s4, s5, s6, s7);

                if (response == null || response.Length < 2)
                    return "ex-error: No response from board.";

                return "ok";
            }
            catch (Exception ex)
            {
                return $"ex-error: {ex.Message}";
            }
        }
    }

    /// <summary>
    /// Helper: Unlock ทุกช่องในครั้งเดียว (CH 1–MaxChannels)
    /// ใช้ UnlockMultiple ด้วย bitmask เต็ม
    /// Return: "ok" หรือ "ex-error: ..."
    /// </summary>
    public static string CmdUnlockAll(byte boardAddr)
    {
        int[] channels = LockerConfig.Instance.Channels.ToArray();
        var (s1, s2, s3, s4, s5, s6, s7) = LockerController.ChannelsToBitmask(channels);
        return CmdUnlockMultiple(boardAddr, s1, s2, s3, s4, s5, s6, s7);
    }

    /// <summary>
    /// Helper: Unlock หลายช่องโดยระบุรายชื่อ channel number (1-based)
    /// ตัวอย่าง: CmdUnlockChannels(0x01, new[]{ 1, 3, 5 })
    /// Return: "ok" หรือ "ex-error: ..."
    /// </summary>
    public static string CmdUnlockChannels(byte boardAddr, int[] channelNumbers)
    {
        var (s1, s2, s3, s4, s5, s6, s7) = LockerController.ChannelsToBitmask(channelNumbers);
        return CmdUnlockMultiple(boardAddr, s1, s2, s3, s4, s5, s6, s7);
    }

    public static void Disconnect()
    {
        lock (_lock)
        {
            _ctrl?.Dispose();
            _ctrl = null;
        }
    }
}