namespace Ranvier.Plume

open System.Collections.Generic
open Ranvier

/// Typed matching and paired entry for parentheses, square brackets and braces.
[<RequireQualifiedAccess>]
module Brackets =
    let private partner ch =
        match ch with
        | '(' -> ')' | '[' -> ']' | '{' -> '}'
        | ')' -> '(' | ']' -> '[' | '}' -> '{'
        | _ -> '\000'

    let private opening ch = ch = '(' || ch = '[' || ch = '{'

    let private charAt (doc: Text) offset =
        if offset < 0 || offset >= doc.Length then '\000'
        else
            let line = Text.lineAt offset doc
            if offset < line.To then line.Text[offset - line.From] else '\000'

    /// Searches at most distance UTF-16 units from the bracket immediately before the
    /// head, or the bracket at the head when the preceding unit is ordinary text.
    /// A mismatched intermediate closer invalidates the entire candidate.
    let matchWithin distance (doc: Text) head =
        if head < 0 || head > doc.Length || distance <= 0 then ValueNone
        else
            let before = charAt doc (head - 1)
            let struct (start, ch) =
                if partner before <> '\000' then struct (head - 1, before)
                else struct (head, charAt doc head)
            if partner ch = '\000' then ValueNone
            else
                let forward = opening ch
                let step = if forward then 1 else -1
                let expected = Stack<char>()
                expected.Push(partner ch)
                let mutable at = start
                let mutable remaining = min distance (if forward then doc.Length - start - 1 else start)
                let mutable result = ValueNone
                while remaining > 0 && expected.Count > 0 do
                    let count = min remaining 256
                    let first = if forward then at + 1 else at - count
                    let block = Text.sliceString first (first + count) doc
                    let mutable i = if forward then 0 else count - 1
                    let stop = if forward then count else -1
                    while i <> stop && expected.Count > 0 do
                        let c = block[i]
                        let other = partner c
                        if other <> '\000' then
                            if opening c = forward then expected.Push(other)
                            elif expected.Pop() <> c then expected.Clear()
                            elif expected.Count = 0 then
                                let found = first + i
                                result <- ValueSome(struct (min start found, max start found))
                        i <- i + step
                    at <- at + step * count
                    remaining <- remaining - count
                result

    /// Matches raw delimiter characters within 10,000 UTF-16 units of the bracket.
    let matchAt doc head = matchWithin 10000 doc head

    let private mapUnit offset changes =
        ChangeDesc.mapPosMode Assoc.After MapMode.TrackAfter offset changes

    let private mapPair (struct (left, right)) changes =
        match mapUnit left changes, mapUnit right changes with
        | ValueSome a, ValueSome b -> ValueSome(struct (a,b))
        | _ -> ValueNone

    let private added = StateEffect.define mapPair
    let private skipped = StateEffect.define mapUnit
    let private pairs =
        StateField.define {
            Create = fun _ -> []
            Update = fun before tx ->
                let mutable live = before
                if not live.IsEmpty && tx.DocChanged then
                    let changes = ChangeSet.desc tx.Changes
                    live <- live |> List.choose (fun pair -> mapPair pair changes |> ValueOption.toOption)
                for effect in tx.Effects do
                    match StateEffect.tryValue added effect, StateEffect.tryValue skipped effect with
                    | ValueSome pair, _ -> live <- pair :: live
                    | _, ValueSome at -> live <- live |> List.filter (fun struct (_,right) -> right <> at)
                    | _ -> ()
                match tx.Selection with
                | ValueSome selection when not live.IsEmpty ->
                    live |> List.filter (fun struct (left,right) ->
                        selection.Ranges |> Array.exists (fun r -> left < r.Head && r.Head <= right))
                | _ -> live
        }

    /// Enables pairing for individually typed brackets. Paste and composition commit remain literal.
    let autoClose = StateField.extension pairs

    /// Types one native text entry at every selection. An opener wraps its selection,
    /// keeping its direction. A closer moves over its generated mate while a
    /// cursor has remained inside the pair. Ordinary closing characters are typed literally.
    let typeText (value: string) (editor: Editor) =
        if value.Length <> 1 || partner value[0] = '\000' then
            Commands.insertText value editor
        else
            let state = Api.untrack (fun () -> editor.State.Value)
            match EditorState.tryField pairs state with
            | ValueNone -> Commands.insertText value editor
            | ValueSome _ when EditorState.facet EditorState.readOnly state -> false
            | ValueSome live ->
                let ch = value[0]
                let typed = Text.ofString value
                let specs = ResizeArray<ChangeSpec>()
                let effects = ResizeArray<TxSpec>()
                let ranges = ResizeArray<SelectionRange>()
                let mutable shift = 0
                if opening ch then
                    let close = Text.ofString (string (partner ch))
                    for r in state.Selection.Ranges do
                        specs.Add { From = r.From; To = r.From; Insert = typed }
                        specs.Add { From = r.To; To = r.To; Insert = close }
                        effects.Add (Tx.effect (added.Of(struct (r.From + shift, r.To + shift + 1))))
                        ranges.Add (EditorSelection.range (r.Anchor + shift + 1) (r.Head + shift + 1))
                        shift <- shift + 2
                else
                    for r in state.Selection.Ranges do
                        ranges.Add (EditorSelection.cursor (r.From + shift + 1))
                        if r.IsEmpty && (live |> List.exists (fun struct (_,right) -> right = r.Head)) && charAt state.Doc r.Head = ch then
                            effects.Add (Tx.effect (skipped.Of(r.Head + shift)))
                        else
                            specs.Add { From = r.From; To = r.To; Insert = typed }
                            shift <- shift + 1 - (r.To - r.From)
                let selection = EditorSelection.create (List.ofSeq ranges) state.Selection.MainIndex
                editor.Dispatch [
                    if specs.Count > 0 then Tx.changeSet (ChangeSet.ofSpecs state.Doc (List.ofSeq specs))
                    Tx.selection selection
                    yield! effects
                    Tx.scrollIntoView
                    if specs.Count > 0 then Tx.userEvent "input.type"
                ]
                true
