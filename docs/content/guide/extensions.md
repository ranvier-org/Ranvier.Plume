---
title: State and view extensions
description: Choose a facet, state field, compartment or view plugin.
---

# State and view extensions

| Requirement | Plume primitive |
| --- | --- |
| Combine contributions from several extensions | Define a `Facet` and install contributions with its `Of` member. Read the result through `editor.Facet`. |
| Keep a value that evolves with transactions | Define a `StateField` with initialization and update logic. Read it through `editor.Field`. |
| Replace a group of extensions while the editor is open | Install a `Compartment` and dispatch `Tx.effect (slot.Reconfigure replacement)`. |
| Attach state or behavior to a mounted view | Define a `ViewPlugin`, install its extension and use `ViewPlugin.tryGet` on the mounted view. |

For syntax highlighting, create a line tokenizer with `Language.ofLineLexer` and install it with `Language.extension`. Return token spans relative to the line and assign a `Tag` to each span. Describe the presentation separately using `HighlightStyle.define`. Keeping the token data independent of CSS lets a theme change without rewriting the lexer.

The showcase implements a working [lexer](https://github.com/ranvier-org/Ranvier.Plume/blob/main/samples/EditorShowcase/Lexer.fs) and [reconfigurable editor](https://github.com/ranvier-org/Ranvier.Plume/blob/main/samples/EditorShowcase/App.fs).
