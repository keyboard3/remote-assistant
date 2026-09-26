# 远程助手（Windows 原型）

一个面向远程桌面操作的贴边快捷助手原型。技术栈是 .NET 7 WPF，无额外 NuGet 依赖。

## 运行

需要 Windows 10/11 和 .NET 7 SDK 或 Desktop Runtime。PowerShell 中在本目录运行：

```powershell
dotnet run -c Release
```

构建：

```powershell
dotnet build -c Release
```

## 使用

启动后，屏幕外沿内侧约 6 个逻辑像素处出现完整可见的圆形“远程助手”入口。左键单击入口展开菜单。点击“区域截图”后，菜单在框选和等待期间仍保持展开；Windows 将截图放入剪贴板后，助手会结束等待。菜单可能遮挡或进入截图区域。使用“投递到当前应用”前，先在目标应用中点选可接收附件的编辑区域，再展开助手并按下投递按钮；助手把剪贴板图像编码为临时 PNG，定位目标编辑区域并启动 Windows OLE 文件拖放，用户释放这次点击时完成投递。第三张动作卡可通过菜单右上角齿轮自定义标题和快捷键；默认标题/热键仍为“Typeless 语音输入”/Right Alt。点击动作卡时，助手只向合格的当前前台应用发送一次该键序列，不常驻注册热键。操作结束后菜单保持展开，只有点击右上角“×”或底部“收起菜单”才会折叠；拖动圆形入口可以换边/换高度，位置保存在当前 Windows 用户的 LocalAppData。

菜单采用可扩展的快捷操作卡片布局。当前只有一个通用快捷动作槽位，可自定义其标题/单次按键组合；默认沿用 Typeless 语音输入。

### 快捷动作配置

菜单右上角齿轮打开设置窗，可编辑第三张动作卡标题和快捷键，点“保存设置”立即生效，不用重新构建。首次运行会在 `%LOCALAPPDATA%\LunaDesktopHelper\settings.json` 创建默认设置；旧版只有 `typelessHotkey` 时会读取该值作为动作热键：

```json
{
  "actionTitle": "Typeless 语音输入",
  "actionHotkey": "RightAlt",
  "typelessHotkey": "RightAlt"
}
```

快捷键写法示例：`RightAlt`、`RightWin+LeftShift`、`Ctrl+Shift+Space`、`Alt+F8`、`Win+F9`。修饰键支持 Ctrl、Alt、Shift、Win 及其 Left/Right 侧别名称，例如 `LeftCtrl+RightShift+V`；允许纯修饰键组合。主键还支持字母、数字、F1–F24、Space、Enter、Tab、Escape、Backspace、Delete、Insert、Home、End、PageUp、PageDown、方向键及 PrintScreen。最多五个按键，不能重复使用同一个物理键。Fn 硬件键、多步宏以及所有 Windows 保留组合键不作承诺；系统可能先截获某些组合键。Right Alt 是 [Typeless 官方 Dictate 快速入门](https://www.typeless.com/help/quickstart/dictate) 所列的 Windows 默认热键；在 Typeless 中可自定义或添加多个热键，LUNA 不修改 Typeless 的设置，需确保 LUNA 设置与 Typeless 绑定一致（参见 [Typeless 设置](https://www.typeless.com/help/quickstart/settings)）。

设置窗需输入文字，会临时取得前台焦点；保存或取消本身不会发送任何快捷键。关闭后请重新点选目标输入框，再点击 LUNA 动作卡。若当前前台仍是 LUNA 自身窗口，助手会拒绝发送，不会把动作发给设置窗。无效配置会在设置窗和菜单中显示错误，点击动作不会偷偷回退并发送默认 Right Alt。保存保留旧 `typelessHotkey` 和其他 JSON 字段供兼容；损坏的 JSON 会先备份原文件再重建。

窗口使用 `WS_EX_NOACTIVATE` 并关闭 ShowActivated，预期不会夺走原应用焦点。多显示器只按 Windows 虚拟桌面整体边界约束位置；首次启动放在主屏，拖动或重启恢复时允许保留副屏坐标。吸附只处理虚拟桌面的最外侧左右边界，不会按每台显示器的工作区贴边；副屏内沿及不同排列/混合 DPI 尚未验收。UAC 安全桌面及独占全屏场景不保证可见。

## 行为限制

当前机器没有 Visual Studio、Windows SDK MakeAppx 或 Windows App SDK 开发模板（未找到 `msbuild.exe`、`makeappx.exe`、`signtool.exe` 命令）。因此此原型不能构建成带 MSIX 身份的应用。Microsoft 新版 Snipping Tool 回调协议要求打包应用并通过 `Launcher.LaunchUriAsync` 发起；所以本原型使用 `Win+Shift+S` 并观察剪贴板序号变化。

**截图等待中的菜单保持展开。** 这让用户取消 Windows 截图后可以直接点“结束截图等待”，但菜单可能遮挡画面、甚至被选入区域截图；这是手动收起与截图画面干净之间的取舍。原型没有可可靠区分 Snipping Tool 完成与 Esc 取消的回调，也不安装键盘钩子（避免截获系统截图界面的 Esc）。若取消后剪贴板没有新图像，点“结束截图等待”即可立即停止本地等待并重新启用截图按钮；不需要再等 120 秒。若用户不手动结束等待，120 秒超时仍会恢复操作。成功时只有在剪贴板序号改变且包含图像后才会结束等待。截图期间若其他程序写入图像，也可能误判为完成。剪贴板内容由 Windows 截图工具写入，原型不替换图片。安全桌面、独占全屏或菜单被其他窗口遮挡时，不能保证该恢复入口可用。

图片投递依赖 LUNA 窗口保持不激活，使原前台应用和编辑区域保持焦点。**请先在目标附件/消息编辑区（例如 Codex 的消息编辑框）点一下，再打开 LUNA 菜单。** 助手优先使用 UI Automation 的焦点矩形作为投放点；拿不到时使用目标窗口下方中央区域。展开菜单时会预先将剪贴板图片编码为 PNG，点击时核对剪贴板是否变化；文件保存在 `%LocalAppData%\LunaDesktopHelper\drops`，超过一天的 `Luna-*.png` 会在下一次投递时尽力清理。按钮在鼠标按下阶段启动 OLE 拖放，鼠标释放后最多等待 350 毫秒让目标接受文件，再完成投递；整个拖放超过两秒会取消。OLE 返回 `Copy` 只说明目标接受了文件拖放，不保证目标应用已经完成上传；不支持文件拖放的应用会明确提示失败。

快捷动作同样只校验原前台顶层窗口。LUNA 使用 `SendInput` 按配置顺序按下各键，保持 100 毫秒后逆序释放；不会注册系统级热键、读取应用状态或调用应用 API。对于 Right Alt，LUNA 使用 VK_RMENU 对应的 E0 38 扩展扫描码；不兼容的映射会中止发送。Windows/UIPI、系统保留组合或远程控制输入层仍可能拦截合成按键；事件插入不代表目标应用已执行动作。Typeless 默认是切换行为，因此每次点击只发送一次 tap，开始/停止取决于 Typeless 自身状态。

## 本机验证记录

- Windows 11 22H2，系统已安装 Snipping Tool。
- 菜单改版前，用户反馈通过 UU 远控可以展开菜单并成功使用区域截图；这只确认当时的截图操作，不代表回到目标应用后的粘贴已成功。
- 本轮菜单改版后，Release 构建成功，0 warning / 0 error；运行日志记录到一次 UU 点击将菜单状态切换为 `Visible`。这是事件和状态日志证据，不等同于改版后菜单外观的视觉验收。
- 2026-09-26 Typeless 与粘贴诊断版 Release 构建成功，0 warning / 0 error；当前运行实例启动日志确认 x64 `INPUT` 结构 40 bytes、`GUITHREADINFO` 72 bytes、Right Alt 扩展扫描码 `E038`，Ctrl/V 扫描码 `001D/002F`，并读取到默认 `RightAlt` 配置。构建/启动检查未向 Typeless 或 Codex 发送按键。
- UU 日志曾确认旧 PasteButton down/up、Click 路由到达，且目标为 ChatGPT/Chrome_WidgetWin_1，但虚拟键和扫描码 SendInput 都没有产生可见结果。2026-09-26 已替换为临时 PNG + OLE 文件拖放，用户确认可以插入附件，但平均需点击 4–5 次；日志显示相同目标坐标下 OLE 时而返回 `None`、时而返回 `Copy`。为缩短按下后的准备时间并避免过早结束拖放，现将 PNG 编码提前到菜单展开时，并在 OLE 拖放期间等待目标接受文件。新版 Release 构建 0 warning / 0 error，单击成功率待实际验收。
- 菜单改版后的区域截图、图像粘贴，以及改版后的视觉外观尚未由用户手动验收。自动化 GUI 点击也未执行。
- 截图等待时胶囊可见及“结束截图等待”恢复入口尚待 UU 手动验收；没有自动识别 Esc 取消。用户需按“手动验收”步骤取消后手动结束等待；若不操作，仍以 120 秒超时恢复。双屏行为也待人工验收。

## 手动验收

1. 在桌面启动助手，确认胶囊贴边显示；点击展开，检查菜单布局与按钮是否易于触控。
2. 用 UU 手机远控点“区域截图”并拖出一个矩形；分别确认截图进入剪贴板，以及能否在目标应用粘贴。
3. 把焦点放在目标应用（例如 Codex）的附件/消息编辑区，展开菜单并按下“投递到当前应用”，正常释放这次点击；确认鼠标短暂定位后复位，并观察目标是否出现 PNG 附件。
4. 点击菜单齿轮，将第三卡标题改成测试标题、快捷键改成与 Typeless 绑定一致的组合，保存后确认第三卡立即更新；关闭设置期间不发送热键。重新点选目标输入框，再点动作卡；若要沿用 Typeless，确认开始 Dictate，再点一次确认停止。
5. 在没有目标应用前台（桌面/任务栏）时点粘贴或 Typeless，确认助手不发送快捷键并提示返回目标应用。
6. 启动截图并按 Esc。确认菜单仍展开；点“结束截图等待”，确认不必等待超时且截图按钮重新出现。留意菜单可能进入截取范围。
7. 拖动胶囊到另一侧并重启助手，确认位置保存。
8. 在双屏环境重复步骤 1–7；特别检查副屏外沿定位。副屏内沿贴边尚不支持验收。安全桌面和独占全屏不纳入验收承诺。
