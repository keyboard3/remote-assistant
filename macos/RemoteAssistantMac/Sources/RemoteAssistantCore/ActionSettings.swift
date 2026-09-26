import Foundation

public struct AssistantAction: Codable, Identifiable, Equatable {
    public var id: String
    public var kind: String
    public var title: String
    public var shortcut: String
    public var enabled: Bool

    public init(id: String = UUID().uuidString, kind: String = "sendKeys", title: String,
                shortcut: String, enabled: Bool = true) {
        self.id = id
        self.kind = kind
        self.title = title
        self.shortcut = shortcut
        self.enabled = enabled
    }
}

public struct ActionSettingsDocument: Codable, Equatable {
    public var schemaVersion: Int
    public var actions: [AssistantAction]

    public init(schemaVersion: Int = 4, actions: [AssistantAction]) {
        self.schemaVersion = schemaVersion
        self.actions = actions
    }

    public init() { self.init(schemaVersion: 4, actions: Self.defaultActions) }

    public static let `default` = ActionSettingsDocument()

    public static let defaultActions = [
        AssistantAction(id: "region-screenshot", title: "区域截图", shortcut: "Control+Command+Shift+4"),
        AssistantAction(id: "copy", title: "复制", shortcut: "Command+C"),
        AssistantAction(id: "paste", title: "粘贴", shortcut: "Command+V"),
        AssistantAction(id: "typeless", title: "Typeless 语音输入", shortcut: "Fn")
    ]
}

public final class ActionSettingsStore {
    public let fileURL: URL
    private var unknownTopLevelFields: [String: Any] = [:]

    public init(fileURL: URL) { self.fileURL = fileURL }

    public func loadOrCreate() -> (document: ActionSettingsDocument, error: String?, backupURL: URL?) {
        guard FileManager.default.fileExists(atPath: fileURL.path) else {
            unknownTopLevelFields = [:]
            do {
                let document = ActionSettingsDocument()
                _ = try save(document)
                return (document, nil, nil)
            } catch { return (ActionSettingsDocument(), "无法创建设置：\(error.localizedDescription)", nil) }
        }

        do {
            let object = try readObject()
            if let version = object["schemaVersion"] as? Int, version == 4 {
                var doc = try JSONDecoder().decode(ActionSettingsDocument.self, from: Data(contentsOf: fileURL))
                doc = try Self.normalized(doc)
                unknownTopLevelFields = object.filter { $0.key != "schemaVersion" && $0.key != "actions" }
                return (doc, nil, nil)
            }
            if object["actions"] != nil {
                throw AssistantError.message("不支持的动作设置版本；原文件未被修改。")
            }
            let backup = try makeBackup(suffix: "legacy")
            let title = object["actionTitle"] as? String ?? "Typeless 语音输入"
            let hotkey = (object["actionHotkey"] as? String) ?? (object["typelessHotkey"] as? String) ?? "Fn"
            var actions = ActionSettingsDocument.defaultActions
            actions[3].title = title
            actions[3].shortcut = hotkey
            let document = try Self.normalized(ActionSettingsDocument(actions: actions))
            unknownTopLevelFields = object.filter { !["schemaVersion", "actions", "actionTitle", "actionHotkey", "typelessHotkey"].contains($0.key) }
            _ = try save(document)
            return (document, nil, backup)
        } catch {
            unknownTopLevelFields = [:]
            return (ActionSettingsDocument(), "设置无效：\(error.localizedDescription)", nil)
        }
    }

    @discardableResult
    public func save(_ document: ActionSettingsDocument) throws -> ActionSettingsDocument {
        let normalized = try Self.normalized(document)
        try FileManager.default.createDirectory(at: fileURL.deletingLastPathComponent(), withIntermediateDirectories: true)

        if FileManager.default.fileExists(atPath: fileURL.path) {
            if let object = try? readObject(), object["actions"] != nil,
               object["schemaVersion"] as? Int != 4 {
                throw AssistantError.message("不支持的动作设置版本；原文件未被修改。")
            }
            do {
                let object = try readObject()
                if object["schemaVersion"] as? Int == 4 {
                    let existing = try JSONDecoder().decode(ActionSettingsDocument.self, from: Data(contentsOf: fileURL))
                    _ = try Self.normalized(existing)
                    unknownTopLevelFields = object.filter { $0.key != "schemaVersion" && $0.key != "actions" }
                } else if unknownTopLevelFields.isEmpty {
                    unknownTopLevelFields = object.filter { !["schemaVersion", "actions", "actionTitle", "actionHotkey", "typelessHotkey"].contains($0.key) }
                }
            } catch {
                _ = try makeBackup(suffix: "broken")
                unknownTopLevelFields = [:]
            }
        }

        var object = unknownTopLevelFields
        object["schemaVersion"] = 4
        object["actions"] = try JSONSerialization.jsonObject(with: JSONEncoder().encode(normalized.actions))
        let data = try JSONSerialization.data(withJSONObject: object, options: [.prettyPrinted, .sortedKeys])
        try data.write(to: fileURL, options: .atomic)
        return normalized
    }

    private func readObject() throws -> [String: Any] {
        let data = try Data(contentsOf: fileURL)
        guard let object = try JSONSerialization.jsonObject(with: data) as? [String: Any] else {
            throw AssistantError.message("设置文件不是 JSON 对象。")
        }
        return object
    }

    private func makeBackup(suffix: String) throws -> URL {
        let backup = fileURL.deletingLastPathComponent().appendingPathComponent("\(fileURL.deletingPathExtension().lastPathComponent).\(suffix)-\(UUID().uuidString).bak")
        try FileManager.default.copyItem(at: fileURL, to: backup)
        return backup
    }

    private static func normalized(_ document: ActionSettingsDocument) throws -> ActionSettingsDocument {
        guard document.schemaVersion == 4 else { throw AssistantError.message("仅支持 schemaVersion 4。") }
        var result = document
        var ids = Set<String>()
        for index in result.actions.indices {
            var action = result.actions[index]
            guard (1...80).contains(action.id.count), !action.id.unicodeScalars.contains(where: CharacterSet.controlCharacters.contains) else {
                throw AssistantError.message("动作 ID 需为 1–80 个可显示字符。")
            }
            guard ids.insert(action.id).inserted else { throw AssistantError.message("动作 ID 必须唯一。") }
            guard action.kind == "sendKeys" else { throw AssistantError.message("暂只支持 sendKeys 动作。") }
            action.title = action.title.trimmingCharacters(in: .whitespacesAndNewlines)
            action.shortcut = action.shortcut.trimmingCharacters(in: .whitespacesAndNewlines)
            guard (1...32).contains(action.title.count),
                  !action.title.unicodeScalars.contains(where: CharacterSet.controlCharacters.contains) else {
                throw AssistantError.message("动作名称需为 1–32 个可显示字符。")
            }
            guard action.shortcut.count <= 80 else { throw AssistantError.message("快捷键过长。") }
            _ = try Shortcut.parse(action.shortcut)
            result.actions[index] = action
        }
        return result
    }
}
