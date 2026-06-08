using System.Reflection;

namespace ConDmsLockerCmd;

/// <summary>
/// โหลดค่าจาก locker_config.ini
/// อ่าน [App] Mode= เพื่อรู้ว่าเครื่องนี้เป็น Pharmacy หรือ Delivery
/// แล้วโหลด section ที่ตรงกัน ([Pharmacy] หรือ [Delivery])
/// </summary>
public sealed class LockerConfig
{
    private static LockerConfig? _instance;
    public static LockerConfig Instance => _instance ??= new LockerConfig();

    // ----------------------------------------------------------
    // [App]
    // ----------------------------------------------------------
    /// <summary>pharmacy | delivery</summary>
    public AppMode Mode     { get; private set; } = AppMode.Pharmacy;
    public string AppTitle  { get; private set; } = "ระบบ Locker ยา IPD";

    // ----------------------------------------------------------
    // [Pharmacy] / [Delivery]  — โหลดตาม Mode
    // ----------------------------------------------------------
    public string Port       { get; private set; } = "COM3";
    public int    BaudRate   { get; private set; } = 9600;
    public int    TimeoutMs  { get; private set; } = 600;
    public byte   BoardAddr  { get; private set; } = 0x01;
    public int    MaxChannels{ get; private set; } = 24;

    // ----------------------------------------------------------
    // [Locker]  — shared
    // ----------------------------------------------------------
    public bool PreventBothSidesOpen { get; private set; } = true;
    public int  AutoRelockDelayMs    { get; private set; } = 0;

    // ----------------------------------------------------------
    // Meta
    // ----------------------------------------------------------
    public string ConfigPath { get; private set; } = string.Empty;

    // ----------------------------------------------------------
    // Constructor
    // ----------------------------------------------------------
    private LockerConfig()
    {
        string dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)
                     ?? AppContext.BaseDirectory;
        ConfigPath = Path.Combine(dir, "locker_config.ini");
        Load();
    }

    // ----------------------------------------------------------
    // Load
    // ----------------------------------------------------------
    public void Load()
    {
        if (!File.Exists(ConfigPath)) return;

        // Pass 1: อ่าน [App] ก่อนเพื่อรู้ Mode
        Mode     = ReadAppMode();
        AppTitle = ReadAppTitle();

        // Pass 2: อ่าน section ตาม Mode + [Locker]
        string targetSection = Mode == AppMode.Pharmacy ? "pharmacy" : "delivery";

        var lines = File.ReadAllLines(ConfigPath);
        string section = "";

        foreach (var raw in lines)
        {
            string line = raw.Trim();
            if (line.StartsWith(';') || line.StartsWith('#') || line.Length == 0) continue;

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1].ToLower();
                continue;
            }

            int eq = line.IndexOf('=');
            if (eq < 0) continue;

            string key = line[..eq].Trim().ToLower();
            string val = StripComment(line[(eq + 1)..].Trim());

            if (section == targetSection)
                ApplySerial(key, val);
            else if (section == "locker")
                ApplyLocker(key, val);
        }
    }

    // ----------------------------------------------------------
    // Helpers
    // ----------------------------------------------------------
    private AppMode ReadAppMode()
    {
        string raw = ReadValue("app", "mode") ?? "pharmacy";
        return raw.Trim().ToLower() == "delivery" ? AppMode.Delivery : AppMode.Pharmacy;
    }

    private string ReadAppTitle()
    {
        return ReadValue("app", "apptitle")
            ?? (Mode == AppMode.Pharmacy ? "ระบบจ่ายยา IPD — ห้องยา" : "ระบบรับยา IPD — จุดรับยา");
    }

    /// <summary>Quick single-key reader (single pass)</summary>
    private string? ReadValue(string targetSection, string targetKey)
    {
        if (!File.Exists(ConfigPath)) return null;
        string section = "";
        foreach (var raw in File.ReadAllLines(ConfigPath))
        {
            string line = raw.Trim();
            if (line.StartsWith(';') || line.StartsWith('#') || line.Length == 0) continue;
            if (line.StartsWith('[') && line.EndsWith(']')) { section = line[1..^1].ToLower(); continue; }
            if (section != targetSection) continue;
            int eq = line.IndexOf('=');
            if (eq < 0) continue;
            if (line[..eq].Trim().ToLower() == targetKey)
                return StripComment(line[(eq + 1)..].Trim());
        }
        return null;
    }

    private void ApplySerial(string key, string val)
    {
        if (key == "port")        Port        = val;
        if (key == "baudrate")    BaudRate    = ParseInt(val, 9600);
        if (key == "timeoutms")   TimeoutMs   = ParseInt(val, 600);
        if (key == "boardaddr")   BoardAddr   = (byte)ParseInt(val, 1);
        if (key == "maxchannels") MaxChannels = ParseInt(val, 24);
    }

    private void ApplyLocker(string key, string val)
    {
        if (key == "preventbothsidesopen") PreventBothSidesOpen = ParseBool(val, true);
        if (key == "autorelockdelayms")    AutoRelockDelayMs    = ParseInt(val, 0);
    }

    private static string StripComment(string v)
    {
        int i = v.IndexOf(';');
        return i >= 0 ? v[..i].Trim() : v;
    }

    private static int  ParseInt (string v, int  def) => int.TryParse(v, out int r) ? r : def;
    private static bool ParseBool(string v, bool def) =>
        v.ToLower() is "true" or "1" or "yes" ? true :
        v.ToLower() is "false" or "0" or "no"  ? false : def;
}

public enum AppMode { Pharmacy, Delivery }
