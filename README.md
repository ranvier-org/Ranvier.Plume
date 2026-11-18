# Ranvier.Plume

A text editor as a fold over a log of edits, built on [Ranvier](https://github.com/ranvier-org/Ranvier).

- `Ranvier.Plume`: the state. An immutable rope, change sets with position mapping, selections, transactions,
  facets, state fields, compartments, history, commands and keymaps. No DOM dependency.
- `Ranvier.Plume.View`: the `EditorView` component for `Ranvier.View`.

A `Document` is one file: its log of transactions, its saved text and its current text. An `Editor` is one view of
a document, with its own selection and extensions. Consumers read through Ranvier memos.

```fsharp
open Ranvier
open Ranvier.Plume

let document = Document.create (Text.ofString "let answer = 42\n")
let editor = Editor.create document [ Keymap.extension Keymap.defaults ]

Api.createEffect (fun () -> printfn "caret at %d" editor.Selection.Value.Main.Head)

editor.Dispatch [ Tx.change { From = 4; To = 10; Insert = Text.ofString "result" }; Tx.userEvent "input.type" ]
```

The design is in [docs/DOSSIER.md](docs/DOSSIER.md). Pre-1.0: the API may change.
