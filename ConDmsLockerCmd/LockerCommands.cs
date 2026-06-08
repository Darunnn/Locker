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
    /// Check สถานะล็อค
    /// Return: true = ล็อคอยู่ (Closed), false = เปิดอยู่ (Open)
    ///
    /// NOTE: ล็อคตัวนี้ feedback กลับขั้วจาก spec:
    ///   0x11 = Locked (ปิดอยู่)    ← confirmed จากการทดสอบจริง
    ///   0x00 = Unlocked (เปิดอยู่) ← confirmed จากการทดสอบจริง
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

            // 0x11 = Locked, 0x00 = Unlocked (confirmed by hardware test)
            return response[3] == 0x11;
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

                return response[3] == 0x11 ? "ok"
                    : "ex-error: Board acknowledged but lock did not open.";
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

    public static void Disconnect()
    {
        lock (_lock)
        {
            _ctrl?.Dispose();
            _ctrl = null;
        }
    }
}