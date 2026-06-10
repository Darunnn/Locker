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
    public AppMode Mode { get; private set; }

    // ----------------------------------------------------------
    // [Pharmacy] / [Delivery]  — โหลดตาม Mode
    // ----------------------------------------------------------
    public string Port { get; private set; } = null!;
    public int BaudRate { get; private set; }
    public int TimeoutMs { get; private set; }
    public byte BoardAddr { get; private set; }

    /// <summary>
    /// CH ที่เครื่องนี้ควบคุม อ่านจาก ini เท่านั้น
    /// เช่น Pharmacy = [1..25], Delivery = [26..50]
    /// รองรับ range (1-25), เดี่ยว (1,3,5) และผสม (1-10,12,15)
    /// </summary>
    public IReadOnlyList<int> Channels { get; private set; } = null!;

    // ----------------------------------------------------------
    // [Locker]  — shared
    // ----------------------------------------------------------
    public bool PreventBothSidesOpen { get; private set; }
    public int AutoRelockDelayMs { get; private set; }

    // ----------------------------------------------------------
    // Meta
    // ----------------------------------------------------------
    public string ConfigPath { get; private set; } = null!;

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
        if (!File.Exists(ConfigPath))
            throw new FileNotFoundException($"ไม่พบไฟล์ config: {ConfigPath}");

        Mode = ReadAppMode();

        string targetSection = Mode == AppMode.Pharmacy ? "pharmacy" : "delivery";

        var serialValues = new Dictionary<string, string>();
        var lockerValues = new Dictionary<string, string>();

        string section = "";
        foreach (var raw in File.ReadAllLines(ConfigPath))
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

            if (section == targetSection) serialValues[key] = val;
            else if (section == "locker") lockerValues[key] = val;
        }

        // Serial — required ทุกตัว ถ้าหายหรือ parse ไม่ได้ → throw
        Port = RequireKey(serialValues, "port", targetSection);
        BaudRate = ParseRequiredInt(serialValues, "baudrate", targetSection);
        TimeoutMs = ParseRequiredInt(serialValues, "timeoutms", targetSection);
        BoardAddr = (byte)ParseRequiredInt(serialValues, "boardaddr", targetSection);
        Channels = ParseRequiredChannels(serialValues, "channels", targetSection);

        // Locker — required ทุกตัว
        PreventBothSidesOpen = ParseRequiredBool(lockerValues, "preventbothsidesopen", "locker");
        AutoRelockDelayMs = ParseRequiredInt(lockerValues, "autorelockdelayms", "locker");
    }

    // ----------------------------------------------------------
    // Helpers
    // ----------------------------------------------------------
    private AppMode ReadAppMode()
    {
        string raw = ReadRequiredValue("app", "mode");
        return raw.Trim().ToLower() == "delivery" ? AppMode.Delivery : AppMode.Pharmacy;
    }

    /// <summary>Single-key reader — throw ถ้าไม่พบ</summary>
    private string ReadRequiredValue(string targetSection, string targetKey)
    {
        string section = "";
        foreach (var raw in File.ReadAllLines(ConfigPath))
        {
            string line = raw.Trim();
            if (line.StartsWith(';') || line.StartsWith('#') || line.Length == 0) continue;
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1].ToLower();
                continue;
            }
            if (section != targetSection) continue;
            int eq = line.IndexOf('=');
            if (eq < 0) continue;
            if (line[..eq].Trim().ToLower() == targetKey)
                return StripComment(line[(eq + 1)..].Trim());
        }
        throw new InvalidOperationException(
            $"[{targetSection}] ไม่พบ key '{targetKey}' ใน {ConfigPath}");
    }

    private static string RequireKey(Dictionary<string, string> d, string key, string section)
    {
        if (d.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v)) return v;
        throw new InvalidOperationException($"[{section}] ไม่พบ key '{key}' หรือค่าว่าง");
    }

    private static int ParseRequiredInt(Dictionary<string, string> d, string key, string section)
    {
        string raw = RequireKey(d, key, section);
        if (int.TryParse(raw, out int r)) return r;
        throw new InvalidOperationException(
            $"[{section}] key '{key}' ค่า '{raw}' ไม่ใช่ตัวเลข");
    }

    private static bool ParseRequiredBool(Dictionary<string, string> d, string key, string section)
    {
        string raw = RequireKey(d, key, section).ToLower();
        if (raw is "true" or "1" or "yes") return true;
        if (raw is "false" or "0" or "no") return false;
        throw new InvalidOperationException(
            $"[{section}] key '{key}' ค่า '{raw}' ไม่ใช่ true/false");
    }

    /// <summary>
    /// Parse Channels= รองรับ:
    ///   range   : 1-25
    ///   เดี่ยว  : 1,3,5
    ///   ผสม     : 1-10,12,15,20-25
    /// ทุก CH ต้องอยู่ใน 1-50 และ from ต้องไม่เกิน to
    /// </summary>
    private static IReadOnlyList<int> ParseRequiredChannels(
        Dictionary<string, string> d, string key, string section)
    {
        string raw = RequireKey(d, key, section);
        var result = new List<int>();

        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            string p = part.Trim();
            var dash = p.Split('-');

            if (dash.Length == 2
                && int.TryParse(dash[0].Trim(), out int from)
                && int.TryParse(dash[1].Trim(), out int to))
            {
                if (from < 1 || to > 50 || from > to)
                    throw new InvalidOperationException(
                        $"[{section}] channels range '{p}' ไม่ถูกต้อง (ต้องอยู่ใน 1-50 และ from ≤ to)");
                for (int i = from; i <= to; i++) result.Add(i);
            }
            else if (int.TryParse(p, out int single))
            {
                if (single < 1 || single > 50)
                    throw new InvalidOperationException(
                        $"[{section}] channel '{p}' ไม่ถูกต้อง (ต้องอยู่ใน 1-50)");
                result.Add(single);
            }
            else
            {
                throw new InvalidOperationException(
                    $"[{section}] channels ค่า '{p}' parse ไม่ได้");
            }
        }

        if (result.Count == 0)
            throw new InvalidOperationException($"[{section}] channels ว่างเปล่า");

        return result.AsReadOnly();
    }

    private static string StripComment(string v)
    {
        int i = v.IndexOf(';');
        return i >= 0 ? v[..i].Trim() : v;
    }
}

public enum AppMode { Pharmacy, Delivery }