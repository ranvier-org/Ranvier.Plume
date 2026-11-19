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

## Text entry and accessible selection

`EditorView.accessibleName.Of "Source editor"` names the native multiline editing control. A focused view exposes the main selection, its direction, and surrounding lines to the host. The native context spans the selected lines and a line on each side when available. It expands for a multiline selection. Plume converts the native LF offsets to document UTF-16 offsets, including across CRLF. Setting the native selection updates the editor.

The native input method owns provisional composition. Its preedit appears at the caret. Commit applies one editor command, and cancellation discards the preedit. A document or selection change during composition invalidates its pending commit. The clipboard uses plain text. Copy and cut join multiple selections with the configured line separator. When the number of pasted lines matches the number of carets, each caret receives its corresponding line. Read-only editors support selection and copying.

## Typed decorations

`Decoration.mark` positions a Ranvier.View template over each visible line segment of a half-open document range. `Decoration.underline` draws the mark and takes a Dsl template for its hover. The template receives a reader for the typed range payload. The range source can read editor fields or Ranvier memos.

```fsharp
type Diagnostic = { Code: string; Message: string }

let hover: TemplateView<unit -> Diagnostic> =
    span () {
        strong () { fun read -> (read()).Code }
        " "
        fun read -> (read()).Message
    }

let diagnostics (field: StateField<RangeSet<Diagnostic>>) =
    Decoration.underline "diagnostic" (fun view -> view.Editor.Field(field).Value) hover
```

For diagnostics, call `Decoration.mapUntouched (ChangeSet.desc tx.Changes) ranges` from the field update. An edit inside the marked text removes the diagnostic. Edits before it move it, and insertions at either boundary stay outside the mark. In EditorShowcase, choose **Go to diagnostic** to stop the tour and reveal the example. Hover the underlined `Mu: float` declaration near the top of the sample file. Editing inside the declaration dismisses the diagnostic.
