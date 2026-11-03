# 发布体积

2026-11-03，Windows 11 x64，.NET SDK 10.0.400，Windows App SDK 2.6.5。

0.1.4 的 MSI 为 75,368,777 字节（71.9 MiB），不含调试符号的发布目录为 244,335,218 字节。0.1.5 的 NativeAOT MSI 约为 22.2 MiB，缩小约 69%；发布目录约为 76.8 MB，缩小约 69%。最终安装包的精确大小、SHA-256、安装文件核对和测试结果记录在发布旁的 `verification.json`，校验文件随 GitHub Release 提供。

## 采用的做法

* 启用 `PublishAot`、`OptimizationPreference=Size`，保留自包含部署。程序不依赖目标电脑上的 .NET；WinUI 原生文件仍随应用分发，因此不是单文件 EXE。[Microsoft NativeAOT 优化](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/optimizing)
* 使用 Windows App SDK 的 WinUI、DWrite 组件包与显式 MSIX 构建工具，避免伞包引入未使用的 ML、AI、搜索、Widgets 等运行时。通过受支持的包依赖选择组件，不手动删除 DLL。[Microsoft 组件包建议](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/release-notes/windows-app-sdk-1-8)
* 使用 CsWinRT 2.3.1 的生成投影，为绑定模型添加 `GeneratedBindableCustomProperty`，WinRT 可见派生类改为 partial。设置、本地化使用 System.Text.Json 源生成；RPC 使用显式 JsonObject/JsonArray，避免反射序列化。[CsWinRT AOT 与裁剪指南](https://github.com/microsoft/CsWinRT/blob/master/docs/aot-trimming.md)
* 保留完整全球化能力、22 种语言、主题资源、PRI 和三个 XBF；不使用 invariant globalization 换取体积。PDB 与构建日志不进入 MSI；CAB 使用高压缩。

Windows App SDK 自 1.6 支持 NativeAOT；官方说明要求适配绑定与反射代码，不能只打开发布开关。[Microsoft NativeAOT 支持说明](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/release-notes/windows-app-sdk-1-6#native-aot-support)

## 实际遇到的问题

本机 SDK 将 CsWinRT 的 `WinRT.Runtime.dll` 运行时资产标记为 `CopyToPublishDirectory=false`，导致 ILC 丢失托管编译输入。构建可退出成功，却生成启动即失败的程序。项目的 `IncludeWinRtRuntimeForNativeCompilation` 目标从已解析的 `ReferencePath` 补充这个 ILC 引用；没有硬编码 NuGet 缓存路径，也没有额外携带 JIT 运行时。

因此发布检查直接运行最终 EXE，等待窗口激活并继续检查初始视图加载后的存活与响应，不能仅凭 publish 成功判断。CI 同样在打包后执行发布程序检查。还检查本机 PE 没有 CLR 入口、安装目录与发布目录文件哈希一致、PRI/XBF 齐全，以及 MSI 原位升级保留用户设置。

干净 CI 主机没有 19041 SDK 的 Platform.xml，而消费已投影的 WinUI 类型不需要重新生成 Windows 投影。依据 [CsWinRT 构建属性](https://github.com/microsoft/CsWinRT/blob/master/nuget/readme.md)，项目关闭冗余的 `CsWinRTGenerateProjection`，保留 AOT 优化器、分析器和绑定源生成。移动本地 obj 后重新构建，零警告、零错误。发布脚本在 restore 后更新许可证目录，使不同 SDK 解析出的 .NET 运行时版本也能如实展示。

## 重现

安装 .NET 10 SDK、Visual Studio 的“使用 C++ 的桌面开发”工具与 Windows SDK，运行：

```powershell
.\scripts\Build-Installer.ps1
.\scripts\Test.ps1 -IncludeInstalledCli -IncludeRelease
.\scripts\Test-Layout.ps1
```

NativeAOT 的 Windows 工具链要求见 [Microsoft 部署指南](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/)。ARM64 配置保留，但本轮体积和运行验证仅针对 x64。
