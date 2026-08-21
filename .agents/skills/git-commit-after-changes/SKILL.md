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
