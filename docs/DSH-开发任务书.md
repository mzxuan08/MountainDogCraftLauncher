# MDCL 开发任务书

**Goal:** 实现 Windows + macOS 的 MDCL，先交付原版安装、登录与真实启动闭环，再逐步接入加载器和整合包。

**Architecture:** 单桌面进程、分层模块与共享核心；系统差异通过 Ports 注入。游戏 JVM 和安装器是受管理子进程，核心规则可以脱离 UI 测试。

**Tech Stack:** C#、.NET 10 LTS、Avalonia、MVVM、System.Text.Json、SQLite、xUnit；依赖锁定稳定版。

**Spec:** [MDCL 架构设计](MDCL-架构设计.md)。验收依据为 [MDCL 验收矩阵](MDCL-验收矩阵.md)。

执行者为 Dsh。本任务书不要求安装 Codex 插件或读取本机 Codex 技能目录。按任务顺序执行，修复失败后再进入依赖它的任务；每个任务独立提交，并在 `docs/evidence/` 记录环境、命令、结果和未验证项。

下文路径 `Core/`、`Application/`、`Infrastructure/`、`Desktop/` 分别是 `src/MDCL.Core/`、`src/MDCL.Application/`、`src/MDCL.Infrastructure/`、`src/MDCL.Desktop/` 的简写；平台目录同样位于 `src/MDCL.Platform.Windows/` 和 `src/MDCL.Platform.MacOS/`。测试目录均位于 `tests/`。

## 全局约束

- 发布目标为 `win-x64`、`osx-arm64`、`osx-x64`；Windows 与 Mac 从 T00 就进入验证。
- 桌面共享项目目标为 `net10.0`，不引入 WPF，不以 Electron 网页套壳替代设计。
- Core 不引用 UI、数据库或平台 API；ViewModel 不直接访问文件、网络、凭据或进程。
- 原版版本 ID 是不透明字符串；Java 需求由元数据和加载器约束决定。
- 原始凭据不进入仓库、日志、诊断包或可迁移实例；不用别人的 OAuth client ID。
- 不复制 PCL / HMCL 实现；新增第三方代码、图标或资源前记录许可。
- 功能失败必须有结构化错误，不用假数据、固定进度条或空方法返回成功。
- 当前交接包只有文档。不要把里面的示例接口或拟定测试视为已经存在的工程。

## 重点复查输入

1. 中文、空格、`&`、长目录以及大小写敏感卷：参数边界和路径身份保持正确。
2. 续传服务器返回 200 / 416 或 ETag 改变：残片不能污染最终文件。
3. 提交文件后、写数据库前崩溃：重启能对账，不能把半成品标成成功。
4. Apple 芯片运行旧游戏：Java 与 natives 架构一致，兼容与原生结果分别记录。
5. 登录取消、Keychain 拒绝访问、刷新令牌轮换：账号状态可恢复且不泄露秘密。

## 任务依赖

```text
T00 → T01 → T02 → T03 → T04 → T06 → T07 → T08
        └────────→ T05 ──────┘
T08 → T09 → T10 / T11 → T12（自更新可后置）
```

账号注册的外部条件可在 T00 开始核实；缺少实际准入时不伪造 T05 通过。每一任务先写会暴露风险的测试，再实现相应最小能力。纯布局与低影响文字调整不必写镜像实现的测试。

## T00：锁定环境，跑通两个系统的最小桌面壳

**文件落点：** `MDCL.sln`、`global.json`、`Directory.Build.props`、`Directory.Packages.props`、`src/MDCL.Desktop/`、`packaging/`、`docs/decisions/0001-stack.md`。

**输入与输出：** 输入本设计的平台范围；输出可在两系统打开的空 Avalonia 窗口，以及锁定的 SDK / 包版本和三 RID 构建命令。

- [ ] 检查现有工作目录和工具，不覆盖已有用户文件；建立工程、项目引用方向与依赖锁文件。
- [ ] 用当前稳定版 Avalonia 建立窗口与组合根，验证 .NET 10、自包含运行及原生库装载；记录选定版本和许可。
- [ ] 运行 `dotnet restore MDCL.sln --locked-mode` 和 `dotnet build MDCL.sln -c Release --no-restore`，确认退出码为 0。
- [ ] 在 Windows、Apple Silicon Mac 与 Intel Mac 各记录窗口交互结果；缺设备的项目写“未实测”，保持后续共享核心开发。
- [ ] 建立 `docs/decisions/0002-auth-registration.md`，列出 MDCL public client 注册、目标 scope、回调和 Minecraft API 接入验证结果；无账号秘密。

**阶段交付：** 三 RID 的编译产物、桌面窗口证据与环境清单。空窗口不称为可用启动器。

## T01：核心模型、系统路径与持久存储

**文件落点：** `Core/Instances/InstanceManifest.cs`、`Core/Installation/InstallPlan.cs`、`Core/Launch/LaunchPlan.cs`、`Core/Errors/MdclError.cs`、`Application/Ports/`、`Infrastructure/Storage/`、两平台的 `Paths/`、`Core.Tests/Instances/`、`Integration.Tests/Storage/`。

**契约：** 固定架构文档中的数据类型和 Ports；增加 `IInstanceRepository.LoadAsync/SaveAsync`、`IInstanceLease.AcquireAsync`、`IAppPaths`。保存返回 revision，冲突要显式报告。

- [ ] 编写实例 ID 不随改名变化、schema 往返、数据库索引可重建及不支持新 schema 时拒绝覆盖的测试。
- [ ] 实现原子清单保存、SQLite journal、实例独占租约与可重建索引；绝对路径只用于本机绑定。
- [ ] 验证默认路径不在安装目录 / `.app` 内；模拟跨磁盘游戏库，并覆盖大小写冲突和中文目录。
- [ ] 运行 `dotnet test tests/MDCL.Core.Tests -c Release --filter FullyQualifiedName~Instances` 和存储集成测试，记录通过数。

**验收：** A01、A02、S01、S02、P01、P02。此任务只管理描述与存储，不下载游戏。

## T02：版本解析、规则求值与可重复的启动描述

**文件落点：** `Core/Versions/{VersionInheritanceResolver,RuleEvaluator,LibraryMerger}.cs`、`Infrastructure/Metadata/MojangMetadataProvider.cs`、`Core.Tests/Versions/`、`tests/Fixtures/versions/`。

**契约：** `IVersionResolver.ResolveAsync(VersionSelection, TargetPlatform, CancellationToken)` 返回 `ResolvedVersion`；元数据 provider 只下载与缓存，Core 实现合并。

- [ ] 先写继承环、缺父节点、有序 rules、同坐标不同 classifier、旧式与现代参数的固定 fixture 测试。
- [ ] 加入 `26.3`、快照名和预发布名测试，确保版本标识不经错误 SemVer 排序后决定运行时。
- [ ] 实现元数据来源与摘要记录、父子合并及目标 OS / 游戏架构解析；未知关键规则给出明确错误。
- [ ] 运行 `dotnet test tests/MDCL.Core.Tests -c Release --filter FullyQualifiedName~Versions`；相同输入的 ResolvedVersion 摘要应一致。

**验收：** V01—V06。fixture 可保存公开描述，但不得附带完整游戏 jar 或账号信息。

## T03：可靠下载、安装事务和中断恢复

**文件落点：** `Infrastructure/Downloads/{ArtifactDownloader,SourceResolver,TransferJournal}.cs`、`Application/Operations/OperationCoordinator.cs`、`Infrastructure/Storage/InstallJournal.cs`、`Infrastructure/Archives/SafeArchiveExtractor.cs`、`Integration.Tests/Downloads/`、`Integration.Tests/Recovery/`。

**契约：** 实现 `IArtifactDownloader.EnsureAsync`、`IInstallPlanner.CreateAsync`、`IOperationCoordinator.RunAsync`。HTTP 假服务器可控制 Range、重定向、延迟和故意坏数据。

- [ ] 写下载摘要错误、Range 返回 200、416、ETag 改变与取消的测试；验证最终目标不会出现坏文件。
- [ ] 写两个实例等待同一 artifact、取消其中一个等待者、实例并发修改被拒绝的测试。
- [ ] 写文件提交前后、清单提交前后、索引更新前后的崩溃注入测试，检查恢复的幂等性。
- [ ] 实现限流、有限重试、`.part` 与目标同卷原子提交；跨卷先复制到目标邻接临时文件。
- [ ] 实现受控解压，拒绝穿越、symlink、大小写冲突和资源超限；验证父目录含重解析点的情形。
- [ ] 运行 `dotnet test tests/MDCL.Integration.Tests -c Release --filter "FullyQualifiedName~Downloads|FullyQualifiedName~Recovery|FullyQualifiedName~Archives"`。

**测试断言示例：** `Resume_Response200_RestartsInsteadOfAppending` 应断言最终文件长度与摘要均等于预期，残片内容未被追加；`Commit_CrashBeforeIndex_ReconcilesOnce` 应断言恢复两次后只有一个可启动实例，用户数据未变。

**验收：** D01—D10、S03、S04。真实网络成功一次不能替代故障注入测试。

## T04：Java 发现、兼容选择与受管安装

**文件落点：** `Core/Java/{JavaRequirement,JavaSelectionPolicy}.cs`、`Infrastructure/Installers/JavaRuntimeInstaller.cs`、两平台 `Java/JavaDiscovery.cs`、`Core.Tests/Java/`、`Integration.Tests/Java/`。

**契约：** `IJavaSelector.SelectAsync` 返回候选结果和淘汰原因；`IJavaDiscovery.DiscoverAsync` 与 `IJavaRuntimeInstaller.InstallAsync` 分离。

- [ ] 测试正确候选不会被不兼容的“优先目录 Java”挤掉；验证 Java 25 元数据与加载器上限冲突时返回不可满足原因。
- [ ] 探测白名单候选，处理卡死、损坏、缺模块和错误 CPU 架构，遵守 5 秒初始超时。
- [ ] 安装 Java 后实际探测再登记；Mac 恢复执行位并解析 JVM bundle，不修改全局 JAVA_HOME。
- [ ] 为 `gameArchitecture=arm64/x64` 写不同候选集和 native 规则测试，旧游戏兼容模式不静默启用。
- [ ] 运行 `dotnet test tests/MDCL.Core.Tests -c Release --filter FullyQualifiedName~Java`，在两系统分别运行 Java 集成测试。

**验收：** J01—J07、P03、P04。

## T05：登录、会话刷新与系统凭据

**文件落点：** `Infrastructure/Authentication/{MicrosoftAccountService,XboxSessionClient,MinecraftSessionClient}.cs`、`Platform.Windows/Credentials/DpapiCredentialVault.cs`、`Platform.MacOS/Credentials/KeychainCredentialVault.cs`、`Integration.Tests/Authentication/`。

**契约：** 实现 `IAccountService.GetSessionAsync` 与 `ICredentialVault` 三方法；`AuthSession` 不自动序列化，无含 token 的 ToString。

- [ ] 针对 OAuth state 错误、PKCE 失败、用户取消、权益缺失、profile 缺失和刷新轮换写协议响应测试。
- [ ] 测试同账号并发刷新只发生一次，Keychain 拒绝访问不会触发明文回退，退出登录删去持久凭据。
- [ ] 使用 MDCL public client 做持有人在系统浏览器内完成的真实登录，记录各阶段成功与失败边界。
- [ ] 将所有网络与进程日志送入脱敏入口，测试凭据不能被异常消息、URL 或结构化日志展开泄露。
- [ ] 运行认证测试；真实外部流程单列人工证据，不作为每次 CI 都要反复登录的测试。

**验收：** U01—U07、S05—S07。未满足外部准入的状态必须如实报告，不改用他人的应用 ID。

## T06：启动计划、游戏进程与诊断

**文件落点：** `Core/Launch/{LaunchPlanBuilder,ArgumentTemplate,LaunchPrecheck}.cs`、`Infrastructure/Processes/{GameProcessService,JavaArgumentFileWriter,LogStreamReader}.cs`、两平台 `Processes/`、`Application/Diagnostics/`、`Core.Tests/Launch/`、`Integration.Tests/Processes/`。

**契约：** 实现 `ILaunchPlanner.CreateAsync`、`IGameProcessService.StartAsync` 和 `IGameSession`；秘密只在执行边界绑定。

- [ ] 测试含中文 / 空格 / `&` 的参数完整到达小型测试子进程；测试不会调用 shell，不会生成多个参数。
- [ ] 对 Java argfile 独立测试引号、反斜杠和编码；验证超长命令走正确策略，Java 8 未支持时返回明确预检错误。
- [ ] 用小型子进程同时大量写 stdout / stderr 并拆分 UTF-8 字符，确认无死锁且解码正确；测试退出与取消竞态。
- [ ] 在 Windows 与 Mac 实测进程归属和定向停止，确认另一实例与其他软件的 Java 未被结束。
- [ ] 加入敏感 argfile 权限和退出 / 崩溃清理测试，验证 LaunchPlan 导出不含真实 token。
- [ ] 实现证据式错误报告与诊断包允许清单；无依据的原因保留 Unknown。
- [ ] 运行 `dotnet test tests/MDCL.Core.Tests -c Release --filter FullyQualifiedName~Launch` 及进程集成测试。

**验收：** L01—L10、S06—S08、P05。进程创建成功与到达游戏主菜单分别记录。

## T07：桌面交互和原版完整闭环

**文件落点：** `Desktop/Views/`、`Desktop/ViewModels/`、`Desktop/Composition/`、`Desktop.Tests/`。

**输入：** 已完成的实例、安装、Java、账号和启动用例；ViewModel 不增加另一套业务实现。

- [ ] 实现实例、安装、账号、任务与设置；空状态、失败、取消和恢复都能从界面进入。
- [ ] 实现进度节流、列表虚拟化、日志环形缓冲与旧请求取消；平台快捷键分别采用 Ctrl / Cmd。
- [ ] 在三种目标真实安装并启动冻结的 `1.20.1` 与 `26.3` 原版；若上游版本变化，增加新样本而非删除固定回归样本。
- [ ] 创建两个同版本实例，分别写入测试配置，验证彼此隔离；重启 MDCL 后再次启动已有实例。
- [ ] 执行 UI 自动化和人工验收，记录当前步骤可读性、取消响应与故障可恢复性。

**验收：** A03—A05、F01、F02、Q01—Q04。首版 Alpha 的产品闭环在此形成。

## T08：三目标打包与发布证据

**文件落点：** `packaging/windows/`、`packaging/macos/{Info.plist,entitlements.plist}`、`docs/evidence/release-matrix.md`、CI 配置。

- [ ] 分别执行 `dotnet publish src/MDCL.Desktop -c Release -r win-x64 --self-contained true`、相应 `osx-arm64` 与 `osx-x64` 命令；不要将三者输出覆盖到同一目录。
- [ ] Windows 打包 zip；macOS 创建 .app，设置正确 RID、Info.plist、图标和执行权限，再在 Mac 上签名、公证及检查 Gatekeeper。
- [ ] 签名 / 公证凭据只由密钥存储或 CI secret 注入。条件未满足则交付明确标识的测试包，并保留正式分发阻塞项。
- [ ] 从干净用户环境启动，验证不需要 .NET SDK，应用目录不可写也能安装游戏；升级包保留用户数据。
- [ ] 输出产物哈希、版本、依赖清单、各平台实测结果与限制；通过后才标记首版正式候选。

**验收：** R01—R06、P06、P07。`osx-x64` 在 Rosetta 下的测试单独记录，不替代 Intel Mac 实测。

## T09：Fabric

**文件落点：** `Infrastructure/Installers/FabricComponentInstaller.cs`、`Core/Versions/Compatibility/`、对应测试与固定 fixtures。

- [ ] 选定一个官方声明兼容的游戏 / Fabric 组合，固定精确加载器版本及来源；实现补丁与依赖计划。
- [ ] 验证安装、重新打开、再次启动及加载器版本切换；对 Java 上限冲突给出解释。
- [ ] 在三目标完成真实主菜单验证，失败不污染已有原版实例。

**验收：** F03、F04、I01。完成后再扩展模组目录与远程检索。

## T10：Forge / NeoForge 与本地模组

**文件落点：** `Infrastructure/Installers/{ForgeComponentInstaller,NeoForgeComponentInstaller}.cs`、`Core/Mods/`、相关测试。

- [ ] 为两个加载器分别固定兼容组合与 installer 版本，测试 processor 的输入输出、退出失败和取消恢复。
- [ ] 所有安装器在 staging 运行，验证受控合并和用户目录保护；不在 UI 层拼接安装命令。
- [ ] 实现本地模组启用 / 禁用与依赖提示；运行中的实例禁止修改关键依赖。

**验收：** F05、F06、I01、I02。不能因为 Fabric 通过就声明其他加载器通过。

## T11：整合包与远程资源

**文件落点：** `Infrastructure/Installers/ModrinthPackImporter.cs`、`Infrastructure/Metadata/ModrinthProvider.cs`、对应计划与路径测试。

- [ ] 先实现 `.mrpack` 计划预览、新实例导入和摘要验证；测试目录穿越、未知格式、缺少资源与脚本字段。
- [ ] 迁移实例时保留数据与组件描述，按目标 OS / arch 重建可执行依赖，验证重新登录要求。
- [ ] 条件具备后再增加 CurseForge adapter；没有有效 API 方案时不伪造检索与下载结果。

**验收：** I03—I06、S03、P08。首版不执行整合包自定义脚本。

## T12：自更新及长期回归

**文件落点：** `Infrastructure/Updates/`、独立更新器工程、签名验证测试和发布工作流。此阶段开始前补充独立的更新信任设计记录。

- [ ] 测试被篡改清单、错误 RID、过期、非授权降级、签名错误和中途失败，确保不能替换正常应用。
- [ ] 实现签名发布清单、摘要验证、外部替换与回退，保留配置迁移兼容检查。
- [ ] 三目标实测升级、回滚和游戏运行期间的更新策略；失败保留旧程序与全部用户数据。

**验收：** R07、R08。首版可以保留手动更新，不能为了展示“自动更新”而省略信任与回滚。

## 每阶段提交格式

在 `docs/evidence/Txx-结果.md` 记录：提交标识、执行环境、实际命令与退出码、测试数量、操作路径、产物位置，以及未验证项。人工游戏测试注明是否到达主菜单、是否正常退出、是否可再次启动。真实 token 和私人路径在落盘前脱敏。

若无法访问 Mac、微软应用注册或签名证书，继续能独立完成的开发，在最终报告列出具体未验收条目；不得将编译、模拟测试或代码阅读写成真实交互成功。
