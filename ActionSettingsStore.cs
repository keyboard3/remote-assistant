using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LunaDesktopHelper;

public sealed record ActionSettings(string Title, string Hotkey)
{
    public static ActionSettings Default { get; } = new("Typeless 语音输入", "RightAlt");
}

public sealed record ShortcutSpec(string Label, ushort[] Modifiers, ushort Key);

public static class ShortcutParser
{
    private static readonly Dictionary<string, ushort> ModifierKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Ctrl"] = 0xA2,
        ["LeftCtrl"] = 0xA2,
        ["RightCtrl"] = 0xA3,
        ["Alt"] = 0xA4,
        ["LeftAlt"] = 0xA4,
        ["RightAlt"] = 0xA5,
        ["Shift"] = 0xA0,
        ["LeftShift"] = 0xA0,
        ["RightShift"] = 0xA1,
        ["Win"] = 0x5B,
        ["LeftWin"] = 0x5B,
        ["RightWin"] = 0x5C
    };

    private static readonly Dictionary<string, ushort> NamedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Space"] = 0x20,
        ["Enter"] = 0x0D,
        ["Tab"] = 0x09,
        ["Escape"] = 0x1B,
        ["Backspace"] = 0x08,
        ["Delete"] = 0x2E,
        ["Insert"] = 0x2D,
        ["Home"] = 0x24,
        ["End"] = 0x23,
        ["PageUp"] = 0x21,
        ["PageDown"] = 0x22,
        ["Left"] = 0x25,
        ["Up"] = 0x26,
        ["Right"] = 0x27,
        ["Down"] = 0x28,
        ["PrintScreen"] = 0x2C
    };

    public static ShortcutSpec Parse(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 80)
            throw new InvalidDataException("请输入一个快捷键；最多 80 个字符。");

        var parts = value.Split('+', StringSplitOptions.TrimEntries);
        if (parts.Any(string.IsNullOrWhiteSpace))
            throw new InvalidDataException("快捷键格式不正确，请用 + 连接修饰键和主键。");
        if (parts.Length < 1 || parts.Length > 5)
            throw new InvalidDataException("最多支持四个修饰键加一个主键。");

        var modifiers = new List<ushort>();
        for (var index = 0; index < parts.Length - 1; index++)
        {
            if (!ModifierKeys.TryGetValue(parts[index], out var modifier))
                throw new InvalidDataException($"不支持修饰键“{parts[index]}”。支持 Ctrl、Alt、Shift、Win 及其左右侧名称。");
            if (modifiers.Contains(modifier))
                throw new InvalidDataException($"修饰键“{parts[index]}”重复。");
            modifiers.Add(modifier);
        }

        var keyName = parts[^1];
        ushort key;
        if (keyName.Length == 1 && char.IsAsciiLetter(keyName[0]))
            key = char.ToUpperInvariant(keyName[0]);
        else if (keyName.Length == 1 && char.IsAsciiDigit(keyName[0]))
            key = keyName[0];
        else if (keyName.StartsWith("F", StringComparison.OrdinalIgnoreCase) &&
                 int.TryParse(keyName.AsSpan(1), out var functionNumber) && functionNumber is >= 1 and <= 24)
            key = (ushort)(0x70 + functionNumber - 1);
        else if (!NamedKeys.TryGetValue(keyName, out key) && !ModifierKeys.TryGetValue(keyName, out key))
            throw new InvalidDataException($"不支持主键“{keyName}”。请用字母、数字、F1–F24、导航键或修饰键名称。");

        if (modifiers.Contains(key))
            throw new InvalidDataException($"按键“{keyName}”与前面的修饰键重复。");

        var canonicalModifiers = parts.Take(parts.Length - 1).Select(name => ModifierKeys.First(pair =>
            string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase)).Key);
        var canonicalKey = keyName.Length == 1 && char.IsAsciiLetterOrDigit(keyName[0])
            ? char.ToUpperInvariant(keyName[0]).ToString()
            : keyName.ToUpperInvariant().StartsWith('F') && key >= 0x70 && key <= 0x87
                ? $"F{key - 0x6F}"
                : NamedKeys.FirstOrDefault(pair => pair.Value == key).Key ??
                  ModifierKeys.FirstOrDefault(pair => string.Equals(pair.Key, keyName, StringComparison.OrdinalIgnoreCase)).Key ??
                  keyName;
        var label = string.Join("+", canonicalModifiers.Append(canonicalKey));
        return new ShortcutSpec(label, modifiers.ToArray(), key);
    }
}

public static class ActionSettingsStore
{
    public static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LunaDesktopHelper",
        "settings.json");

    public static ActionSettings ReadForDisplay(out string? error)
    {
        error = null;
        if (!File.Exists(SettingsPath)) return ActionSettings.Default;

        try
        {
            var root = JsonNode.Parse(File.ReadAllText(SettingsPath)) as JsonObject
                ?? throw new InvalidDataException("设置文件根节点必须是 JSON 对象。");
            var title = ReadString(root, "actionTitle") ?? ActionSettings.Default.Title;
            // Migrate older installations in memory: the legacy Typeless binding
            // remains as-is on disk until a user saves the generalized action.
            var hotkey = ReadString(root, "actionHotkey") ??
                         ReadString(root, "typelessHotkey") ??
                         ActionSettings.Default.Hotkey;
            try { Validate(new ActionSettings(title, hotkey)); }
            catch (Exception ex) when (ex is ArgumentException or InvalidDataException)
            {
                error = ex.Message;
            }
            return new ActionSettings(title, hotkey);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            error = $"无法读取设置：{ex.Message}";
            return ActionSettings.Default;
        }
    }

    public static ActionSettings ReadForAction()
    {
        var settings = ReadForDisplay(out var error);
        if (error is not null) throw new InvalidDataException(error);
        Validate(settings);
        return settings;
    }

    public static ActionSettings Save(string title, string hotkey, out string? backupPath)
    {
        var settings = new ActionSettings(title.Trim(), ShortcutParser.Parse(hotkey).Label);
        Validate(settings);
        backupPath = null;

        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        JsonObject root;
        if (File.Exists(SettingsPath))
        {
            try
            {
                root = JsonNode.Parse(File.ReadAllText(SettingsPath)) as JsonObject
                    ?? throw new InvalidDataException("设置文件根节点必须是 JSON 对象。");
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidDataException)
            {
                // Preserve malformed user data before replacing it with a valid settings object.
                backupPath = SettingsPath + ".broken-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".bak";
                File.Copy(SettingsPath, backupPath, overwrite: false);
                root = new JsonObject();
            }
        }
        else
        {
            root = new JsonObject();
        }

        root["actionTitle"] = settings.Title;
        root["actionHotkey"] = settings.Hotkey;
        // Keep an existing legacy typelessHotkey untouched for rollback compatibility.
        // New installations still include it so older builds retain their Typeless binding.
        root["typelessHotkey"] ??= settings.Hotkey;

        var temporaryPath = SettingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporaryPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporaryPath, SettingsPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }

        return settings;
    }

    private static string? ReadString(JsonObject root, string name)
    {
        if (!root.TryGetPropertyValue(name, out var node) || node is null) return null;
        if (node is JsonValue value && value.TryGetValue<string>(out var text)) return text;
        throw new InvalidDataException($"设置字段“{name}”必须是字符串。");
    }

    private static void Validate(ActionSettings settings)
    {
        var title = settings.Title.Trim();
        if (title.Length is < 1 or > 32 || title.Any(char.IsControl))
            throw new InvalidDataException("动作名称需为 1–32 个可显示字符。");
        _ = ShortcutParser.Parse(settings.Hotkey);
    }
}
