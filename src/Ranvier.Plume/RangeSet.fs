namespace Ranvier.Plume

open System
open System.Collections.Generic

/// Ranges with a value each, sorted by start and then by end. Positions are held in two flat arrays beside the
/// values.
[<Sealed>]
type RangeSet<'T> internal (from: int[], to': int[], values: 'T[]) =
    member _.From = from
    member _.To = to'
    member _.Values = values
    member _.Count = from.Length

[<RequireQualifiedAccess>]
module RangeSet =

    let empty<'T> : RangeSet<'T> = RangeSet<'T> ([||], [||], [||])

    let private ofSorted (ranges: struct (int * int * 'T)[]) : RangeSet<'T> =
        let from = Array.zeroCreate ranges.Length
        let to' = Array.zeroCreate ranges.Length
        let values = Array.zeroCreate ranges.Length

        for index in 0 .. ranges.Length - 1 do
            let struct (start, finish, value) = ranges[index]
            from[index] <- start
            to'[index] <- finish
            values[index] <- value

        RangeSet<'T> (from, to', values)

    let private sorted (ranges: seq<struct (int * int * 'T)>) : struct (int * int * 'T)[] =
        let items = Array.ofSeq ranges

        for struct (start, finish, _) in items do
            if start < 0 || finish < start then
                raise (ArgumentException (String.Concat ("The range [", string start, ", ", string finish, ") is not a range.")))

        // A stable sort: ranges with equal positions keep the order they were given in.
        List.sortWith (fun (struct (a1, a2, _)) (struct (b1, b2, _)) -> if a1 <> b1 then compare a1 b1 else compare a2 b2) (List.ofArray items)
        |> Array.ofList

    /// The set of <c>ranges</c>, each a start, an end and a value.
    let ofRanges (ranges: (int * int * 'T) list) : RangeSet<'T> =
        ranges
        |> Seq.map (fun (start, finish, value) -> struct (start, finish, value))
        |> sorted
        |> ofSorted

    let count (set: RangeSet<'T>) : int = set.Count

    /// Calls <c>f from to value</c> for every range, in order.
    let iter (f: int -> int -> 'T -> unit) (set: RangeSet<'T>) : unit =
        for index in 0 .. set.Count - 1 do
            f set.From[index] set.To[index] set.Values[index]

    /// Calls <c>f from to value</c> for every range that touches [<c>from</c>, <c>to'</c>], in order.
    let between (from: int) (to': int) (f: int -> int -> 'T -> unit) (set: RangeSet<'T>) : unit =
        let mutable index = 0

        while index < set.Count && set.From[index] <= to' do
            if set.To[index] >= from then
                f set.From[index] set.To[index] set.Values[index]

            index <- index + 1

    /// The ranges of <c>set</c> that pass <c>filter</c>, with <c>add</c>.
    let update (add: (int * int * 'T) list) (filter: int -> int -> 'T -> bool) (set: RangeSet<'T>) : RangeSet<'T> =
        seq {
            for index in 0 .. set.Count - 1 do
                if filter set.From[index] set.To[index] set.Values[index] then
                    yield struct (set.From[index], set.To[index], set.Values[index])

            for start, finish, value in add do
                yield struct (start, finish, value)
        }
        |> sorted
        |> ofSorted

    /// The ranges after <c>changes</c>. A range covers the text it covered before: text inserted at either end stays
    /// outside. A range whose text is deleted is dropped; a range without text stays.
    let map (changes: ChangeDesc) (set: RangeSet<'T>) : RangeSet<'T> =
        if set.Count = 0 || ChangeDesc.isEmpty changes then
            set
        else
            let sections = changes.Sections
            // Positions before the first replaced range stay; positions after the last one move by one amount.
            let mutable firstStart = -1
            let mutable lastEnd = 0
            let mutable position = 0
            let mutable i = 0

            while i < sections.Length do
                if sections[i + 1] >= 0 then
                    if firstStart < 0 then
                        firstStart <- position

                    lastEnd <- position + sections[i]

                position <- position + sections[i]
                i <- i + 2

            let delta = ChangeDesc.newLength changes - position

            let inline move (assoc: Assoc) (at: int) =
                if at < firstStart then at
                elif at > lastEnd then at + delta
                else ChangeDesc.mapPos assoc at changes

            let from = Array.zeroCreate set.Count
            let to' = Array.zeroCreate set.Count
            // False when a range was dropped or two ranges changed order: the set is then rebuilt.
            let mutable intact = true

            for index in 0 .. set.Count - 1 do
                let start = set.From[index]
                let finish = set.To[index]

                if start = finish then
                    from[index] <- move Assoc.Before start
                    to'[index] <- from[index]
                else
                    from[index] <- move Assoc.After start
                    to'[index] <- move Assoc.Before finish

                    if to'[index] <= from[index] then
                        from[index] <- -1
                        intact <- false

                if
                    index > 0
                    && (from[index] < from[index - 1]
                        || (from[index] = from[index - 1] && to'[index] < to'[index - 1]))
                then
                    intact <- false

            if intact then
                RangeSet<'T> (from, to', set.Values)
            else
                seq {
                    for index in 0 .. set.Count - 1 do
                        if from[index] >= 0 then
                            yield struct (from[index], to'[index], set.Values[index])
                }
                |> sorted
                |> ofSorted

    /// The ranges of every set of <c>sets</c>.
    let join (sets: RangeSet<'T> list) : RangeSet<'T> =
        seq {
            for set in sets do
                for index in 0 .. set.Count - 1 do
                    yield struct (set.From[index], set.To[index], set.Values[index])
        }
        |> sorted
        |> ofSorted

    /// The ranges held by one of <c>a</c> and <c>b</c> and not the other, as positions, overlapping ranges merged.
    let compare (a: RangeSet<'T>) (b: RangeSet<'T>) : (int * int) list when 'T: equality =
        let comparer = EqualityComparer<'T>.Default

        let missingFrom (other: RangeSet<'T>) (set: RangeSet<'T>) =
            seq {
                for index in 0 .. set.Count - 1 do
                    let mutable found = false
                    let mutable at = 0

                    while not found && at < other.Count && other.From[at] <= set.From[index] do
                        found <-
                            other.From[at] = set.From[index]
                            && other.To[at] = set.To[index]
                            && comparer.Equals (other.Values[at], set.Values[index])

                        at <- at + 1

                    if not found then
                        yield set.From[index], set.To[index]
            }

        let differing = Seq.append (missingFrom b a) (missingFrom a b) |> Seq.sort |> List.ofSeq

        let rec merge (ranges: (int * int) list) =
            match ranges with
            | (from1, to1) :: (from2, to2) :: rest when from2 <= to1 -> merge ((from1, max to1 to2) :: rest)
            | head :: rest -> head :: merge rest
            | [] -> []

        merge differing
