---
title: Documents and transactions
description: Understand ownership, offsets, history and incremental reads.
---

# Documents and transactions

A document owns the text shared by its editors. An editor owns a selection and extension configuration. Reading `editor.Text.Value` or `editor.Selection.Value` inside a Ranvier effect subscribes that effect to subsequent changes.

```text
editor command → transaction specs → document log → document text and history
                                         ↓
                                 editor state and selection
                                         ↓
                                    visible rows
```

An edit range is half-open. Offsets count UTF-16 code units and lines are zero-based. A supplementary character occupies two code units. Screen columns also depend on tabs and wide characters. Use editor movement commands for caret navigation and the view’s coordinate mapping when handling a pointer.

Group related specs into one `editor.Dispatch` call. Use `Tx.change` to replace a range and `Tx.userEvent` to describe its origin. To submit against a previously observed document revision, use `Document.DispatchAt` with that revision. Independent consumers can obtain their own `document.Reader()` or `editor.Updates()` and read changes since their previous read.

For a save operation, persist the text snapshot, then call `document.MarkSaved()` when the saved snapshot is still current. Observe `document.IsDirty.Value` for a modified indicator. History belongs to the document. Its undo and redo depths are available as memos, and `Commands.undo` and `Commands.redo` operate through an editor.
