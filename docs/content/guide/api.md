---
title: API reference
description: Navigate the public modules and their documented declarations.
---

# API reference

Browse the generated [state API](https://ranvier-org.github.io/Ranvier.Plume/reference/ranvier-plume/) and [view API](https://ranvier-org.github.io/Ranvier.Plume/reference/ranvier-plume-view/) for signatures, members and XML comments from the release assemblies.

The packages include XML documentation beside each assembly for editor completion. The linked modules contain the signatures, discriminated unions and remarks for this release.

- [Text](https://github.com/ranvier-org/Ranvier.Plume/blob/v0.3.0/src/Ranvier.Plume/Text.fs) — immutable text and line operations.
- [ChangeSet](https://github.com/ranvier-org/Ranvier.Plume/blob/v0.3.0/src/Ranvier.Plume/ChangeSet.fs) — position mapping across edits.
- [Selection](https://github.com/ranvier-org/Ranvier.Plume/blob/v0.3.0/src/Ranvier.Plume/Selection.fs) — ranges, anchors and primary selection.
- [RangeSet](https://github.com/ranvier-org/Ranvier.Plume/blob/v0.3.0/src/Ranvier.Plume/RangeSet.fs) — position-indexed ranges.
- [Transaction](https://github.com/ranvier-org/Ranvier.Plume/blob/v0.3.0/src/Ranvier.Plume/Transaction.fs) — edit specifications, effects and annotations.
- [State](https://github.com/ranvier-org/Ranvier.Plume/blob/v0.3.0/src/Ranvier.Plume/State.fs) — facets, state fields, compartments and precedence.
- [History](https://github.com/ranvier-org/Ranvier.Plume/blob/v0.3.0/src/Ranvier.Plume/History.fs) — undo and redo state.
- [Document](https://github.com/ranvier-org/Ranvier.Plume/blob/v0.3.0/src/Ranvier.Plume/Document.fs) — shared log, save point and readers.
- [Editor](https://github.com/ranvier-org/Ranvier.Plume/blob/v0.3.0/src/Ranvier.Plume/Editor.fs) — per-pane state and update readers.
- [Columns](https://github.com/ranvier-org/Ranvier.Plume/blob/v0.3.0/src/Ranvier.Plume/Columns.fs) — visual columns, tabs and offset conversion.
- [Commands](https://github.com/ranvier-org/Ranvier.Plume/blob/v0.3.0/src/Ranvier.Plume/Commands.fs) — movement, selection and editing commands.
- [Keymap](https://github.com/ranvier-org/Ranvier.Plume/blob/v0.3.0/src/Ranvier.Plume/Keymap.fs) — typed bindings, precedence and multi-stroke dispatch.
- [Language](https://github.com/ranvier-org/Ranvier.Plume/blob/v0.3.0/src/Ranvier.Plume/Language.fs) — line tokenizers, tags and highlight styles.
- [Styling](https://github.com/ranvier-org/Ranvier.Plume/blob/v0.3.0/src/Ranvier.Plume.View/Styling.fs) — editor themes and class rules.
- [Types](https://github.com/ranvier-org/Ranvier.Plume/blob/v0.3.0/src/Ranvier.Plume.View/Types.fs) — view props, geometry, plugins, layers and gutters.
- [Features](https://github.com/ranvier-org/Ranvier.Plume/blob/v0.3.0/src/Ranvier.Plume.View/Features.fs) — built-in view features.
- [Decoration](https://github.com/ranvier-org/Ranvier.Plume/blob/v0.3.0/src/Ranvier.Plume.View/Decoration.fs) — typed marks, diagnostic underlines and hover templates.
- [Search](https://github.com/ranvier-org/Ranvier.Plume/blob/v0.3.0/src/Ranvier.Plume/Search.fs) provides literal and regex queries, match navigation and undoable replacement. Install `SearchView.highlight` to display matches.
- [Folding](https://github.com/ranvier-org/Ranvier.Plume/blob/v0.3.0/src/Ranvier.Plume/Folding.fs) collapses inclusive line ranges per editor. Navigation to a hidden selection reveals its enclosing folds.
- [Brackets](https://github.com/ranvier-org/Ranvier.Plume/blob/v0.3.0/src/Ranvier.Plume/Brackets.fs) provides delimiter matching and opt-in paired entry. Install `BracketMatching.extension` for visual feedback.
- [Component](https://github.com/ranvier-org/Ranvier.Plume/blob/v0.3.0/src/Ranvier.Plume.View/Component.fs) — component creation and mounted-view lookup.
