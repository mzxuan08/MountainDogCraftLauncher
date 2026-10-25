# MDCL 具体架构设计（实施基线）

版本：2.0；日期：2026-10-25；范围：T00—T12；目标平台：Windows 11 24H2+ x64、macOS 15+ arm64/x64。

本文把《MDCL 架构设计》《DSH 开发任务书》《MDCL 验收矩阵》细化为可直接实施的模块、接口、数据、状态机和故障协议。原交接文档中的“必须”“不得”继续有效；本文只消除实现歧义，不扩大首版承诺，也不表示任何功能已经实现或通过验收。

---

## 1. 产品边界与不可变约束

### 1.1 首版 Alpha（T00—T08）

首版只形成以下真实闭环：

1. 创建相互隔离的原版实例；
2. 从 Mojang 官方元数据解析版本与 Java 要求；
3. 下载并校验 client、libraries、assets、logging 与 natives；
4. 发现已有 Java，或从受支持渠道安装受管 Java；
5. 添加明确标识的本地身份；外部条件具备后使用 MDCL 专属 public client 完成微软登录；
6. 生成可审查的 LaunchPlan，直接启动 Java，显示实时日志与退出状态；
7. 正常退出后再次启动同一实例；
8. 分别生成 `win-x64`、`osx-arm64`、`osx-x64` 自包含产物。

首版不做插件市场、云同步、联机隧道、自动执行整合包脚本、通用模组运行沙箱，也不以网页原型代替桌面产品。

### 1.2 后续扩展顺序

- T09：Fabric；
- T10：Forge、NeoForge、本地模组启停；
- T11：`.mrpack`、Modrinth；CurseForge 仅在 API 与分发条件明确后增加；
- T12：带离线信任根、签名清单、外部替换与回退的自更新。

任何后续组件不得绕过首版的下载、事务、路径、凭据和进程边界。

### 1.3 全局不变量

1. `VersionId`、`InstanceId`、`ResolvedVersion` 是三个不同身份。
2. 版本 ID 是不透明字符串；Java 要求来自元数据与可信组件约束的交集。
3. 实例用户数据独立；共享依赖只读复用、受控提交，不硬链接到可写实例。
4. 未通过大小和摘要验证的文件绝不成为可见目标。
5. 用户存档、配置和凭据不属于安装器可写范围。
6. 凭据只在系统凭据库与执行边界出现，不进入普通 JSON、SQLite、日志、诊断包和实例导出。
7. Java 参数逐项传递，不经过 shell；游戏架构同时约束 Java 与 natives。
8. 进程创建、游戏运行、到达主菜单、正常退出是四个独立事实。
9. Mac 交叉编译不等于 Mac 运行；模拟响应不等于真实微软登录；未签名测试包不等于正式分发。
10. 任一失败返回结构化错误；取消不能留下“看似成功”的实例、Java、账号或更新。

---

## 2. 解决方案与依赖方向

### 2.1 固定工程

```text
MDCL.sln
src/
  MDCL.Core/                 纯领域模型与纯规则
  MDCL.Application/          用例、编排、Ports、DTO
  MDCL.Infrastructure/       网络、SQLite、文件事务、认证协议、进程
  MDCL.Platform.Windows/     Windows Paths、DPAPI、注册表、Job Object
  MDCL.Platform.MacOS/       macOS Paths、Keychain、JVM bundle、进程组
  MDCL.Desktop/              Avalonia Views、ViewModels、Composition
  MDCL.Updater/              T12 独立、最小化外部更新器
tests/
  MDCL.Core.Tests/
  MDCL.Integration.Tests/
  MDCL.Desktop.Tests/
  MDCL.Updater.Tests/
  Fixtures/
packaging/
eng/
```

共享项目目标为 `net10.0`。平台适配程序集保持可编译、可单元测试；仅组合根按当前 OS 注册，不允许 Core 或 Application 使用 `OperatingSystem.IsWindows()` 选择业务规则。

### 2.2 引用规则

```text
Core                    → 无 MDCL 项目、UI、数据库或平台 API
Application             → Core
Infrastructure          → Application + Core
Platform.Windows        → Application + Core
Platform.MacOS          → Application + Core
Updater                 → 仅更新签名/文件替换所需的最小共享契约
Desktop                 → Application + Core
Desktop/Composition     → Infrastructure + 两个 Platform 项目
Core.Tests              → Core
Integration.Tests       → Application + Infrastructure + Platform
Desktop.Tests           → Desktop + Application 测试替身
```

具体约束：

- Core 禁止引用 Avalonia、Microsoft.Data.Sqlite、HttpClient、Process、DPAPI、Security.framework。
- ViewModel 禁止引用 Infrastructure、Platform、HttpClient、File、SQLite、Process、Keychain。
- Infrastructure 可实现 Application Ports，但不得反向引用 Desktop。
- 平台共有文件处理放 Infrastructure；只有真正系统 API 放 Platform。
- 首版组件策略编译进应用，不加载实例或整合包中的陌生 DLL。

### 2.3 稳定依赖

SDK 与包版本由 `global.json`、`Directory.Packages.props` 和 `packages.lock.json` 固定。默认依赖为：

- Avalonia、Avalonia.Desktop、Fluent、Inter；
- CommunityToolkit.Mvvm；
- Microsoft.Extensions.DependencyInjection / Logging；
- System.Text.Json；
- Microsoft.Data.Sqlite；
- System.Security.Cryptography.ProtectedData；
- xUnit、Microsoft.NET.Test.Sdk、Avalonia.Headless.XUnit。

不默认启用 Native AOT、裁剪或单文件。新增代码、字体、图标、安装器辅助文件前，在 `THIRD-PARTY-NOTICES.md` 记录来源、精确版本和许可。

---

## 3. 进程、线程与组合根

### 3.1 进程模型

- MDCL 为单桌面进程。
- 游戏 JVM、Forge/NeoForge installer/processor、T12 更新器为受管子进程。
- 首版不启动本地 HTTP 服务；微软回调只在一次登录期间绑定指定 loopback 端口。
- 游戏退出、MDCL 退出和实例租约释放分别处理，不扫描或结束所有 Java。

### 3.2 线程与调度

- UI 属性变更只发生在 Avalonia UI 线程。
- 网络、磁盘、SQLite、Java 探测、进程等待全部异步，不阻塞 UI。
- Core 规则为同步纯函数；需要 I/O 的加载由 Ports 完成。
- `OperationCoordinator` 使用有界调度器：全局下载并发默认 8、单主机默认 4；Java 探测默认并发 2。
- UI 进度 100 ms 节流，终态、取消和错误立即发送。
- 日志使用有界通道；UI 环形缓冲默认 2,000 行，落盘文件按大小轮转。

### 3.3 Composition 根注册

| 服务 | 生命周期 | 说明 |
| --- | --- | --- |
| `IAppPaths` | Singleton | 启动时按 OS 创建并验证目录 |
| `HttpClient` / 下载策略 | Singleton | 统一 Handler、超时、重定向与脱敏 |
| `IInstanceRepository` | Singleton | 无内存权威副本，清单为事实来源 |
| `IInstanceIndex` | Singleton | SQLite 可重建索引 |
| `IInstanceLeaseManager` | Singleton | 每次操作用 `await using` 获取 |
| `IVersionResolver` | Singleton | Provider + 纯 Core resolver |
| `IArtifactDownloader` | Singleton | 管理共享传输与 artifact 锁 |
| `IOperationCoordinator` | Singleton | 操作队列、journal、恢复入口 |
| `IJavaDiscovery` | Singleton | 当前平台 adapter |
| `IJavaSelector` | Singleton | 纯兼容过滤与排序 |
| `IAccountService` | Singleton | 每账号刷新 single-flight |
| `ICredentialVault` | Singleton | DPAPI 或 Keychain，无明文备用 |
| `IGameProcessService` | Singleton | 管理 `SessionId → IGameSession` |
| ViewModel | Transient 或每主窗口 Singleton | 只调用 Application 用例 |

`ServiceProvider` 只在组合根持有；禁止 service locator 传入 Core。

---

## 4. 核心身份与数据契约

### 4.1 标识与平台

```csharp
readonly record struct InstanceId(Guid Value);
readonly record struct AccountId(Guid Value);
readonly record struct OperationId(Guid Value);
readonly record struct SessionId(Guid Value);

enum CpuArchitecture { X64, Arm64 }
enum LauncherOperatingSystem { Windows, MacOS }
sealed record TargetPlatform(
    LauncherOperatingSystem OS,
    CpuArchitecture LauncherArchitecture,
    CpuArchitecture GameArchitecture);
```

显式映射：

- Mojang OS：`windows` / `osx`；
- RID：`win-x64` / `osx-x64` / `osx-arm64`；
- Java 架构与 native classifier 使用 `GameArchitecture`，不得使用 RID 字符串替代规则求值。

### 4.2 InstanceManifest

持久字段固定为：

```text
schemaVersion       当前 1；未知更高版本只读并拒绝覆盖
instanceId          UUID，创建后不变
displayName         用户可修改，不参与身份
baseVersion         不透明版本 ID
loader              { kind, version }；原版 version=null
gameDirectory       默认 ".minecraft"，相对实例根
javaPolicy          { boundRuntimeId, preferManaged, gameArchitecture, preferredMajor? }
memoryMiB           正整数，启动预检再验证合理范围
extraJvmArgs        字符串数组，不保存需重新拆分的命令行
createdAt           UTC 时间戳
revision            每次成功保存单调递增
```

`instance.json` 是实例事实来源。SQLite 只保存可重建索引、任务和会话记录，不保存另一份可独立编辑的实例配置。

### 4.3 版本与安装

```text
VersionSelection    baseVersionId + loaderKind + loaderVersion?
ResolvedVersion     versionIdentity、mainClass、有序 JVM/game 模板、libraries、natives、
                    assets、logging、JavaRequirement、来源与摘要、确定性 planHash
ArtifactSpec        artifactKey、候选 URI、预期大小、摘要算法/值、摘要来源、
                    相对目标路径、trustLevel
InstallPlan         planId、instanceId、planHash、revision、步骤 DAG、目标快照
InstallStep         stepId、dependsOn、kind、输入、预期产物、重试/取消策略
```

`planHash` 对规范化目标快照、步骤输入、依赖关系和组件版本计算；任何输入变化都生成新 revision，旧完成记录不得复用。

### 4.4 Java

```text
JavaRequirement     preferredMajor、minimumMajor、maximumMajor?、architecture、
                    requiredModules、constraints[]
JavaConstraint      minimum/maximum、适用组件、来源
JavaRuntime         runtimeId、home、executable、major、vendor、architecture、
                    probeStatus、lastProbedAt、isManaged、modules
JavaSelection       selected?、每个候选的淘汰原因、约束冲突/绑定失效错误
```

允许范围与首选版本分别建模。缺少独立兼容依据时，不自动把官方 preferredMajor 升级为更高版本。

### 4.5 账号与启动

`AccountProfile` 只含公开身份：`AccountId`、显示名、`Local/Microsoft`、Minecraft UUID、凭据引用键。

`AuthSession` 为运行期对象：公开 profile、短期 access token、到期时间。禁止自动序列化，`ToString()` 不包含 token。

`LaunchPlan` 含实例 ID、Java、工作目录、参数模板、classpath、native 目录、依赖摘要、预检证据、目标架构；不含 access token。秘密仅在 `GameProcessService.StartAsync` 的执行边界绑定。

---

## 5. Application Ports 与用例

### 5.1 核心 Ports

```csharp
IVersionResolver.ResolveAsync(VersionSelection, TargetPlatform, CancellationToken)
    -> ResolvedVersion

IInstallPlanner.CreateAsync(InstallRequest, CancellationToken)
    -> InstallPlan

IOperationCoordinator.RunAsync(
    InstallPlan, IProgress<OperationEvent>, CancellationToken) -> OperationResult

IArtifactDownloader.EnsureAsync(
    ArtifactSpec, IProgress<TransferEvent>, CancellationToken) -> ArtifactReceipt

IJavaDiscovery.DiscoverAsync(CancellationToken) -> IReadOnlyList<JavaRuntime>
IJavaSelector.SelectAsync(
    JavaRequirement, JavaPolicy, IReadOnlyList<JavaRuntime>, CancellationToken) -> JavaSelection
IJavaRuntimeInstaller.InstallAsync(
    JavaRequirement, IProgress<OperationEvent>, CancellationToken) -> JavaRuntime

IAccountService.GetSessionAsync(AccountId, bool allowInteractive, CancellationToken)
    -> AuthSession
ICredentialVault.ReadAsync(string key, CancellationToken) -> ReadOnlyMemory<byte>?
ICredentialVault.ReplaceAsync(string key, ReadOnlyMemory<byte> value, CancellationToken)
ICredentialVault.DeleteAsync(string key, CancellationToken)

ILaunchPlanner.CreateAsync(LaunchRequest, CancellationToken) -> LaunchPlan
IGameProcessService.StartAsync(LaunchPlan, AuthSession, CancellationToken)
    -> IGameSession
```

### 5.2 存储 Ports

```csharp
IInstanceRepository.LoadAsync(InstanceId, CancellationToken)
IInstanceRepository.ListAsync(CancellationToken)
IInstanceRepository.SaveAsync(manifest, expectedRevision, CancellationToken) -> newRevision
IInstanceLeaseManager.AcquireAsync(InstanceId, ownerId, CancellationToken)
IInstanceIndex.RebuildAsync(CancellationToken)
```

`SaveAsync` 比较 expectedRevision；冲突返回 `INSTANCE_REVISION_CONFLICT`。成功写文件后才增加 revision。租约owner 必须包含操作/会话 ID、PID 和创建时间，便于诊断，但不能仅凭 stale 文件强行抢锁。

### 5.3 用例划分

- `CreateInstance`：校验名称与版本选择，分配 UUID，建目录，保存初始清单，重建索引。
- `RenameInstance`：读 revision、修改显示名、原子保存；不改变目录名。
- `InstallInstance`：获取修改租约、解析、计划、执行、提交、发布索引。
- `SelectJava`：发现、探测、过滤、解释；安装 Java 是独立用例。
- `AddLocalAccount`：创建本地 UUID，不伪造微软会话。
- `LoginMicrosoft` / `RefreshSession` / `Logout`：分阶段状态与凭据生命周期。
- `LaunchInstance`：获取启动租约、重新校验计划、绑定会话、启动、监控。
- `ImportModpack`：只生成新实例计划，用户确认后执行。
- `ExportDiagnostics`：仅允许清单内容，先预览再导出。

---

## 6. 文件布局、JSON 与 SQLite

### 6.1 系统路径

| 用途 | Windows | macOS |
| --- | --- | --- |
| 配置/数据库 | `%LOCALAPPDATA%\MDCL\` | `~/Library/Application Support/MDCL/` |
| 可重建缓存 | 配置根 `cache/` | `~/Library/Caches/MDCL/` |
| 脱敏日志 | 配置根 `logs/` | `~/Library/Logs/MDCL/` |
| 默认游戏库 | 配置根 `data/` | 配置根 `data/` |

任何用户数据不得写入安装目录或 `.app`。外置游戏库是本机绑定；失效、断开或权限不足必须显式报告。

### 6.2 游戏库

```text
data/
  instances/<uuid>/
    instance.json
    resolved-version.json
    .minecraft/
    backups/instance.rev<N>.json
    .mdcl.lock
  shared/
    libraries/
    assets/objects/<2位前缀>/<hash>
    versions/<上游受管身份>/
    objects/<algorithm>/<hash>
  runtimes/<os>-<arch>/<runtime-id>/
  staging/<operation-id>/
  trash/<instance-id>-<utc timestamp>/
  sessions/<session-id>/natives/
```

相对路径在 JSON 中统一使用 `/`，系统边界再转换。实例复制不携带外部 Java 绑定、平台 natives、凭据和会话目录。

### 6.3 原子清单协议

1. 读取当前清单和 revision；未知新 schema 立即拒绝写入。
2. 在实例目录创建随机临时文件，限制权限。
3. 写入完整 JSON，`Flush(true)`。
4. 旧版本复制到 `backups/instance.rev<N>.json`。
5. 同卷 rename 替换 `instance.json`。
6. 更新 SQLite 索引；索引失败可在下次启动重建，不回滚已有效清单。

崩溃遗留临时文件不参与加载。恢复操作可重复执行，不能截断现有有效清单。

### 6.4 SQLite 表

```sql
schema_metadata(key TEXT PRIMARY KEY, value TEXT NOT NULL);

instances(
  instance_id TEXT PRIMARY KEY,
  display_name TEXT NOT NULL,
  base_version TEXT NOT NULL,
  revision INTEGER NOT NULL);

operations(
  operation_id TEXT PRIMARY KEY,
  instance_id TEXT,
  plan_hash TEXT NOT NULL,
  revision INTEGER NOT NULL,
  state TEXT NOT NULL,
  journal_path TEXT NOT NULL,
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL);

operation_steps(
  operation_id TEXT NOT NULL,
  step_id TEXT NOT NULL,
  state TEXT NOT NULL,
  attempt INTEGER NOT NULL,
  evidence_json TEXT NOT NULL,
  PRIMARY KEY(operation_id, step_id));

artifacts(
  artifact_key TEXT PRIMARY KEY,
  algorithm TEXT NOT NULL,
  hash TEXT NOT NULL,
  size INTEGER NOT NULL,
  final_path TEXT NOT NULL,
  state TEXT NOT NULL,
  updated_at TEXT NOT NULL);

artifact_sources(
  artifact_key TEXT NOT NULL,
  uri TEXT NOT NULL,
  trust_source TEXT NOT NULL,
  last_used_at TEXT NOT NULL,
  PRIMARY KEY(artifact_key, uri));

accounts(
  account_id TEXT PRIMARY KEY,
  kind TEXT NOT NULL,
  display_name TEXT NOT NULL,
  minecraft_uuid TEXT,
  credential_key TEXT);

game_sessions(
  session_id TEXT PRIMARY KEY,
  instance_id TEXT NOT NULL,
  pid INTEGER,
  process_started_at TEXT,
  state TEXT NOT NULL,
  exit_code INTEGER,
  evidence_path TEXT);
```

SQLite 使用 WAL、外键和受限事务。数据库丢失时从清单重建 `instances`；未决 operations 必须进入恢复检查，不能根据缺失索引推断成功。账号表不存 token。

---

## 7. 错误、取消、并发与恢复统一模型

### 7.1 MdclError

```text
ErrorCode、Stage、Summary、Evidence[]、SuggestedActions[]、
Retryable、OperationId?、InnerException（仅内部，不直接落盘）
```

常用错误码固定为：

- `INSTANCE_BUSY`、`INSTANCE_REVISION_CONFLICT`、`INSTANCE_SCHEMA_UNSUPPORTED`；
- `VERSION_PARENT_MISSING`、`VERSION_INHERITANCE_CYCLE`、`VERSION_RULE_UNSUPPORTED`；
- `DOWNLOAD_HASH_MISMATCH`、`DOWNLOAD_RANGE_INVALID`、`DOWNLOAD_DISK_FULL`；
- `INSTALL_RECOVERY_REQUIRED`、`ARCHIVE_UNSAFE_ENTRY`、`ARCHIVE_LIMIT_EXCEEDED`；
- `JAVA_NO_COMPATIBLE_RUNTIME`、`JAVA_CONSTRAINT_CONFLICT`、`JAVA_PROBE_TIMEOUT`；
- `AUTH_CANCELED`、`AUTH_STATE_MISMATCH`、`AUTH_ENTITLEMENT_MISSING`、`AUTH_PROFILE_MISSING`；
- `NATIVE_ARCH_MISMATCH`、`PROCESS_COMMAND_TOO_LONG`、`UPDATE_SIGNATURE_INVALID`。

异常消息、URL 和 Evidence 先脱敏再记录。

### 7.2 取消规则

- 每个 UI 用例持有独立 `CancellationTokenSource`，页面切换取消旧搜索/刷新。
- 等待共享下载的取消只取消等待者；没有其他等待者时才取消实际传输。
- 文件提交阶段收到取消时，先到达明确 journal 边界，再返回 Canceled 或 RecoveryRequired。
- installer/processor 子进程通常只能终止后重做，不显示虚假的“暂停”。
- 取消登录关闭 loopback listener、清理 state/PKCE，不添加账号。

### 7.3 锁层级

固定顺序避免死锁：

```text
实例修改/启动租约 → operation 锁 → artifact 锁 → 账号刷新锁
```

不得持账号锁等待实例锁。每实例同时只有一个修改者；运行期间禁止修改关键依赖，但允许查看日志。artifact 锁按稳定 artifactKey，不按 URL。

---

## 8. 版本解析具体架构

### 8.1 类型与文件

```text
Core/Versions/
  VersionDocument.cs
  VersionInheritanceResolver.cs
  RuleEvaluator.cs
  MavenCoordinate.cs
  LibraryMerger.cs
  ArgumentMerger.cs
  ResolvedVersionHasher.cs
Infrastructure/Metadata/
  MojangMetadataProvider.cs
  MetadataCache.cs
```

Provider 只负责 manifest/version JSON 的下载、缓存、来源和 SHA-1 校验；Core 负责继承、规则和合并。

### 8.2 解析流水线

1. 读取 `version_manifest_v2.json`，按精确 ID 选择版本，不做 SemVer 排序。
2. 下载版本 JSON，记录 URL、预期摘要、实际摘要和读取时间。
3. 构建 `inheritsFrom` 图，检测缺父、环和最大深度。
4. 从根到叶依次合并：父提供基础，子覆盖标量；参数按协议顺序合并。
5. 归一化 Maven 坐标为 `group:artifact:version[:classifier][@extension]`。
6. 同坐标不同 classifier 不去重；同坐标不同版本仅由显式组件策略解决，否则报冲突。
7. 按有序 rules 求值 OS、arch、feature；未知且影响启动的规则报错。
8. 应用加载器补丁，生成最终 classpath 和 mainClass。
9. 解析 `javaVersion`，与组件约束求交。
10. 规范化并计算 ResolvedVersion 哈希，保存原始来源而不就地修改原 JSON。

### 8.3 规则求值

每条 rule 按原顺序应用 `allow/disallow`；最后匹配结果生效。支持：

- `os.name`：windows/osx 等显式映射；
- `os.arch`：游戏进程架构；
- `features`：由启动上下文提供，如 demo、自定义分辨率；
- natives classifier 与 `downloads.classifiers`；
- 旧式 `minecraftArguments` 与现代 `arguments.jvm/game` 分开解析。

固定 fixtures 至少覆盖：缺父、继承环、有序规则、classifier、旧/现代参数、`1.20.1`、`26.3`、快照名和预发布名。

### 8.4 确定性

同一原始 JSON、目标平台、feature 集和组件补丁必须产生相同 ResolvedVersion 哈希。集合输出前排序；classpath 保留协议顺序，不用无序字典顺序决定启动参数。

---

## 9. 下载、安装事务与安全解压

### 9.1 下载器结构

```text
ArtifactDownloader       对外 EnsureAsync、缓存命中、最终提交
SourceResolver           按资源类型选择官方/可关闭镜像，不做全局字符串替换
TransferEngine           单源 HTTP、Range、超时、有限重试
TransferJournal          URL 身份、ETag、Last-Modified、已写字节、预期摘要
SharedTransferRegistry   artifactKey 引用计数、等待者取消
HashVerifier             SHA-1 / SHA-256 流式校验
```

### 9.2 单文件协议

1. 目标存在且大小/摘要正确：返回 CacheHit 和既有来源记录。
2. 目标存在但损坏：移入受控隔离区或重建，绝不追加使用。
3. `.part` 必须位于最终目标同卷；跨卷先复制到目标邻接临时文件。
4. 只有 URL、预期摘要和验证器均可信时才续传。
5. Range 请求后：
   - `206`：核对 `Content-Range`、总大小、ETag/Last-Modified；
   - `200`：截断并从零下载，不追加；
   - `416`：校验残片；身份不确定则从零；
   - ETag 改变或源切换：丢弃不可信残片。
6. 网络错误、429、5xx 初始最多 3 次指数退避；429 尊重 `Retry-After`。
7. 401/403、权限不足、磁盘满不无限重试；摘要错误最多完整重取 2 次。
8. 写完后检查大小和摘要，`Flush(true)`，同卷 rename，记录来源。

HTTPS 验证不可关闭。重定向仅允许预期协议；跨域不继承 Authorization/Cookie。认证响应不进入普通下载缓存。总大小未知时 `TotalBytes=null`，UI 不显示假百分比。

### 9.3 安装 DAG 与状态

```text
Created → Planning → Queued → Running → Verifying → Committing → Completed
                           ↘ Canceled / Failed / RecoveryRequired
```

步骤类型：`ResolveVersion`、`EnsureArtifact`、`ExtractNatives`、`InstallComponent`、`CommitFiles`、`CommitManifest`、`UpdateIndex`。

每步声明输入、依赖、产物、是否可重试、是否可取消。调度器只运行依赖已完成且 planHash/revision 一致的步骤。

### 9.4 InstallJournal

每个 operation 使用独立 JSON journal，关键阶段写盘并刷盘：

```text
Prepared
FilesCommitted       每个目标的路径、摘要、备份位置
ManifestCommitted    新 manifest revision
IndexUpdated
Completed
```

文件与 SQLite 没有全局事务，恢复以实际文件和摘要为准。

### 9.5 崩溃恢复矩阵

| 最后证据 | 启动恢复动作 |
| --- | --- |
| 无 Prepared | 清理可识别的 staging；不显示新实例 |
| Prepared，未提交 | 校验 staging；安全清理或继续，原实例不变 |
| 部分 FilesCommitted | 逐项核对目标/备份；只回滚或继续本操作产物 |
| 文件全提交，清单未提交 | 验证完整产物后重做清单提交，或恢复备份 |
| 清单已提交，索引未更新 | 从清单重建索引，不重复写用户数据 |
| IndexUpdated，未 Completed | 对账后只补终态 |
| journal 与文件均不可信 | 标记 RecoveryRequired，保留现场，不猜成功 |

恢复必须幂等；执行两次仍只有一个可启动实例。已有实例升级使用新配置快照，回滚不删除整个 `.minecraft`。

### 9.6 安全解压

`SafeArchiveExtractor` 统一服务 Java、natives、整合包：

- 条目必须是相对路径；拒绝 `../`、绝对路径、UNC、盘符、ADS、末尾空格和 Windows 保留名。
- 使用 `Path.GetFullPath` 加目录边界检查，不用简单 `StartsWith` 字符串判断。
- 根目录必须新建受控；检查每级父目录，拒绝重解析点/symlink 越界。
- 默认拒绝 symlink/hardlink 和特殊文件。
- 同时按 Ordinal 与目标平台折叠规则检测重名；大小写折叠冲突拒绝。
- 限制条目数、单条目大小、总展开大小和压缩比；超限即失败。
- 只为受管 Java/必要 natives 恢复白名单执行位，不恢复包内任意脚本权限。

---

## 10. Java 发现、选择与安装

### 10.1 约束求交

`JavaRequirementFactory` 输入官方 `javaVersion`、加载器约束、整合包可信约束和目标架构。输出：

```text
minimum = 所有有效下限的最大值
maximum = 所有有效上限的最小值（若存在）
preferred = 官方 major，且必须落在交集内
architecture = gameArchitecture
requiredModules = 各可信来源并集
```

`minimum > maximum` 或 preferred 不合法时返回 `JAVA_CONSTRAINT_CONFLICT`，列出所有来源，不丢弃任何一方。

缺少 javaVersion 的旧版本才使用集中管理、带来源和回归用例的兼容表；禁止从“26.3”等字符串推断 Java。

### 10.2 发现与探测

Windows 白名单：MDCL runtimes、用户添加目录、JAVA_HOME、PATH、常见安装目录、注册表。

macOS 白名单：MDCL runtimes、用户目录、JAVA_HOME、PATH、`/Library/Java/JavaVirtualMachines`、用户 JVM bundle，必要时 `/usr/libexec/java_home`；识别 `Contents/Home/bin/java`。

每个候选运行受控探测：

```text
java -XshowSettings:properties -version
```

同时解析 stdout/stderr，初始超时 5 秒；记录 major、vendor、os.arch、java.home。必要时用小型受管探测类检查模块。卡死、损坏、架构错误、缺模块均只淘汰当前候选。

### 10.3 选择顺序

先兼容过滤，再排序：

1. 实例显式绑定；
2. 实例 preferredMajor / 策略；
3. MDCL 受管运行时；
4. 其他已验证候选。

显式绑定失效返回具体原因，不偷偷换用不兼容候选；用户可改回自动。所有被淘汰候选保留原因供 UI 展开。

### 10.4 受管安装

`JavaRuntimeInstaller` 与 discovery 分离：

1. 从受支持分发元数据选择精确 OS/arch/major 包与摘要；
2. 使用 ArtifactDownloader；
3. 解压到 `staging/<operation>`；
4. macOS 恢复必要执行位，识别 JVM bundle；
5. 实际探测版本、架构和模块；
6. 写入 runtime 描述，原子移动到 `runtimes/<os>-<arch>/<runtime-id>`；
7. 再次探测后登记；中断可清理或重做，不把压缩包完成当作安装成功。

Apple Silicon 旧游戏兼容必须显式选择 `gameArchitecture=x64`，同时选 x64 Java 与 x64 natives，并检测 Rosetta；不静默安装 Rosetta，不混用 arm64 native。

---

## 11. 账号与凭据架构

### 11.1 本地身份

`LocalAccountService` 生成稳定 AccountId 和本地 Minecraft UUID，界面始终标记“本地身份”。本地身份可启动离线模式，但不获得正版服务器会话，不伪造 Xbox/Minecraft 验证。

### 11.2 微软链路

```text
MicrosoftAccountService   授权码 + PKCE、loopback、刷新、single-flight
XboxSessionClient         Microsoft token → Xbox Live → XSTS
MinecraftSessionClient    Minecraft token → entitlements → profile
ICredentialVault          持久 refresh token
AuthRedactor              全阶段日志与异常脱敏
```

仅使用 MDCL 自己 public client，无 client secret。目标 scope、回调和 API 准入由 MDCL 独立验证；未提供 client ID 时真实登录保持“条件不足”，协议测试仍可运行。

### 11.3 浏览器授权

- 使用系统浏览器；密码和验证码不经过 MDCL。
- 每次生成密码学随机 state、PKCE verifier/challenge 和一次性登录 ID。
- listener 只绑定注册允许的 `127.0.0.1` 或 `[::1]` loopback，不监听所有网卡。
- 回调先校验 state，再交换 code；取消、超时、错误回调分别处理。
- code、verifier、token 不写日志；listener 在终态立即关闭。

### 11.4 刷新与轮换

每账号一个 `SemaphoreSlim(1,1)`：第一个请求读取并刷新，其余等待后复用新结果。新 refresh token 与账号记录按“先写系统凭据、再更新公开状态”的可恢复协议原子替换；失败不直接删除账号身份。只有不可恢复授权失效才要求重新登录。

### 11.5 系统凭据库

Windows：DPAPI `CurrentUser` 加密专用文件，文件名只含稳定凭据键哈希，限制当前用户访问；写临时文件后原子替换。

macOS：Security.framework Keychain API，service=`MDCL`，account=凭据键；不通过 shell 拼接 `security` 传秘密。锁定/拒绝访问返回可重试错误，不降级明文。

凭据键示例：`account/<uuid>/refresh-token`。退出登录删除该键、清理内存会话，不自动结束已启动游戏。实例导出永不包含凭据键指向的秘密。

### 11.6 分阶段错误

至少区分：用户取消、state/PKCE 错误、Xbox 家庭/地区限制、XSTS 拒绝、Minecraft 权益缺失、profile 未创建、网络错误、令牌过期。普通微软登录成功不能显示为“Minecraft 可用”。

---

## 12. 启动计划与游戏进程

### 12.1 LaunchPlanBuilder

输入：InstanceManifest、ResolvedVersion、JavaRuntime、目标平台、本地绑定和依赖校验结果。

步骤：

1. 获取实例启动租约；
2. 检查文件可读写、Java 可执行与架构、依赖摘要、磁盘空间、内存范围；
3. 重建每会话 natives 目录并校验架构；
4. 展开 JVM/game 模板中的受控占位符；
5. 生成有序 classpath；
6. 仅在 macOS 且版本要求时加入 `-XstartOnFirstThread`；
7. 选择直接参数或 Java 9+ `@argfile`；
8. 输出不含 token 的 LaunchPlan 与脱敏预览。

### 12.2 参数边界

最终顺序：

```text
executable → JVM args → classpath/module args → mainClass → game args
```

使用 `ProcessStartInfo.ArgumentList` 逐项传递，`UseShellExecute=false`。中文、空格、`&`、多层目录均保持单参数。额外 JVM 参数使用明确语法的解析器与预览，禁止简单空格 `Split`。

Java argfile 有独立写入器：按 Java 规则处理空白、引号、反斜杠和换行，使用约定编码并做往返测试。Java 8 未实现可靠缩短策略时返回 `PROCESS_COMMAND_TOO_LONG`，不错误使用 `@argfile`。

### 12.3 秘密绑定

`GameProcessService` 在执行前把 `auth_player_name`、`uuid`、`access_token` 等绑定到参数副本。若敏感 argfile 必须落盘：

- 位于当前用户专属临时目录；
- 权限仅当前用户；
- 文件名随机，不进入普通缓存；
- 会话结束清理；启动时清理可确认归属的遗留文件；
- LaunchPlan、日志、诊断导出只含 `<redacted>`。

### 12.4 环境策略

默认移除 `JAVA_TOOL_OPTIONS`、`_JAVA_OPTIONS`、`JDK_JAVA_OPTIONS` 和无关 `CLASSPATH`，只记录变量名和处理策略。其他环境变量采用显式继承清单，不允许整合包自动注入 prelaunch/postexit 脚本。

### 12.5 IGameSession

```text
SessionId、InstanceId、PID、ProcessStartedAt、State、
LogStream、ExitTask、StopAsync(CancellationToken)
```

状态：

```text
Preparing → ProcessStarted → Running → Exited
                   ↘ StartFailed / Crashed / Unknown
```

stdout/stderr 使用独立异步泵和有界通道，`Decoder` 增量处理跨读取边界的 UTF-8 字符。日志事件含 UTC、相对时序、流来源和脱敏文本。

拿到 PID 只记录 ProcessStarted；只有匹配明确游戏日志/窗口证据才记录 MainMenuReached。退出码 0 不反推主菜单，非 1 不统一判为内存不足。

### 12.6 定向停止与退出

Windows 记录 PID+启动时间并使用 Job Object 管理归属进程树；macOS 使用可验证进程组/进程树。StopAsync 只作用于 SessionId 已确认归属的进程。

MDCL 关闭时提供：

- 保持后台监控；
- 结束游戏并退出。

不承诺首版完全脱离监控。游戏退出后释放实例租约、回收 natives/敏感临时文件；随后可再次启动。

---

## 13. 日志、诊断与隐私

### 13.1 脱敏入口

所有网络、进程、异常和 UI 日志经过统一 `SensitiveDataRedactor`，规则覆盖：

- `Authorization: Bearer ...`、Cookie；
- access/refresh token、OAuth code、PKCE verifier；
- Xbox/XSTS/Minecraft token；
- URL 中 code、token、签名参数；
- 启动参数中的秘密值；
- 完整用户目录映射为 `<user>` 或 `<mdcl-data>`。

脱敏在落盘前发生，不只依赖 UI 遮盖。

### 13.2 诊断包允许清单

允许：脱敏文本日志、LaunchPlan 预览、依赖摘要、OS/CPU/.NET/Java 版本、结构化错误、操作 journal 的脱敏视图。

禁止：SQLite 原文件、账号库、凭据、存档、完整用户路径、未审查二进制 dump、整个实例目录。

诊断规则必须带证据位置和置信度；无证据时原因为 Unknown。导出先预览，上传由用户主动执行。

---

## 14. Avalonia 桌面架构

### 14.1 导航与 ViewModel

```text
MainWindowViewModel
  InstancesViewModel       列表、创建、复制、改名、删除/恢复、启动
  InstallViewModel         版本选择、Java/组件计划、安装确认
  AccountsViewModel        本地身份、微软登录、状态、退出登录
  OperationsViewModel      当前步骤、字节/文件数、取消、恢复
  SettingsViewModel        下载并发、内存默认值、外置库、镜像开关
  SessionLogViewModel      有界日志、状态、停止、诊断导出
```

ViewModel 使用 CommunityToolkit.Mvvm；异步命令防重复触发并持有取消。所有业务通过 Application 用例，不在事件处理器重新实现解析、安装或启动。

### 14.2 首页状态

首页固定显示：当前实例、baseVersion+loader、账号类型/状态、Java major/arch、依赖状态、启动按钮、当前操作。不可启动时按钮旁显示具体原因和可执行下一步。

空状态、加载、失败、取消、恢复均为独立视觉状态，不用固定进度条或空方法模拟成功。

### 14.3 列表与响应

- 实例/账号/任务列表虚拟化；日志使用环形缓冲。
- 搜索和刷新带结果 generation，旧响应不能覆盖新筛选。
- 进度事件包含 phase、文件数、doneBytes、nullable totalBytes。
- 取消点击立即反馈“正在取消”；不可中断提交步骤显示当前边界。
- Windows 使用 Ctrl，macOS 使用 Cmd；菜单/Dock/托盘生命周期由平台适配。

---

## 15. Fabric、Forge / NeoForge 与本地模组

### 15.1 组件策略接口

```csharp
IComponentInstaller
{
    LoaderKind Kind { get; }
    Task<ComponentPlan> CreatePlanAsync(
        ComponentRequest request, ResolvedVersion baseVersion, CancellationToken ct);
    Task<ComponentPatch> ExecuteAsync(
        ComponentPlan plan, IComponentExecutionContext context, CancellationToken ct);
}
```

策略注册表按 LoaderKind 解析；UI 不直接拼接安装命令。每个 patch 包含版本继承/库覆盖、mainClass、参数、Java 约束、来源摘要和回归组合。

### 15.2 Fabric（T09）

`FabricMetadataProvider` 固定游戏、loader、installer 精确版本及官方来源。计划包括验证后的 Fabric JSON、libraries 和兼容性补丁。安装后生成新 ResolvedVersion，不覆盖原版本元数据。

切换 loader 版本生成新 plan revision；损坏依赖重新校验。失败保留原实例和原版快照。三目标主菜单验收分别记录，不能由原版本通过推断 Fabric 通过。

### 15.3 Forge / NeoForge（T10）

两者使用独立 installer，不共用“下载 jar 后运行”的粗略流程：

1. 固定兼容的游戏、loader、installer 组合；
2. 校验 installer 与所有 processor 依赖；
3. 在独立 staging 构造最小输入目录；
4. 使用选定兼容 Java，逐项参数启动 installer/processor；
5. 记录脱敏命令、输入输出摘要、退出码和超时；
6. 只允许清单声明的产物合并到共享库/新配置快照；
7. 取消或失败清理 staging，不接触原 saves/config；
8. 安装后重新解析并启动验证。

`LocalModService` 只操作当前实例 `mods`：启用/禁用通过受控后缀切换，读取公开元数据，提示已知依赖。实例运行或关键依赖修改锁存在时拒绝修改。

---

## 16. 整合包与远程资源

### 16.1 mrpack 导入流水线

```text
SafeArchiveExtractor 预扫描
→ ModrinthPackManifestReader
→ PackImportPlanner
→ UI 预览
→ 用户确认
→ 新实例 + 常规 OperationCoordinator
```

预览包含：游戏版本、loader 精确版本、文件数、展开/下载大小、来源、摘要、覆盖范围和限制。默认只创建新实例，不覆盖已有存档。

每个下载文件转成 ArtifactSpec，必须带官方/包清单给出的摘要和来源；缺资源、未知格式、不支持 loader 均明确列出未完成项。

### 16.2 外部输入限制

包内路径、JVM 参数、URL 和脚本字段均不可信：

- 路径经安全归档与目标边界校验；
- 不执行 prelaunch/postexit、自带程序或脚本；
- 存在脚本字段时显示“不支持及影响”，不得静默删除后宣称完整成功；
- overrides 只合并到新的实例用户目录，冲突先展示差异。

### 16.3 Modrinth / CurseForge

`ModrinthProvider` 独立处理搜索、版本、文件和分发错误；远程不可用不伪造结果。CurseForge 只有有效 API 方案与文件分发权限明确后才实现，不内嵌应保密 key，不绕分发限制。

迁移 Windows → Mac 时保留实例描述、用户数据和组件版本，按目标 OS/arch 重建 Java、libraries、natives；要求重新登录。

---

## 17. 自更新信任与回退（T12）

### 17.1 独立信任设计

更新不依赖 HTTPS 或远端自报 hash。应用内嵌离线根公钥；发布清单在解析业务字段前先验证签名。

签名清单包含：

```text
formatVersion、channel、rid、version、publishedAt、expiresAt、
packageUrl、size、sha256、minimumLauncherVersion、rollbackPolicy、signature
```

校验顺序：签名 → 有效期 → channel/RID → 版本与降级授权 → 下载 → 大小/SHA-256 → 平台代码签名 → 外部替换。

### 17.2 更新器

`MDCL.Updater` 是独立最小程序：

1. 接收已验证包、目标安装目录、旧版本备份目录和操作 ID；
2. 等待 MDCL 正常退出；游戏是否继续由明确策略控制；
3. 在同卷 staging 解包并核对清单；
4. 旧应用重命名到备份；
5. 新应用原子替换；
6. 启动健康检查；失败则恢复旧应用；
7. 不触碰数据根、实例、配置和系统凭据。

更新中断时旧目录或备份至少一个始终可启动。配置 schema 不兼容时旧程序只读阻止覆盖，不强行回写。

### 17.3 密钥管理

根公钥轮换、撤回、紧急降级授权另有关键记录。签名私钥只在 CI secret/硬件或受控签名环境，不进入仓库、日志和更新包。首版可保持手动更新，不能为展示功能省略信任链。

---

## 18. 打包、发布与 CI

### 18.1 构建矩阵

| 目标 | 构建/发布 | 运行验收 |
| --- | --- | --- |
| win-x64 | Windows runner | Windows 11 24H2+ x64 真机/干净用户 |
| osx-arm64 | macOS arm64 runner | Apple Silicon 原生 |
| osx-x64 | macOS x64 或明确交叉构建 | Intel Mac；Rosetta 另列 |

每个 RID 独立 `artifacts/<rid>`，禁止覆盖同一目录。发布均为自包含、非裁剪、非单文件。

### 18.2 Windows 包

首版为 zip：包含 exe、托管程序集、原生库、许可和版本信息。安装/替换只修改应用目录，用户数据保留在外部数据根。干净用户环境验证无需 .NET SDK，应用目录只读仍可安装游戏。

### 18.3 macOS 包

```text
MDCL.app/
  Contents/
    Info.plist
    MacOS/MDCL
    Resources/
    Frameworks/ 或自包含运行库约定位置
```

分别生成 arm64/x64，不用通用二进制结果冒充两架构实测。设置 apphost、图标、执行权限、RID 对应库；在 Mac 上完成 codesign、notarization、Gatekeeper 检查。缺条件时只交明确标识的未签名/未公证测试包，不要求关闭全局安全。

### 18.4 CI 阶段

1. 精确 SDK 与 locked restore；
2. Release build，警告视为错误；
3. Core / Integration / Desktop / Updater 测试；
4. 三 RID 发布；
5. 包结构、依赖锁、许可、SHA-256 清单；
6. 平台真机交互和固定游戏版本人工/自动证据；
7. 具备凭据的独立签名发布流程。

普通 CI 不反复执行真实微软登录；人工外部证据单列。

---

## 19. 测试架构与故障注入

### 19.1 测试替身

- `ControlledHttpServer`：可配置 Range、200/206/416、ETag、Retry-After、延迟、断流、坏数据、跨域重定向。
- `CrashPointInjector`：文件提交前后、清单提交前后、索引更新前后、更新替换前后。
- `FakeJavaProbe` / 小型真实探测进程：卡死、损坏、错架构、缺模块。
- `ProtocolAuthServer`：state、PKCE、取消、权益、profile、refresh rotation；不代表真实登录。
- `ArgumentEchoProcess`：精确输出收到的参数。
- `LoadGeneratorProcess`：同时大量写 stdout/stderr、拆分 UTF-8 字符。
- 固定脱敏 fixtures：版本 JSON、Fabric/Forge/NeoForge 描述、mrpack；不包含完整游戏 jar、token 或私人路径。

### 19.2 必测规则

- 实例：改名 ID 稳定、revision 冲突、schema 拒绝、索引重建、跨进程租约。
- 版本：继承环/缺父、有序 rules、classifier、参数合并、26.3 和快照 ID。
- 下载：摘要错、200/416、ETag 改变、取消共享等待者、磁盘满、跨卷提交。
- 恢复：所有 crash point 恢复两次结果一致，用户数据哈希不变。
- 归档：穿越、绝对/UNC/ADS、链接、大小写冲突、炸弹、父目录重解析点。
- Java：范围冲突、优先目录不兼容、探测超时、安装后探测、arm64/x64 配对。
- 账号：并发只刷新一次、Keychain/DPAPI 失败无明文、登出删除、日志无秘密。
- 启动：参数边界、argfile、日志死锁/乱码、环境注入、定向停止、二次启动。
- 扩展：组件切换不污染原实例、processor 失败、模组运行中保护、mrpack 限制。
- 更新：坏签名、过期、错 RID、未授权降级、中断、替换失败与回退。

### 19.3 真实证据与测试证据分离

每个验收项标记：纯规则、故障注入/集成、真机交互、正式分发。真实原版 `1.20.1`、`26.3` 和在线最新正式版分别记录：

```text
游戏/Java 精确版本、OS、CPU、RID、进程创建、是否到主菜单、
是否正常退出、是否二次启动、证据路径、失败原因
```

任一未知项写“未执行”或“条件不足”，不能写“理论兼容”。

---

## 20. 配置、证据与仓库卫生

### 20.1 普通配置

`settings.json` 只存非秘密偏好：下载并发、单主机限制、镜像开关、默认内存、外置库绑定、窗口偏好。schema 升级先备份；未知新版本只读。

公开认证配置只含 MDCL client ID 和 loopback 注册信息；client secret 不存在。签名和公证凭据只由系统/CI secret 注入。

### 20.2 每任务证据

`docs/evidence/Txx-结果.md` 固定包含：

1. 提交 ID；
2. OS、CPU、SDK、RID；
3. 实际命令和退出码；
4. 测试总数/通过/失败/跳过；
5. 操作路径和产物路径；
6. 真实交互边界；
7. 未验证项与外部条件。

证据文件在落盘前脱敏。测试报告可保存机器可读 TRX；构建产物附 SHA-256、依赖和许可清单。

### 20.3 仓库禁止项

不得提交：token、密码、验证码、client secret、签名私钥、账号数据库、完整游戏 jar、用户存档、个人临时路径、构建缓存。PCL/HMCL 仅作公开行为和职责研究，不复制或逐段翻译实现。

---

## 21. T00—T12 实施边界

| 任务 | 具体交付边界 | 完成定义 |
| --- | --- | --- |
| T00 | 锁定 SDK/包、解决方案、空窗口、三 RID 命令、认证 ADR | locked restore/build 成功；平台窗口状态分别记录 |
| T01 | 全核心模型、Ports、路径、清单、SQLite、租约 | revision/schema/原子保存/重建/竞争测试通过 |
| T02 | 官方 provider、继承、rules、库与参数合并、Java 元数据 | fixtures 与确定性哈希通过 |
| T03 | 下载、共享传输、journal、恢复、安全解压 | 所有受控 HTTP/crash/archive 故障测试通过 |
| T04 | 两平台发现、约束选择、受管安装、架构配对 | 规则测试和可用平台实际探测通过 |
| T05 | 本地身份、微软协议、DPAPI/Keychain、刷新轮换 | 协议测试通过；真实链路按外部条件单列 |
| T06 | LaunchPlan、argfile、进程、日志、诊断、定向停止 | 小型子进程测试通过；主菜单证据不伪造 |
| T07 | 五类页面、完整原版闭环、隔离和二次启动 | 固定版本在可测目标真实验收 |
| T08 | zip/.app、哈希、许可、发布矩阵、CI | 三产物独立；签名/运行状态如实记录 |
| T09 | Fabric 元数据、补丁、切换和恢复 | 固定组合安装与三目标状态独立记录 |
| T10 | Forge、NeoForge installer/processor、本地模组 | 两加载器分别验收，staging 不污染用户数据 |
| T11 | mrpack 预览/导入、Modrinth、迁移 | 摘要/路径/脚本限制与新实例启动验收 |
| T12 | 签名清单、密钥策略、独立更新器、回退 | 篡改/过期/RID/降级/中断测试通过 |

依赖任务失败时，不进入依赖它的下一阶段；外部注册、Mac 或签名条件不足只阻塞对应真实验收，不妨碍可独立完成的规则与适配开发。

---

## 22. 架构审查清单

实施每个 PR 前逐项确认：

- [ ] 引用方向未被破坏，ViewModel 没有 I/O，Core 没有平台依赖。
- [ ] 版本 ID 未参与 SemVer 推断；Java 与 natives 使用 gameArchitecture。
- [ ] 新文件都有来源、摘要、目标和恢复语义。
- [ ] 取消、重试和并发有上限，不留下假成功状态。
- [ ] 安装器只访问 staging；用户数据未被纳入回滚删除范围。
- [ ] 新秘密路径已进入系统凭据库或执行边界，并通过脱敏测试。
- [ ] 新进程使用参数数组、明确工作目录、输出泵和归属停止。
- [ ] 新归档条目使用统一安全提取器。
- [ ] 新 UI 状态覆盖空、失败、取消、恢复，不阻塞 UI。
- [ ] 新平台能力从 Windows 与 macOS 同时设计适配和证据。
- [ ] 文档、许可、测试、证据和未实测项同步更新。

本文是 T00—T12 的完整实施架构基线。后续若实测与本文不符，先提交设计修订和影响分析，再调整实现；不得用实现现状反向篡改验收事实。
