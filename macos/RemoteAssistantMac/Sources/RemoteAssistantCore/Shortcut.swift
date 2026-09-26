import CoreGraphics
import Foundation

public enum AssistantError: LocalizedError {
    case message(String)
    public var errorDescription: String? {
        if case let .message(value) = self { return value }
        return nil
    }
}

public struct Shortcut {
    public struct Key {
        public let code: CGKeyCode
        public let flag: CGEventFlags
        public let isModifier: Bool
    }

    public let keys: [Key]

    public static func parse(_ text: String) throws -> Shortcut {
        let parts = text.split(separator: "+", omittingEmptySubsequences: false)
            .map { $0.trimmingCharacters(in: .whitespaces).uppercased() }
        guard (1...5).contains(parts.count), parts.allSatisfy({ !$0.isEmpty }) else {
            throw AssistantError.message("快捷键需要 1–5 个以 + 分隔的按键。")
        }
        var keys: [Key] = []
        var seen = Set<CGKeyCode>()
        var seenModifierFlags: CGEventFlags = []
        for (index, name) in parts.enumerated() {
            guard let key = key(named: name) else {
                throw AssistantError.message("不支持按键“\(name)”。请使用 Mac 快捷键名称，不能直接沿用 Win 键。")
            }
            guard seen.insert(key.code).inserted else {
                throw AssistantError.message("快捷键不能重复使用同一个物理按键。")
            }
            if key.isModifier {
                guard !seenModifierFlags.contains(key.flag) else {
                    throw AssistantError.message("同一类修饰键不能同时使用左右两侧。")
                }
                seenModifierFlags.insert(key.flag)
            }
            if !key.isModifier && index != parts.count - 1 {
                throw AssistantError.message("字母、数字或功能键必须放在组合键末尾。")
            }
            keys.append(key)
        }
        return Shortcut(keys: keys)
    }

    /// A stable, layout-independent name for keys the shortcut parser can replay.
    /// The recorder uses these names instead of typed characters, which change with
    /// keyboard layout and input method.
    public static func name(forKeyCode code: CGKeyCode) -> String? {
        let modifiers: [CGKeyCode: String] = [
            55: "LeftCommand", 54: "RightCommand",
            59: "LeftControl", 62: "RightControl",
            58: "LeftOption", 61: "RightOption",
            56: "LeftShift", 60: "RightShift", 63: "Fn"
        ]
        if let name = modifiers[code] { return name }

        let names = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789".map { String($0) }
            + ["Space", "Enter", "Tab", "Escape", "Backspace"]
            + (1...12).map { "F\($0)" }
        return names.first { key(named: $0.uppercased())?.code == code }
    }

    private static func key(named name: String) -> Key? {
        let modifiers: [String: (CGKeyCode, CGEventFlags)] = [
            "COMMAND": (55, .maskCommand), "CMD": (55, .maskCommand),
            "LEFTCOMMAND": (55, .maskCommand), "RIGHTCOMMAND": (54, .maskCommand),
            "CONTROL": (59, .maskControl), "CTRL": (59, .maskControl),
            "LEFTCONTROL": (59, .maskControl), "RIGHTCONTROL": (62, .maskControl),
            "OPTION": (58, .maskAlternate), "ALT": (58, .maskAlternate),
            "LEFTOPTION": (58, .maskAlternate), "RIGHTOPTION": (61, .maskAlternate),
            "SHIFT": (56, .maskShift), "LEFTSHIFT": (56, .maskShift),
            "RIGHTSHIFT": (60, .maskShift), "FN": (63, .maskSecondaryFn)
        ]
        if let (code, flag) = modifiers[name] { return Key(code: code, flag: flag, isModifier: true) }
        let letters: [String: CGKeyCode] = [
            "A": 0, "B": 11, "C": 8, "D": 2, "E": 14, "F": 3, "G": 5,
            "H": 4, "I": 34, "J": 38, "K": 40, "L": 37, "M": 46,
            "N": 45, "O": 31, "P": 35, "Q": 12, "R": 15, "S": 1,
            "T": 17, "U": 32, "V": 9, "W": 13, "X": 7, "Y": 16, "Z": 6
        ]
        let digits: [String: CGKeyCode] = [
            "0": 29, "1": 18, "2": 19, "3": 20, "4": 21,
            "5": 23, "6": 22, "7": 26, "8": 28, "9": 25
        ]
        let named: [String: CGKeyCode] = [
            "SPACE": 49, "ENTER": 36, "RETURN": 36, "TAB": 48,
            "ESC": 53, "ESCAPE": 53, "BACKSPACE": 51, "DELETE": 51,
            "F1": 122, "F2": 120, "F3": 99, "F4": 118,
            "F5": 96, "F6": 97, "F7": 98, "F8": 100,
            "F9": 101, "F10": 109, "F11": 103, "F12": 111
        ]
        guard let code = letters[name] ?? digits[name] ?? named[name] else { return nil }
        return Key(code: code, flag: [], isModifier: false)
    }
}
