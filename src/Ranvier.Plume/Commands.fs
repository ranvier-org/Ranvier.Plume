namespace Ranvier.Plume

open System
open Ranvier
open Ranvier.Keyboard

/// An action on an editor. True when the action applied; false lets the next binding of a key run.
type Command = Editor -> bool

/// A command over a state and a function that dispatches a transaction, so it runs without an editor.
type StateCommand = EditorState -> (TxSpec list -> unit) -> bool

[<RequireQualifiedAccess>]
module Commands =

    /// <c>command</c> on an editor's current state.
    let ofState (command: StateCommand) : Command =
        fun editor -> command (Api.untrack (fun () -> editor.State.Value)) editor.Dispatch

    // ---- Positions ----

    let private column (line: Line) (position: int) : int =
        min (position - line.From) line.Text.Length

    let private charLeft (doc: Text) (position: int) : int =
        let line = Text.lineAt position doc
        let at = column line position

        if at > 0 then line.From + Grapheme.prevClusterStart line.Text at
        elif line.Number > 0 then (Text.line (line.Number - 1) doc).To
        else position

    let private charRight (doc: Text) (position: int) : int =
        let line = Text.lineAt position doc
        let at = column line position

        if at < line.Text.Length then
            line.From + at + Grapheme.nextClusterLength line.Text at
        elif line.Number < doc.Lines - 1 then
            (Text.line (line.Number + 1) doc).From
        else
            position

    /// 0 for white space, 1 for a word character, 2 for anything else.
    let private category (text: string) (index: int) : int =
        let ch = text[index]

        if Char.IsWhiteSpace ch then 0
        elif Char.IsLetterOrDigit ch || ch = '_' || Char.IsSurrogate ch then 1
        else 2

    let private groupRight (doc: Text) (position: int) : int =
        let line = Text.lineAt position doc
        let text = line.Text
        let mutable at = column line position

        if at >= text.Length then
            charRight doc position
        else
            while at < text.Length && category text at = 0 do
                at <- at + 1

            if at < text.Length then
                let kind = category text at

                while at < text.Length && category text at = kind do
                    at <- at + 1

            line.From + at

    let private groupLeft (doc: Text) (position: int) : int =
        let line = Text.lineAt position doc
        let text = line.Text
        let mutable at = column line position

        if at = 0 then
            charLeft doc position
        else
            while at > 0 && category text (at - 1) = 0 do
                at <- at - 1

            if at > 0 then
                let kind = category text (at - 1)

                while at > 0 && category text (at - 1) = kind do
                    at <- at - 1

            line.From + at

    // ---- Selection motion ----

    /// Moves every range's head to `target state range`, which returns the position and the goal column to keep.
    let private move (extend: bool) (target: EditorState -> SelectionRange -> struct (int * int)) : StateCommand =
        fun state dispatch ->
            let moved =
                state.Selection.Ranges
                |> Array.map (fun range ->
                    let struct (head, goal) = target state range

                    {
                        Anchor = (if extend then range.Anchor else head)
                        Head = head
                        GoalColumn = goal
                        Assoc = range.Assoc
                    })

            let selection = EditorSelection.create (List.ofArray moved) state.Selection.MainIndex

            if selection = state.Selection then
                false
            else
                dispatch [ Tx.selection selection; Tx.scrollIntoView; Tx.userEvent "select" ]
                true

    /// A horizontal motion: a range that is not extended collapses to its edge on the side of the motion.
    let private horizontal (extend: bool) (forward: bool) (step: Text -> int -> int) : StateCommand =
        move extend (fun state range ->
            if not extend && not range.IsEmpty then
                struct ((if forward then range.To else range.From), -1)
            else
                struct (step state.Doc range.Head, -1))

    /// A vertical motion keeps a column of the grid (`Columns`): the place a reader sees, where a tab reaches to
    /// its stop and a wide character takes two columns.
    let private vertical (extend: bool) (lines: EditorState -> int) : StateCommand =
        move extend (fun state range ->
            let doc = state.Doc
            let tabSize = EditorState.facet EditorState.tabSize state
            let line = Text.lineAt range.Head doc

            let goal =
                if range.GoalColumn >= 0 then
                    range.GoalColumn
                else
                    Columns.columnOf line.Text tabSize (column line range.Head)

            let target = line.Number + lines state

            if target < 0 then
                struct (0, goal)
            elif target >= doc.Lines then
                struct (doc.Length, goal)
            else
                let onto = Text.line target doc
                struct (onto.From + Columns.offsetAt onto.Text tabSize (float goal), goal))

    let private lineEdge (extend: bool) (toEnd: bool) : StateCommand =
        move extend (fun state range ->
            let line = Text.lineAt range.Head state.Doc
            struct ((if toEnd then line.To else line.From), -1))

    let private docEdge (extend: bool) (toEnd: bool) : StateCommand =
        move extend (fun state _ -> struct ((if toEnd then state.Doc.Length else 0), -1))

    let private paged (extend: bool) (direction: int) : Command =
        fun editor ->
            let lines = direction * max 1 editor.PageLines.Peek
            ofState (vertical extend (fun _ -> lines)) editor

    let cursorCharLeft: Command = ofState (horizontal false false charLeft)
    let cursorCharRight: Command = ofState (horizontal false true charRight)
    let cursorGroupLeft: Command = ofState (horizontal false false groupLeft)
    let cursorGroupRight: Command = ofState (horizontal false true groupRight)
    let cursorLineStart: Command = ofState (lineEdge false false)
    let cursorLineEnd: Command = ofState (lineEdge false true)
    let cursorLineUp: Command = ofState (vertical false (fun _ -> -1))
    let cursorLineDown: Command = ofState (vertical false (fun _ -> 1))
    /// Moves up by the number of lines in <c>Editor.PageLines</c>.
    let cursorPageUp: Command = paged false -1
    let cursorPageDown: Command = paged false 1
    let cursorDocStart: Command = ofState (docEdge false false)
    let cursorDocEnd: Command = ofState (docEdge false true)

    let selectCharLeft: Command = ofState (horizontal true false charLeft)
    let selectCharRight: Command = ofState (horizontal true true charRight)
    let selectGroupLeft: Command = ofState (horizontal true false groupLeft)
    let selectGroupRight: Command = ofState (horizontal true true groupRight)
    let selectLineStart: Command = ofState (lineEdge true false)
    let selectLineEnd: Command = ofState (lineEdge true true)
    let selectLineUp: Command = ofState (vertical true (fun _ -> -1))
    let selectLineDown: Command = ofState (vertical true (fun _ -> 1))
    let selectPageUp: Command = paged true -1
    let selectPageDown: Command = paged true 1
    let selectDocStart: Command = ofState (docEdge true false)
    let selectDocEnd: Command = ofState (docEdge true true)

    let selectAll: Command =
        ofState (fun state dispatch ->
            dispatch [ Tx.selection (EditorSelection.single 0 state.Doc.Length); Tx.userEvent "select" ]
            true)

    /// Selects the lines of every range, each with its line break.
    let selectLine: Command =
        ofState (fun state dispatch ->
            let doc = state.Doc

            let ranges =
                state.Selection.Ranges
                |> Array.map (fun range ->
                    let first = Text.lineAt range.From doc
                    let last = Text.lineAt range.To doc

                    let finish =
                        if last.Number < doc.Lines - 1 then
                            (Text.line (last.Number + 1) doc).From
                        else
                            last.To

                    EditorSelection.range first.From finish)

            dispatch [ Tx.selection (EditorSelection.create (List.ofArray ranges) state.Selection.MainIndex); Tx.userEvent "select" ]
            true)

    // ---- Editing ----

    /// Replaces, for every range, the span `plan` gives by its text, and leaves a cursor after it. A span that
    /// overlaps the one before it is left out. False when the state is read-only or no range gives a span.
    let private edit (event: string) (plan: EditorState -> SelectionRange -> struct (int * int * string) voption) : StateCommand =
        fun state dispatch ->
            if EditorState.facet EditorState.readOnly state then
                false
            else
                let ranges = state.Selection.Ranges
                let specs = ResizeArray<ChangeSpec> ()
                let ends = Array.zeroCreate<int> ranges.Length
                let mutable lastTo = -1

                for index in 0 .. ranges.Length - 1 do
                    ends[index] <- ranges[index].To

                    match plan state ranges[index] with
                    | ValueSome (struct (from, to', text)) when from >= lastTo && (to' > from || text.Length > 0) ->
                        specs.Add { From = from; To = to'; Insert = Text.ofString text }
                        ends[index] <- to'
                        lastTo <- to'
                    | _ -> ()

                if specs.Count = 0 then
                    false
                else
                    let changes = ChangeSet.ofSpecs state.Doc (List.ofSeq specs)

                    let cursors =
                        ends
                        |> Array.map (fun finish -> EditorSelection.cursor (ChangeSet.mapPos Assoc.After finish changes))

                    dispatch
                        [
                            Tx.changeSet changes
                            Tx.selection (EditorSelection.create (List.ofArray cursors) state.Selection.MainIndex)
                            Tx.scrollIntoView
                            Tx.userEvent event
                        ]

                    true

    /// Replaces every range by <c>text</c>.
    let insertText (text: string) : Command =
        ofState (edit "input.type" (fun _ range -> ValueSome (struct (range.From, range.To, text))))

    /// Replaces every range by the state's line separator.
    let insertNewline: Command =
        ofState (edit "input" (fun state range -> ValueSome (struct (range.From, range.To, EditorState.facet EditorState.lineSeparator state))))

    /// Replaces every range by a line break and the white space the range's line starts with.
    let insertNewlineKeepIndent: Command =
        ofState (
            edit "input" (fun state range ->
                let line = Text.lineAt range.From state.Doc
                let limit = column line range.From
                let mutable indent = 0

                while indent < limit && (line.Text[indent] = ' ' || line.Text[indent] = '\t') do
                    indent <- indent + 1

                ValueSome (struct (range.From, range.To, EditorState.facet EditorState.lineSeparator state + line.Text.Substring (0, indent))))
        )

    /// Deletes every range, or the character before a cursor.
    let deleteCharBackward: Command =
        ofState (
            edit "delete.backward" (fun state range ->
                if not range.IsEmpty then
                    ValueSome (struct (range.From, range.To, ""))
                else
                    let from = charLeft state.Doc range.Head

                    if from = range.Head then
                        ValueNone
                    else
                        ValueSome (struct (from, range.Head, "")))
        )

    /// Deletes every range, or the character after a cursor.
    let deleteCharForward: Command =
        ofState (
            edit "delete.forward" (fun state range ->
                if not range.IsEmpty then
                    ValueSome (struct (range.From, range.To, ""))
                else
                    let to' = charRight state.Doc range.Head

                    if to' = range.Head then
                        ValueNone
                    else
                        ValueSome (struct (range.Head, to', "")))
        )

    /// Inserts the state's indent unit.
    let insertTab: Command =
        ofState (edit "input" (fun state range -> ValueSome (struct (range.From, range.To, EditorState.facet EditorState.indentUnit state))))

    /// Undoes the document's last history item.
    let undo: Command = Editor.undo

    /// Redoes the history item last undone.
    let redo: Command = Editor.redo
