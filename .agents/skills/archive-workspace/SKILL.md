---
name: archive-workspace
description: 归档或移除已收官的 devSchedule 与 brain-storm 规划工作区，先保存可验证的历史快照，保留仍被活跃工具引用的夹具与文档链接。Use when the user invokes $archive-workspace, asks to 归档/删除已收官工作区或脑洞, freezes a pre-reconstruction baseline, or needs to move reusable drafts and fixtures out of a planning workspace before removal.
---

# Archive Workspace

Use this skill inside the current project repository. Archive planning workspaces without making historical material look authoritative and without leaving active tools dependent on a deleted path.

## Preconditions

- Confirm the repository root（`git rev-parse --show-toplevel`）.
- Confirm every target is an explicit child of `devSchedule/` or `brain-storm/`（项目未使用 devSchedule 时，仅 brain-storm/）.
- Require either a finalized design document or an explicit user request to freeze/archive unfinished work.
- Identify reusable drafts, fixtures, scripts, and generated inputs that active tools still need.
- Inspect the dirty worktree and preserve unrelated user changes.

Ask only when unique information cannot be preserved safely. Never create a branch, stage, commit, or run a project build unless separately requested.

## Context To Read

Before editing, read:

- 项目级约定文档（`AGENTS.md` 或等效，若存在）
- 文档分层 / 收官规范、Harness 标准（若项目存在此类规范文档）
- `devSchedule/README.md`（若存在）
- Target `概述.md`, canonical `里程碑-{工作区名}.md` or `收敛计划-{工作区名}.md`, `harness.md`, and root brain-storm documents when present
- Legacy `里程碑.md` or `里程碑-脑洞-{工作区名}.md` when an older workspace still uses those names
- Related stable documents under `doc/`
- Active source or tool files that reference the target

Find references with `rg --files` and `rg -n`. Exclude 开发日志目录（如 `doc/开发日志/**`）from active-reference cleanup because it records historical state.

## Archive Layout

Use a named batch:

```text
archive/{YYYY-MM-DD}-{主题}/
├── README.md
├── files.original.sha256
├── files.sha256
├── devSchedule/{系统名}/...
└── brain-storm/{项目名}/...
```

（若项目没有 `archive/` 目录，先创建；若项目没有 devSchedule，只归档 brain-storm 部分。）

The batch `README.md` must state:

- Why the snapshot exists and whether it is authoritative
- Original and archived paths
- Original and current file counts, total bytes, and deterministic tree SHA-256 values for every workspace
- Any archive-only filename normalization and its old-to-new path mapping
- Any active fixture copied elsewhere
- The rule that historical content may not be treated as current design

## Workflow

### 1. Inventory

- Resolve the repository root and every source/target path to absolute paths.
- Verify each source is exactly the intended child and each destination is inside `archive/`.
- Before copying, preserving fixtures, renaming, or deleting anything, inspect each source workspace for planning-name conflicts:
  - stop if `里程碑-{工作区名}.md` and legacy `里程碑.md` coexist in the same `devSchedule` workspace;
  - stop if `收敛计划-{工作区名}.md` and legacy `里程碑-脑洞-{工作区名}.md` coexist in the same `brain-storm` workspace.
  Never choose, merge, overwrite, or continue archiving a conflicted workspace automatically.
- Record sorted relative file paths, file sizes, and SHA-256 hashes before copying.
- Stop if the destination already contains different content.

### 2. Preserve active fixtures

- Copy reusable inputs to the owning tool location（如 `tools/{tool}/drafts/`、`tools/{tool}/fixtures/`，或项目内等价的工具目录；无对应工具时先与用户确认去向）.
- Keep the original copy inside the historical snapshot.
- Update active code, messages, and documentation to use the tool-owned path.
- Do not make production code read from `archive/`.

### 3. Copy and verify

- Copy every target workspace into the archive batch without rewriting its contents.
- Compare source and destination relative paths, counts, byte sizes, and per-file SHA-256 hashes.
- Generate `files.original.sha256` from the verified original paths and bytes before any archive-only filename normalization.
- Delete an original only after its own comparison succeeds.

### 4. Remove exact originals

- Re-resolve the absolute source path immediately before deletion.
- Verify it remains inside the repository and is neither `devSchedule/` nor `brain-storm/`.
- Delete with the native recursive remove of the current shell（PowerShell：`Remove-Item -LiteralPath ... -Recurse -Force`；macOS/Linux：`rm -rf -- <字面路径>`）.
- If the execution environment blocks recursive removal after the copy is verified, compare every source file with its canonical archived counterpart again, move each verified file onto that counterpart with the shell's native move（PowerShell：`Move-Item -LiteralPath ... -Force`；macOS/Linux：`mv -f`）, and move the remaining empty directory skeleton under the same batch's `.source-directory-markers/`. Report this recoverable fallback explicitly.
- Report any target that was copied but not deleted.

### 5. Normalize archived planning filenames

- After the original workspace has been removed, rename standard planning files only inside the archive:
  - `devSchedule/{工作区名}/doc/里程碑-{工作区名}.md` to `devSchedule/{工作区名}/doc/归档-{工作区名}-执行记录.md`
  - `brain-storm/{工作区名}/收敛计划-{工作区名}.md` to `brain-storm/{工作区名}/归档-{工作区名}-收敛记录.md`
- Accept legacy `devSchedule/{工作区名}/doc/里程碑.md` and `brain-storm/{工作区名}/里程碑-脑洞-{工作区名}.md` as alternate sources for the same archive destinations.
- Defensively repeat the canonical-versus-legacy conflict check before normalization; a conflict must already have stopped the workflow during Inventory.
- Keep each renamed document's body and first-level heading byte-for-byte unchanged unless the user explicitly requests content edits.
- Repair Markdown links that directly target renamed files. Preserve non-link mentions of original paths as historical evidence.
- Generate `files.sha256` from the current normalized archive paths and bytes. Keep `files.original.sha256` unchanged.
- Record the path mapping plus both original and current byte counts and tree SHA-256 values in the batch `README.md`.

### 6. Update authority and references

- Point active work to a current `doc/`, `devSchedule/`, or `brain-storm/` owner.
- Link to archived files only as explicitly labelled historical context.
- Do not rewrite historical development logs.
- 若项目存在设计重置 / 重构状态文档（如 `doc/重构状态.md`），在重置场景下更新它，避免旧实现被误认为当前意图。

### 7. Validate

- Verify `files.original.sha256` still matches the pre-copy inventory.
- Verify every entry in `files.sha256` against the current archive and confirm normalized documents retain their original content hashes.
- Confirm standard archived planning filenames use `归档-{工作区名}-执行记录.md` or `归档-{工作区名}-收敛记录.md`, while unrelated active workspace filenames remain unchanged.
- Search active paths for references to deleted workspace locations, excluding `archive/**` 与开发日志目录.
- Run syntax checks for directly edited scripts, archive Markdown link checks, `git diff --check`, and `git status --short --branch`.
- 遵循项目约定（`AGENTS.md` 或等效）执行必要的检查；不替用户启动本地运行时或构建。

## Report

Report the archive batch, deleted originals, preserved active fixtures, reference changes, and every validation result. Do not commit unless the user separately asks or invokes the commit skill.
