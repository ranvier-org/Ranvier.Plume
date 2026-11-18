namespace Ranvier.Plume

open System

/// A selected range. The head is the end that moves when the selection is extended; a cursor has both ends at one
/// position.
[<Struct>]
type SelectionRange =
    {
        Anchor: int
        Head: int
        /// The column vertical motion aims for, or -1.
        GoalColumn: int
        /// The side of its position a cursor belongs to.
        Assoc: Assoc
    }

    member this.From = min this.Anchor this.Head
    member this.To = max this.Anchor this.Head
    member this.IsEmpty = this.Anchor = this.Head

/// One or more selected ranges, sorted and apart, one of them the main range.
type EditorSelection =
    {
        Ranges: SelectionRange[]
        MainIndex: int
    }

    member this.Main = this.Ranges[this.MainIndex]

[<RequireQualifiedAccess>]
module EditorSelection =

    let cursor (position: int) : SelectionRange =
        {
            Anchor = position
            Head = position
            GoalColumn = -1
            Assoc = Assoc.Before
        }

    let range (anchor: int) (head: int) : SelectionRange =
        {
            Anchor = anchor
            Head = head
            GoalColumn = -1
            Assoc = Assoc.Before
        }

    /// A selection of one range.
    let single (anchor: int) (head: int) : EditorSelection =
        {
            Ranges = [| range anchor head |]
            MainIndex = 0
        }

    /// A selection of <c>ranges</c>, sorted, with overlapping ranges merged. The main range is the one that holds
    /// the range at <c>mainIndex</c>.
    /// <exception cref="T:System.ArgumentException"><c>ranges</c> is empty or <c>mainIndex</c> is outside it.</exception>
    let create (ranges: SelectionRange list) (mainIndex: int) : EditorSelection =
        if List.isEmpty ranges then
            raise (ArgumentException ("A selection holds at least one range.", "ranges"))

        if mainIndex < 0 || mainIndex >= List.length ranges then
            raise (ArgumentException ("The main index is outside the ranges.", "mainIndex"))

        let main = List.item mainIndex ranges
        let sorted = ranges |> List.sortBy (fun range -> range.From) |> Array.ofList
        let merged = ResizeArray<SelectionRange> sorted.Length
        let mutable mainAt = 0
        let mutable mainSeen = false

        for range in sorted do
            let isMain = not mainSeen && range = main

            if isMain then
                mainSeen <- true

            let joins =
                merged.Count > 0
                && (let previous = merged[merged.Count - 1]

                    if range.IsEmpty then
                        range.From <= previous.To
                    else
                        range.From < previous.To)

            if joins then
                let previous = merged[merged.Count - 1]
                let from = previous.From
                let to' = max previous.To range.To

                merged[merged.Count - 1] <-
                    if range.Anchor > range.Head then
                        { range with Anchor = to'; Head = from; GoalColumn = -1 }
                    else
                        { range with Anchor = from; Head = to'; GoalColumn = -1 }
            else
                merged.Add range

            if isMain then
                mainAt <- merged.Count - 1

        {
            Ranges = merged.ToArray ()
            MainIndex = mainAt
        }

    let private mapRange (changes: ChangeDesc) (range: SelectionRange) : SelectionRange =
        if range.IsEmpty then
            let position = ChangeDesc.mapPos Assoc.Before range.Head changes

            if position = range.Head then
                range
            else
                { range with Anchor = position; Head = position; GoalColumn = -1 }
        else
            let from = ChangeDesc.mapPos Assoc.After range.From changes
            let to' = max from (ChangeDesc.mapPos Assoc.Before range.To changes)

            if from = range.From && to' = range.To then
                range
            elif range.Anchor > range.Head then
                { range with Anchor = to'; Head = from; GoalColumn = -1 }
            else
                { range with Anchor = from; Head = to'; GoalColumn = -1 }

    /// The selection after <c>changes</c>. A cursor stays before text inserted at it; a range keeps the text it
    /// covered.
    let map (changes: ChangeDesc) (selection: EditorSelection) : EditorSelection =
        if ChangeDesc.isEmpty changes then
            selection
        elif selection.Ranges.Length = 1 then
            let mapped = mapRange changes selection.Ranges[0]

            if mapped = selection.Ranges[0] then
                selection
            else
                { Ranges = [| mapped |]; MainIndex = 0 }
        else
            create (selection.Ranges |> Array.map (mapRange changes) |> List.ofArray) selection.MainIndex

    /// The selection with every position inside [0, <c>length</c>].
    let clamp (length: int) (selection: EditorSelection) : EditorSelection =
        let inside (position: int) = max 0 (min length position)

        if
            selection.Ranges
            |> Array.forall (fun range -> range.From >= 0 && range.To <= length)
        then
            selection
        else
            create
                (selection.Ranges
                 |> Array.map (fun range ->
                     { range with
                         Anchor = inside range.Anchor
                         Head = inside range.Head
                         GoalColumn = -1
                     })
                 |> List.ofArray)
                selection.MainIndex

    /// The main range alone.
    let asSingle (selection: EditorSelection) : EditorSelection =
        if selection.Ranges.Length = 1 then
            selection
        else
            { Ranges = [| selection.Main |]; MainIndex = 0 }
