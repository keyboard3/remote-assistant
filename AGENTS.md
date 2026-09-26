# 远程助手项目约定

## Windows 唯一开发更新入口

- Windows 小助手的唯一受支持构建、安装和启动命令是仓库根目录的 `./install-windows.ps1`。
- 修改 Windows 端代码后，除非用户明确要求只检查而不更新本机程序，否则必须执行 `./install-windows.ps1`，让固定安装版自动更新并重启。
- 不要把 `dotnet run`、`dotnet build`、`bin/` 或 `.build/` 中的临时产物作为交付给用户的运行入口，也不要引导用户直接启动这些临时 exe。
- 用户唯一需要使用的入口是桌面上的“远程助手”快捷方式；其目标必须是 `%LocalAppData%\Programs\RemoteAssistant\RemoteAssistant.exe`。
- `.build/windows-release` 只是固定的中间发布目录，不是用户入口。
- 用户按钮配置位于 `%USERPROFILE%\.remote-assistant\settings.json`，安装或更新不得删除或覆盖这份文件。仅允许首次升级时把旧 `%LocalAppData%\RemoteAssistant` 或 `%LocalAppData%\LunaDesktopHelper` 目录迁移到新目录。
- 更新完成后应确认只运行固定安装目录中的实例，并向用户报告已更新固定安装版；不要再提供其他 exe 路径作为备选入口。
