# 远程助手（Windows / macOS）

一个面向远程桌面操作的贴边快捷助手，提供彼此独立的 Windows 和 macOS 原生实现。

| 平台 | 实现 | 系统要求 |
| --- | --- | --- |
| Windows | .NET 7 WPF | Windows 10/11、.NET 7 SDK 或 Desktop Runtime |
| macOS | SwiftUI / AppKit | macOS 13+、Xcode 命令行工具 |

## 运行

### Windows

在仓库根目录运行：

```powershell
dotnet run -c Release
```

构建：

```powershell
dotnet build -c Release
```

### macOS

在 Mac 上运行：

```sh
cd macos/RemoteAssistantMac
sh build-app.sh
open .build/远程助手.app
```

构建脚本会先执行 Swift 测试，再生成原生 `.app`。首次执行快捷键时，macOS 可能要求授予辅助功能权限。详细说明见 [macOS README](macos/RemoteAssistantMac/README.md)。Windows 与 macOS 不共享可执行文件或设置文件。

## 使用

启动后，屏幕外沿内侧约 6 个逻辑像素处出现圆形“远程助手”入口。左键单击入口展开可滚动菜单。菜单按钮由动作配置按顺序动态生成；按钮本身就是触发入口，点击后执行该按钮配置的组合键。操作结束后菜单保持展开，只有点击右上角“×”或底部“收起菜单”才会折叠。

两端首次启动都会按“区域截图 → 复制 → 粘贴 → Typeless”创建四个普通快捷键按钮：

| 平台 | 设置文件 | 默认修饰键 |
| --- | --- | --- |
| Windows | `%LocalAppData%\LunaDesktopHelper\settings.json` | `Win` / `Ctrl` / `Alt` |
| macOS | `~/Library/Application Support/RemoteAssistantMac/settings.json` | `Command` / `Control` / `Option` / `Fn` |

所有按钮都能在设置面板修改名称、录制或手动输入组合键、拖动左侧手柄排序（也可用 ↑ ↓）、启停、删除或动态新增。Windows 端关闭“显示”会保留配置，但不在外部菜单展示；重新开启即可恢复。macOS 端关闭“启用”后仍显示禁用按钮。点“应用设置”后顺序写回各平台自己的配置文件。按钮只负责向当前应用发送配置的按键；图片是否进入剪贴板、粘贴到哪里由系统和目标应用决定。

### 快捷动作配置

菜单右上角齿轮打开设置窗。快捷键框默认用于录制；遇到被系统或其他程序抢占、无法正常录制的快捷键时，可打开“手动编辑”直接输入组合键。录制和手动输入共用保存校验。旧版设置启动时会先备份，再迁移为 `schemaVersion: 4` 的有序快捷键数组；已有自定义按钮和顺序保留。

Windows 手动编辑会让原快捷键框直接变为普通文本框，可输入 `LeftWin+LeftShift+S`；macOS 会显示手动输入框，可输入 `Control+Command+Shift+4`。平台按键名称不会自动互译。

以下是 Windows 配置示例：

```json
{
  "schemaVersion": 4,
  "actions": [
    { "id": "builtin.capture", "kind": "sendKeys", "title": "区域截图", "shortcut": "LeftWin+LeftShift+S", "enabled": true },
    { "id": "builtin.copy", "kind": "sendKeys", "title": "复制", "shortcut": "LeftCtrl+C", "enabled": true },
    { "id": "builtin.paste", "kind": "sendKeys", "title": "粘贴", "shortcut": "LeftCtrl+V", "enabled": true },
    { "id": "custom.legacy", "kind": "sendKeys", "title": "Typeless 语音输入", "shortcut": "RightAlt", "enabled": true }
  ]
}
```

Windows 执行快捷键支持组合键，也允许 `RightAlt`、`LeftWin+LeftShift` 这类纯修饰键组合；macOS 支持左右侧 `Command`、`Control`、`Option`、`Shift`、`Fn` 及常用主键。左右修饰键会在录制和发送时保留。保存或取消设置本身不会向目标应用发送按键。无效配置会显示错误；配置文件中的 `actions` 是按钮唯一来源，程序不会在已有配置里自动补回已删除的按钮。

### Windows 窗口行为

窗口使用 `WS_EX_NOACTIVATE` 并关闭 ShowActivated，预期不会夺走原应用焦点。多显示器只按 Windows 虚拟桌面整体边界约束位置；首次启动放在主屏，拖动或重启恢复时允许保留副屏坐标。吸附只处理虚拟桌面的最外侧左右边界，不会按每台显示器的工作区贴边；副屏内沿及不同排列/混合 DPI 尚未验收。UAC 安全桌面及独占全屏场景不保证可见。

## 行为限制

### Windows

“区域截图”只发送系统快捷键 `Win+Shift+S`；“复制”和“粘贴”分别发送 `Ctrl+C` 和 `Ctrl+V`。粘贴按钮不会把图片转成 PNG 文件，也不会拖放附件。截图后请确认图片进入系统剪贴板，再选中目标应用的编辑区域执行粘贴。按钮发送后菜单保持展开，可能遮挡截图区域；需要时手动收起。

快捷键动作只校验原前台顶层窗口，也允许桌面作为目标（例如截桌面或复制桌面文件）。LUNA 使用 `SendInput` 发送组合键；本机运行 PowerToys Keyboard Manager 时使用其兼容标记，避免 Ctrl/Alt 键位互换影响模拟按键。程序不注册每个动作的全局触发键。Windows/UIPI、系统保留组合、远程控制输入层或目标应用仍可能拦截动作。

### macOS

“区域截图”默认发送 `Control+Command+Shift+4`，“复制”和“粘贴”分别发送 `Command+C` 和 `Command+V`。程序通过系统键盘事件发送组合键，不直接调用 `screencapture`，也不会把剪贴板图片转换成文件。键盘布局、Fn 硬件行为、系统保留快捷键、辅助功能权限、远程控制输入层或目标应用仍可能影响结果。

## 本机验证记录

- 2026-09-26，Windows Debug / Release 构建成功，0 warning / 0 error。
- Windows 旧版 v2/v3 配置迁移样本、当前配置和四个默认按钮通过本地检查。
- 当前开发环境为 Windows，macOS 源码和测试已提供，但仍需在 Mac 上运行 `sh build-app.sh` 完成 Swift 测试与原生交互验收。

## 手动验收

以下步骤应分别在 Windows 和 macOS 上执行，并使用各平台对应的默认组合键：

1. 在桌面启动助手，确认胶囊贴边显示；点击展开，检查菜单布局与按钮是否易于触控。
2. 用 UU 手机远控点“区域截图”并拖出一个矩形；确认截图进入剪贴板。回到 PPT 幻灯片后点击“粘贴”，确认图片插入。
3. 在目标应用选中文字或图片，点“复制”，再到目标位置点“粘贴”，确认内容正确。
4. 点击菜单齿轮，修改预置按钮名称和组合键；新增按钮，拖动左侧手柄排序，再删除一个按钮，保存并重启，确认配置与菜单一致且删除的按钮不会复活。
5. 在桌面前台时点“区域截图”，确认系统截图工具启动；在目标应用前台时点复制或粘贴，确认操作发生在该应用。
6. 拖动胶囊到另一侧并重启助手，确认位置保存。双屏场景特别检查副屏外沿定位；副屏内沿贴边尚不支持验收。
