---
name: git-push
description: 只执行当前 Git 仓库的 push 操作。Use when the user invokes /git-push, $git-push, or asks to push the current branch without committing, staging, pulling, force pushing, or generating logs.
---

# /git-push

Push the current git branch without changing its tracking configuration. Keep this command intentionally small: it pushes only.

## Project Scope

This skill is local to the current repository. Use it only when the active workspace is that repository or one of its subdirectories. Do not install or sync it into a global skill directory.

## Workflow

1. Confirm the current directory is a git repository:
   - Run `git rev-parse --is-inside-work-tree`.
   - If it is not a git repository, tell the user and stop.

2. Check the current branch context:
   - Run `git status --short --branch`.
   - Run `git remote -v`.
   - Run `git rev-parse --show-toplevel` and compare it with the first `worktree` entry from `git worktree list --porcelain`.
   - If the current path is an external worktree, require a named branch and verify `%(upstream:short)` is empty. If it is not empty, report the policy violation and stop; do not unset or use it automatically.
   - Resolve the current branch name, then run `git ls-remote --heads <remote> refs/heads/<current-branch>`. If the exact remote branch does not already exist, stop. A push that creates it is forbidden unless the user explicitly requested creation of that exact repository branch in the current request.
   - Do not require a clean working tree. Uncommitted changes are not pushed by git, but they also do not block a normal push.

3. Enforce the GitHub Actions cost prohibition:
   - Run `git ls-files '.github/workflows/*.yml' '.github/workflows/*.yaml'`.
   - If any active workflow file is tracked, stop without pushing and report the policy violation.
   - Never create, restore, enable, trigger, or recommend GitHub Actions. All automation must remain local because GitHub Actions costs money.

4. Push:
   - In the main worktree, run plain `git push`.
   - In an external worktree, use an explicit refspec that does not create tracking, such as `git push origin HEAD:refs/heads/<current-branch>`. Stop if the target remote is ambiguous.
   - Do not run `git add`, `git commit`, `git pull`, `git push --force`, `git push --force-with-lease`, `git push -u`, or `git push --set-upstream`.
   - Never turn a normal push request into branch creation. Historical plans, a missing remote ref, or the `codex/` naming convention are not authorization to create a branch.

5. Report the result:
   - If push succeeds, summarize the branch and remote that were pushed.
   - If a main-worktree push fails because no upstream is configured, report the failure without suggesting or setting upstream automatically.
   - If push is rejected, report the key rejection reason and stop unless the user asks for follow-up handling.

## Guardrails

- Do not modify files.
- Do not stage or commit changes.
- Do not generate commit summaries or development logs.
- Do not force push unless explicitly instructed by the user in the same request.
- Never create, modify, or recommend an upstream for an external worktree, even when the user requests a push; use an explicit refspec instead.
- Never create a local or remote branch on the agent's own initiative. Require explicit current-request authorization for the exact repository and branch name.
- Never use or recommend GitHub Actions, and never push a repository that tracks an active `.github/workflows/*.yml` or `.github/workflows/*.yaml` file.
