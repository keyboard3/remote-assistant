# 远程助手 macOS 原型

这是 Windows 版“远程助手”的原生 macOS 实现起点，要求 macOS 13 或更新版本及 Xcode 命令行工具。Windows 版不受影响。

在 Mac 上进入此目录，运行 `sh build-app.sh`，然后打开 `.build/远程助手.app`。首次使用快捷键或图片投递时，macOS 可能要求为该应用授予发送键盘事件的权限；请由用户在系统设置中自行批准，然后重新测试。

圆形入口单击展开，拖动可贴边；菜单里的截图、PNG 投递和自定义快捷动作完成后保持展开，仅“×”或“收起菜单”会关闭。截图调用 macOS 自带的交互式截图工具，直接复制到剪贴板；Esc 取消后可点“结束截图等待”。

“投递到当前应用”会把剪贴板图片存成真实 PNG 文件，在系统剪贴板放入该文件 URL，再向原前台应用发送一次 Command+V。这保持单击交互，但不是 Windows OLE 拖放：目标应用可能不接受文件型粘贴，且系统没有通用的接收成功回执。界面只报告已发送，必须在目标应用中确认文件确实出现；这一步需要在真实 Mac 和具体目标应用中验收。成功发送后原剪贴板图片会变成 PNG 文件引用；发送失败时会尽力恢复图片内容，但不保证恢复原剪贴板的全部格式。

快捷动作支持 `Command`、`Control`、`Option`、`Shift`、`Fn` 及左右侧修饰键，或与字母、数字、空格、回车、Tab、Escape、F1–F12 组合。默认 Typeless macOS 快捷键按官方说明是 `Fn`，但不同键盘及 macOS 设置可能改变 Fn 行为；请在 Typeless 和本应用中用相同设置实际验收。Mac 版独立保存设置，不会悄悄把 Windows 的 `RightWin+LeftShift` 转换成 Mac 快捷键。配置位于 `~/Library/Application Support/RemoteAssistantMac/settings.json`，窗口位置由用户默认设置保存。

当前开发环境是 Windows，无法编译或运行 AppKit。本目录的编译、授权、截图、快捷键及目标应用投递仍需在 Mac 上运行 `sh build-app.sh` 并逐项验收。
