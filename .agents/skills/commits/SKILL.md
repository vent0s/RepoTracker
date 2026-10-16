---
name: commits
description: 提交当前 Git 分支的指定变更，自动生成中文 commit 摘要与开发日志，并推送到远程。Use when the user invokes $commits, /commit, /commits, asks to commit current changes, or requests a commit together with project development logs.
---

# commits

# /commit — 提交变更、生成开发日志并推送

当用户调用 `/commit`、`/commits` 或 `$commits` 时，按以下步骤执行。

## 前置条件

- 当前目录是一个 git 仓库
- 存在未提交的变更（staged 或 unstaged）

## 执行步骤

### 第一步：检查变更

运行 `git status` 和 `git diff --staged` 以及 `git diff`（unstaged），确认当前有哪些文件变更。

如果没有变更，告知用户并结束。

### 第二步：确定本次序号

检查 `doc/开发日志/commits/` 目录下已有文件。找出当天日期 `{today}`（格式 `YYYY-MM-DD`）下已有的最大序号。

- 若当天已有 `commit-{today}-01.md`、`commit-{today}-02.md`，则本次序号为 `03`
- 若当天无任何文件，则本次序号为 `01`
- 序号始终为两位数字，不足补零

### 第三步：生成 commit message 并编写日志文件

先分析变更内容，生成中文 commit message。规则：

- 格式：`<类型>: <简短描述>`
- 类型：`feat`（新功能/新文档）、`fix`（修复）、`refactor`（重构）、`docs`（文档）、`chore`（杂项）
- 描述简洁，一行以内
- 示例：`docs: 新增主界面设计文档`

如果用户要求自定义 commit message，优先使用用户的 message。

**先不要 commit。** 接下来先写入 commit 摘要和开发日志，再将它们一起提交。

### 第四步：写入 commit 摘要

在 `doc/开发日志/commits/commit-{today}-{NN}.md` 写入以下内容：

```markdown
# Commit {today}-{NN}

**时间**：{timestamp}
**分支**：{current branch name}

## 提交信息

{commit message}

## 变更文件

- {file path} — {简要说明变更内容}
- ...

## 变更摘要

{用 2-5 句话概括本次 commit 做了什么，新增/修改/删除了什么}
```

变更文件列表通过 `git status` 和 `git diff --stat HEAD`（对比当前 HEAD）获取。

### 第五步：写入开发日志

在 `doc/开发日志/journals/journal-{today}-{NN}.md` 写入以下内容：

```markdown
# 开发日志 {today}-{NN}

**时间**：{timestamp}
**关联 Commit**：[commit-{today}-{NN}](../commits/commit-{today}-{NN}.md)

## 本次新增内容

{详细说明本次新增了什么。不只是文件列表，要说明这些新增内容是什么、为什么加、在项目中的作用。2-5 段，具体程度以能让一周后的自己看懂为准。}

## 进度定位

{将本次工作放到项目当前进度框架中定位。回答：当前处于哪个阶段/里程碑？本次工作推进了它的哪个具体维度？该阶段整体完成了大约多少？如果本次工作跨越了阶段边界，明确指出。}

## 当前迭代位置

**当前阶段**：{阶段名称}
**阶段进度**：{粗略估算，如 "约 20%""早期""过半""接近完成"}
**下一步**：{下一步最应该做的事，1-2 句}
```

### 第六步：提交全部变更

将新写入的 commit 摘要和开发日志一并纳入提交：

```bash
git add -A
git commit -m "<commit message>"
```

如果 commit 失败（如 pre-commit hook 拒绝），修复问题后重新执行本步，不要重新生成日志文件。

### 第七步：推送到远程

```bash
git push
```

- 默认推送当前分支到其上游分支。
- 若仓库没有配置远程（`git remote -v` 为空），如实告知用户"已提交但未推送（无远程）"并结束。
- 若推送失败（网络、凭据、远端拒绝等），如实报告错误，不回退 commit，不用 `--force` 绕过；除非用户明确要求改写历史，否则绝不用强制推送。
- 推送成功后，核对远程与本地一致：

```bash
git rev-parse HEAD origin/$(git branch --show-current)
```

## 阶段口径

判断进度定位时，实时读取项目中的进度定义（优先顺序：`doc/开发日志/README.md`、`devSchedule/README.md`、脑洞工作区的收敛计划），不要在本技能内硬编码阶段或里程碑编号。

如果项目尚无任何进度定义，在日志中按项目现状给出定位并说明推断依据；如本次工作属于尚未定义的新阶段范畴，在日志中提出阶段定义建议。

## 注意事项

- 不要提交包含密钥、token、密码等敏感信息的文件
- 不要提交 `node_modules/`、`.env`、构建产物
- 如果用户明确表示不想 commit 某文件，从 `git add` 中排除
- 若 `doc/开发日志/` 被 `.gitignore` 忽略，日志文件仅本地保留，不强行加入提交
- commit 摘要和开发日志使用中文撰写
