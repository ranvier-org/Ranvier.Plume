namespace Ranvier.Plume

open System
open Ranvier

/// A disjoint run of hidden lines after a visible header.
[<Struct>]
type internal FoldRun =
    { Header: int
      Last: int
      Row: int }

/// Maps visual rows to document lines and hidden lines to their visible header.
[<Sealed>]
type FoldProjection internal (count: int, runs: FoldRun[]) =
    member _.Count = count
    member _.HasFolds = runs.Length > 0

    member private _.Preceding(value: int, visual: bool) =
        let mutable lo = 0
        let mutable hi = runs.Length
        while lo < hi do
            let mid = lo + (hi - lo) / 2
            let key = if visual then runs[mid].Row else runs[mid].Header
            if key <= value then lo <- mid + 1 else hi <- mid
        lo - 1

    member this.DocumentLine(row: int) =
        let index = this.Preceding(row, true)
        if index < 0 then row
        else
            let run = runs[index]
            if row = run.Row then run.Header else run.Last + (row - run.Row)

    member this.VisualRow(line: int) =
        let index = this.Preceding(line, false)
        if index < 0 then line
        else
            let run = runs[index]
            run.Row + max 0 (line - run.Last)

    member this.IsVisible(line: int) = this.DocumentLine(this.VisualRow line) = line

    member this.IsHeader(line: int) =
        let index = this.Preceding(line, false)
        index >= 0 && runs[index].Header = line

[<RequireQualifiedAccess>]
module Folding =
    // Text.lineAt assigns both code units of a CRLF to the line it terminates.
    type private Bounds = { HeaderEnd: int; FirstHidden: int; LastEnd: int }

    let private contains position range = range.FirstHidden <= position && position <= range.LastEnd

    let private mapRange range changes =
        let mutable touched = false
        ChangeDesc.iterChangedRanges (fun a b _ _ ->
            if a <= range.LastEnd && b > range.HeaderEnd then touched <- true) changes
        if touched then ValueNone
        else
            ValueSome {
                HeaderEnd = ChangeDesc.mapPos Assoc.After range.HeaderEnd changes
                FirstHidden = ChangeDesc.mapPos Assoc.After range.FirstHidden changes
                LastEnd = ChangeDesc.mapPos Assoc.Before range.LastEnd changes
            }

    let private close = StateEffect.define mapRange
    let private openAt = StateEffect.define (fun p changes -> ValueSome (ChangeDesc.mapPos Assoc.After p changes))
    let private field =
        StateField.define {
            Create = fun _ -> []
            Update = fun before tx ->
                let mutable folds = before
                if tx.DocChanged && not folds.IsEmpty then
                    let changes = ChangeSet.desc tx.Changes
                    folds <- folds |> List.choose (fun r -> mapRange r changes |> ValueOption.toOption)
                if not folds.IsEmpty then
                    match tx.Selection with
                    | ValueSome s ->
                        let hidesEndpoint r = s.Ranges |> Array.exists (fun sel -> contains sel.Anchor r || contains sel.Head r)
                        if List.exists hidesEndpoint folds then folds <- List.filter (hidesEndpoint >> not) folds
                    | ValueNone -> ()
                for effect in tx.Effects do
                    match StateEffect.tryValue openAt effect, StateEffect.tryValue close effect with
                    | ValueSome p, _ -> folds <- folds |> List.filter (fun r -> r.HeaderEnd <> p)
                    | _, ValueSome r -> folds <- r :: folds |> List.sortBy (fun bounds -> bounds.HeaderEnd)
                    | _ -> ()
                folds
        }

    /// Keeps collapsed ranges in this editor's state. Editing hidden text opens its fold.
    let extension = StateField.extension field

    let private ranges state = EditorState.tryField field state |> ValueOption.defaultValue []

    /// The visual line layout. Nested folds return when their enclosing fold is opened.
    let projection (state: EditorState) =
        let folds = ranges state
        if folds.IsEmpty then FoldProjection(state.Doc.Lines, Array.empty)
        else
            let runs = ResizeArray<FoldRun>()
            let mutable skipped = 0
            let mutable previous = -1
            for fold in folds do
                let first = (Text.lineAt fold.HeaderEnd state.Doc).Number
                let last = (Text.lineAt fold.LastEnd state.Doc).Number
                if first > previous && last > first then
                    runs.Add { Header = first; Last = last; Row = first - skipped }
                    skipped <- skipped + last - first
                    previous <- last
            FoldProjection(state.Doc.Lines - skipped, runs.ToArray())

    /// Reuses the projection when a transaction leaves its inputs unchanged.
    let updateProjection (before: EditorState) (previous: FoldProjection) (after: EditorState) =
        if obj.ReferenceEquals(ranges before, ranges after) &&
           (obj.ReferenceEquals(before.Doc, after.Doc) || (not previous.HasFolds && before.Doc.Lines = after.Doc.Lines)) then previous
        else projection after

    /// A selection entering a collapsed range opens it. A collapse moves hidden endpoints to the header.
    let fold firstLine lastLine (editor: Editor) =
        let state = Api.untrack (fun () -> editor.State.Value)
        if firstLine < 0 || firstLine >= lastLine || lastLine >= state.Doc.Lines || (EditorState.tryField field state).IsNone then false
        else
            let r =
                { HeaderEnd = (Text.line firstLine state.Doc).To
                  FirstHidden = (Text.line (firstLine + 1) state.Doc).From
                  LastEnd =
                    if lastLine + 1 = state.Doc.Lines then state.Doc.Length
                    else (Text.line (lastLine + 1) state.Doc).From - 1 }
            let incompatible other =
                other.HeaderEnd = r.HeaderEnd
                || (other.HeaderEnd < r.HeaderEnd && r.HeaderEnd <= other.LastEnd && other.LastEnd < r.LastEnd)
                || (r.HeaderEnd < other.HeaderEnd && other.HeaderEnd <= r.LastEnd && r.LastEnd < other.LastEnd)
            if ranges state |> List.exists incompatible then false
            else
                let project p = if contains p r then r.HeaderEnd else p
                let selection = state.Selection
                let specs =
                    [ if selection.Ranges |> Array.exists (fun s -> contains s.Anchor r || contains s.Head r) then
                          let moved = selection.Ranges |> Array.map (fun s -> { s with Anchor = project s.Anchor; Head = project s.Head; GoalColumn = -1 })
                          yield Tx.selection (EditorSelection.create (List.ofArray moved) selection.MainIndex)
                      yield Tx.effect (close.Of r) ]
                editor.Dispatch specs
                true

    /// Opens the fold whose header is on this line.
    let unfold line (editor: Editor) =
        let state = Api.untrack (fun () -> editor.State.Value)
        if line < 0 || line >= state.Doc.Lines then false
        else
            let header = (Text.line line state.Doc).To
            match ranges state |> List.tryFind (fun r -> r.HeaderEnd = header) with
            | Some _ ->
                editor.Dispatch [Tx.effect (openAt.Of header)]
                true
            | None -> false

    /// Opens every fold hiding a document offset, including enclosing folds.
    let reveal position (editor: Editor) =
        let state = Api.untrack (fun () -> editor.State.Value)
        let effects = ranges state |> List.choose (fun r -> if contains position r then Some (Tx.effect (openAt.Of r.HeaderEnd)) else None)
        if effects.IsEmpty then false
        else
            editor.Dispatch effects
            true

