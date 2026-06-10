using System.Reflection;

namespace ConDmsLockerCmd;

/// <summary>
/// โหลดค่าจาก locker_config.ini
/// อ่าน [App] Mode= เพื่อรู้ว่าเครื่องนี้เป็น Pharmacy หรือ Delivery
/// แล้วโหลด section ที่ตรงกัน ([Pharmacy] หรือ [Delivery])
///
/// [ChannelMap] เป็นแหล่งข้อมูลเดียวสำหรับ channel:
///   - Label = ชื่อที่แสดงบน UI (A, B, C ...)
///   - CH    = หมายเลข channel จริงบน board (1-50)
///   - Channels property derive มาจาก ChannelMap อัตโนมัติ
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
    public string? Port { get; private set; }
    public int BaudRate { get; private set; }
    public int TimeoutMs { get; private set; }
    public byte BoardAddr { get; private set; }

    // ----------------------------------------------------------
    // [ChannelMap]  — แหล่งข้อมูลเดียวสำหรับ channel
    // ----------------------------------------------------------
    /// <summary>
    /// Label → CH mapping ตามลำดับใน ini
    /// key   = label บน UI เช่น "A", "B"
    /// value = CH number จริง เช่น 1, 3, 5
    /// </summary>
    public IReadOnlyList<(string Label, int Channel)> ChannelMap { get; private set; } = null!;

    /// <summary>
    /// Derive จาก ChannelMap — CH ทั้งหมดที่เครื่องนี้ควบคุม
    /// ใช้แทน Channels= เดิมใน ini
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
        // ใช้ List of tuple เพื่อรักษาลำดับบรรทัดจาก ini
        var mapEntries = new List<(string key, string value)>();

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
            else if (section == "channelmap") mapEntries.Add((key, val));
        }

        // Serial — required ทุกตัว
        Port = serialValues.TryGetValue("port", out var p) ? p : null;
        BaudRate = ParseRequiredInt(serialValues, "baudrate", targetSection);
        TimeoutMs = ParseRequiredInt(serialValues, "timeoutms", targetSection);
        BoardAddr = (byte)ParseRequiredInt(serialValues, "boardaddr", targetSection);

        // ChannelMap — required, derive Channels จากมัน
        ChannelMap = ParseChannelMap(mapEntries);
        Channels = ChannelMap.Select(x => x.Channel).ToList().AsReadOnly();

        // Locker — required ทุกตัว
        PreventBothSidesOpen = ParseRequiredBool(lockerValues, "preventbothsidesopen", "locker");
        AutoRelockDelayMs = ParseRequiredInt(lockerValues, "autorelockdelayms", "locker");
    }

    // ----------------------------------------------------------
    // ChannelMap helpers
    // ----------------------------------------------------------

    /// <summary>แปลง label → CH, throw ถ้าไม่พบ</summary>
    public int LabelToChannel(string label)
    {
        var entry = ChannelMap.FirstOrDefault(
            x => x.Label.Equals(label, StringComparison.OrdinalIgnoreCase));

        if (entry == default)
            throw new InvalidOperationException(
                $"ไม่พบ label '{label}' ใน [ChannelMap]");

        return entry.Channel;
    }

    /// <summary>แปลง CH → label, คืน "CH{n}" ถ้าไม่มี map</summary>
    public string ChannelToLabel(int ch)
    {
        var entry = ChannelMap.FirstOrDefault(x => x.Channel == ch);
        return entry == default ? $"CH{ch}" : entry.Label;
    }

    // ----------------------------------------------------------
    // Parse [ChannelMap]
    // ----------------------------------------------------------
    private static IReadOnlyList<(string Label, int Channel)> ParseChannelMap(
        List<(string key, string value)> entries)
    {
        if (entries.Count == 0)
            throw new InvalidOperationException(
                "[ChannelMap] ไม่มี entry เลย — ต้องระบุอย่างน้อย 1 รายการ");

        var result = new List<(string Label, int Channel)>();
        var seenLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenCh = new HashSet<int>();

        foreach (var (rawLabel, rawVal) in entries)
        {
            string label = rawLabel.ToUpper();

            if (!seenLabels.Add(label))
                throw new InvalidOperationException(
                    $"[ChannelMap] label '{label}' ซ้ำ");

            if (!int.TryParse(rawVal, out int ch))
                throw new InvalidOperationException(
                    $"[ChannelMap] label '{label}' ค่า '{rawVal}' ไม่ใช่ตัวเลข");

            if (ch < 1 || ch > 50)
                throw new InvalidOperationException(
                    $"[ChannelMap] label '{label}' → CH {ch} ต้องอยู่ใน 1-50");

            if (!seenCh.Add(ch))
                throw new InvalidOperationException(
                    $"[ChannelMap] CH {ch} ซ้ำ");

            result.Add((label, ch));
        }

        return result.AsReadOnly();
    }

    // ----------------------------------------------------------
    // Private helpers
    // ----------------------------------------------------------
    private AppMode ReadAppMode()
    {
        string raw = ReadRequiredValue("app", "mode");
        return raw.Trim().ToLower() == "delivery" ? AppMode.Delivery : AppMode.Pharmacy;
    }

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

    private static string StripComment(string v)
    {
        int i = v.IndexOf(';');
        return i >= 0 ? v[..i].Trim() : v;
    }
}

public enum AppMode { Pharmacy, Delivery }