# MDCL 发布矩阵（2026-10-25）

状态说明：只有明确记录的系统、架构和操作才算通过。交叉编译不是运行通过。

| 目标 | 构建 | 包结构 | 真机窗口 | 原版启动 | 签名/公证 | 状态 |
| --- | --- | --- | --- | --- | --- | --- |
| win-x64 | 通过（本机 .NET 10.0.402） | Windows zip 116,697,818 字节，含 `updater/MDCL.Updater.exe` 与新标签页 UI | 通过 | 1.20.1 原版主菜单、退出、二次启动通过；Forge 47.2.0 安装并进入主菜单；NeoForge 仅 URL/版本解析测试；新版 UI 已截图核验 | 未签名（开发测试包） | Windows 开发可用，正式分发未完成 |
| osx-arm64 | 交叉发布通过 | `.app` 结构 + 未签名 tar.gz 已生成，80,181,641 字节 | 未实测 | 未实测 | 未签名/未公证 | 仅构建证据，不可分发 |
| osx-x64 | 交叉发布通过 | `.app` 结构 + 未签名 tar.gz 已生成，84,741,214 字节 | 未实测 | 未实测 | 未签名/未公证 | 仅构建证据，不可分发 |

## 外部阻塞项

- Apple Silicon / Intel Mac 设备：窗口、Java 发现、Keychain、原生启动与 Gatekeeper 未验证。
- Apple Developer ID、公证凭据：正式 macOS 分发未完成。
- MDCL Microsoft public client：真实微软登录未验证。
- Intel Mac / Rosetta：未验证，不能用交叉编译或 Rosetta 结果替代真机验收。

## 固定回归样本

- `1.20.1`：Windows x64 本地身份，真实 Mojang 安装，主菜单/退出/二次启动通过。
- `26.3`：尚未安装启动。其官方元数据要求 Java 25，需另行在线验证。

所有哈希见发布目录 `SHA256SUMS.txt`；包内 `release-manifest.json` 标记 `signed=false`。
