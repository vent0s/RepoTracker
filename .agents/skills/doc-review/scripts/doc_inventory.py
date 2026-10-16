#!/usr/bin/env python3
"""Read-only documentation inventory for the current project.

Prints a Markdown summary of doc/ files while excluding doc/开发日志/**.
The brainstorming workspace is intentionally outside this inventory.
"""

from __future__ import annotations

import argparse
import re
import sys
from dataclasses import dataclass, field
from pathlib import Path
from urllib.parse import unquote


LOG_DIR_PARTS = ("doc", "开发日志")
LINK_RE = re.compile(r"!?\[[^\]]*]\(([^)]+)\)")


@dataclass
class DocInfo:
    path: Path
    rel: str
    title: str = ""
    status: str = ""
    last_updated: str = ""
    depends_on: list[str] = field(default_factory=list)
    has_frontmatter: bool = False
    missing_metadata: list[str] = field(default_factory=list)


@dataclass
class BrokenLink:
    source: str
    line: int
    target: str


def rel_posix(path: Path, root: Path) -> str:
    return path.relative_to(root).as_posix()


def is_in_log_dir(path: Path, root: Path) -> bool:
    try:
        parts = path.relative_to(root).parts
    except ValueError:
        return False
    return len(parts) >= 2 and parts[0] == LOG_DIR_PARTS[0] and parts[1] == LOG_DIR_PARTS[1]


def read_text(path: Path) -> str:
    return path.read_text(encoding="utf-8-sig", errors="replace")


def parse_frontmatter(lines: list[str]) -> tuple[dict[str, object], int]:
    if not lines or lines[0].strip() != "---":
        return {}, 0

    metadata: dict[str, object] = {}
    current_list_key = ""
    end_index = 0

    for index, line in enumerate(lines[1:], start=1):
        stripped = line.strip()
        if stripped == "---":
            end_index = index + 1
            break
        if not stripped or stripped.startswith("#"):
            continue
        if stripped.startswith("- ") and current_list_key:
            value = stripped[2:].strip().strip("\"'")
            current_value = metadata.setdefault(current_list_key, [])
            if isinstance(current_value, list):
                current_value.append(value)
            continue
        if ":" in line:
            key, value = line.split(":", 1)
            key = key.strip()
            value = value.strip()
            current_list_key = ""
            if value in ("", "[]"):
                metadata[key] = [] if value == "" else []
                current_list_key = key if value == "" else ""
            else:
                metadata[key] = value.strip("\"'")

    return metadata, end_index


def first_heading(lines: list[str], start_index: int = 0) -> str:
    for line in lines[start_index:]:
        if line.startswith("# "):
            return line[2:].strip()
    return ""


def parse_doc(path: Path, root: Path) -> DocInfo:
    text = read_text(path)
    lines = text.splitlines()
    metadata, body_start = parse_frontmatter(lines)
    info = DocInfo(
        path=path,
        rel=rel_posix(path, root),
        title=first_heading(lines, body_start),
        status=str(metadata.get("status", "")),
        last_updated=str(metadata.get("last_updated", "")),
        has_frontmatter=bool(metadata),
    )

    depends_on = metadata.get("depends_on", [])
    if isinstance(depends_on, list):
        info.depends_on = [str(item) for item in depends_on if str(item).strip()]
    elif depends_on:
        info.depends_on = [str(depends_on)]

    if info.rel != "doc/README.md":
        for key, value in (
            ("status", info.status),
            ("last_updated", info.last_updated),
            ("depends_on", info.depends_on),
        ):
            if not value:
                info.missing_metadata.append(key)

    return info


def normalize_for_search(value: str) -> str:
    return value.replace("\\", "/").replace("%20", " ").lower()


def find_unindexed_docs(docs: list[DocInfo], root: Path) -> list[DocInfo]:
    readme = root / "doc" / "README.md"
    if not readme.exists():
        return [doc for doc in docs if doc.rel != "doc/README.md"]

    content = normalize_for_search(read_text(readme))
    unindexed: list[DocInfo] = []
    for doc in docs:
        if doc.rel == "doc/README.md":
            continue
        candidates = {
            normalize_for_search(doc.rel),
            normalize_for_search(doc.rel.removeprefix("doc/")),
            normalize_for_search("./" + doc.rel.removeprefix("doc/")),
            normalize_for_search(doc.path.stem),
        }
        if not any(candidate and candidate in content for candidate in candidates):
            unindexed.append(doc)
    return unindexed


def clean_link_target(raw_target: str) -> str:
    target = raw_target.strip()
    if target.startswith("<") and target.endswith(">"):
        target = target[1:-1].strip()
    if " " in target and not target.startswith("./") and not target.startswith("../"):
        target = target.split(" ", 1)[0]
    return unquote(target.split("#", 1)[0].strip())


def is_external_or_anchor(target: str) -> bool:
    lowered = target.lower()
    return (
        not target
        or target.startswith("#")
        or lowered.startswith(("http://", "https://", "mailto:", "tel:", "app://"))
    )


def resolve_link(source: Path, target: str) -> Path:
    return (source.parent / target).resolve()


def find_broken_links(docs: list[DocInfo], root: Path) -> list[BrokenLink]:
    broken: list[BrokenLink] = []
    for doc in docs:
        lines = read_text(doc.path).splitlines()
        for line_no, line in enumerate(lines, start=1):
            for match in LINK_RE.finditer(line):
                raw_target = match.group(1)
                target = clean_link_target(raw_target)
                if is_external_or_anchor(target):
                    continue
                if re.match(r"^[A-Za-z][A-Za-z0-9+.-]*:", target):
                    continue
                resolved = resolve_link(doc.path, target)
                if resolved.exists():
                    continue
                if target.endswith("/") and resolved.with_suffix(".md").exists():
                    continue
                broken.append(BrokenLink(doc.rel, line_no, raw_target))
    return broken


def count_files(path: Path, patterns: tuple[str, ...]) -> int:
    if not path.exists():
        return 0
    total = 0
    for pattern in patterns:
        total += sum(1 for item in path.rglob(pattern) if item.is_file())
    return total


def project_context(root: Path) -> list[tuple[str, str]]:
    """Describe the entry points that actually exist in this project.

    Only existing paths are listed, so the inventory never claims a project
    structure that is not there.
    """
    candidates: list[tuple[str, str]] = [
        ("README.md", "项目入口说明"),
        ("package.json", "Node 脚本与依赖"),
        ("pyproject.toml", "Python 项目配置"),
        ("Cargo.toml", "Rust 项目配置"),
        ("pnpm-workspace.yaml", "工作区配置"),
    ]
    entries = [
        (path, note) for path, note in candidates if (root / path).exists()
    ]
    directory_notes = [
        ("src/", ("*.ts", "*.tsx", "*.js", "*.jsx"), "个源文件"),
        ("tools/", ("*",), "个文件"),
        ("scripts/", ("*",), "个文件"),
        ("deploy/", ("*",), "个文件"),
        ("devSchedule/", ("*.md", "*.json", "*.mjs", "*.js", "*.html"), "个规划/工具文件"),
    ]
    for path, patterns, unit in directory_notes:
        if (root / path).exists():
            entries.append((path, f"{count_files(root / path, patterns)} {unit}"))
    return entries


def print_markdown(root: Path, docs: list[DocInfo], unindexed: list[DocInfo], broken: list[BrokenLink]) -> None:
    print("# doc-review 文档盘点")
    print()
    print(f"- 项目根目录：`{root.as_posix()}`")
    print(f"- 审阅文档数：{len(docs)}")
    print("- 排除范围：`doc/开发日志/**`")
    print("- 未成形构思目录不参与审阅或索引判断。")
    print()

    print("## 项目上下文入口")
    print()
    print("| 路径 | 摘要 |")
    print("|------|------|")
    for path, note in project_context(root):
        print(f"| `{path}` | {note} |")
    print()

    print("## 非日志 doc 文档清单")
    print()
    print("| 文档 | 标题 | status | last_updated | depends_on | 缺少元数据 |")
    print("|------|------|--------|--------------|------------|------------|")
    for doc in docs:
        depends_on = ", ".join(doc.depends_on)
        missing = ", ".join(doc.missing_metadata)
        print(
            f"| `{doc.rel}` | {doc.title or '-'} | {doc.status or '-'} | "
            f"{doc.last_updated or '-'} | {depends_on or '-'} | {missing or '-'} |"
        )
    print()

    print("## doc/README.md 索引疑点")
    print()
    if not (root / "doc" / "README.md").exists():
        print("- 缺少 `doc/README.md`，文档没有导航入口。")
    elif unindexed:
        for doc in unindexed:
            print(f"- `{doc.rel}`")
    else:
        print("- 未发现明显未索引文档。")
    print()

    print("## 可能断链")
    print()
    if broken:
        for item in broken:
            print(f"- `{item.source}:{item.line}` -> `{item.target}`")
    else:
        print("- 未发现明显断链。")
    print()

    print("## 后续人工审阅提示")
    print()
    print("- 对照实际存在的源码、脚本与规划目录，判断系统设计文档是否过期或缺失。")
    print("- 删除或合并建议需要先确认替代的权威文档。")


def main(argv: list[str]) -> int:
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8")
        sys.stderr.reconfigure(encoding="utf-8")

    parser = argparse.ArgumentParser(description="Inventory non-log docs for doc-review.")
    parser.add_argument(
        "root",
        nargs="?",
        default=".",
        help="Project root. Defaults to current working directory.",
    )
    args = parser.parse_args(argv)

    root = Path(args.root).resolve()
    doc_dir = root / "doc"
    if not doc_dir.exists():
        print(f"error: doc directory not found under {root}", file=sys.stderr)
        return 1

    docs = [
        parse_doc(path, root)
        for path in sorted(doc_dir.rglob("*.md"))
        if not is_in_log_dir(path, root)
    ]
    unindexed = find_unindexed_docs(docs, root)
    broken = find_broken_links(docs, root)
    print_markdown(root, docs, unindexed, broken)
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
