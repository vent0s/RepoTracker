# 发布到 GitHub（维护者说明）

## 不更改本地 remote 的历史同步

可以直接向 `git@github.com:vent0s/RepoTracker.git` 推送明确的 ref，无需添加或修改 remote，也无需 clone 发布仓库。普通分支推送会传输该提交可达的完整历史，保留提交 SHA、作者、时间与提交说明。它不包含尚未提交的工作区内容，也不迁移 GitLab 的 issue、合并请求、CI 配置之外的服务设置或发布附件。

建议将本地 `main` 同步到 GitHub `main`，以后继续向同一目标推送。不要把这种历史同步误写成必须使用 `--mirror`：Git 的 `--mirror` 会处理 `refs/` 下的全部引用，包括远程跟踪引用，并会强制更新及删除目标引用，不适合这里日常从开发仓库向发布仓库同步的需求。[Git push 官方说明](https://git-scm.com/docs/git-push)。

## 首次准备时的只读检查结果（2026-10-19，提交前历史记录）

- 本地是主工作树，当前分支为 `main`，仍跟踪原来的 `origin/main`；没有改动 remote 或 tracking。
- 本地已提交历史有 4 个提交，HEAD 为 `4cb0d57ddbb29997eb1754aeed07bbcb5af73b09`，无 tag。
- GitHub 连接器确认 `vent0s/RepoTracker` 为公开仓库；`git ls-remote --symref git@github.com:vent0s/RepoTracker.git HEAD` 及 `git ls-remote --heads git@github.com:vent0s/RepoTracker.git` 均成功返回空结果，尚未创建任何分支。
- 当前安装器、0.5.1 功能和文档等存在未提交变更；直接推送现有 HEAD 不会带上这些内容。
- 首次准备阶段只完成 README、截图与同步说明，未 commit、创建分支、push、创建 GitHub Release 或上传附件。

首次推送会创建远端 `refs/heads/main`。说明这一点后，用户明确回复“好的，提交并推送吧，不管是 gitlab 还是 github”，授权本次提交并同步 GitLab 既有 `main` 与 GitHub 首次 `main`。本地不新增分支、不更改 remote / tracking，不用强推；后续其他新分支仍须单独获得明确授权。

## 实际发布顺序

1. 确认要公开的完整历史范围和本项目许可证。历史会包含此前已提交的规划文档、本机路径、提交作者信息，以及后来删除的内容；仅在当前工作区删文件或加入 `.gitignore` 不会从历史中移除它们。若需要清理历史，应另外讨论范围，因为改写历史会改变提交 SHA。
2. 在现有工作树整理、验证并提交拟发布内容，保留其他未纳入发布的改动；不自动创建本地分支。README 默认英文，中文入口为 `README.zh-CN.md`，截图一并提交。
3. 重新读取 GitHub refs。若目标不存在，必须先取得上方明确的首次创建授权；若出现已有提交，检查能否快进，拒绝时先调查，不直接强推。
4. 目标分支已存在或已明确获准首次创建后，先预演再推送：

   ```powershell
   git ls-remote --heads git@github.com:vent0s/RepoTracker.git
   git push --dry-run git@github.com:vent0s/RepoTracker.git refs/heads/main:refs/heads/main
   git push git@github.com:vent0s/RepoTracker.git refs/heads/main:refs/heads/main
   ```

   不使用 `-u`、`--set-upstream`、`--force`、`--mirror` 或 `--prune`，也不修改本地 `origin`。

5. 对比以下两个 SHA，并再次核对 `git remote -v` 与本地分支 upstream 保持原值：

   ```powershell
   git rev-parse refs/heads/main
   git ls-remote --heads git@github.com:vent0s/RepoTracker.git refs/heads/main
   git remote -v
   git for-each-ref --format='%(refname) %(upstream:short)' refs/heads
   ```

6. 从选定的已提交源码构建正式分发包，检查 `package-manifest.json` / `installer-manifest.json` 的源码提交、版本、`SourceDirty` 和 SHA-256。准备发布说明，按用户明确授权创建对应 tag / GitHub Release 并上传 Setup、ZIP、校验文件及清单。Git 历史同步本身不会上传 `publish/` 中的安装包。

## 当前交付边界

现有本机包为 0.5.1，`SourceCommit` 为上述 HEAD，但 `SourceDirty: true`；它不是“该提交即完整构建源码”的可复现公开发行依据。项目尚未选择许可证，包未签名，Windows 11 与物理高 / 混合 DPI 等边界见[兼容性说明](兼容性与发布说明.md)。这份文档不把文档就绪或本机包可运行等同于已经公开发行。
