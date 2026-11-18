# PCL CE 联机大厅配置与第三方接入调查

调查日期：2026-10-31

## 结论

上游没有在公开仓库中提供可直接复制到 fork 的大厅认证密钥。上游公开的构建流程通过 GitHub Actions Secrets 在构建时注入相关值；这些值不会随源码 fork 复制，也不应从发布二进制中提取。

上游维护者在官方仓库 Issue #1448 的评论中明确表示：`LinkServers` 为空属于预期行为，相关信息具有保密性，需要开发者自行填写且不应公开；并将 `naid client secret` 等列为类似的 secrets，同时表示当前社区不提倡外部开发者调试依赖这些 secrets 的功能。如有问题，应在上游仓库开 Issue。

## 证据

1. 上游 PCL CE 的可复用构建工作流公开列出构建期注入项，但没有公开值：
   - `PCL_NAID_CLIENT_ID: ${{ secrets.NAID_CLIENT_ID }}`
   - `PCL_NAID_CLIENT_SECRET: ${{ secrets.NAID_CLIENT_SECRET }}`
   - `PCL_LINK_SERVER_ROOT: ${{ secrets.LINK_SERVER_ROOTS }}`
   - `PCL_LOBBY_DEFAULT_SECRET: ${{ secrets.LOBBY_DEFAULT_SECRET }}`
   - 来源：[PCL CE reusable-build.yml](https://github.com/PCL-Community/PCL-CE/blob/dev/.github/workflows/reusable-build.yml)

2. 上游 Issue #1448 的维护者/贡献者评论说明 `LinkServers` 为空是预期行为，信息有保密性，需要自行填写且不应公开；同类项包括 Natayark client secret，并不提倡外部开发者调试依赖这些 secrets 的功能：
   - 来源：[Issue #1448 评论](https://github.com/PCL-Community/PCL-CE/issues/1448#issuecomment-3218898528)
   - Issue：[联机大厅无法使用](https://github.com/PCL-Community/PCL-CE/issues/1448)

3. 上游贡献指南只说明通用开发环境、构建和 Pull Request 流程，没有公开大厅密钥或第三方接入申请表：
   - 来源：[PCL CE CONTRIBUTING.md](https://github.com/PCL-Community/PCL-CE/blob/dev/CONTRIBUTING.md)
   - 来源：[官方开发环境配置](https://github.com/PCL-Community/docs.pclc.cc/blob/main/docs/zhHans/ce/developers/setup.md)

4. 上游公开 PR #741 说明大厅使用 EasyTier 作为联机核心、Natayark ID 作为身份验证，但这是功能说明，不是第三方密钥发放政策：
   - 来源：[PR #741](https://github.com/PCL-Community/PCL-CE/pull/741)

## 对当前 fork 的实际影响

当前 fork 的源码读取以下构建期配置：

- `NAID_CLIENT_ID`
- `NAID_CLIENT_SECRET`
- `LINK_SERVER_ROOT`
- `TelemetryKey`（大厅控制器运行时读取；需要和实际构建注入命名保持一致）

这些值不能从上游仓库 API 读取。GitHub Secrets 只允许写入和覆盖，不能读取明文；上游 Release 二进制也不应作为提取密钥的来源。

## 可行路线

### 路线 A：向上游申请/确认授权

在 [PCL CE Issue 区](https://github.com/PCL-Community/PCL-CE/issues/new/choose) 说明：

- fork 仓库地址；
- 使用的分支和版本；
- 需要哪些公开 Client ID、服务器地址或独立第三方凭据；
- 不要求上游泄露 private secret；
- 是否可以使用上游大厅服务，或需要自行部署服务端。

应以维护者明确回复为准。当前公开资料没有发现正式的申请表或自动发放流程。

### 路线 B：自建服务

需要自己提供/配置兼容的 Natayark OAuth 客户端、大厅根服务器/中继节点和遥测接收服务，并修改客户端的服务地址与认证流程。仅把 `LINK_SERVER_ROOT` 改成一个地址不能替代 OAuth 客户端注册和服务端协议。

## 安全提醒

不要把 Client Secret、TelemetryKey 或任何 Actions Secret 粘贴到聊天、Issue、源码、Release 说明或构建产物中。如果某个密钥曾被编译进公开发布包，应按可能泄露处理并联系服务方轮换。
