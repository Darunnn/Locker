// ============================================================
//  LockerDllWrapper.cs  —  ใส่ไว้ใน WPF project
//  วิธีใช้: เรียกผ่าน managed wrapper (แนะนำ) หรือ P/Invoke raw
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
//  Option A — Managed wrapper (ง่ายที่สุด, แนะนำสำหรับ All-in-One)
// ============================================================
public static class LockerService
{
    // เรียก DLL method โดยตรง (same process)
    public static bool ConnectPort(string portName)
        => ConDmsLockerCmd.LockerCommands.CmdConnectPort(portName);

    public static bool CheckLocked(byte boardAddr, byte lockAddr)
        => ConDmsLockerCmd.LockerCommands.CmdCheckLocked(boardAddr, lockAddr);

    /// <returns>"ok" or "ex-error: ..." </returns>
    public static string Unlock(byte boardAddr, byte lockAddr)
        => ConDmsLockerCmd.LockerCommands.CmdUnlock(boardAddr, lockAddr);

    public static void Disconnect()
        => ConDmsLockerCmd.LockerCommands.Disconnect();
}

// ============================================================
//  Option B — P/Invoke สำหรับ native DLL (NativeAOT build)
//  ใช้เมื่อ build DLL เป็น native shared library แยก process
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

    // Helper: แปลง IntPtr → string และ free memory
    public static string CmdUnlockString(byte boardAddr, byte lockAddr)
    {
        IntPtr ptr = CmdUnlock(boardAddr, lockAddr);
        string result = Marshal.PtrToStringAnsi(ptr) ?? "ex-error: null response";
        Marshal.FreeHGlobal(ptr);
        return result;
    }
}
