namespace Ranvier.Plume

open System
open System.Runtime.CompilerServices
open System.Text.RegularExpressions

[<NoEquality; NoComparison>]
type internal MatchIndex =
    | Hits of RangeSet<unit>
    | Join of width: int * count: int * left: MatchIndex * right: MatchIndex

    member this.Count =
        match this with
        | Hits hits -> hits.Count
        | Join (_, count, _, _) -> count

    member this.At(rank, offset) : struct (int * int) =
        match this with
        | Hits hits -> struct (offset + hits.From[rank], offset + hits.To[rank])
        | Join (width, _, left, right) ->
            if rank < left.Count then left.At(rank, offset)
            else right.At(rank - left.Count, offset + width)

    member this.Cut(point, ends, afterEqual) =
        match this with
        | Join (width, _, left, right) ->
            if point < width || (point = width && not afterEqual) then left.Cut(point, ends, afterEqual)
            else left.Count + right.Cut(point - width, ends, afterEqual)
        | Hits hits ->
            let positions = if ends then hits.To else hits.From
            let mutable lo = 0
            let mutable hi = positions.Length
            while lo < hi do
                let mid = lo + (hi - lo) / 2
                if positions[mid] < point || (afterEqual && positions[mid] = point) then lo <- mid + 1
                else hi <- mid
            lo

/// A literal or regular expression indexed in UTF-16 document coordinates.
[<Sealed>]
type SearchQuery internal (scan: string -> RangeSet<unit>, leafLocal: bool) =
#if FABLE_COMPILER
    let mutable cached = ValueNone
    member internal _.Index(doc: Text) =
        match cached with
        | ValueSome (previous, hits) when obj.ReferenceEquals(previous, doc) -> hits
        | _ ->
            let hits = Hits (scan (Text.toString doc))
            cached <- ValueSome (doc, hits)
            hits
#else
    let nodes = ConditionalWeakTable<Node, MatchIndex>()
    let rec index node =
        match nodes.TryGetValue node with
        | true, hits -> hits
        | _ ->
            let hits =
                match node with
                | Branch b when leafLocal && b.LineCount > Rope.ChunkLines * 4 ->
                    let left, right = index b.Left, index b.Right
                    Join (b.Left.CharLength, left.Count + right.Count, left, right)
                | _ -> Hits (scan (Rope.getText node))
            nodes.GetValue(node, fun _ -> hits)
    member internal _.Index(doc: Text) = index doc.Root
#endif

[<RequireQualifiedAccess>]
module Search =
    let private collect scan =
        let starts = ResizeArray<int>()
        let ends = ResizeArray<int>()
        scan (fun first last ->
            starts.Add first
            ends.Add last)
        RangeSet<unit>(starts.ToArray(), ends.ToArray(), Array.zeroCreate starts.Count)

    /// Non-overlapping literal matches, compared ordinally. An empty query has no matches.
    let literal (pattern: string) caseSensitive =
        if isNull pattern then nullArg "pattern"
        let comparison = if caseSensitive then StringComparison.Ordinal else StringComparison.OrdinalIgnoreCase
        SearchQuery((fun text ->
            collect (fun add ->
                if pattern.Length > 0 then
                    let mutable offset = text.IndexOf(pattern, comparison)
                    while offset >= 0 do
                        let finish = offset + pattern.Length
                        add offset finish
                        offset <- text.IndexOf(pattern, finish, comparison))), pattern.IndexOfAny(Rope.breakChars) < 0)

    /// A culture-invariant, non-backtracking .NET regex. Zero-length hits are omitted.
    /// Invalid patterns, unsupported constructs and Fable targets return an error.
    let regex (pattern: string) (caseSensitive: bool) : Result<SearchQuery,string> =
#if FABLE_COMPILER
        Error "Regular expression search requires the .NET non-backtracking engine."
#else
        let options = RegexOptions.NonBacktracking ||| RegexOptions.CultureInvariant
        try
            let matcher = Regex(pattern, if caseSensitive then options else options ||| RegexOptions.IgnoreCase)
            Ok (SearchQuery((fun text ->
                collect (fun add ->
                    let mutable hits = matcher.EnumerateMatches(text.AsSpan())
                    while hits.MoveNext() do
                        let hit = hits.Current
                        if hit.Length > 0 then add hit.Index (hit.Index + hit.Length))), false))
        with
        | :? ArgumentException as error -> Error error.Message
        | :? NotSupportedException as error -> Error error.Message
#endif

    let private changeQuery = StateEffect.defineSimple<SearchQuery voption>()
    let private query =
        StateField.define {
            Create = fun _ -> ValueNone
            Update = fun previous tx ->
                let mutable current = previous
                for effect in tx.Effects do
                    match StateEffect.tryValue changeQuery effect with
                    | ValueSome value -> current <- value
                    | ValueNone -> ()
                current
        }

    let extension = StateField.extension query

    /// Sets or clears the query in this editor. Requires the search extension.
    let setQuery value (editor: Editor) =
        if (EditorState.tryField query editor.State.Value).IsSome then
            editor.Dispatch [Tx.effect (changeQuery.Of value)]
            true
        else false

    let private empty = Hits RangeSet.empty

    let private index (state: EditorState) =
        match EditorState.tryField query state with
        | ValueSome (ValueSome active) -> active.Index state.Doc
        | _ -> empty

    /// Returns all matches in ascending UTF-16 order. The arrays belong to the caller.
    let matches state =
        let hits = index state
        let starts, ends = Array.zeroCreate hits.Count, Array.zeroCreate hits.Count
        for i in 0 .. hits.Count - 1 do
            let struct (a, b) = hits.At(i, 0)
            starts[i] <- a
            ends[i] <- b
        RangeSet<unit>(starts, ends, Array.zeroCreate hits.Count)

    /// Visits matches intersecting the half-open interval, including ones starting before it.
    let between first last visit state =
        if first < last then
            let hits = index state
            let finish = hits.Cut(last, false, false)
            for i in hits.Cut(first, true, true) .. finish - 1 do
                let struct (a, b) = hits.At(i, 0)
                visit a b

    let private navigate forward (editor: Editor) =
        let state = editor.State.Value
        let hits = index state
        if hits.Count = 0 then false
        else
            let selection = state.Selection.Main
            let i =
                if forward then
                    let after = hits.Cut(selection.To, false, false)
                    if after = hits.Count then 0 else after
                else
                    let before = hits.Cut(selection.From, true, true)
                    if before = 0 then hits.Count - 1 else before - 1
            let struct (a, b) = hits.At(i, 0)
            editor.Dispatch [Tx.selection (EditorSelection.single a b); Tx.scrollIntoView]
            true

    let next editor = navigate true editor
    let previous editor = navigate false editor

    let private replace all replacement (editor: Editor) =
        let state = editor.State.Value
        if EditorState.facet EditorState.readOnly state then false
        else
            let hits = index state
            let selection = state.Selection.Main
            let selected = hits.Cut(selection.From, false, false)
            let first, count =
                if all then 0, hits.Count
                elif selected < hits.Count && hits.At(selected, 0) = struct (selection.From, selection.To) then selected, 1
                else 0, 0
            if count = 0 then false
            else
                let insert = Text.ofString replacement
                let changes =
                    [ for i in first .. first + count - 1 do
                        let struct (a, b) = hits.At(i, 0)
                        yield { From = a; To = b; Insert = insert } ]
                editor.Dispatch [Tx.changes changes; Tx.userEvent "input.replace"; Tx.isolateHistory]
                true

    /// Replaces an exactly selected match with literal text.
    let replaceCurrent replacement editor = replace false replacement editor

    /// Replaces all matches in one isolated history transaction.
    let replaceAll replacement editor = replace true replacement editor
