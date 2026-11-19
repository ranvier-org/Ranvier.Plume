---
title: Installation and your first edit
description: Choose a package and make a change under a Ranvier owner.
---

# Installation and your first edit

Install the state library for a headless tool, or install the view package to bring in the editor state and native view dependencies together.

```sh
dotnet add package Ranvier.Plume --version 0.2.0
# For a project which mounts the editor:
dotnet add package Ranvier.Plume.View --version 0.2.0
```

Both libraries target .NET 8 and .NET 10. For a traced Ranvier application use `Ranvier.Plume.Traced` and `Ranvier.Plume.View.Traced` throughout. The state package also includes Fable sources. The view package ships compiled .NET assemblies.

Call this function from component setup or another Ranvier owner. It creates an editor with the standard editing and history shortcuts and changes the name in a declaration.

```fsharp
open Ranvier.Plume

let openExample () =
    let document = Document.create (Text.ofString "let answer = 42\n")
    let editor = Editor.create document [ Keymap.extension Keymap.defaults ]
    editor.Dispatch [
        Tx.change { From = 4; To = 10; Insert = Text.ofString "result" }
        Tx.userEvent "input.type"
    ]
    Text.toString editor.Text.Value // "let result = 42\n"
```

Store the document for as long as the file is open. Create an editor for each pane that needs its own selection. [Documents and transactions](documents.md) explains the two lifetimes.
