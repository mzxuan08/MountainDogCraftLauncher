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

## 额外排除：java.io.tmpdir

使用同一 Fabric 0.16.14 `OutputConsumerPath.Builder(...).assumeArchive(true)` 探针，将 `java.io.tmpdir`/TEMP/TMP 指向当前受限的系统临时目录 `C:\Users\<user>\AppData\Local\Temp`：

```text
java.io.tmpdir=C:\Users\<user>\AppData\Local\Temp
OPEN_OK true 22
```

仍成功。因此不是 Java 临时目录权限或 ZipFS 对临时目录的依赖导致。

## 额外排除：JDK 17.0.4

使用 Temurin `17.0.20.1+1`（远新于机器自带 17.0.4）重新完成原版安装、Fabric 0.16.14 安装并启动：

```text
RuntimeException: error remapping game jars
IOException: the jar file ...client-intermediary.jar.tmp can't be written
at tinyremapper.OutputConsumerPath.<init>
at Fabric GameProviderHelper.deobfuscate
```

失败点与旧 JDK 完全相同；`.tmp` 仍留下 22 字节空 ZIP。由此排除 JDK 17.0.4 的版本缺陷。

证据：`T09-fabric-jdk17.0.20-failure.png`

## 进一步排除路径与重复打开

- 在实际实例路径 `data/instances/.../.fabric/remappedJars/.../client-intermediary.jar.tmp` 直接运行同一 `OutputConsumerPath.Builder(...).assumeArchive(true)`：成功创建 22 字节 ZIP。
- 在**不删除**同一 `.tmp` 的情况下关闭后重新打开：第二次仍成功，不是“重开变只读”。
- Fabric Loader 0.16.14 jar 没有注册 `java.nio.file.spi.FileSystemProvider`。
- 因此已排除：实例路径 ACL/重解析点、同一 JVM 二次打开、Fabric 自带 FileSystemProvider。

## 仍未定位

失败只在 Fabric Loader 的游戏进程内出现。游戏进程与独立探针的差异包括：

- 大量 Mojang/Fabric 库同时在 classpath 上；
- client.jar、intermediary、Mixin、ASM 都已打开；
- Fabric 的 GameProvider 初始化与其他线程活动；
- MDCL 启动的 JVM 参数与环境。

目前没有证据证明是 MDCL 的 LaunchPlan/classpath 逻辑造成，也没有证据证明不是。不得通过伪造已 remap jar、跳过摘要或关闭安全校验绕过。

## 下一步

需要在该游戏 JVM 内加入受控诊断（例如记录 ZipFS provider、isReadOnly、URI、锁持有者和线程状态），或使用标准启动器在同一机器复现；确认差异后再决定是否属于上游 bug、文件系统/驱动/杀软问题，或 MDCL 启动环境问题。
