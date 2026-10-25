# T09 接续验证：首次 remap 仍未通过

日期：2026-10-25；基线 46d02ca 后的接续开发分支。Windows x64、.NET 10.0.402、Microsoft OpenJDK 17.0.4.1。

- 全新实例安装 Minecraft 1.20.1 + Fabric 0.16.14；没有放入人工生成的 remap jar。
- 真实启动再次在 tiny-remapper OutputConsumerPath 创建输出 ZIPFS 时失败，未到主菜单。F03/F04 保持未通过。
- 新增游戏 JVM 内诊断：普通文件创建和 java.io.File.canWrite 均成功；相同文件的 Files.isWritable=false，ZipFS.isReadOnly=true，向 ZIP 写入抛出 ReadOnlyFileSystemException；DOS readonly=false、底层 FileStore readonly=false。
- 同一路径、JDK、探针从 PowerShell 直接启动时：Files.isWritable=true、ZIPFS readonly=false、写入成功。
- 独立、只含 System.Diagnostics.Process.Start 的 .NET 10 程序也复现 false；CreateNoWindow=true/false 两组结果相同。这将复现范围缩小到 .NET 启动的子进程环境/权限检查，不能归因于 MDCL 的 classpath、Fabric 安装逻辑或 CreateNoWindow。
- 没有修改 ACL、安全策略或 Fabric jar，没有自动采用人工预生成缓存绕过。具体 token/AccessCheck 差异仍待进一步外部环境排查。
- Mac 全部未实测。旧手工预 remap 主菜单证据只说明该绕过曾成功，不替代首次启动验收。

## 实际探针结果

```text
shell JVM: canWrite=true nioWritable=true dosReadOnly=false storeReadOnly=false
shell JVM: zipReadOnly=false zipWrite=ok
plain .NET child, CreateNoWindow=True: canWrite=true nioWritable=false
plain .NET child, CreateNoWindow=False: canWrite=true nioWritable=false
both .NET children: zipReadOnly=true zipWrite=ReadOnlyFileSystemException
Fabric JVM agent: canWrite=true nioWritable=false zipReadOnly=true
```

探针源：fabric-zipfs-probe.java.txt。原始运行日志位于本机构建目录 artifacts/verification，不把带本机路径的原始文件提交。
