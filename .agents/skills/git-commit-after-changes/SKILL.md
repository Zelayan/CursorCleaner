---
name: git-commit-after-changes
description: After any completed file change in this CursorCleaner repo, create a git commit without waiting to be asked. Use whenever you finish implementing, fixing, refactoring, adding tests, docs, or skills — even if the user did not say git, commit, 提交, or 提交代码. Also use when the user says git, commit, 提交, push, or 发布.
---

# Commit after every change

This repo expects a git commit as soon as a coherent change is done. Do not leave completed work uncommitted and do not wait for the user to say commit.

## When to commit

Commit after you have finished one logical unit of work, for example:

- a bug fix and its tests
- one feature slice
- a docs or skill update
- a follow-up that is a different concern from the previous commit

If the working tree still has leftover edits that belong to that same unit, include them in the same commit. If you then start a second, unrelated unit, make a second commit.

Do not create empty commits. If there is nothing to commit, stop.

## What not to wait for

Do not ask “要不要提交”. Do not end a turn with uncommitted completed work unless you are blocked on a destructive or policy decision.

Push, tag, and GitHub release are separate. Only push or publish when the user asks (`push` / `发布` / `git commit push`).

## How to commit

1. Inspect `git status` and `git diff`. Also look at `git log -5 --oneline` and match that message style.
2. Stage only the files that belong to this unit. Do not `git add .` if that would include unrelated lockfile, publish, or generated noise.
3. Commit with a heredoc message. Do not use `git commit --amend` unless the user explicitly asked. Do not skip hooks.
4. Prefer one commit per logical change, not one giant commit for mixed unrelated edits.

Recent messages in this repo look like:

```text
Add default occupancy scan and one-click cleanup by retention.
Replace WPF UI with Avalonia Core and Desktop for Windows and Mac.
```

Write the subject in English, sentence case, imperative, ending with a period. Keep it to one line unless the body is needed to record a safety constraint.

Example:

```bash
git add CursorCleaner.Core/ViewModels/MainViewModel.cs CursorCleaner.Tests/MainViewModelTests.cs
git commit -m "$(cat <<'EOF'
Summarize many SQLite paths in the cleanup confirm dialog.

EOF
)"
```

## Do not commit

- Secrets, tokens, local settings, or user Cursor data
- `bin/`, `obj/`, `publish/`, `.zcode/` (already gitignored)
- Accidental `packages.lock.json` RID sections created by a Windows restore on macOS
- Unrelated dirty files you did not intend to change

If `packages.lock.json` changed only because of a RID-specific restore, restore it with `git checkout --` instead of committing.

## After the commit

Tell the user the commit hash and subject. If they also asked to push or release, do that next. Otherwise stop after the commit; do not push on your own.

## GitHub releases

When the user asks to 发布 / release, create the GitHub release after packaging. Write **release notes in Chinese**, not English.

Keep the notes short: what changed for the user, then the standing safety lines. Do not paste commit subjects or internal file names unless the user-facing change needs them.

Template:

```text
<用一两段中文写用户能感知的变化。>

附件为 Windows 自包含 EXE 和未签名的 macOS Apple Silicon 应用。清理前请关闭 Cursor。文件会进入回收站 / 废纸篓，清空后才会真正释放磁盘空间。删除 SQLite 行不会立刻缩小数据库，需要时可手动优化。
```

Example for a UI flatten:

```text
所有页面始终可见。总览、历史会话、工作区、空间分析和数据库工具不再藏在高级开关后面。

清理确认框会按文件名汇总多个 SQLite 数据库及其总大小，不再逐条列出每个 `state.vscdb`。

附件为 Windows 自包含 EXE 和未签名的 macOS Apple Silicon 应用。清理前请关闭 Cursor。文件会进入回收站 / 废纸篓，清空后才会真正释放磁盘空间。删除 SQLite 行不会立刻缩小数据库，需要时可手动优化。
```

If an already-published release has English notes, edit it to Chinese with `gh release edit` instead of creating a duplicate. Tag names such as `v0.4.0` stay as-is.
