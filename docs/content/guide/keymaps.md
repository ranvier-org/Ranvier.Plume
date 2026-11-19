---
title: Typed shortcuts
description: Declare a chord, bind a sequence and handle a partially matched shortcut.
---

# Typed shortcuts

Use Ranvier.Keyboard’s `chord` expression at declaration sites. It builds a typed logical key with a modifier mask. Reordering modifiers gives the same shortcut. The list passed to Plume preserves the order of successive strokes.

```fsharp
open System.Text
open Ranvier.Keyboard
open Ranvier.Plume

let prefix = chord { ctrl; key (Character (Rune 'k')) }
let select = chord { ctrl; key (Character (Rune 'a')) }
let left = chord { key (Navigation ArrowLeft) }

let bindings = Keymap.extension [
    Keymap.bindChords [ prefix; select ] Commands.selectAll
    Keymap.bindChordsWithShift [ left ] Commands.cursorCharLeft Commands.selectCharLeft
]
```

Pass `bindings` in the extensions list when creating the editor. A command returns a Boolean indicating whether it applied. Plume tries exact bindings in precedence order, then a Shift variant for the last stroke. Use the logical character delivered by the event for an explicit shifted printable binding, for example uppercase `Rune 'Z'` for Ctrl+Shift+Z.

The mounted view handles input. In a custom host, call `Keymap.run editor pending stroke`. Retain the returned strokes on `Pending strokes`, consume the event on `Handled`, and allow ordinary input on `Unhandled`. Reset pending strokes after completion or cancellation. Keep pending state per input context.

`Keymap.standard` provides movement, selection, deletion, indentation and select-all. `Keymap.history` adds undo and redo. `Keymap.defaults` combines them. For a text configuration file, parse a binding with `Keymap.bind` or `Keymap.bindShift`.
