# 如何为项目做贡献

## 这是 PCL₃

本仓库是 [PCL CE](https://github.com/PCL-Community/PCL-CE) 的个人改编版，**不是** PCL CE 官方仓库。

- 使用问题、Bug 反馈请提到 [本仓库 Issues](https://github.com/dyyxxhh/pcl3/issues/new/choose)，不要提交到 PCL CE 官方仓库。
- 上游功能本身的 Bug，欢迎一并提给 [上游](https://github.com/PCL-Community/PCL-CE/issues) 帮助改善 PCL CE。

## 提交代码

1. Fork 本仓库并克隆到本地
2. 从 `dev` 切出分支：`git checkout -b feat/your-feat-name`
3. 遵循项目既有代码风格
4. 提交信息使用 Angular 规范（`feat:` / `fix:` / `docs:` …）
5. 向 `dev` 分支发起 Pull Request

## 与上游的关系

本仓库基于上游 `dev` 构建，改动集中在「解除区域限制、解除账号限制、品牌更名」三类（详见 README）。较大幅度的功能改动请优先贡献给上游 PCL CE，以免两边分叉扩大。

## 生成式 AI 使用

使用生成式 AI 工具时，请按 [PCL CE 的 AI 方针](https://docs.pclc.cc/ce/developers/ai-policy) 披露使用情况，并对提交内容的正确性负责。
