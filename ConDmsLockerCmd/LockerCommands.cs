using System.IO.Ports;

namespace ConDmsLockerCmd;

public static class LockerCommands
{
    private static LockerController? _ctrl;
    private static readonly object _lock = new();

    // ----------------------------------------------------------
    // Guard helpers
    // ----------------------------------------------------------

    private static void AssertOwnedChannel(byte lockAddr)
    {
        if (!LockerConfig.Instance.Channels.Contains(lockAddr))
            throw new InvalidOperationException(
                $"CH {lockAddr} ไม่ใช่ channel ของเครื่องนี้ " +
                $"(Mode={LockerConfig.Instance.Mode}, " +
                $"Labels={string.Join(",", LockerConfig.Instance.ChannelMap.Select(x => $"{x.Label}={x.Channel}"))})");
    }

    private static (byte, byte, byte) MaskToOwnedChannels(byte s1, byte s2, byte s3)
    {
        var (a1, a2, a3) = LockerController.ChannelsToBitmask(
            LockerConfig.Instance.Channels.ToArray());
        return ((byte)(s1 & a1), (byte)(s2 & a2), (byte)(s3 & a3));
    }

    // ----------------------------------------------------------
    // [DEBUG] expose bitmask — ใช้ใน Program.cs
    // ----------------------------------------------------------
    public static (byte s1, byte s2, byte s3) DebugBitmask(int[] channels)
        => LockerController.ChannelsToBitmask(channels);

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

            // spec: 0x00 = Closed/Locked, 0x11 = Open/Unlocked
            return response[3] == 0x00;
        }
    }

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

    public static byte[]? ReadRaw(byte boardAddr, byte lockAddr)
    {
        AssertOwnedChannel(lockAddr);
        lock (_lock)
        {
            if (_ctrl == null) return null;
            return _ctrl.CheckSingle(boardAddr, lockAddr);
        }
    }

    public static string ReadRawHex(byte boardAddr, byte lockAddr)
    {
        byte[]? raw = ReadRaw(boardAddr, lockAddr);
        if (raw == null || raw.Length == 0)
            return "(no response)";
        return string.Join(" ", raw.Select(b => $"{b:X2}"));
    }

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

    public static string CmdUnlock(byte boardAddr, byte lockAddr)
    {
        try { AssertOwnedChannel(lockAddr); }
        catch (InvalidOperationException ex) { return $"ex-error: {ex.Message}"; }

        lock (_lock)
        {
            try
            {
                if (_ctrl == null)
                    return "ex-error: Port not connected.";

                byte[]? response = _ctrl.UnlockSingle(boardAddr, lockAddr);

                if (response == null || response.Length < 4)
                    return "ex-error: No response from board.";

                // spec: response[3] = 0x11 = Unlocked สำเร็จ, 0x00 = ยังล็อคอยู่
                return response[3] == 0x11 ? "ok"
                    : $"ex-error: Unexpected response byte 0x{response[3]:X2}";
            }
            catch (Exception ex)
            {
                return $"ex-error: {ex.Message}";
            }
        }
    }

    public static byte[]? ReadAllStatus(byte boardAddr)
    {
        lock (_lock)
        {
            if (_ctrl == null) return null;
            return _ctrl.ReadAllStatus(boardAddr);
        }
    }

    public static Dictionary<int, bool> ParseAllStatusOwned(byte[] response)
    {
        var result = new Dictionary<int, bool>();
        if (response == null || response.Length < 7) return result;

        var owned = LockerConfig.Instance.Channels;
        var openChannels = LockerController.ParseAllStatus(response).ToHashSet();

        foreach (int ch in owned)
            result[ch] = !openChannels.Contains(ch); // true = Locked

        return result;
    }

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
    /// Unlock หลายช่องพร้อมกัน ด้วย command 0x90
    /// ตาม spec: 90 [board] S1 S2 S3 [BCC]  = 6 bytes (S1–S3 เท่านั้น, CH1–24)
    /// </summary>
    public static string CmdUnlockMultiple(byte boardAddr, byte s1, byte s2, byte s3)
    {
        (s1, s2, s3) = MaskToOwnedChannels(s1, s2, s3);

        lock (_lock)
        {
            try
            {
                if (_ctrl == null)
                    return "ex-error: Port not connected.";

                byte[]? raw = _ctrl.UnlockMultiple(boardAddr, s1, s2, s3);

                if (raw == null || raw.Length == 0)
                    Console.WriteLine("[DEBUG] UnlockMultiple: board ไม่ส่ง response (ปกติตาม spec)");
                else
                    Console.WriteLine($"[DEBUG] UnlockMultiple raw ({raw.Length} bytes): " +
                                      string.Join(" ", raw.Select(b => $"{b:X2}")));

                return "ok";
            }
            catch (Exception ex)
            {
                return $"ex-error: {ex.Message}";
            }
        }
    }

    public static string CmdUnlockAll(byte boardAddr)
    {
        int[] channels = LockerConfig.Instance.Channels.ToArray();
        var (s1, s2, s3) = LockerController.ChannelsToBitmask(channels);
        return CmdUnlockMultiple(boardAddr, s1, s2, s3);
    }

    public static string CmdUnlockChannels(byte boardAddr, int[] channelNumbers)
    {
        var owned = LockerConfig.Instance.Channels.ToHashSet();
        int[] filtered = channelNumbers.Where(ch => owned.Contains(ch)).ToArray();

        if (filtered.Length == 0)
            return "ex-error: ไม่มี channel ที่เป็นของเครื่องนี้อยู่ในรายการ";

        var (s1, s2, s3) = LockerController.ChannelsToBitmask(filtered);
        return CmdUnlockMultiple(boardAddr, s1, s2, s3);
    }

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