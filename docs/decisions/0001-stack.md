# ADR 0001：锁定 .NET 10 与 Avalonia 桌面技术栈

- 日期：2026-10-25
- 状态：Accepted
- 决定：所有共享项目使用 `net10.0`，SDK 固定为 `10.0.402`，`rollForward=disable`；UI 使用 Avalonia `12.1.4` 与 MVVM。
- 依赖：CommunityToolkit.Mvvm `8.4.2`、Microsoft.Extensions.* `10.0.13`、Microsoft.Data.Sqlite `10.0.13`、ProtectedData `10.0.13`。测试使用 xUnit `2.9.3`。
- 发布：`win-x64`、`osx-arm64`、`osx-x64` 分开目录，自包含、非单文件、不裁剪、不启用 Native AOT。
- 原因：满足交接设计的双平台、单一共享核心和可重建要求。
- 后果：构建机器必须具有精确 SDK 或使用仓库说明的本地 SDK；Mac 交叉发布仅证明编译，不代表 Mac 交互或签名验收。

NuGet 版本通过包注册表逐一核查，精确版本写入 `Directory.Packages.props`，还原后提交各项目 `packages.lock.json`。
