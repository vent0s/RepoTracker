---
name: doc-review
description: 扫描当前项目并审阅 doc/ 中除 doc/开发日志/** 之外的项目文档，判断是否需要更新、合并、新建、删除或归档；审阅时不得读取、搜索、对照或引用 brain-storm/**。Use when the user asks for 项目文档审阅、文档盘点、doc 维护建议、检查文档是否过期、查找缺失/重复/断链文档，或要求扫描整个项目后给出 doc 目录维护清单。
---

# doc-review

Use this skill to review project documentation health for this repository. The default output is an audit report and action list only; do not edit, delete, move, merge, or create documentation unless the user explicitly asks for implementation after the review.

## Project Scope

This skill is local to the current repository. Use it only when the active workspace is that repository or one of its subdirectories. Do not install or sync it into a global skill directory.

## Scope

- Review `doc/` Markdown documents, excluding the entire `doc/开发日志/**` subtree.
- Do not read, search, compare against, cite, or summarize `brain-storm/**`; unfinished brainstorming must not affect formal documentation review.
- Treat `doc/README.md` as the documentation navigation source of truth when it exists. If it does not exist, report that the documentation has no navigation entry and propose one instead of assuming another index.
- Use project-root-relative paths in all reports.
- Write reports in Chinese by default.
- Cite evidence with paths and line numbers when a specific claim depends on a document or source file.
- Judge the document map from the directories that actually exist in this project. Do not assume a fixed set of design-directory names or document types.

## Quick Inventory

Run the bundled read-only inventory script from the project root when starting a review:

```bash
python3 .agents/skills/doc-review/scripts/doc_inventory.py
```

The script prints a Markdown inventory covering non-log `doc/` files, frontmatter metadata, `doc/README.md` indexing gaps, possible broken links, and high-level project context entries. Use it as a starting point, not as the final review.

## Review Workflow

1. Scan the project shape first:
   - `README.md`, plus dependency manifests such as `package.json`, `pyproject.toml`, or `Cargo.toml` when present.
   - Source directories, especially types, data, services, components, and tools-facing code.
   - Auxiliary directories that exist in this project, such as `scripts/`, `tools/`, `deploy/`, or `devSchedule/`.
   - `doc/README.md` and the non-log `doc/` tree.

2. Build the document map:
   - List authoritative entry documents from the actual `doc/` tree, for example `doc/README.md` and any top-level overview or specification document.
   - Group documents by the directories that actually exist under `doc/`.
   - Note frontmatter state (`status`, `last_updated`, `depends_on`) when present.
   - Distinguish source-of-truth docs from derived or public summaries.

3. Check maintenance needs:
   - **Update**: stale `last_updated`, content contradicted by current code, outdated status, missing recent systems, or references to removed implementation.
   - **Merge**: overlapping design docs, absorbed predecessor docs, duplicate public summaries, or split docs that no longer justify separate maintenance.
   - **Create**: code, deployment, or planning work with no corresponding doc, or important workflows missing from `doc/README.md`.
   - **Delete/Archive**: obsolete drafts, superseded docs, empty placeholders, or docs whose content is fully absorbed elsewhere.
   - **Index/Link Fixes**: documents missing from `doc/README.md`, broken local links, renamed files, inconsistent relative links.

4. Verify high-impact findings:
   - For claims about implementation drift, search code with `rg -n` and cite the relevant file/line.
   - For claims about documentation duplication or supersession, cite both documents.
   - For proposed deletion, identify the replacement source of truth or state that no replacement exists.
   - Do not infer deletion from age alone; require clear supersession, emptiness, or contradiction.

## Writing Tone

Write the review in plain human language. The report should help the user understand what the project can or cannot rely on, not merely list document modules.

- Summaries must describe user-facing, design-facing, or business-readable results instead of listing internal module names.
- For stale, missing, duplicated, or risky docs, say directly what cannot be done, what will be misunderstood, or what error may happen.
- In the `建议` column, start with the concrete consequence when possible, then name the document action. Example: "新成员会按旧入口接入；更新本页的入口说明".
- Technical names and paths are evidence, not the conclusion. Pair each technical finding with a sentence explaining its impact on project work.

## Report Format

Return a concise Chinese report with these sections:

```markdown
## 文档审阅结论

- **总体状态**：{healthy / needs-maintenance / fragmented}
- **最高优先级**：{1-3 个最该处理的问题}

## 建议清单

| 优先级 | 类型 | 文档 | 建议 | 证据 |
|------|------|------|------|------|
| P0/P1/P2 | 更新/合并/新建/删除/归档/索引 | `doc/...` |  | `path:line` |

## 需要人工确认

- {涉及设定取舍、删除、合并边界或公开口径的事项}

## 本次未处理范围

- `doc/开发日志/**`
- {其他用户未要求深入审阅的范围}
```

Use priority levels consistently:

- `P0`: Broken navigation, severe contradiction with implementation, or a missing source-of-truth doc blocking current work.
- `P1`: Stale or duplicated docs that can mislead future work.
- `P2`: Metadata cleanup, minor index improvements, wording consistency, or low-risk archive candidates.

## Safety Rules

- Default to review-only output.
- Do not edit `doc/开发日志/**`; it is maintained by commit/log skills.
- Do not inspect `brain-storm/**` during doc review, even when searching broadly.
- Do not modify generated artifacts, build output, dependency directories, or local environment files as part of this skill.
- Ask only when a recommendation requires product intent that cannot be discovered from repo context.
