module Ranvier.Plume.Tests.SelectionTests

open System
open Expecto
open Ranvier.Plume
open Ranvier.Plume.Tests.Helpers

let private doc = Text.ofString "0123456789abcdefghij"

let private change (from: int) (to': int) (insert: string) : ChangeDesc =
    ChangeSet.desc (ChangeSet.ofSpecs doc [ { From = from; To = to'; Insert = Text.ofString insert } ])

let private spans (selection: EditorSelection) =
    selection.Ranges |> Array.map (fun range -> range.Anchor, range.Head) |> List.ofArray

[<Tests>]
let selectionTests =
    testList
        "EditorSelection"
        [
            testCase "create sorts and merges overlapping ranges"
            <| fun () ->
                let selection = EditorSelection.create [ EditorSelection.range 3 8; EditorSelection.range 0 5 ] 0
                Expect.equal (spans selection) [ (0, 8) ] "merged"
                Expect.equal selection.MainIndex 0 "main"

                let apart = EditorSelection.create [ EditorSelection.range 12 10; EditorSelection.cursor 2; EditorSelection.range 4 6 ] 0
                Expect.equal (spans apart) [ (2, 2); (4, 6); (12, 10) ] "sorted"
                Expect.equal apart.MainIndex 2 "the main range keeps its identity"
                Expect.equal apart.Main.From 10 "from"
                Expect.equal apart.Main.To 12 "to"

                // Ranges that touch stay apart; a cursor at a range's end joins it.
                Expect.equal (spans (EditorSelection.create [ EditorSelection.range 0 4; EditorSelection.range 4 8 ] 0)) [ (0, 4); (4, 8) ] "touching"
                Expect.equal (spans (EditorSelection.create [ EditorSelection.range 0 4; EditorSelection.cursor 4 ] 1)) [ (0, 4) ] "cursor at the end"
                Expect.throwsT<ArgumentException> (fun () -> EditorSelection.create [] 0 |> ignore) "empty"

            testCase "map follows changes"
            <| fun () ->
                let cursor = EditorSelection.single 10 10
                Expect.equal (spans (EditorSelection.map (change 2 2 "xx") cursor)) [ (12, 12) ] "insertion before"
                Expect.equal (spans (EditorSelection.map (change 10 10 "xx") cursor)) [ (10, 10) ] "insertion at the cursor"
                Expect.equal (spans (EditorSelection.map (change 15 15 "xx") cursor)) [ (10, 10) ] "insertion after"
                Expect.equal (spans (EditorSelection.map (change 8 14 "") cursor)) [ (8, 8) ] "deletion across"

                let range = EditorSelection.single 12 6
                Expect.equal (spans (EditorSelection.map (change 6 6 "xx") range)) [ (14, 8) ] "insertion at the start stays outside"
                Expect.equal (spans (EditorSelection.map (change 12 12 "xx") range)) [ (12, 6) ] "insertion at the end stays outside"
                Expect.equal (spans (EditorSelection.map (change 4 14 "") range)) [ (4, 4) ] "deleted range collapses"
                Expect.isTrue (obj.ReferenceEquals (EditorSelection.map (change 15 15 "x") range, range)) "an unmoved selection is the same value"

            testCase "map merges ranges that meet"
            <| fun () ->
                let selection = EditorSelection.create [ EditorSelection.range 2 4; EditorSelection.cursor 8 ] 1
                let mapped = EditorSelection.map (change 3 9 "") selection
                Expect.equal (spans mapped) [ (2, 3) ] "merged"
                Expect.equal mapped.MainIndex 0 "main"

            testCase "clamp and asSingle"
            <| fun () ->
                Expect.equal (spans (EditorSelection.clamp 3 (EditorSelection.single 9 9))) [ (3, 3) ] "cursor"
                Expect.equal (spans (EditorSelection.clamp 5 (EditorSelection.single 2 9))) [ (2, 5) ] "range"
                let inside = EditorSelection.single 2 4
                Expect.isTrue (obj.ReferenceEquals (EditorSelection.clamp 5 inside, inside)) "inside"

                let two = EditorSelection.create [ EditorSelection.cursor 1; EditorSelection.cursor 5 ] 1
                Expect.equal (spans (EditorSelection.asSingle two)) [ (5, 5) ] "main alone"

            testCase "map at the ends of the document"
            <| fun () ->
                let atEnd = EditorSelection.single 20 20
                Expect.equal (spans (EditorSelection.map (change 20 20 "x") atEnd)) [ (20, 20) ] "insertion at the end"
                Expect.equal (spans (EditorSelection.map (change 0 20 "") atEnd)) [ (0, 0) ] "everything deleted"
        ]

let private ranges (set: RangeSet<string>) =
    let items = ResizeArray ()
    RangeSet.iter (fun from to' value -> items.Add (from, to', value)) set
    List.ofSeq items

[<Tests>]
let rangeSetTests =
    testList
        "RangeSet"
        [
            testCase "ofRanges sorts"
            <| fun () ->
                let set = RangeSet.ofRanges [ (5, 9, "b"); (1, 3, "a"); (5, 6, "c") ]
                Expect.equal (ranges set) [ (1, 3, "a"); (5, 6, "c"); (5, 9, "b") ] "sorted"
                Expect.equal (RangeSet.count set) 3 "count"
                Expect.equal (RangeSet.count RangeSet.empty<string>) 0 "empty"
                Expect.throwsT<ArgumentException> (fun () -> RangeSet.ofRanges [ (4, 2, "x") ] |> ignore) "reversed"

            testCase "between visits the ranges that touch"
            <| fun () ->
                let set = RangeSet.ofRanges [ (0, 2, "a"); (4, 6, "b"); (9, 12, "c"); (20, 22, "d") ]
                let seen = ResizeArray ()
                RangeSet.between 5 10 (fun _ _ value -> seen.Add value) set
                Expect.sequenceEqual seen [ "b"; "c" ] "between"

            testCase "update adds and filters"
            <| fun () ->
                let set = RangeSet.ofRanges [ (0, 2, "a"); (4, 6, "b") ]
                let updated = RangeSet.update [ (3, 4, "x"); (0, 1, "y") ] (fun _ _ value -> value <> "a") set
                Expect.equal (ranges updated) [ (0, 1, "y"); (3, 4, "x"); (4, 6, "b") ] "updated"

            testCase "map moves, shrinks and drops"
            <| fun () ->
                let set = RangeSet.ofRanges [ (2, 4, "a"); (6, 8, "b"); (10, 10, "point"); (12, 16, "c") ]
                Expect.equal (ranges (RangeSet.map (change 0 0 "xx") set)) [ (4, 6, "a"); (8, 10, "b"); (12, 12, "point"); (14, 18, "c") ] "insertion before"
                Expect.equal (ranges (RangeSet.map (change 5 9 "") set)) [ (2, 4, "a"); (6, 6, "point"); (8, 12, "c") ] "a range inside a deletion is dropped"
                Expect.equal (ranges (RangeSet.map (change 13 15 "") set)) [ (2, 4, "a"); (6, 8, "b"); (10, 10, "point"); (12, 14, "c") ] "a deletion inside shrinks"
                Expect.equal (ranges (RangeSet.map (change 2 2 "x") set) |> List.head) (3, 5, "a") "an insertion at the start stays outside"
                Expect.equal (ranges (RangeSet.map (change 4 4 "x") set) |> List.head) (2, 4, "a") "an insertion at the end stays outside"
                Expect.isTrue (obj.ReferenceEquals (RangeSet.map (ChangeSet.desc (ChangeSet.empty 20)) set, set)) "no change"

            testCase "map agrees with mapPos"
            <| fun () ->
                forSeeds 2000 (fun rng ->
                    let source = randomString rng "ab" 40
                    let text = Text.ofString source
                    let changes = ChangeSet.desc (ChangeSet.ofSpecs text (ChangeSetTests.randomSpecs rng source.Length))

                    let items =
                        [
                            for index in 0 .. rng.Next 6 do
                                let from = rng.Next (0, source.Length + 1)
                                from, rng.Next (from, source.Length + 1), index
                        ]

                    let expected =
                        items
                        |> List.choose (fun (from, to', value) ->
                            if from = to' then
                                let moved = ChangeDesc.mapPos Assoc.Before from changes
                                Some (moved, moved, value)
                            else
                                let start = ChangeDesc.mapPos Assoc.After from changes
                                let finish = ChangeDesc.mapPos Assoc.Before to' changes
                                if finish > start then Some (start, finish, value) else None)
                        |> List.sortBy (fun (from, to', value) -> from, to', value)

                    let mapped = RangeSet.map changes (RangeSet.ofRanges items)
                    let actual = ResizeArray ()
                    RangeSet.iter (fun from to' value -> actual.Add (from, to', value)) mapped
                    Expect.equal (actual |> List.ofSeq |> List.sortBy (fun (from, to', value) -> from, to', value)) expected "mapped ranges"

                    for index in 1 .. mapped.Count - 1 do
                        Expect.isTrue
                            (mapped.From[index - 1] < mapped.From[index]
                             || (mapped.From[index - 1] = mapped.From[index] && mapped.To[index - 1] <= mapped.To[index]))
                            "sorted")

            testCase "join and compare"
            <| fun () ->
                let a = RangeSet.ofRanges [ (0, 2, "a"); (8, 9, "c") ]
                let b = RangeSet.ofRanges [ (4, 6, "b") ]
                Expect.equal (ranges (RangeSet.join [ a; b ])) [ (0, 2, "a"); (4, 6, "b"); (8, 9, "c") ] "join"
                Expect.equal (RangeSet.compare a a) [] "equal sets"
                let changed = RangeSet.ofRanges [ (0, 2, "a"); (8, 9, "z"); (20, 30, "new") ]
                Expect.equal (RangeSet.compare a changed) [ (8, 9); (20, 30) ] "differing ranges"
        ]
