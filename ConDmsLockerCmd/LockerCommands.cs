using System.IO.Ports;

namespace ConDmsLockerCmd;

public static class LockerCommands
{
    private static LockerController? _ctrl;
    private static readonly object _lock = new();

    // ----------------------------------------------------------
    // Guard helpers
    // ----------------------------------------------------------

    /// <summary>
    /// Throw ถ้า channel ไม่ได้อยู่ใน ChannelMap ของเครื่องนี้
    /// (Channels derive มาจาก ChannelMap อัตโนมัติ)
    /// </summary>
    private static void AssertOwnedChannel(byte lockAddr)
    {
        if (!LockerConfig.Instance.Channels.Contains(lockAddr))
            throw new InvalidOperationException(
                $"CH {lockAddr} ไม่ใช่ channel ของเครื่องนี้ " +
                $"(Mode={LockerConfig.Instance.Mode}, " +
                $"Labels={string.Join(",", LockerConfig.Instance.ChannelMap.Select(x => $"{x.Label}={x.Channel}"))})");
    }

    /// <summary>
    /// AND bitmask S1–S7 กับ mask ของ channel ที่เครื่องนี้ดูแลเท่านั้น
    /// </summary>
    private static (byte, byte, byte, byte, byte, byte, byte) MaskToOwnedChannels(
        byte s1, byte s2, byte s3, byte s4, byte s5, byte s6, byte s7)
    {
        var (a1, a2, a3, a4, a5, a6, a7) = LockerController.ChannelsToBitmask(
            LockerConfig.Instance.Channels.ToArray());
        return (
            (byte)(s1 & a1), (byte)(s2 & a2), (byte)(s3 & a3),
            (byte)(s4 & a4), (byte)(s5 & a5), (byte)(s6 & a6),
            (byte)(s7 & a7));
    }

    // ----------------------------------------------------------

    public static bool CmdConnectPort(string portName)
    {
        if (string.IsNullOrWhiteSpace(portName))
            throw new ArgumentException("ต้องระบุ port name");

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
    /// Check สถานะล็อคช่องเดียว — เฉพาะ CH ของเครื่องนี้
    /// Return: true = ล็อคอยู่ (Closed), false = เปิดอยู่ (Open)
    /// </summary>
    public static bool CmdCheckLocked(byte boardAddr, byte lockAddr)
    {
        AssertOwnedChannel(lockAddr);
        lock (_lock)
        {
            if (_ctrl == null)
                throw new InvalidOperationException("Port not connected.");

            byte[]? response = _ctrl.CheckSingle(boardAddr, lockAddr);

            if (response == null || response.Length < 4)
                throw new IOException("No response from board.");

            return response[3] == 0x11;
        }
    }

    /// <summary>
    /// Check สถานะล็อคแบบ raw — เฉพาะ CH ของเครื่องนี้
    /// 0x11 = Locked, 0x00 = Unlocked
    /// </summary>
    public static byte CmdCheckLockedRaw(byte boardAddr, byte lockAddr)
    {
        AssertOwnedChannel(lockAddr);
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
    /// ดึง raw bytes — เฉพาะ CH ของเครื่องนี้
    /// </summary>
    public static byte[]? ReadRaw(byte boardAddr, byte lockAddr)
    {
        AssertOwnedChannel(lockAddr);
        lock (_lock)
        {
            if (_ctrl == null) return null;
            return _ctrl.CheckSingle(boardAddr, lockAddr);
        }
    }

    /// <summary>
    /// Debug hex string — เฉพาะ CH ของเครื่องนี้
    /// </summary>
    public static string ReadRawHex(byte boardAddr, byte lockAddr)
    {
        byte[]? raw = ReadRaw(boardAddr, lockAddr);
        if (raw == null || raw.Length == 0)
            return "(no response)";
        return string.Join(" ", raw.Select(b => $"{b:X2}"));
    }

    /// <summary>
    /// Unlock ช่องเดียว โดยใช้ label — แปลง label → CH ก่อนส่ง
    /// Return: "ok" = สำเร็จ, "ex-error: ..." = ล้มเหลว
    /// </summary>
    public static string CmdUnlockByLabel(byte boardAddr, string label)
    {
        try
        {
            int ch = LockerConfig.Instance.LabelToChannel(label);
            return CmdUnlock(boardAddr, (byte)ch);
        }
        catch (InvalidOperationException ex)
        {
            return $"ex-error: {ex.Message}";
        }
    }

    /// <summary>
    /// Unlock ช่องเดียว — เฉพาะ CH ของเครื่องนี้
    /// Return: "ok" = สำเร็จ, "ex-error: ..." = ล้มเหลว
    /// </summary>
    public static string CmdUnlock(byte boardAddr, byte lockAddr)
    {
        try
        {
            AssertOwnedChannel(lockAddr);
        }
        catch (InvalidOperationException ex)
        {
            return $"ex-error: {ex.Message}";
        }

        lock (_lock)
        {
            try
            {
                if (_ctrl == null)
                    return "ex-error: Port not connected.";

                byte[]? response = _ctrl.UnlockSingle(boardAddr, lockAddr);

                if (response == null || response.Length < 4)
                    return "ex-error: No response from board.";

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
    /// Read All Status — คืน raw 11 bytes
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
    /// Parse ReadAllStatus → กรองเฉพาะ CH ของเครื่องนี้
    /// คืน dict ของทุก CH ที่เครื่องดูแล: true = Locked, false = Unlocked
    /// </summary>
    public static Dictionary<int, bool> ParseAllStatusOwned(byte[] response)
    {
        var result = new Dictionary<int, bool>();
        if (response == null || response.Length < 11) return result;

        var owned = LockerConfig.Instance.Channels;
        var openChannels = LockerController.ParseAllStatus(response).ToHashSet();

        foreach (int ch in owned)
            result[ch] = !openChannels.Contains(ch); // true = Locked

        return result;
    }

    /// <summary>
    /// Parse ReadAllStatus → แสดงผลเป็น label แทน CH number
    /// คืน dict ของทุก label ที่เครื่องดูแล: true = Locked, false = Unlocked
    /// </summary>
    public static Dictionary<string, bool> ParseAllStatusByLabel(byte[] response)
    {
        var chStatus = ParseAllStatusOwned(response);
        var result = new Dictionary<string, bool>();

        foreach (var (label, ch) in LockerConfig.Instance.ChannelMap)
        {
            if (chStatus.TryGetValue(ch, out bool locked))
                result[label] = locked;
        }

        return result;
    }

    /// <summary>
    /// Unlock หลายช่องพร้อมกัน — AND mask กับ CH ของเครื่องนี้ก่อนส่งเสมอ
    /// </summary>
    public static string CmdUnlockMultiple(byte boardAddr,
                                            byte s1, byte s2, byte s3,
                                            byte s4, byte s5, byte s6, byte s7)
    {
        (s1, s2, s3, s4, s5, s6, s7) =
            MaskToOwnedChannels(s1, s2, s3, s4, s5, s6, s7);

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
    /// Unlock ทุกช่อง — เฉพาะ CH ของเครื่องนี้
    /// </summary>
    public static string CmdUnlockAll(byte boardAddr)
    {
        int[] channels = LockerConfig.Instance.Channels.ToArray();
        var (s1, s2, s3, s4, s5, s6, s7) = LockerController.ChannelsToBitmask(channels);
        return CmdUnlockMultiple(boardAddr, s1, s2, s3, s4, s5, s6, s7);
    }

    /// <summary>
    /// Unlock หลายช่องที่ระบุ — กรองเฉพาะ CH ของเครื่องนี้ก่อนส่ง
    /// </summary>
    public static string CmdUnlockChannels(byte boardAddr, int[] channelNumbers)
    {
        var owned = LockerConfig.Instance.Channels.ToHashSet();
        int[] filtered = channelNumbers.Where(ch => owned.Contains(ch)).ToArray();

        if (filtered.Length == 0)
            return "ex-error: ไม่มี channel ที่เป็นของเครื่องนี้อยู่ในรายการ";

        var (s1, s2, s3, s4, s5, s6, s7) = LockerController.ChannelsToBitmask(filtered);
        return CmdUnlockMultiple(boardAddr, s1, s2, s3, s4, s5, s6, s7);
    }

    /// <summary>
    /// Unlock หลายช่องโดยใช้ label — แปลง label → CH ก่อนส่ง
    /// </summary>
    public static string CmdUnlockLabels(byte boardAddr, string[] labels)
    {
        try
        {
            int[] channels = labels
                .Select(l => LockerConfig.Instance.LabelToChannel(l))
                .ToArray();
            return CmdUnlockChannels(boardAddr, channels);
        }
        catch (InvalidOperationException ex)
        {
            return $"ex-error: {ex.Message}";
        }
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