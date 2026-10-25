# T09 Fabric remap 诊断记录

- 日期：2026-10-25
- 依据：Fabric Loader 0.16.14 `GameProviderHelper.deobfuscate0` 源码

## 已确认的上游行为

Fabric 首次启动时：

```java
OutputConsumerPath outputConsumer = new OutputConsumerPath.Builder(tmpFile)
    .assumeArchive(true)
    .build();
```

输出路径为：

```text
<gameDir>/.fabric/remappedJars/minecraft-<game>-<loader>/client-intermediary.jar.tmp
```

失败点正是 `.build()` 内的 `OutputConsumerPath` 构造：ZipFileSystem 被判定 read-only。

## 独立复现结果

使用：

- 同一 JDK 17（`C:\Program Files\Microsoft\jdk-17.0.4.101-hotspot`）
- 同一 Fabric Loader 0.16.14 jar
- 同一 `OutputConsumerPath.Builder(path).assumeArchive(true).build()` 调用
- 同一 `.tmp` 路径结构

结果：`OPEN_OK true 22`，成功创建；不出现 `the jar file ... can't be written`。

因此已排除：

- 路径长度本身；
- ZIPFS 对 `.tmp` 扩展名的普遍拒绝；
- JDK 17 本身无法创建该 ZipFS；
- Fabric 0.16.14 的 Builder 在单纯路径调用下必然失败。

## 仍未定位

失败只在 Fabric Loader 的游戏进程内出现。游戏进程与独立探针的差异包括：

- 大量 Mojang/Fabric 库同时在 classpath 上；
- client.jar、intermediary、Mixin、ASM 都已打开；
- Fabric 的 GameProvider 初始化与其他线程活动；
- MDCL 启动的 JVM 参数与环境。

目前没有证据证明是 MDCL 的 LaunchPlan/classpath 逻辑造成，也没有证据证明不是。不得通过伪造已 remap jar、跳过摘要或关闭安全校验绕过。

## 下一步

需要在该游戏 JVM 内加入受控诊断（例如记录 ZipFS provider、isReadOnly、URI、锁持有者和线程状态），或使用标准启动器在同一机器复现；确认差异后再决定是否属于上游 bug、文件系统/驱动/杀软问题，或 MDCL 启动环境问题。
