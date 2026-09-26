import AppKit
import ApplicationServices
import CoreGraphics
import SwiftUI
import UniformTypeIdentifiers
import RemoteAssistantCore

private let actionDragType = UTType(exportedAs: "com.remoteassistantmac.action-id")

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

private final class AssistantController: NSObject, ObservableObject, NSApplicationDelegate, NSWindowDelegate {
    @Published var expanded = false
    @Published var status = "操作后菜单保持展开，可手动收起。"
    @Published var actions = ActionSettingsDocument.default.actions
    @Published var menuHeight: CGFloat = 520
    private var panel: NSPanel!
    private var settingsWindow: NSWindow?
    private var dragOrigin: NSPoint?
    private var settingsError: String?
    private let compactSize = NSSize(width: 88, height: 88)
    private var rightEdge = true
    private let settingsStore: ActionSettingsStore = {
        let root = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
        let url = root.appendingPathComponent("RemoteAssistantMac", isDirectory: true)
            .appendingPathComponent("settings.json")
        return ActionSettingsStore(fileURL: url)
    }()

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
        menuHeight = desiredMenuHeight()
        resizePanel(to: NSSize(width: 300, height: menuHeight))
    }

    private func reloadSettings() {
        let loaded = settingsStore.loadOrCreate()
        actions = loaded.document.actions
        settingsError = loaded.error
        if let error = loaded.error { status = error }
    }

    private func desiredMenuHeight() -> CGFloat {
        let maxHeight = max(280, screenForPanel().visibleFrame.height - 24)
        return min(maxHeight, max(330, CGFloat(actions.count) * 76 + 220))
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

    func performAction(_ action: AssistantAction) {
        do {
            if let settingsError = settingsError { throw AssistantError.message(settingsError) }
            let target = try targetPID()
            let shortcut = try Shortcut.parse(action.shortcut)
            panel.orderOut(nil)
            defer { panel.orderFrontRegardless() }
            try KeyboardSender.send(shortcut, to: target)
            status = "已发送 \(action.shortcut)；请确认“\(action.title)”的结果。"
        } catch {
            status = "快捷动作失败：\(error.localizedDescription)"
        }
    }

    func showSettings() {
        if let settingsWindow = settingsWindow {
            settingsWindow.makeKeyAndOrderFront(nil)
            return
        }
        panel.orderOut(nil)
        let window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 620, height: 620),
                              styleMask: [.titled, .closable, .resizable], backing: .buffered, defer: false)
        window.title = "快捷操作设置 · 远程助手"
        window.minSize = NSSize(width: 520, height: 400)
        window.contentView = NSHostingView(rootView: SettingsView(model: self))
        window.delegate = self
        window.center()
        settingsWindow = window
        if #available(macOS 14.0, *) { NSApp.activate() }
        else { NSApp.activate(ignoringOtherApps: true) }
        window.makeKeyAndOrderFront(nil)
    }

    func saveSettings(_ editedActions: [AssistantAction]) -> String? {
        do {
            let saved = try settingsStore.save(ActionSettingsDocument(actions: editedActions))
            actions = saved.actions
            settingsError = nil
            status = "设置已保存；请重新聚焦目标应用后再触发动作。"
            if expanded {
                menuHeight = desiredMenuHeight()
                resizePanel(to: NSSize(width: 300, height: menuHeight))
            }
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
            ScrollView {
                LazyVStack(spacing: 10) {
                    ForEach(model.actions) { action in
                        actionButton(action.title, subtitle: "执行 · \(action.shortcut)", symbol: "keyboard") {
                            model.performAction(action)
                        }
                        .disabled(!action.enabled)
                    }
                    if model.actions.isEmpty {
                        Text("暂无按钮，可通过齿轮新增。")
                            .font(.system(size: 12)).foregroundColor(.gray)
                            .frame(maxWidth: .infinity, minHeight: 60)
                    }
                }
            }
            Text(model.status)
                .font(.system(size: 10.5)).foregroundColor(.gray)
                .lineLimit(3).frame(height: 36, alignment: .topLeading)
            Button("收起菜单", action: model.collapse)
                .buttonStyle(.plain)
                .font(.system(size: 12))
                .foregroundColor(.white.opacity(0.8))
                .frame(maxWidth: .infinity)
                .frame(height: 30)
                .background(RoundedRectangle(cornerRadius: 8).fill(.white.opacity(0.07)))
            Button("退出助手") { NSApp.terminate(nil) }
                .buttonStyle(.plain)
                .font(.system(size: 12))
                .foregroundColor(.red.opacity(0.8))
                .frame(maxWidth: .infinity)
                .frame(height: 30)
                .background(RoundedRectangle(cornerRadius: 8).fill(.white.opacity(0.07)))
        }
        .padding(14)
        .frame(width: 300, height: model.menuHeight, alignment: .topLeading)
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
    @State private var draft: [AssistantAction]
    @State private var error: String?
    @State private var draggedID: String?
    @State private var manualEntry = false

    init(model: AssistantController) {
        self.model = model
        _draft = State(initialValue: model.actions)
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack {
                Text("快捷操作设置").font(.title3).fontWeight(.semibold)
                Spacer()
                Button("＋ 新增按钮") {
                    draft.append(AssistantAction(id: "custom.\(UUID().uuidString)", title: "新快捷操作", shortcut: ""))
                }
            }
            HStack {
                Text("拖动左侧 ≡ 调整顺序；点击快捷键框后直接按键录制。")
                    .font(.caption).foregroundColor(.secondary)
                Spacer()
                Toggle("手动编辑", isOn: $manualEntry).font(.caption)
                    .help("系统快捷键无法录制时，可手动输入组合键")
            }
            ScrollView {
                LazyVStack(spacing: 10) {
                    ForEach($draft) { $action in
                        VStack(alignment: .leading, spacing: 8) {
                            HStack(spacing: 8) {
                                Image(systemName: "line.3.horizontal")
                                    .frame(width: 24, height: 32)
                                    .foregroundColor(.secondary)
                                    .help("按住拖动调整顺序")
                                    .onDrag {
                                        draggedID = action.id
                                        let provider = NSItemProvider()
                                        let id = action.id
                                        provider.registerDataRepresentation(for: actionDragType, visibility: .ownProcess) { completion in
                                            completion(Data(id.utf8), nil)
                                            return nil
                                        }
                                        return provider
                                    }
                                Toggle("启用", isOn: $action.enabled).labelsHidden()
                                TextField("按钮名称", text: $action.title)
                                Button { move(action.id, by: -1) } label: { Image(systemName: "arrow.up") }
                                    .help("上移")
                                Button { move(action.id, by: 1) } label: { Image(systemName: "arrow.down") }
                                    .help("下移")
                                Button("删除") { draft.removeAll { $0.id == action.id } }
                            }
                            HStack(spacing: 8) {
                                Text("执行快捷键").font(.caption).foregroundColor(.secondary)
                                ShortcutRecorder(shortcut: $action.shortcut, error: $error)
                                    .frame(height: 36)
                                Text("按键录制").font(.caption2).foregroundColor(.secondary)
                            }
                            if manualEntry {
                                TextField("例如 Control+Command+Shift+4", text: $action.shortcut)
                            }
                        }
                        .padding(12)
                        .background(RoundedRectangle(cornerRadius: 10).fill(.primary.opacity(0.06)))
                        .onDrop(of: [actionDragType], delegate: ActionReorderDelegate(
                            targetID: action.id, actions: $draft, draggedID: $draggedID))
                    }
                }
                .padding(.vertical, 2)
            }
            if let error = error { Text(error).font(.caption).foregroundColor(.red) }
            HStack {
                Spacer()
                Button("取消") { NSApp.keyWindow?.close() }
                Button("应用设置") { error = model.saveSettings(draft) }
                    .keyboardShortcut(.defaultAction)
            }
        }
        .padding(20)
        .frame(minWidth: 520, minHeight: 400)
    }

    private func move(_ id: String, by offset: Int) {
        guard let index = draft.firstIndex(where: { $0.id == id }) else { return }
        let destination = index + offset
        guard draft.indices.contains(destination) else { return }
        draft.move(fromOffsets: IndexSet(integer: index), toOffset: destination > index ? destination + 1 : destination)
    }
}

private struct ActionReorderDelegate: DropDelegate {
    let targetID: String
    @Binding var actions: [AssistantAction]
    @Binding var draggedID: String?

    func dropUpdated(info: DropInfo) -> DropProposal? { DropProposal(operation: .move) }

    func performDrop(info: DropInfo) -> Bool {
        defer { draggedID = nil }
        guard info.hasItemsConforming(to: [actionDragType]),
              let draggedID = draggedID,
              let source = actions.firstIndex(where: { $0.id == draggedID }),
              let target = actions.firstIndex(where: { $0.id == targetID }),
              source != target else { return false }
        withAnimation {
            actions.move(fromOffsets: IndexSet(integer: source), toOffset: target > source ? target + 1 : target)
        }
        return true
    }
}

private struct ShortcutRecorder: NSViewRepresentable {
    @Binding var shortcut: String
    @Binding var error: String?

    func makeNSView(context: Context) -> ShortcutRecorderView { ShortcutRecorderView(frame: .zero) }

    func updateNSView(_ view: ShortcutRecorderView, context: Context) {
        view.displayText = shortcut
        view.onCapture = { value in
            shortcut = value
            error = nil
        }
        view.onError = { message in error = message }
    }
}

private final class ShortcutRecorderView: NSView {
    var onCapture: ((String) -> Void)?
    var onError: ((String) -> Void)?
    var displayText = "" { didSet { label.stringValue = displayText.isEmpty ? "点击后按键录制" : displayText } }
    private let label = NSTextField(labelWithString: "点击后按键录制")
    private var heldModifiers: [CGKeyCode] = []
    private var completed = false

    override init(frame: NSRect) {
        super.init(frame: frame)
        wantsLayer = true
        layer?.cornerRadius = 6
        layer?.borderWidth = 1
        layer?.borderColor = NSColor.separatorColor.cgColor
        layer?.backgroundColor = NSColor.controlBackgroundColor.cgColor
        label.translatesAutoresizingMaskIntoConstraints = false
        label.cell?.lineBreakMode = .byTruncatingMiddle
        addSubview(label)
        NSLayoutConstraint.activate([
            label.leadingAnchor.constraint(equalTo: leadingAnchor, constant: 9),
            label.trailingAnchor.constraint(equalTo: trailingAnchor, constant: -9),
            label.centerYAnchor.constraint(equalTo: centerYAnchor)
        ])
        setAccessibilityLabel("录制执行快捷键")
    }

    required init?(coder: NSCoder) { fatalError("Use init(frame:)") }
    override var acceptsFirstResponder: Bool { true }

    override func mouseDown(with event: NSEvent) { window?.makeFirstResponder(self) }

    override func becomeFirstResponder() -> Bool {
        layer?.borderColor = NSColor.systemGreen.cgColor
        return true
    }

    override func resignFirstResponder() -> Bool {
        heldModifiers.removeAll()
        layer?.borderColor = NSColor.separatorColor.cgColor
        return true
    }

    override func flagsChanged(with event: NSEvent) {
        let code = CGKeyCode(event.keyCode)
        guard [55, 54, 59, 62, 58, 61, 56, 60, 63].contains(code) else { return }
        if let index = heldModifiers.firstIndex(of: code) {
            heldModifiers.remove(at: index)
        } else {
            if heldModifiers.isEmpty { completed = false }
            heldModifiers.append(code)
        }
        if !completed && !heldModifiers.isEmpty {
            let names = heldModifiers.compactMap { Shortcut.name(forKeyCode: $0) }
            onCapture?(names.joined(separator: "+"))
        }
    }

    override func keyDown(with event: NSEvent) {
        if event.isARepeat { return }
        guard let name = Shortcut.name(forKeyCode: CGKeyCode(event.keyCode)) else {
            onError?("暂不支持按键码 \(event.keyCode)；请录制字母、数字、F1–F12 或常用控制键。")
            return
        }
        let names = heldModifiers.compactMap { Shortcut.name(forKeyCode: $0) } + [name]
        let value = names.joined(separator: "+")
        do {
            _ = try Shortcut.parse(value)
            onCapture?(value)
            completed = true
        } catch {
            onError?(error.localizedDescription)
        }
    }
}

let application = NSApplication.shared
private let controller = AssistantController()
application.delegate = controller
application.run()
