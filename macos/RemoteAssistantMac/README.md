# 远程助手 macOS 版

原生 SwiftUI/AppKit 实现，要求 macOS 13 或更新版本及 Xcode 命令行工具。Windows 版是单独的 WPF 程序，不共享可执行文件或设置。

在 Mac 上进入此目录运行 `sh build-app.sh`，再打开 `.build/远程助手.app`。首次执行快捷键时，macOS 可能要求允许应用发送键盘事件；需要在系统设置中批准并重试。

圆形入口单击展开、拖动贴边；菜单按钮来自 `~/Library/Application Support/RemoteAssistantMac/settings.json` 的有序 `actions` 数组。首次启动默认提供四个普通快捷键按钮：

| 按钮 | 默认组合键 | 用途 |
| --- | --- | --- |
| 区域截图 | `Control+Command+Shift+4` | 截取区域并复制到剪贴板 |
| 复制 | `Command+C` | 复制当前选中内容 |
| 粘贴 | `Command+V` | 粘贴剪贴板内容 |
| Typeless 语音输入 | `Fn` | 按目标应用配置触发 |

所有按钮均可在齿轮设置面板改名、录制组合键、启停、删除、新增，以及通过 ≡ 拖拽或 ↑ ↓ 排序；点击“应用设置”后写入配置文件。录制窗口接收真实按键而非字符输入。若某个系统保留快捷键被 macOS 截获、无法录制，可打开“手动编辑”输入组合键，保存时仍会校验。菜单内容过多时可滚动；动作发送后菜单保持展开，可手动收起或退出助手。

旧版仅包含 `actionTitle` / `actionHotkey` / `typelessHotkey` 的设置会备份后迁移为 `schemaVersion: 4`。已有 v4 `actions` 顺序与删除状态会原样保留，不会在重启时补回按钮。Mac 配置独立于 Windows，不会自动翻译 `Win` 键。快捷键支持左右侧 `Command`、`Control`、`Option`、`Shift`、`Fn`，以及字母、数字、空格、回车、Tab、Escape、F1–F12 等；录制和实际发送仍受键盘布局、Fn 硬件行为、系统保留快捷键与目标应用限制。

截图现在只发送系统快捷键，不再启动 `screencapture` 进程或等待剪贴板结果；粘贴也不再把图片转换为 PNG 文件引用。因此目标应用如果只接受文件上传，普通 `Command+V` 不一定等价。截图后请确认图片已进入剪贴板，再执行粘贴。

当前开发环境是 Windows，没有 Swift/macOS 工具链，不能在此处编译或运行 AppKit。请在 Mac 上运行 `sh build-app.sh`（包含 `swift test`），并验收设置面板拖拽、按键录制、截图、复制/粘贴及 Typeless。系统区域截图到剪贴板的默认组合键依据 [Apple 截图说明](https://support.apple.com/en-au/102646)。
