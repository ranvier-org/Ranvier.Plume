---
title: Mounting and styling the editor
description: Mount the native component and extend its virtualised rows.
---

# Mounting and styling the editor

Create the component definition once. Supply the editor and its host frame scheduler through props. In a Ranvier.View template, the mounted component follows the current props.

```fsharp
open Ranvier.View.Dsl
open Ranvier.Plume.View

let editorView = EditorView.create EditorView.defaults

// FileSlot is the showcase's model for an open file.
let fileView =
    render editorView.Component (fun (file: FileSlot) ->
        { Editor = file.Editor; Scheduler = file.Scheduler })
```

After mounting, `EditorView.current editor` returns a value option. Use the mounted view’s `Focus()`, `ScrollTo` or `ScrollBy` to drive it. Read `Viewport` to follow the visible lines. `PosAtCoords` maps a viewport point to a text offset, and `CoordsAtPos` returns the character rectangle on the sheet.

Use `Gutter.define` to place a marker beside each visible line. Its key function declares which extra state can invalidate the markers. Use `Layer.define` for rectangles above or below the text. Assign classes to markers and supply their rules through the host stylesheet. The showcase’s [style definitions](https://github.com/ranvier-org/Ranvier.Plume/blob/main/samples/EditorShowcase/Styles.fs) and [stylesheet assembly](https://github.com/ranvier-org/Ranvier.Plume/blob/main/samples/EditorShowcase/LiveSheet.fs) show how they reach the native renderer.
