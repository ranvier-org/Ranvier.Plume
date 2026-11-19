namespace Ranvier.Plume

open System
open System.Text
open Ranvier
open Ranvier.Keyboard

/// A command bound to a key or a sequence of keys.
[<NoEquality; NoComparison>]
type KeyBinding =
    {
        /// The strokes, in order.
        Keys: KeyChord list
        Run: Command
        /// The command for the same strokes with Shift held on the last one.
        Shift: Command voption
        /// Where the binding applies; "" for the editor.
        Scope: string
    }

/// What a key did.
type KeymapResult =
    /// A command applied.
    | Handled
    /// The strokes so far start a longer binding.
    | Pending of KeyChord list
    | Unhandled

[<RequireQualifiedAccess>]
module Keymap =

    /// The bindings of an editor, in precedence order.
    let facet: Facet<KeyBinding list, KeyBinding[]> =
        Facet.define (fun (inputs: KeyBinding list[]) -> inputs |> Array.collect Array.ofList)

    /// An extension that adds <c>bindings</c>.
    let extension (bindings: KeyBinding list) : Extension = facet.Of bindings

    /// Bind an ordered sequence of typed strokes to a command.
    let bindChords (keys: KeyChord list) (command: Command) : KeyBinding =
        { Keys = keys; Run = command; Shift = ValueNone; Scope = "" }

    /// Bind a sequence with an alternate command for Shift on the final stroke.
    let bindChordsWithShift (keys: KeyChord list) (command: Command) (shifted: Command) : KeyBinding =
        { bindChords keys command with Shift = ValueSome shifted }

    /// Motion, selection and editing keys.
    let standard: KeyBinding list =
        [
            bindChordsWithShift [ chord { key (Navigation ArrowLeft) } ] Commands.cursorCharLeft Commands.selectCharLeft
            bindChordsWithShift [ chord { key (Navigation ArrowRight) } ] Commands.cursorCharRight Commands.selectCharRight
            bindChordsWithShift [ chord { ctrl; key (Navigation ArrowLeft) } ] Commands.cursorGroupLeft Commands.selectGroupLeft
            bindChordsWithShift [ chord { ctrl; key (Navigation ArrowRight) } ] Commands.cursorGroupRight Commands.selectGroupRight
            bindChordsWithShift [ chord { key (Navigation ArrowUp) } ] Commands.cursorLineUp Commands.selectLineUp
            bindChordsWithShift [ chord { key (Navigation ArrowDown) } ] Commands.cursorLineDown Commands.selectLineDown
            bindChordsWithShift [ chord { key (Navigation PageUp) } ] Commands.cursorPageUp Commands.selectPageUp
            bindChordsWithShift [ chord { key (Navigation PageDown) } ] Commands.cursorPageDown Commands.selectPageDown
            bindChordsWithShift [ chord { key (Navigation Home) } ] Commands.cursorLineStart Commands.selectLineStart
            bindChordsWithShift [ chord { key (Navigation End) } ] Commands.cursorLineEnd Commands.selectLineEnd
            bindChordsWithShift [ chord { ctrl; key (Navigation Home) } ] Commands.cursorDocStart Commands.selectDocStart
            bindChordsWithShift [ chord { ctrl; key (Navigation End) } ] Commands.cursorDocEnd Commands.selectDocEnd
            bindChords [ chord { ctrl; key (Character (Rune 'a')) } ] Commands.selectAll
            bindChords [ chord { key (Whitespace Enter) } ] Commands.insertNewlineKeepIndent
            bindChords [ chord { key (Editing EditingKey.Backspace) } ] Commands.deleteCharBackward
            bindChords [ chord { key (Editing EditingKey.Delete) } ] Commands.deleteCharForward
            bindChords [ chord { key (Whitespace Tab) } ] Commands.insertTab
        ]

    /// Undo on Ctrl-z; redo on Ctrl-y and Ctrl-Shift-z.
    let history: KeyBinding list =
        [
            bindChords [ chord { ctrl; key (Character (Rune 'z')) } ] Commands.undo
            bindChords [ chord { ctrl; key (Character (Rune 'y')) } ] Commands.redo
            bindChords [ chord { ctrl; shift; key (Character (Rune 'Z')) } ] Commands.redo
        ]

    /// <c>standard</c> and <c>history</c>.
    let defaults: KeyBinding list = standard @ history

    /// The chord of one stroke: modifiers and a W3C key name joined by "-", as in "Ctrl-z", "Ctrl-Shift-z",
    /// "Shift-ArrowLeft", "Enter".
    /// <exception cref="T:System.ArgumentException">The stroke names no key or an unknown modifier.</exception>
    let chord (stroke: string) : KeyChord =
        if String.IsNullOrEmpty stroke then
            raise (ArgumentException ("A stroke names a key.", "stroke"))

        // The key is what follows the last "-" that has a modifier before it; "Ctrl--" is Ctrl and "-".
        let split =
            if stroke.Length > 1 && stroke.EndsWith ("-", StringComparison.Ordinal) then
                stroke.Length - 2
            else
                stroke.LastIndexOf '-'

        let key = if split <= 0 then stroke else stroke.Substring (split + 1)
        let mutable ctrl = false
        let mutable alt = false
        let mutable shift = false
        let mutable meta = false

        if split > 0 then
            for name in stroke.Substring(0, split).Split '-' do
                match name with
                | "Ctrl"
                | "Control" -> ctrl <- true
                | "Alt" -> alt <- true
                | "Shift" -> shift <- true
                | "Meta"
                | "Cmd" -> meta <- true
                | other -> raise (ArgumentException (String.Concat ("'", other, "' is not a modifier."), "stroke"))

        // A keyboard event names a letter typed with Shift by its upper case.
        let key =
            if shift && key.Length = 1 then
                key.ToUpperInvariant ()
            else
                key

        KeyChord.ofLogical
            (LogicalKey.ofW3cKey key)
            {
                Ctrl = ctrl
                Alt = alt
                Shift = shift
                Meta = meta
                AltGraph = false
            }

    let private parseStrokes (keys: string) =
        keys.Split (' ', StringSplitOptions.RemoveEmptyEntries) |> Array.map chord |> List.ofArray

    /// Bind a space-separated sequence in editor key notation.
    let bind (keys: string) (command: Command) : KeyBinding =
        bindChords (parseStrokes keys) command

    /// <c>bind</c>, with <c>shifted</c> for the same strokes with Shift held.
    let bindShift (keys: string) (command: Command) (shifted: Command) : KeyBinding =
        bindChordsWithShift (parseStrokes keys) command shifted

    let private unshifted (chord: KeyChord) : KeyChord =
        let modifiers = { chord.Modifiers with Shift = false }

        match chord.Logical with
        | Character rune -> KeyChord.ofLogical (Character (Rune.ToLowerInvariant rune)) modifiers
        | logical -> KeyChord.ofLogical logical modifiers

    let rec private startsWith (prefix: KeyChord list) (keys: KeyChord list) : bool =
        match prefix, keys with
        | [], _ -> true
        | p :: prefixRest, k :: keysRest -> p = k && startsWith prefixRest keysRest
        | _ -> false

    /// Runs the bindings of <c>editor</c> for <c>chord</c>, pressed after the strokes of <c>pending</c>. Bindings
    /// run in precedence order until a command applies. With Shift held and no binding for the chord, the
    /// <c>Shift</c> command of a binding for the chord without Shift runs.
    let run (editor: Editor) (pending: KeyChord list) (chord: KeyChord) : KeymapResult =
        let bindings =
            Api.untrack (fun () -> EditorState.facet facet editor.State.Value)
            |> Array.filter (fun binding -> binding.Scope = "")

        let strokes = pending @ [ chord ]

        let exact =
            bindings
            |> Array.exists (fun binding -> binding.Keys = strokes && binding.Run editor)

        if exact then
            Handled
        else
            let shifted =
                chord.Shift
                && (let plain = pending @ [ unshifted chord ]

                    bindings
                    |> Array.exists (fun binding ->
                        binding.Keys = plain
                        && (match binding.Shift with
                            | ValueSome command -> command editor
                            | ValueNone -> false)))

            if shifted then
                Handled
            elif
                bindings
                |> Array.exists (fun binding -> binding.Keys.Length > strokes.Length && startsWith strokes binding.Keys)
            then
                Pending strokes
            else
                Unhandled
