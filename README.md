![](./assets/icon.svg)

# StartDown

许多软件 *本应该* 静默地开机启动，不弹窗直接进托盘。有些软件提供了静默启动选项，有些则没有，或者是静默启动选项不知怎地失效。结果就是每次开机都会弹出一堆窗口，搞半天还要自己关。

StartDown 是一个软件启动工具。其使命很简单：用户配置一些程序，开机时启动这些程序，并在满足条件时自动把弹出的窗口关掉。完成后 StartDown 会自行退出。

StartDown 这个名字来源于 startup 反过来。

## 工作方式

你需要做的事：

1. 在目标软件中关闭其原有的开机自启动
2. 在 StartDown 中添加配置
   - 指定目标程序（支持 exe 文件与微软商店应用），可从快捷方式导入
   - 配置窗口识别条件，例如标题、窗口类、窗口长宽等
   - 配置命中动作：关闭、最小化或隐藏
3. 允许 StartDown 开机启动

StartDown 将在开机时：

1. 监听现有和新增的窗口
2. 启动所有目标程序
3. 窗口命中规则后将其关闭
4. 完成所有配置，或达到总超时后自行退出

关闭窗口动作等效于用户关闭窗口。关闭后是否常驻并显示托盘图标，取决于目标软件自身的行为。

## 安装

在 [仓库 release 页面](https://github.com/Linho1219/startdown/releases) 下载安装包。

安装包提供两个版本：

- `framework-dependent`：需要电脑安装 [.NET 10 运行时](https://dotnet.microsoft.com/download/dotnet/10.0)，约 3 MB
- `self-contained`：内含运行时，约 36 MB

## 构建与测试

需要 .NET 10 SDK 和 Windows Desktop Runtime 和 Inno Setup 7。

```powershell
dotnet build StartDown.slnx
dotnet run --project tests/StartDown.Core.Tests/StartDown.Core.Tests.csproj --no-build
dotnet run --project tests/StartDown.IntegrationTests/StartDown.IntegrationTests.csproj --no-build
dotnet publish src/StartDown/StartDown.csproj -c Release -r win-x64 --self-contained false -o artifacts/StartDown

# 打包 self-contained + framework-dependent 两个安装包
pwsh scripts/build-installer.ps1
```

安装包输出到 `artifacts/installer`。

## License

MIT
