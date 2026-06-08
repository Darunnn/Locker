using System.IO.Ports;
using System.Runtime.InteropServices;

namespace ConDmsLockerCmd;

// ============================================================
//  con_dms_locker_cmd.dll
//  RS-485 Electronic Lock Controller — Hospital IPD Drug Locker
//
//  Protocol: 9600 baud, 8N1, BCC = XOR of all bytes in packet
//
//  Exported API (P/Invoke friendly):
//    cmdConnectPort(portName)   → true / false
//    cmdCheckLocked(board, ch)  → true=locked / false=open
//    cmdUnlock(board, ch)       → "ok" / "ex-error: <msg>"
// ============================================================

/// <summary>
/// Static facade exported for P/Invoke from WPF frontend.
/// All methods are thread-safe via internal locking.
/// </summary>
public static class LockerCommands
{
    private static LockerController? _ctrl;
    private static readonly object _lock = new();

    // ----------------------------------------------------------
    // cmdConnectPort
    // ----------------------------------------------------------
    /// <summary>
    /// Open RS-485 serial port.
    /// </summary>
    /// <param name="portName">e.g. "COM3"</param>
    /// <returns>true = connected, false = failed</returns>
    [UnmanagedCallersOnly(EntryPoint = "cmdConnectPort")]
    public static bool CmdConnectPortNative(IntPtr portNamePtr)
    {
        string portName = Marshal.PtrToStringAnsi(portNamePtr) ?? string.Empty;
        return CmdConnectPort(portName);
    }

    public static bool CmdConnectPort(string portName)
    {
        // ถ้าไม่ส่ง portName มา → ใช้ค่าจาก ini
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

    // ----------------------------------------------------------
    // cmdCheckLocked
    // ----------------------------------------------------------
    /// <summary>
    /// Check if a locker channel is locked (door closed).
    /// </summary>
    /// <param name="boardAddr">Board address 0x01–0x20</param>
    /// <param name="lockAddr">Channel 0x01–0x18</param>
    /// <returns>true = locked/closed, false = unlocked/open</returns>
    [UnmanagedCallersOnly(EntryPoint = "cmdCheckLocked")]
    public static bool CmdCheckLockedNative(byte boardAddr, byte lockAddr)
        => CmdCheckLocked(boardAddr, lockAddr);

    public static bool CmdCheckLocked(byte boardAddr, byte lockAddr)
    {
        lock (_lock)
        {
            if (_ctrl == null)
                throw new InvalidOperationException("Port not connected. Call cmdConnectPort first.");

            byte[]? response = _ctrl.CheckSingle(boardAddr, lockAddr);

            // Response: 80 [board] [lock] [state] [BCC]
            // state 0x00 = Closed/Locked, 0x11 = Open/Unlocked
            if (response == null || response.Length < 4)
                throw new IOException("No response from board.");

            return response[3] == 0x00; // true = locked
        }
    }

    // ----------------------------------------------------------
    // cmdUnlock
    // ----------------------------------------------------------
    /// <summary>
    /// Send unlock command to a locker channel.
    /// </summary>
    /// <param name="boardAddr">Board address 0x01–0x20</param>
    /// <param name="lockAddr">Channel 0x01–0x18</param>
    /// <returns>"ok" on success, "ex-error: &lt;message&gt;" on failure</returns>
    [UnmanagedCallersOnly(EntryPoint = "cmdUnlock")]
    public static IntPtr CmdUnlockNative(byte boardAddr, byte lockAddr)
    {
        string result = CmdUnlock(boardAddr, lockAddr);
        return Marshal.StringToHGlobalAnsi(result);
    }

    public static string CmdUnlock(byte boardAddr, byte lockAddr)
    {
        lock (_lock)
        {
            try
            {
                if (_ctrl == null)
                    return "ex-error: Port not connected. Call cmdConnectPort first.";

                byte[]? response = _ctrl.UnlockSingle(boardAddr, lockAddr);

                if (response == null || response.Length < 4)
                    return "ex-error: No response from board.";

                // Response state: 0x11 = unlocked, 0x00 = still locked
                if (response[3] == 0x11)
                    return "ok";

                return "ex-error: Board acknowledged but lock did not open (state=0x00).";
            }
            catch (Exception ex)
            {
                return $"ex-error: {ex.Message}";
            }
        }
    }

    // ----------------------------------------------------------
    // Helper: disconnect
    // ----------------------------------------------------------
    public static void Disconnect()
    {
        lock (_lock)
        {
            _ctrl?.Dispose();
            _ctrl = null;
        }
    }
}
