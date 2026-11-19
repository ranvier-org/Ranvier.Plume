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

builds the solution in Release and runs both test projects, the state tests on `net10.0` and `net8.0` (the view
tests open hidden windows).
`dotnet fsi --exec build.fsx -- check` builds it in Debug and runs `rfs check --fail-on-error`.
`dotnet fsi --exec build.fsx -- dev` runs the sample under the hot reload host, window off screen, and checks that
edits to a copy of its sources reach the page.
`dotnet fsi --exec build.fsx -- traced` builds both libraries on the traced core and runs the state tests on them.
`dotnet fsi --exec build.fsx -- acceptance` recomputes the acceptance verdicts of the dossier's section 7.
`dotnet fsi --exec build.fsx -- red` shows the tests of plan tasks 1 and 2 failing to compile without their
implementation.

The sample in `samples/EditorShowcase` is the workspace's `EditorShowcase` rebuilt on the library:

```
dotnet run -c Release --project samples/EditorShowcase                 # the window
dotnet run -c Release --project samples/EditorShowcase -- --bench      # frame cost, hidden window
dotnet run -c Release --project samples/EditorShowcase -- --memory     # what history holds
dotnet run -c Release --project samples/EditorShowcase -- --load       # scalar against vectorised line scan
dotnet run -c Release --project samples/EditorShowcase -- --profile    # frame cost with each view feature off
dotnet run -c Release --project samples/EditorShowcase -- --writes     # the page writes of a keystroke and a scroll
dotnet run -c Release --project samples/EditorShowcase -- --tour       # the app as its window runs it, hidden
dotnet run -c Release --project samples/EditorShowcase -- --tour --shown out.png   # the same, window shown
dotnet run -c Release --project samples/EditorShowcase -- --tour --shown out.png --pointer   # and the real pointer moved
dotnet run -c Release --project samples/EditorShowcase -- --shot out.png
```

The phase 1 measurements are in [docs/ACCEPTANCE.md](docs/ACCEPTANCE.md).
