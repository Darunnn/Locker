// ============================================================
//  LockerDllWrapper.cs  —  ใส่ไว้ใน WPF project
//
//  Option A (แนะนำ): Reference DLL โดยตรงใน .csproj
//    <ProjectReference Include="..\ConDmsLockerCmd\ConDmsLockerCmd.csproj"/>
//    แล้วเรียก ConDmsLockerCmd.LockerCommands.CmdConnectPort(...)
//
//  Option B: P/Invoke (NativeAOT export)  — ดู native signatures ด้านล่าง
// ============================================================

using System.Runtime.InteropServices;

namespace YourWpfApp;

// ============================================================
//  Option A — Managed wrapper (แนะนำสำหรับ All-in-One)
// ============================================================
public static class LockerService
{
    // --- Connection ---

    public static bool ConnectPort(string portName)
        => ConDmsLockerCmd.LockerCommands.CmdConnectPort(portName);

    public static void Disconnect()
        => ConDmsLockerCmd.LockerCommands.Disconnect();

    // --- ChannelMap info ---

    /// <summary>
    /// คืน label ทั้งหมดตามลำดับใน ini เช่น ["A","B","C","D"]
    /// ใช้ bind กับ UI list
    /// </summary>
    public static IReadOnlyList<string> GetLabels()
        => ConDmsLockerCmd.LockerConfig.Instance.ChannelMap
            .Select(x => x.Label)
            .ToList()
            .AsReadOnly();

    /// <summary>
    /// คืน (Label, Channel) ทั้งหมด — ใช้เมื่อต้องการแสดงทั้ง label และ CH บน UI
    /// </summary>
    public static IReadOnlyList<(string Label, int Channel)> GetChannelMap()
        => ConDmsLockerCmd.LockerConfig.Instance.ChannelMap;

    // --- Check status ---

    /// <summary>
    /// Check สถานะโดยใช้ CH number โดยตรง
    /// Return: true = Locked, false = Unlocked
    /// </summary>
    public static bool CheckLocked(byte boardAddr, byte lockAddr)
        => ConDmsLockerCmd.LockerCommands.CmdCheckLocked(boardAddr, lockAddr);

    /// <summary>
    /// Check สถานะโดยใช้ label เช่น "A", "B"
    /// Return: true = Locked, false = Unlocked
    /// Throw: InvalidOperationException ถ้า label ไม่พบ หรือ port ไม่ได้ต่อ
    /// </summary>
    public static bool CheckLockedByLabel(byte boardAddr, string label)
    {
        int ch = ConDmsLockerCmd.LockerConfig.Instance.LabelToChannel(label);
        return ConDmsLockerCmd.LockerCommands.CmdCheckLocked(boardAddr, (byte)ch);
    }

    /// <summary>
    /// Read All Status แล้วคืนเป็น label → locked
    /// ใช้ refresh หน้า UI ทีเดียวทุกช่อง
    /// </summary>
    public static Dictionary<string, bool>? ReadAllStatusByLabel(byte boardAddr)
    {
        byte[]? response = ConDmsLockerCmd.LockerCommands.ReadAllStatus(boardAddr);
        if (response == null || response.Length < 11) return null;
        return ConDmsLockerCmd.LockerCommands.ParseAllStatusByLabel(response);
    }

    // --- Unlock ---

    /// <summary>
    /// Unlock โดยใช้ CH number โดยตรง
    /// Return: "ok" หรือ "ex-error: ..."
    /// </summary>
    public static string Unlock(byte boardAddr, byte lockAddr)
        => ConDmsLockerCmd.LockerCommands.CmdUnlock(boardAddr, lockAddr);

    /// <summary>
    /// Unlock โดยใช้ label เช่น "A"
    /// Return: "ok" หรือ "ex-error: ..."
    /// </summary>
    public static string UnlockByLabel(byte boardAddr, string label)
        => ConDmsLockerCmd.LockerCommands.CmdUnlockByLabel(boardAddr, label);

    /// <summary>
    /// Unlock หลาย label พร้อมกัน เช่น ["A", "C"]
    /// Return: "ok" หรือ "ex-error: ..."
    /// </summary>
    public static string UnlockLabels(byte boardAddr, string[] labels)
        => ConDmsLockerCmd.LockerCommands.CmdUnlockLabels(boardAddr, labels);

    /// <summary>
    /// Unlock ทุกช่องพร้อมกัน
    /// Return: "ok" หรือ "ex-error: ..."
    /// </summary>
    public static string UnlockAll(byte boardAddr)
        => ConDmsLockerCmd.LockerCommands.CmdUnlockAll(boardAddr);
}

// ============================================================
//  Option B — P/Invoke สำหรับ native DLL (NativeAOT build)
// ============================================================
internal static class LockerNative
{
    private const string DllName = "con_dms_locker_cmd";

    [DllImport(DllName, EntryPoint = "cmdConnectPort", CharSet = CharSet.Ansi)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CmdConnectPort([MarshalAs(UnmanagedType.LPStr)] string portName);

    [DllImport(DllName, EntryPoint = "cmdCheckLocked")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CmdCheckLocked(byte boardAddr, byte lockAddr);

    [DllImport(DllName, EntryPoint = "cmdUnlock")]
    public static extern IntPtr CmdUnlock(byte boardAddr, byte lockAddr);

    public static string CmdUnlockString(byte boardAddr, byte lockAddr)
    {
        IntPtr ptr = CmdUnlock(boardAddr, lockAddr);
        string result = Marshal.PtrToStringAnsi(ptr) ?? "ex-error: null response";
        Marshal.FreeHGlobal(ptr);
        return result;
    }
}