# ADR 0002：MDCL 微软 public client 注册状态

- 日期：2026-10-25
- 状态：Blocked by external registration；不妨碍离线身份与协议测试开发

## 必须使用的配置

MDCL 只能使用自己注册的 Microsoft Entra public client，不包含 client secret，不借用 PCL、HMCL 或官方启动器的应用 ID。密码、验证码与授权交互必须由持有人在系统浏览器完成。

## 待持有人完成/确认

1. 提供 MDCL 专属 public client 的应用（客户端）ID；该 ID 属公开标识，但不是当前可假定已存在的配置。
2. 应用允许目标个人 Microsoft 账号类型，并配置仅 loopback 的重定向地址。
3. 验证授权码 + PKCE、`XboxLive.signin offline_access` scope、刷新令牌轮换。
4. 逐阶段验证 Xbox Live、XSTS、Minecraft 会话、权益和 profile API 的真实准入。

## 当前证据边界

当前没有 MDCL 专属 client ID，也没有由持有人完成的浏览器授权。因此 U01 及真实微软链路均为“条件不足”，不得标记通过。离线身份和协议故障测试可独立实现，但不能据此推断真实登录可用。
