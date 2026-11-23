namespace Ranvier.Plume

open System
open System.Runtime.CompilerServices
open System.Text.RegularExpressions

/// A literal or regular expression with an index per immutable document snapshot.
[<Sealed>]
type SearchQuery internal (scan: string -> RangeSet<unit>) =
#if FABLE_COMPILER
    let mutable cached = ValueNone
    member internal _.Index(doc: Text) =
        match cached with
        | ValueSome (previous, hits) when obj.ReferenceEquals(previous, doc) -> hits
        | _ ->
            let hits = scan (Text.toString doc)
            cached <- ValueSome (doc, hits)
            hits
#else
    let snapshots = ConditionalWeakTable<Text, RangeSet<unit>>()
    let build = ConditionalWeakTable<Text, RangeSet<unit>>.CreateValueCallback(fun doc -> scan (Text.toString doc))
    member internal _.Index(doc: Text) = snapshots.GetValue(doc, build)
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
        SearchQuery(fun text ->
            collect (fun add ->
                if pattern.Length > 0 then
                    let mutable offset = text.IndexOf(pattern, comparison)
                    while offset >= 0 do
                        let finish = offset + pattern.Length
                        add offset finish
                        offset <- text.IndexOf(pattern, finish, comparison)))

    /// A culture-invariant, non-backtracking .NET regex. Zero-length hits are omitted.
    /// Invalid patterns, unsupported constructs and Fable targets return an error.
    let regex (pattern: string) (caseSensitive: bool) : Result<SearchQuery,string> =
#if FABLE_COMPILER
        Error "Regular expression search requires the .NET non-backtracking engine."
#else
        let options = RegexOptions.NonBacktracking ||| RegexOptions.CultureInvariant
        try
            let matcher = Regex(pattern, if caseSensitive then options else options ||| RegexOptions.IgnoreCase)
            Ok (SearchQuery(fun text ->
                collect (fun add ->
                    let mutable hits = matcher.EnumerateMatches(text.AsSpan())
                    while hits.MoveNext() do
                        let hit = hits.Current
                        if hit.Length > 0 then add hit.Index (hit.Index + hit.Length))))
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

    let private index (state: EditorState) =
        match EditorState.tryField query state with
        | ValueSome (ValueSome active) -> active.Index state.Doc
        | _ -> RangeSet.empty

    /// Returns all matches in ascending UTF-16 order. The arrays belong to the caller.
    let matches state =
        let hits = index state
        RangeSet<unit>(Array.copy hits.From, Array.copy hits.To, Array.zeroCreate hits.Count)

    // The match endpoints are strictly increasing because matches are nonempty and disjoint.
    let private cut (positions: int[]) point afterEqual =
        let mutable lo = 0
        let mutable hi = positions.Length
        while lo < hi do
            let mid = lo + (hi - lo) / 2
            if positions[mid] < point || (afterEqual && positions[mid] = point) then
                lo <- mid + 1
            else
                hi <- mid
        lo

    /// Visits matches intersecting the half-open interval, including ones starting before it.
    let between first last visit state =
        if first < last then
            let hits = index state
            let mutable i = cut hits.To first true
            while i < hits.Count && hits.From[i] < last do
                visit hits.From[i] hits.To[i]
                i <- i + 1

    let private navigate forward (editor: Editor) =
        let state = editor.State.Value
        let hits = index state
        if hits.Count = 0 then false
        else
            let selection = state.Selection.Main
            let i =
                if forward then
                    let after = cut hits.From selection.To false
                    if after = hits.Count then 0 else after
                else
                    let before = cut hits.To selection.From true
                    if before = 0 then hits.Count - 1 else before - 1
            editor.Dispatch [Tx.selection (EditorSelection.single hits.From[i] hits.To[i]); Tx.scrollIntoView]
            true

    let next editor = navigate true editor
    let previous editor = navigate false editor

    let private replace all replacement (editor: Editor) =
        let state = editor.State.Value
        if EditorState.facet EditorState.readOnly state then false
        else
            let hits = index state
            let selection = state.Selection.Main
            let selected = cut hits.From selection.From false
            let first, count =
                if all then 0, hits.Count
                elif selected < hits.Count && hits.From[selected] = selection.From && hits.To[selected] = selection.To then selected, 1
                else 0, 0
            if count = 0 then false
            else
                let insert = Text.ofString replacement
                let changes =
                    [ for i in first .. first + count - 1 ->
                        { From = hits.From[i]; To = hits.To[i]; Insert = insert } ]
                editor.Dispatch [Tx.changes changes; Tx.userEvent "input.replace"; Tx.isolateHistory]
                true

    /// Replaces an exactly selected match with literal text.
    let replaceCurrent replacement editor = replace false replacement editor

    /// Replaces all matches in one isolated history transaction.
    let replaceAll replacement editor = replace true replacement editor
