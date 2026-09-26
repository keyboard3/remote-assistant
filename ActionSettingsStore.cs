using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LunaDesktopHelper;

public sealed record ActionSettings(string Title, string Hotkey)
{
    public static ActionSettings Default { get; } = new("Typeless 语音输入", "RightAlt");
}

public static class ActionKinds
{
    public const string SendKeys = "sendKeys";
    public const string LegacyCapture = "capture";
    public const string LegacyDeliverClipboardImage = "deliverClipboardImage";
}

public sealed record ActionDefinition(string Id, string Kind, string Title, string? Shortcut, bool Enabled);

public sealed record ActionSettingsDocument(int SchemaVersion, IReadOnlyList<ActionDefinition> Actions)
{
    public const int CurrentSchemaVersion = 4;
    public const string CaptureId = "builtin.capture";
    public const string CopyId = "builtin.copy";
    public const string PasteId = "builtin.paste";
    public const string LegacyCustomActionId = "custom.legacy";

    public static ActionSettingsDocument Default { get; } = new(CurrentSchemaVersion, new ActionDefinition[]
    {
        new(CaptureId, ActionKinds.SendKeys, "区域截图", "LeftWin+LeftShift+S", true),
        new(CopyId, ActionKinds.SendKeys, "复制", "LeftCtrl+C", true),
        new(PasteId, ActionKinds.SendKeys, "粘贴", "LeftCtrl+V", true),
        new(LegacyCustomActionId, ActionKinds.SendKeys, ActionSettings.Default.Title, ActionSettings.Default.Hotkey, true)
    });

    private ActionDefinition? CompatibilityAction => Actions.FirstOrDefault(a => a.Id == LegacyCustomActionId) ?? Actions.FirstOrDefault(a => a.Kind == ActionKinds.SendKeys);
    public string Title => CompatibilityAction?.Title ?? ActionSettings.Default.Title;
    public string Hotkey => CompatibilityAction?.Shortcut ?? ActionSettings.Default.Hotkey;
    public static implicit operator ActionSettings(ActionSettingsDocument document) => new(document.Title, document.Hotkey);
}

public sealed record ShortcutSpec(string Label, ushort[] Modifiers, ushort Key);

public static class ShortcutParser
{
    private static readonly Dictionary<string, ushort> ModifierKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Ctrl"] = 0xA2, ["LeftCtrl"] = 0xA2, ["RightCtrl"] = 0xA3,
        ["Alt"] = 0xA4, ["LeftAlt"] = 0xA4, ["RightAlt"] = 0xA5,
        ["Shift"] = 0xA0, ["LeftShift"] = 0xA0, ["RightShift"] = 0xA1,
        ["Win"] = 0x5B, ["LeftWin"] = 0x5B, ["RightWin"] = 0x5C
    };

    private static readonly Dictionary<string, ushort> NamedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Space"] = 0x20, ["Enter"] = 0x0D, ["Tab"] = 0x09, ["Escape"] = 0x1B,
        ["Backspace"] = 0x08, ["Delete"] = 0x2E, ["Insert"] = 0x2D, ["Home"] = 0x24,
        ["End"] = 0x23, ["PageUp"] = 0x21, ["PageDown"] = 0x22, ["Left"] = 0x25,
        ["Up"] = 0x26, ["Right"] = 0x27, ["Down"] = 0x28, ["PrintScreen"] = 0x2C
    };

    public static ShortcutSpec Parse(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 80) throw new InvalidDataException("请输入一个快捷键；最多 80 个字符。");
        var parts = value.Split('+', StringSplitOptions.TrimEntries);
        if (parts.Any(string.IsNullOrWhiteSpace)) throw new InvalidDataException("快捷键格式不正确，请用 + 连接修饰键和主键。");
        if (parts.Length is < 1 or > 5) throw new InvalidDataException("最多支持四个修饰键加一个主键。");

        var modifiers = new List<ushort>();
        var names = new List<string>();
        for (var i = 0; i < parts.Length - 1; i++)
        {
            if (!ModifierKeys.TryGetValue(parts[i], out var modifier)) throw new InvalidDataException($"不支持修饰键“{parts[i]}”。");
            if (modifiers.Contains(modifier)) throw new InvalidDataException($"修饰键“{parts[i]}”重复。");
            modifiers.Add(modifier);
            names.Add(CanonicalModifier(parts[i]));
        }

        var keyName = parts[^1];
        var isModifier = ModifierKeys.TryGetValue(keyName, out var modifierKey);
        ushort key;
        if (keyName.Length == 1 && char.IsAsciiLetter(keyName[0])) key = char.ToUpperInvariant(keyName[0]);
        else if (keyName.Length == 1 && char.IsAsciiDigit(keyName[0])) key = keyName[0];
        else if (keyName.StartsWith("F", StringComparison.OrdinalIgnoreCase) && int.TryParse(keyName.AsSpan(1), out var f) && f is >= 1 and <= 24) key = (ushort)(0x70 + f - 1);
        else if (!NamedKeys.TryGetValue(keyName, out key) && !isModifier) throw new InvalidDataException($"不支持主键“{keyName}”。");
        else if (isModifier) key = modifierKey;
        if (modifiers.Contains(key)) throw new InvalidDataException($"按键“{keyName}”与前面的修饰键重复。");

        var canonicalKey = keyName.Length == 1 && char.IsAsciiLetterOrDigit(keyName[0]) ? char.ToUpperInvariant(keyName[0]).ToString()
            : keyName.StartsWith("F", StringComparison.OrdinalIgnoreCase) && key >= 0x70 && key <= 0x87 ? $"F{key - 0x6F}"
            : NamedKeys.FirstOrDefault(p => p.Value == key).Key ?? (isModifier ? CanonicalModifier(keyName) : keyName);
        return new ShortcutSpec(string.Join("+", names.Append(canonicalKey)), modifiers.ToArray(), key);
    }

    private static string CanonicalModifier(string value)
    {
        foreach (var generic in new[] { "Ctrl", "Alt", "Shift", "Win" })
            if (value.Equals(generic, StringComparison.OrdinalIgnoreCase)) return generic;
        return ModifierKeys.Keys.First(k => k.Equals(value, StringComparison.OrdinalIgnoreCase));
    }
}

public static class ActionSettingsStore
{
    public static string SettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LunaDesktopHelper", "settings.json");

    public static ActionSettingsDocument LoadOrCreate(out string? error, out string? migrationBackupPath)
    {
        var document = ReadForDisplay(out error);
        migrationBackupPath = null;
        if (error is not null) return document;

        try
        {
            if (File.Exists(SettingsPath))
            {
                var root = JsonNode.Parse(File.ReadAllText(SettingsPath)) as JsonObject
                    ?? throw new InvalidDataException("设置文件根节点必须是 JSON 对象。");
                if (ReadInt(root, "schemaVersion") == ActionSettingsDocument.CurrentSchemaVersion &&
                    root.TryGetPropertyValue("actions", out var actions) && actions is JsonArray) return document;

                migrationBackupPath = SettingsPath + ".before-shortcuts-" + DateTime.Now.ToString("yyyyMMdd-HHmmssfff") + ".bak";
                File.Copy(SettingsPath, migrationBackupPath, false);
            }

            Save(document, out _);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            error = $"已加载动作，但无法写入新版配置：{ex.Message}";
        }
        return document;
    }

    public static ActionSettingsDocument ReadForDisplay(out string? error)
    {
        error = null;
        if (!File.Exists(SettingsPath)) return ActionSettingsDocument.Default;
        try
        {
            var root = JsonNode.Parse(File.ReadAllText(SettingsPath)) as JsonObject ?? throw new InvalidDataException("设置文件根节点必须是 JSON 对象。");
            var document = root.TryGetPropertyValue("actions", out var node) && node is not null ? ReadActions(root, node) : MigrateLegacy(root);
            try { return Prepare(document); }
            catch (Exception ex) when (ex is ArgumentException or InvalidDataException) { error = ex.Message; return document; }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            error = $"无法读取设置：{ex.Message}";
            return ActionSettingsDocument.Default;
        }
    }

    public static ActionDefinition ReadForAction(string id)
    {
        var document = ReadForDisplay(out var error);
        if (error is not null) throw new InvalidDataException(error);
        return Prepare(document).Actions.FirstOrDefault(a => a.Id == id) ?? throw new InvalidDataException($"找不到动作“{id}”。");
    }

    public static ActionSettings ReadForAction()
    {
        var document = ReadForDisplay(out var error);
        if (error is not null) throw new InvalidDataException(error);
        var prepared = Prepare(document);
        var action = prepared.Actions.FirstOrDefault(a => a.Id == ActionSettingsDocument.LegacyCustomActionId) ?? prepared.Actions.FirstOrDefault(a => a.Kind == ActionKinds.SendKeys) ?? throw new InvalidDataException("没有可执行的自定义按键动作。");
        return new ActionSettings(action.Title, action.Shortcut!);
    }

    public static ActionSettingsDocument Prepare(ActionSettingsDocument document)
    {
        if (document.Actions is null) throw new InvalidDataException("动作列表不能为空。");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var normalized = new List<ActionDefinition>(document.Actions.Count);
        foreach (var source in document.Actions)
        {
            var id = source.Id?.Trim() ?? "";
            var kind = source.Kind?.Trim() ?? "";
            var title = source.Title?.Trim() ?? "";
            if (id.Length is < 1 or > 80 || id.Any(char.IsControl)) throw new InvalidDataException("动作 ID 需为 1–80 个可显示字符。");
            if (!ids.Add(id)) throw new InvalidDataException($"动作 ID“{id}”重复。");
            if (kind != ActionKinds.SendKeys) throw new InvalidDataException($"动作“{title}”使用了不支持的类型“{kind}”。");
            if (title.Length is < 1 or > 32 || title.Any(char.IsControl)) throw new InvalidDataException($"动作“{id}”的名称需为 1–32 个可显示字符。");

            if (string.IsNullOrWhiteSpace(source.Shortcut)) throw new InvalidDataException($"动作“{title}”必须录制执行快捷键。");
            var shortcut = ShortcutParser.Parse(source.Shortcut).Label;
            normalized.Add(new(id, kind, title, shortcut, source.Enabled));
        }
        return new(ActionSettingsDocument.CurrentSchemaVersion, normalized);
    }

    public static ActionSettingsDocument Save(ActionSettingsDocument document, out string? backupPath)
    {
        var prepared = Prepare(document);
        backupPath = null;
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        JsonObject root;
        if (File.Exists(SettingsPath))
        {
            try { root = JsonNode.Parse(File.ReadAllText(SettingsPath)) as JsonObject ?? throw new InvalidDataException("设置文件根节点必须是 JSON 对象。"); }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidDataException)
            {
                backupPath = SettingsPath + ".broken-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".bak";
                File.Copy(SettingsPath, backupPath, false);
                root = new();
            }
        }
        else root = new();

        root["schemaVersion"] = ActionSettingsDocument.CurrentSchemaVersion;
        var array = new JsonArray();
        foreach (var action in prepared.Actions)
            array.Add(new JsonObject { ["id"] = action.Id, ["kind"] = action.Kind, ["title"] = action.Title, ["shortcut"] = action.Shortcut, ["enabled"] = action.Enabled });
        root["actions"] = array;

        var compatibility = prepared.Actions.FirstOrDefault(a => a.Id == ActionSettingsDocument.LegacyCustomActionId) ?? prepared.Actions.FirstOrDefault(a => a.Kind == ActionKinds.SendKeys);
        if (compatibility is not null)
        {
            root["actionTitle"] = compatibility.Title;
            root["actionHotkey"] = compatibility.Shortcut;
            root["typelessHotkey"] ??= compatibility.Shortcut;
        }

        // Write in place instead of replacing the file. In packaged/redirected desktop
        // environments SettingsPath can be a hard link into LocalCache; Move(..., true)
        // breaks that link and makes the update visible only to the current process view.
        var json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(SettingsPath, json);
        var persisted = File.ReadAllText(SettingsPath);
        if (!string.Equals(persisted, json, StringComparison.Ordinal))
            throw new IOException("设置写入后回读不一致，未确认保存成功。");
        return prepared;
    }

    public static ActionSettings Save(string title, string hotkey, out string? backupPath)
    {
        var document = ReadForDisplay(out _);
        var actions = document.Actions.ToList();
        var index = actions.FindIndex(a => a.Id == ActionSettingsDocument.LegacyCustomActionId);
        var replacement = new ActionDefinition(ActionSettingsDocument.LegacyCustomActionId, ActionKinds.SendKeys, title, hotkey, true);
        if (index >= 0) actions[index] = replacement; else actions.Add(replacement);
        var saved = Save(new(ActionSettingsDocument.CurrentSchemaVersion, actions), out backupPath);
        return new(saved.Title, saved.Hotkey);
    }

    private static ActionSettingsDocument ReadActions(JsonObject root, JsonNode node)
    {
        if (node is not JsonArray array) throw new InvalidDataException("设置字段“actions”必须是数组。");
        var version = ReadInt(root, "schemaVersion") ?? 2;
        if (version is not (2 or 3 or ActionSettingsDocument.CurrentSchemaVersion)) throw new InvalidDataException($"不支持的动作设置版本：{version}。");
        var actions = new List<ActionDefinition>();
        foreach (var child in array)
        {
            if (child is not JsonObject item) throw new InvalidDataException("每个动作必须是 JSON 对象。");
            var kind = ReadString(item, "kind", true)!;
            var shortcut = version == 2 ? ReadString(item, "sendHotkey") : ReadString(item, "shortcut");
            var title = ReadString(item, "title", true)!;
            if (version < ActionSettingsDocument.CurrentSchemaVersion)
            {
                if (kind == ActionKinds.LegacyCapture)
                {
                    kind = ActionKinds.SendKeys;
                    if (string.IsNullOrWhiteSpace(shortcut)) shortcut = "LeftWin+LeftShift+S";
                }
                else if (kind == ActionKinds.LegacyDeliverClipboardImage)
                {
                    kind = ActionKinds.SendKeys;
                    shortcut = "LeftCtrl+V";
                    if (title == "投递到当前应用") title = "粘贴";
                }
            }
            actions.Add(new(ReadString(item, "id", true)!, kind, title, shortcut, ReadBool(item, "enabled") ?? true));
        }
        return new(ActionSettingsDocument.CurrentSchemaVersion, actions);
    }

    private static ActionSettingsDocument MigrateLegacy(JsonObject root)
    {
        var title = ReadString(root, "actionTitle") ?? ActionSettings.Default.Title;
        var hotkey = ReadString(root, "actionHotkey") ?? ReadString(root, "typelessHotkey") ?? ActionSettings.Default.Hotkey;
        var actions = ActionSettingsDocument.Default.Actions.ToList();
        actions[^1] = actions[^1] with { Title = title, Shortcut = hotkey };
        return new(ActionSettingsDocument.CurrentSchemaVersion, actions);
    }

    private static string? ReadString(JsonObject root, string name, bool required = false)
    {
        if (!root.TryGetPropertyValue(name, out var node) || node is null) { if (required) throw new InvalidDataException($"设置字段“{name}”不能为空。"); return null; }
        if (node is JsonValue value && value.TryGetValue<string>(out var text)) return text;
        throw new InvalidDataException($"设置字段“{name}”必须是字符串。");
    }

    private static int? ReadInt(JsonObject root, string name)
    {
        if (!root.TryGetPropertyValue(name, out var node) || node is null) return null;
        if (node is JsonValue value && value.TryGetValue<int>(out var number)) return number;
        throw new InvalidDataException($"设置字段“{name}”必须是整数。");
    }

    private static bool? ReadBool(JsonObject root, string name)
    {
        if (!root.TryGetPropertyValue(name, out var node) || node is null) return null;
        if (node is JsonValue value && value.TryGetValue<bool>(out var boolean)) return boolean;
        throw new InvalidDataException($"设置字段“{name}”必须是布尔值。");
    }
}
