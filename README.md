# StartDown

StartDown 是一个短生命周期的 Windows 启动编排器：它先开始监听窗口，再启动你配置的后台程序；当目标窗口满足条件时，StartDown 自动关闭、最小化或隐藏它。所有配置处理完成，或达到总超时后，StartDown 自行退出。

它面向这样的场景：QQ、Telegram 等程序需要登录后常驻收消息，但自身的“最小化启动”无效，每次登录都会弹出主窗口。

## 工作方式

1. 在目标软件中关闭其原有的开机自启动。
2. 在 StartDown 中添加目标程序、启动参数和窗口条件。
3. StartDown 登录后启动，先安装 WinEvent 窗口监听器并补扫已有窗口。
4. StartDown 启动所有启用的目标程序。
5. 窗口命中规则后，StartDown 执行动作；所有条目完成或超时后退出。

这种顺序不再依赖 Windows 各类启动项之间没有保证的执行顺序。

## 当前功能

- WinForms 配置界面，不使用 WebView 或第三方 UI 框架。
- 默认只匹配被启动 exe；也可显式匹配另一个 exe 或某个目录下的版本化程序。
- 窗口条件全部使用 AND 组合：
  - 标题任意、包含、完全相同或正则表达式；
  - 窗口类；
  - 最小/最大宽度和高度；
  - 可见、顶层、无 owner、尚未最小化。
- 动作：模拟点击关闭按钮、最小化、隐藏。
- 每项可设置窗口出现后的动作延迟、预期处理数量和独立超时。
- 全局硬超时，默认 300 秒。
- 目标已经运行时可安全跳过，或显式接管已有实例。
- “从当前窗口读取”工具，可读取程序路径、标题、窗口类和当前尺寸。
- “测试所选”和“运行全部”状态窗；真实开机模式无主窗口、无任务栏按钮。
- 当前用户自启动开关，写入 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`。
- 配置和日志默认位于 `%LOCALAPPDATA%\StartDown`。

## 使用

直接运行 `StartDown.exe` 打开配置界面。

推荐流程：

1. 添加程序并选择启动 exe。
2. 如果稳定启动器会拉起 `bin\vX.X.X\program.exe`，把“窗口所属程序”改为：
   - “指定可执行文件”，适合固定子进程；或
   - “指定目录下的程序”，适合版本号目录。目录模式会匹配该目录的所有子目录，请尽量选择最小范围。
3. 先打开一次目标窗口，用“从当前窗口读取”填入标题与窗口类。
4. 按需设置尺寸下限，避免误关同一程序的小对话框。
5. 使用“测试所选”验证点击 X 后目标程序仍在自己的托盘中。
6. 确认目标软件自己的自启动已关闭，再启用 StartDown 自启动。

## 命令行

```text
StartDown.exe                         打开配置界面
StartDown.exe --run                   静默运行所有启用项
StartDown.exe --startup               登录自启动模式（静默）
StartDown.exe --run --show-status     运行并显示状态窗
StartDown.exe --run --entry <GUID>    只运行指定条目
StartDown.exe --config <path>         使用指定配置文件
```

## 构建与测试

需要 .NET 10 SDK 和 Windows Desktop Runtime：

```powershell
dotnet build StartDown.slnx
dotnet run --project tests/StartDown.Core.Tests/StartDown.Core.Tests.csproj --no-build
dotnet run --project tests/StartDown.IntegrationTests/StartDown.IntegrationTests.csproj --no-build
dotnet publish src/StartDown/StartDown.csproj -c Release -r win-x64 --self-contained false -o artifacts/StartDown
```

核心测试不操作桌面。端到端测试会启动仓库内的 `StartDown.WindowFixture` 假窗口，验证 StartDown 能启动它、匹配并关闭窗口，然后自行退出；需在交互式 Windows 桌面运行。

## 已知边界

- “关闭”通过 `WM_SYSCOMMAND / SC_CLOSE` 模拟窗口关闭按钮。消息成功投递不代表目标应用一定会服从；应用也可能选择退出、进入托盘、弹确认框或忽略。
- “隐藏”不会替应用创建托盘图标。对于需要在 StartDown 退出后继续访问的程序，应优先使用应用自己的 close-to-tray 行为。
- 普通权限的 StartDown 不能操作管理员权限窗口，日志会记录 Access Denied。StartDown 默认不会整体提权。
- 进程路径不可读或由宿主进程承载的旧 UWP 窗口可能无法按 exe 精确匹配。
- 当前监听 `EVENT_OBJECT_SHOW` 与 `EVENT_OBJECT_NAMECHANGE`；窗口显示两秒后才单纯改变尺寸、且不再改变标题的极端情况可能需要放宽条件或增加动作延迟。

## License

MIT
