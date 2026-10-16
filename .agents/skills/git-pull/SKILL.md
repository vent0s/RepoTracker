---
name: git-pull
description: 只执行当前 Git 仓库的 pull 操作，并在本地未提交、分支分叉或可能产生冲突时停止。Use when the user invokes /git-pull, $git-pull, or asks to pull the current branch without committing, staging, rebasing, force operations, or conflict resolution.
---

# /git-pull

Pull the current git branch only when it is safe. Keep this command intentionally small: it pulls only, and refuses to proceed when local or remote state may conflict.

## Project Scope

This skill is local to the current repository. Use it only when the active workspace is that repository or one of its subdirectories. Do not install or sync it into a global skill directory.

## Workflow

1. Confirm the current directory is a git repository:
   - Run `git rev-parse --is-inside-work-tree`.
   - If it is not a git repository, tell the user and stop.

2. Inspect local state:
   - Run `git status --short --branch`.
   - Compare `git rev-parse --show-toplevel` with the first `worktree` entry from `git worktree list --porcelain`. If the current path is an external worktree, verify it has no upstream and stop: external worktrees are intentionally non-tracking, and this pull-only skill must not create or use an upstream for them.
   - If there are staged, unstaged, untracked, merge, rebase, cherry-pick, or revert states, stop and explain that `/git-pull` requires a clean working tree.
   - Do not run `git add`, `git commit`, `git stash`, `git reset`, or conflict-resolution commands.

3. Inspect upstream:
   - Run `git rev-parse --abbrev-ref --symbolic-full-name @{u}`.
   - If no upstream is configured, stop and report the issue. Do not set upstream automatically.
   - Run `git fetch` to update remote-tracking refs before comparing local and upstream commits.

4. Refuse unsafe histories before pulling:
   - Compare `HEAD` and `@{u}` after remote-tracking refs are current:
     - `git rev-list --left-right --count HEAD...@{u}`
   - If local is ahead and remote is not ahead, stop. This command is pull-only and should not combine local unpublished commits with a pull.
   - If both local and remote are ahead, stop. Treat this as a divergent branch with possible conflicts.
   - If remote is ahead and local is not ahead, continue.
   - If neither side is ahead, report that the branch is already up to date and stop.

5. Pull:
   - Run plain `git pull`.
   - Do not run `git pull --rebase`, `git pull --ff-only`, `git merge`, `git rebase`, `git push`, or any force operation unless the user explicitly asked for that exact behavior.

6. Report the result:
   - If pull succeeds, summarize the branch and upstream pulled.
   - If pull is rejected or reports conflicts, stop immediately and report the key reason. Do not attempt to resolve conflicts automatically.

## Guardrails

- Never create a local or remote branch. If the requested branch is absent, stop and report it; do not create or switch to a new branch as a pull fallback.
- Do not modify files except through the successful `git pull` itself.
- Do not stage, commit, stash, reset, manually merge, rebase, or resolve conflicts.
- Do not pull when the working tree is dirty.
- Do not pull when local and upstream histories diverge.
- Do not pull when local has unpublished commits.
- Never set, modify, recommend, or rely on upstream tracking for an external worktree.
