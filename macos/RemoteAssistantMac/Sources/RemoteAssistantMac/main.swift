import AppKit
import ApplicationServices
import CoreGraphics
import SwiftUI
import RemoteAssistantCore

private enum KeyboardSender {
    static func checkPermission() throws {
        guard CGPreflightPostEventAccess() else {
            _ = CGRequestPostEventAccess()
            throw AssistantError.message("请在 macOS 隐私设置中允许远程助手发送键盘事件，然后重试。")
        }
    }

    static func send(_ shortcut: Shortcut, to targetPID: pid_t) throws {
        try checkPermission()
        guard NSWorkspace.shared.frontmostApplication?.processIdentifier == targetPID else {
            throw AssistantError.message("前台应用已变化；请重新聚焦目标输入框后重试。")
        }
        for key in shortcut.keys where CGEventSource.keyState(.combinedSessionState, key: key.code) {
            throw AssistantError.message("快捷键中的按键已处于按下状态；请松开后重试。")
        }
        var events: [(CGEvent, Bool)] = []
        var flags: CGEventFlags = []
        for key in shortcut.keys {
            if key.isModifier { flags.insert(key.flag) }
            guard let event = CGEvent(keyboardEventSource: nil, virtualKey: key.code, keyDown: true) else {
                throw AssistantError.message("无法生成快捷键按下事件。")
            }
            event.flags = flags
            events.append((event, true))
        }
        for key in shortcut.keys.reversed() {
            guard let event = CGEvent(keyboardEventSource: nil, virtualKey: key.code, keyDown: false) else {
                throw AssistantError.message("无法生成快捷键释放事件。")
            }
            if key.isModifier { flags.remove(key.flag) }
            event.flags = flags
            events.append((event, false))
        }
        let count = shortcut.keys.count
        for index in 0..<count { events[index].0.post(tap: .cghidEventTap) }
        Thread.sleep(forTimeInterval: 0.10)
        for index in count..<events.count { events[index].0.post(tap: .cghidEventTap) }
        // Quartz does not provide a target-application completion acknowledgment.
    }
}

private enum SettingsStore {
    static var fileURL: URL {
        let root = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
        return root.appendingPathComponent("RemoteAssistantMac", isDirectory: true)
            .appendingPathComponent("settings.json")
    }

    static func load() -> (String, String, String?) {
        guard FileManager.default.fileExists(atPath: fileURL.path) else {
            return ("Typeless 语音输入", "Fn", nil)
        }
        do {
            let data = try Data(contentsOf: fileURL)
            guard let object = try JSONSerialization.jsonObject(with: data) as? [String: Any] else {
                throw AssistantError.message("设置文件不是 JSON 对象。")
            }
            let title: String
            if let value = object["actionTitle"] {
                guard let text = value as? String else { throw AssistantError.message("actionTitle 必须是文字。") }
                title = text
            } else { title = "Typeless 语音输入" }
            let hotkey: String
            if let value = object["actionHotkey"] ?? object["typelessHotkey"] {
                guard let text = value as? String else { throw AssistantError.message("actionHotkey 必须是文字。") }
                hotkey = text
            } else { hotkey = "Fn" }
            try validate(title: title, hotkey: hotkey)
            return (title, hotkey, nil)
        } catch {
            return ("Typeless 语音输入", "Fn", "设置无效：\(error.localizedDescription)。请打开齿轮修复。")
        }
    }

    static func validate(title: String, hotkey: String) throws {
        let trimmed = title.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty, trimmed.count <= 32 else {
            throw AssistantError.message("动作名称需为 1–32 个字符。")
        }
        guard hotkey.count <= 80 else { throw AssistantError.message("快捷键过长。") }
        _ = try Shortcut.parse(hotkey)
    }

    static func save(title: String, hotkey: String) throws {
        try validate(title: title, hotkey: hotkey)
        let url = fileURL
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        var object: [String: Any] = [:]
        if FileManager.default.fileExists(atPath: url.path) {
            do {
                let data = try Data(contentsOf: url)
                guard let current = try JSONSerialization.jsonObject(with: data) as? [String: Any] else {
                    throw AssistantError.message("设置文件不是 JSON 对象。")
                }
                if let value = current["actionTitle"], !(value is String) {
                    throw AssistantError.message("actionTitle 必须是文字。")
                }
                if let value = current["actionHotkey"] ?? current["typelessHotkey"], !(value is String) {
                    throw AssistantError.message("actionHotkey 必须是文字。")
                }
                object = current
            } catch {
                let backup = url.deletingLastPathComponent().appendingPathComponent("settings.broken-\(UUID().uuidString).bak")
                try FileManager.default.copyItem(at: url, to: backup)
            }
        }
        object["actionTitle"] = title.trimmingCharacters(in: .whitespacesAndNewlines)
        object["actionHotkey"] = hotkey.trimmingCharacters(in: .whitespacesAndNewlines)
        let data = try JSONSerialization.data(withJSONObject: object, options: [.prettyPrinted, .sortedKeys])
        try data.write(to: url, options: .atomic)
    }
}

private enum ImageFileService {
    static func writeClipboardImage() throws -> URL {
        guard let image = NSImage(pasteboard: .general),
              let cgImage = image.cgImage(forProposedRect: nil, context: nil, hints: nil),
              let png = NSBitmapImageRep(cgImage: cgImage).representation(using: .png, properties: [:]) else {
            throw AssistantError.message("剪贴板中没有可投递的图片。")
        }
        let root = FileManager.default.urls(for: .cachesDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("RemoteAssistantMac/drops", isDirectory: true)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        let now = Date()
        if let oldFiles = try? FileManager.default.contentsOfDirectory(at: root, includingPropertiesForKeys: [.contentModificationDateKey]) {
            for url in oldFiles where url.lastPathComponent.hasPrefix("RemoteAssistant-") && url.pathExtension == "png" {
                let modified = (try? url.resourceValues(forKeys: [.contentModificationDateKey]))?.contentModificationDate ?? now
                if now.timeIntervalSince(modified) > 86_400 { try? FileManager.default.removeItem(at: url) }
            }
        }
        let file = root.appendingPathComponent("RemoteAssistant-\(UUID().uuidString).png")
        try png.write(to: file, options: .atomic)
        return file
    }
}

private final class AssistantController: NSObject, ObservableObject, NSApplicationDelegate, NSWindowDelegate {
    @Published var expanded = false
    @Published var capturing = false
    @Published var status = "操作后菜单保持展开，可手动收起。"
    @Published var actionTitle = "Typeless 语音输入"
    @Published var actionHotkey = "Fn"
    private var panel: NSPanel!
    private var settingsWindow: NSWindow?
    private var captureProcess: Process?
    private var captureChangeCount = 0
    private var dragOrigin: NSPoint?
    private var settingsError: String?
    private let compactSize = NSSize(width: 88, height: 88)
    private let menuSize = NSSize(width: 288, height: 424)
    private var rightEdge = true

    func applicationDidFinishLaunching(_ notification: Notification) {
        NSApp.setActivationPolicy(.accessory)
        reloadSettings()
        panel = NSPanel(contentRect: NSRect(origin: .zero, size: compactSize),
                        styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        panel.isFloatingPanel = true
        panel.becomesKeyOnlyIfNeeded = true
        panel.hidesOnDeactivate = false
        panel.level = .floating
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
        panel.isOpaque = false
        panel.backgroundColor = .clear
        panel.hasShadow = true
        panel.contentView = NSHostingView(rootView: AssistantView(model: self))
        let screen = NSScreen.main ?? NSScreen.screens[0]
        let area = screen.visibleFrame
        let defaults = UserDefaults.standard
        let hasSavedPosition = defaults.object(forKey: "panelX") != nil && defaults.object(forKey: "panelY") != nil
        let origin = hasSavedPosition
            ? NSPoint(x: defaults.double(forKey: "panelX"), y: defaults.double(forKey: "panelY"))
            : NSPoint(x: area.maxX - compactSize.width - 6, y: area.midY)
        panel.setFrameOrigin(origin)
        snapToEdge()
        panel.orderFrontRegardless()
    }

    func expand() {
        guard !expanded else { return }
        reloadSettings()
        expanded = true
        resizePanel(to: menuSize)
    }

    private func reloadSettings() {
        let saved = SettingsStore.load()
        actionTitle = saved.0
        actionHotkey = saved.1
        settingsError = saved.2
        if let error = saved.2 { status = error }
    }

    func collapse() {
        guard expanded else { return }
        expanded = false
        resizePanel(to: compactSize)
        snapToEdge()
    }

    private func resizePanel(to size: NSSize) {
        let oldFrame = panel.frame
        panel.setContentSize(size)
        var origin = oldFrame.origin
        if rightEdge { origin.x = oldFrame.maxX - size.width }
        origin.y = oldFrame.maxY - size.height
        panel.setFrameOrigin(origin)
        clampToVisibleScreen()
    }

    func dragChanged(_ translation: CGSize) {
        if dragOrigin == nil { dragOrigin = panel.frame.origin }
        guard let origin = dragOrigin else { return }
        panel.setFrameOrigin(NSPoint(x: origin.x + translation.width, y: origin.y - translation.height))
    }

    func dragEnded() {
        dragOrigin = nil
        snapToEdge()
    }

    private func screenForPanel() -> NSScreen {
        let point = NSPoint(x: panel.frame.midX, y: panel.frame.midY)
        return NSScreen.screens.first(where: { $0.frame.contains(point) }) ?? NSScreen.main ?? NSScreen.screens[0]
    }

    private func clampToVisibleScreen() {
        let area = screenForPanel().visibleFrame
        let x = min(max(panel.frame.minX, area.minX + 6), max(area.minX + 6, area.maxX - panel.frame.width - 6))
        let y = min(max(panel.frame.minY, area.minY), max(area.minY, area.maxY - panel.frame.height))
        panel.setFrameOrigin(NSPoint(x: x, y: y))
    }

    private func snapToEdge() {
        let area = screenForPanel().visibleFrame
        rightEdge = panel.frame.midX >= area.midX
        let x = rightEdge ? area.maxX - panel.frame.width - 6 : area.minX + 6
        let y = min(max(panel.frame.minY, area.minY), max(area.minY, area.maxY - panel.frame.height))
        panel.setFrameOrigin(NSPoint(x: x, y: y))
        UserDefaults.standard.set(x, forKey: "panelX")
        UserDefaults.standard.set(y, forKey: "panelY")
    }

    private func targetPID() throws -> pid_t {
        guard let target = NSWorkspace.shared.frontmostApplication,
              target.processIdentifier != ProcessInfo.processInfo.processIdentifier else {
            throw AssistantError.message("请先聚焦目标应用的输入框，再点击助手按钮。")
        }
        return target.processIdentifier
    }

    func capture() {
        guard !capturing else { return }
        capturing = true
        captureChangeCount = NSPasteboard.general.changeCount
        status = "截图中…取消后可点“结束截图等待”。"
        let task = Process()
        task.executableURL = URL(fileURLWithPath: "/usr/sbin/screencapture")
        task.arguments = ["-i", "-c"]
        task.terminationHandler = { [weak self] finished in
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.2) {
                guard let self = self, self.captureProcess === finished else { return }
                self.captureProcess = nil
                self.capturing = false
                let board = NSPasteboard.general
                self.status = finished.terminationStatus == 0 && board.changeCount != self.captureChangeCount && NSImage(pasteboard: board) != nil
                    ? "截图已复制到剪贴板。"
                    : "截图已取消或未产生图片；可重新选择区域截图。"
            }
        }
        do {
            try task.run()
            captureProcess = task
        } catch {
            capturing = false
            status = "无法启动系统截图：\(error.localizedDescription)"
        }
    }

    func endCaptureWait() {
        guard capturing else { return }
        let task = captureProcess
        captureProcess = nil
        capturing = false
        if task?.isRunning == true { task?.terminate() }
        status = "已结束截图等待；可重新选择区域截图。"
    }

    func pasteImage() {
        guard !capturing else { return }
        var replacementChangeCount: Int?
        var originalPNG: Data?
        do {
            let target = try targetPID()
            try KeyboardSender.checkPermission()
            let file = try ImageFileService.writeClipboardImage()
            originalPNG = try Data(contentsOf: file)
            let pasteShortcut = try Shortcut.parse("Command+V")
            guard NSWorkspace.shared.frontmostApplication?.processIdentifier == target else {
                throw AssistantError.message("前台应用已变化；没有投递图片。")
            }
            let board = NSPasteboard.general
            board.clearContents()
            replacementChangeCount = board.changeCount
            guard board.writeObjects([file as NSURL]) else {
                throw AssistantError.message("无法将 PNG 文件写入系统剪贴板。")
            }
            replacementChangeCount = board.changeCount
            panel.orderOut(nil)
            defer { panel.orderFrontRegardless() }
            try KeyboardSender.send(pasteShortcut, to: target)
            status = "已向目标发送 PNG 文件粘贴；请确认目标应用已接收。"
        } catch {
            var restoration = ""
            let board = NSPasteboard.general
            if let count = replacementChangeCount, board.changeCount == count, let png = originalPNG {
                board.clearContents()
                let item = NSPasteboardItem()
                _ = item.setData(png, forType: .png)
                if !board.writeObjects([item]) { restoration = "；原图片剪贴板也未能恢复" }
            }
            status = "图片投递失败：\(error.localizedDescription)\(restoration)"
        }
    }

    func performAction() {
        guard !capturing else { return }
        do {
            reloadSettings()
            if let settingsError = settingsError { throw AssistantError.message(settingsError) }
            let target = try targetPID()
            let shortcut = try Shortcut.parse(actionHotkey)
            panel.orderOut(nil)
            defer { panel.orderFrontRegardless() }
            try KeyboardSender.send(shortcut, to: target)
            status = "已向目标发送 \(actionHotkey)；未确认“\(actionTitle)”是否生效。"
        } catch {
            status = "快捷动作失败：\(error.localizedDescription)"
        }
    }

    func showSettings() {
        guard !capturing else { return }
        panel.orderOut(nil)
        let window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 400, height: 220),
                              styleMask: [.titled, .closable], backing: .buffered, defer: false)
        window.title = "快捷操作设置 · 远程助手"
        window.contentView = NSHostingView(rootView: SettingsView(model: self))
        window.delegate = self
        window.center()
        settingsWindow = window
        if #available(macOS 14.0, *) { NSApp.activate() }
        else { NSApp.activate(ignoringOtherApps: true) }
        window.makeKeyAndOrderFront(nil)
    }

    func saveSettings(title: String, hotkey: String) -> String? {
        do {
            try SettingsStore.save(title: title, hotkey: hotkey)
            actionTitle = title.trimmingCharacters(in: .whitespacesAndNewlines)
            actionHotkey = hotkey.trimmingCharacters(in: .whitespacesAndNewlines)
            settingsError = nil
            status = "设置已保存；请重新聚焦目标应用后再触发动作。"
            settingsWindow?.close()
            return nil
        } catch {
            return error.localizedDescription
        }
    }

    func windowWillClose(_ notification: Notification) {
        settingsWindow = nil
        panel.orderFrontRegardless()
    }
}

private struct AssistantView: View {
    @ObservedObject var model: AssistantController
    private let background = Color(red: 0.11, green: 0.14, blue: 0.18)
    private let accent = Color(red: 0.35, green: 0.85, blue: 0.65)

    var body: some View {
        Group {
            if model.expanded { menu } else { capsule }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(RoundedRectangle(cornerRadius: model.expanded ? 22 : 44).fill(background))
        .overlay(RoundedRectangle(cornerRadius: model.expanded ? 22 : 44).stroke(.gray.opacity(0.5)))
    }

    private var capsule: some View {
        VStack(spacing: 4) {
            Image(systemName: "desktopcomputer")
                .font(.system(size: 25))
                .foregroundColor(accent)
                .frame(width: 38, height: 38)
                .background(Circle().fill(accent.opacity(0.13)))
            Text("远程助手").font(.system(size: 12, weight: .semibold)).foregroundColor(.white)
        }
        .frame(width: 88, height: 88)
        .contentShape(Circle())
        .onTapGesture { model.expand() }
        .simultaneousGesture(DragGesture(minimumDistance: 4)
            .onChanged { model.dragChanged($0.translation) }
            .onEnded { _ in model.dragEnded() })
        .accessibilityLabel("远程助手，单击展开菜单")
    }

    private var menu: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack {
                VStack(alignment: .leading, spacing: 2) {
                    Text("远程助手").font(.system(size: 16, weight: .semibold)).foregroundColor(.white)
                    Text("远程桌面快捷操作").font(.system(size: 11)).foregroundColor(.gray)
                }
                Spacer()
                Button(action: model.showSettings) { Image(systemName: "gearshape") }
                    .help("设置快捷操作")
                Button(action: model.collapse) { Image(systemName: "xmark") }
                    .help("收起菜单")
            }
            .buttonStyle(.plain)
            Text("快捷操作").font(.system(size: 11, weight: .semibold)).foregroundColor(accent)
            actionButton(model.capturing ? "结束截图等待" : "区域截图",
                         subtitle: model.capturing ? "取消后点此恢复截图按钮" : "截取画面并复制到剪贴板",
                         symbol: model.capturing ? "arrow.counterclockwise" : "viewfinder",
                         action: model.capturing ? model.endCaptureWait : model.capture)
            actionButton("投递到当前应用", subtitle: "先聚焦输入框，再投递 PNG 文件", symbol: "doc.on.clipboard", action: model.pasteImage)
                .disabled(model.capturing)
            actionButton(model.actionTitle, subtitle: "先聚焦输入框 · \(model.actionHotkey)", symbol: "waveform", action: model.performAction)
                .disabled(model.capturing)
            Text(model.status)
                .font(.system(size: 10.5)).foregroundColor(.gray)
                .lineLimit(3).frame(height: 34, alignment: .topLeading)
            Button("收起菜单", action: model.collapse)
                .buttonStyle(.plain)
                .font(.system(size: 12))
                .foregroundColor(.white.opacity(0.8))
                .frame(maxWidth: .infinity)
                .frame(height: 30)
                .background(RoundedRectangle(cornerRadius: 8).fill(.white.opacity(0.07)))
        }
        .padding(14)
        .frame(width: 288, height: 424, alignment: .topLeading)
    }

    private func actionButton(_ title: String, subtitle: String, symbol: String, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            HStack(spacing: 10) {
                Image(systemName: symbol).font(.system(size: 20)).foregroundColor(accent)
                    .frame(width: 38, height: 38)
                    .background(RoundedRectangle(cornerRadius: 11).fill(accent.opacity(0.12)))
                VStack(alignment: .leading, spacing: 3) {
                    Text(title).font(.system(size: 14, weight: .semibold)).foregroundColor(.white)
                    Text(subtitle).font(.system(size: 10.5)).foregroundColor(.gray).lineLimit(1)
                }
                Spacer(minLength: 0)
                Image(systemName: "chevron.right").foregroundColor(.gray)
            }
            .padding(.horizontal, 8)
            .frame(height: 66)
            .background(RoundedRectangle(cornerRadius: 12).fill(.white.opacity(0.06)))
        }
        .buttonStyle(.plain)
    }
}

private struct SettingsView: View {
    @ObservedObject var model: AssistantController
    @State private var title: String
    @State private var hotkey: String
    @State private var error: String?

    init(model: AssistantController) {
        self.model = model
        _title = State(initialValue: model.actionTitle)
        _hotkey = State(initialValue: model.actionHotkey)
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            Text("快捷操作设置").font(.headline)
            TextField("动作名称", text: $title)
            TextField("快捷键，如 Fn 或 Command+Shift+R", text: $hotkey)
            Text("请与目标应用中绑定的快捷键保持一致。Windows 的 Win 键设置不会自动迁移。")
                .font(.caption).foregroundColor(.secondary)
            if let error = error { Text(error).font(.caption).foregroundColor(.red) }
            HStack {
                Spacer()
                Button("取消") { NSApp.keyWindow?.close() }
                Button("保存设置") { error = model.saveSettings(title: title, hotkey: hotkey) }
                    .keyboardShortcut(.defaultAction)
            }
        }
        .padding(22)
        .frame(width: 400, height: 220)
    }
}

let application = NSApplication.shared
let controller = AssistantController()
application.delegate = controller
application.run()
