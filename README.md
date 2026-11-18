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
let editor = Editor.create document [ Keymap.extension Keymap.defaults ]   // under a Ranvier owner

Api.createEffect (fun () -> printfn "caret at %d" editor.Selection.Value.Main.Head)

editor.Dispatch [ Tx.change { From = 4; To = 10; Insert = Text.ofString "result" }; Tx.userEvent "input.type" ]
```

The design is in [docs/DOSSIER.md](docs/DOSSIER.md). Pre-1.0: the API may change.

## Build, test and measure

```
dotnet fsi --exec build.fsx -- ci
```

builds the solution in Release and runs both test projects (the view tests open hidden windows).

The sample in `samples/EditorShowcase` is the workspace's `EditorShowcase` rebuilt on the library:

```
dotnet run -c Release --project samples/EditorShowcase                 # the window
dotnet run -c Release --project samples/EditorShowcase -- --bench      # frame cost, hidden window
dotnet run -c Release --project samples/EditorShowcase -- --memory     # what history holds
dotnet run -c Release --project samples/EditorShowcase -- --load       # scalar against vectorised line scan
dotnet run -c Release --project samples/EditorShowcase -- --profile    # frame cost with each view feature off
dotnet run -c Release --project samples/EditorShowcase -- --writes     # the page writes of a keystroke and a scroll
dotnet run -c Release --project samples/EditorShowcase -- --shot out.png
```

The phase 1 measurements are in [docs/ACCEPTANCE.md](docs/ACCEPTANCE.md).
