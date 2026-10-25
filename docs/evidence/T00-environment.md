# T00 环境清单

- 核查时间：2026-10-25（Asia/Shanghai）
- 开发机：Windows，RID `win-x64`，系统 build `10.0.26300`。
- 原全局环境：仅有 .NET SDK 5.0.408 / 8.0.201，不满足设计。
- 项目 SDK：`.NET SDK 10.0.402`，运行时 `10.0.13`，安装在工作区 `.dotnet10`，未替换全局 SDK。
- 包版本：Avalonia `12.1.4`；精确依赖见 `Directory.Packages.props`。
- 系统临时目录首次触发 `UnauthorizedAccessException`。权限诊断未做修改，报告在工作区 `acl-recovery/`；后续构建将 TEMP/TMP、DOTNET_CLI_HOME 和 NUGET_PACKAGES 指向工作区。
- Apple Silicon Mac：无可用设备，窗口与原生运行未实测。
- Intel Mac：无可用设备，窗口与原生运行未实测。
- macOS 签名、公证、Gatekeeper：无 Mac 与签名凭据，未执行。
- MDCL Microsoft public client：尚未注册/提供，真实登录条件不足。

本清单不构成任何 Mac 或真实游戏启动通过证据。
