# MDCL 0.2.0-alpha.2 发布矩阵

2026-10-25。开发预览版，未达到全部正式分发验收目标。发布目录 release-manifest.json 和 SHA256SUMS 记录实际提交、文件大小与摘要；历史包不能替代本版。首轮远程 Mac CI 因 PowerShell 没有设置 TMPDIR，系统 /var 链接被提取安全检查拒绝；alpha.2 将临时目录统一放入受控 artifacts/work/tmp，保留链接拒绝策略。

| 目标 | 交付 | 运行证据 | 未验证 |
| --- | --- | --- | --- |
| win-x64 | 自包含 ZIP，桌面与独立 updater | 本机 .NET 10.0.402 编译/测试；原版 1.20.1 和 26.3 各两次主菜单、正常退出；PCL 高仿布局十页、两种窗口尺寸导出 | 干净 Windows 用户环境、正式签名、完整性能指标 |
| osx-arm64 | 自包含未签名 `.app.tar.gz`，含执行位 | Windows 交叉发布；Mach-O arm64 头及 bundle 结构检查 | Apple Silicon 真机、窗口、Java、Keychain、游戏、Gatekeeper、公证 |
| osx-x64 | 自包含未签名 `.app.tar.gz`，含执行位 | Windows 交叉发布；Mach-O x64 头及 bundle 结构检查 | Intel Mac 真机、窗口、Java、Keychain、游戏、Gatekeeper、公证 |

## 固定样本

- 原版 1.20.1：Microsoft OpenJDK 17.0.4.1，真实安装、两次主菜单、两次 Quit Game 退出码 0。证据 continuation-1.20.1-*.png。
- 原版 26.3：按官方元数据自动安装 Temurin 25.0.4+101.0.LTS x64；中文路径；两次主菜单、两次 Quit Game 退出码 0。证据 continuation-26.3-*.jpg。
- Forge 47.2.0、NeoForge 47.1.106、Forge + Embeddium 整合包：保留原开发阶段 Windows 主菜单证据，本轮未重新手工跑全组合。
- Fabric 0.16.14 / 0.20.1：首次 remap 失败；F03/F04 未通过，详见 continuation-T09.md。不将人工预 remap 主菜单算作首次安装成功。

## 发布边界

- 自有源码 MIT，第三方组件逐项许可；发布不包含 Minecraft 客户端和 Java 分发包。
- 未提供 MDCL 专属 Microsoft public client，真实微软登录 U01 未验证；协议 fixture 不能替代账号验收。
- 更新器可用临时测试钥匙验签及回退，但这不构成生产信任链。生产根密钥、轮换/撤销、平台签名仍待接入；不启用桌面自动更新。
- Mac 产物明确标为 UNSIGNED / UNVERIFIED；交叉编译不代表运行或 Gatekeeper 通过。远程 CI 状态以 GitHub Actions 实际记录为准。
- 全部原始验收目标仍保留在 docs/MDCL-验收矩阵.md；未覆盖的磁盘满、断电、多 DPI、跨卷及性能压力场景不宣称通过。
