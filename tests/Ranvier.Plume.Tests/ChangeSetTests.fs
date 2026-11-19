module Ranvier.Plume.Tests.ChangeSetTests

open System
open Expecto
open Ranvier.Plume
open Ranvier.Plume.Tests.Helpers

/// Up to four changes to a document of `length` characters, sorted and apart.
let randomSpecs (rng: Random) (length: int) : ChangeSpec list =
    let count = rng.Next (0, 5)
    let cuts = Array.init (count * 2) (fun _ -> rng.Next (0, length + 1)) |> Array.sort

    [
        for index in 0 .. count - 1 do
            {
                From = cuts[index * 2]
                To = cuts[index * 2 + 1]
                Insert = Text.ofString (randomString rng "xy\n" 3)
            }
    ]

let private applyToString (specs: ChangeSpec list) (source: string) : string =
    // The specs are sorted; applied last to first, each leaves the positions of the ones before it alone.
    specs
    |> List.rev
    |> List.fold (fun (text: string) spec -> text.Remove(spec.From, spec.To - spec.From).Insert (spec.From, Text.toString spec.Insert)) source

/// Every replaced range of `changes` holds the text it removes from `doc` and the text it inserts.
let private expectTexts (doc: Text) (changes: ChangeSet) (message: string) =
    let sections = changes.Sections
    let mutable position = 0
    let mutable change = 0

    for index in 0..2 .. sections.Length - 2 do
        let length = sections[index]
        let ins = sections[index + 1]

        if ins >= 0 then
            Expect.equal (Text.toString changes.Removed[change]) (Text.sliceString position (position + length) doc) (message + ": removed text")
            Expect.equal (Text.length changes.Inserted[change]) ins (message + ": inserted length")
            change <- change + 1

        position <- position + length

    Expect.equal change changes.Inserted.Length (message + ": one inserted text per replaced range")
    Expect.equal change changes.Removed.Length (message + ": one removed text per replaced range")

[<Tests>]
let tests =
    testList
        "ChangeSet"
        [
            testCase "a change holds the text it removes"
            <| fun () ->
                let doc = Text.ofString "one\ntwo\nthree"
                let changes = ChangeSet.ofSpecs doc [ { From = 2; To = 6; Insert = Text.ofString "X" }; { From = 8; To = 8; Insert = Text.ofString "Y" } ]
                Expect.equal changes.Removed.Length 2 "one removed text for each range"
                Expect.equal (Text.toString changes.Removed[0]) "e\ntw" "the replaced slice, its line break with it"
                Expect.equal (Text.toString changes.Removed[1]) "" "an insertion removes nothing"
                Expect.equal (Text.toString (ChangeSet.apply doc changes)) "onXo\nYthree" "applied"
                let inverse = ChangeSet.invert changes
                Expect.equal (Text.toString inverse.Removed[0]) "X" "the inverse removes what was inserted"
                Expect.equal (Text.toString inverse.Inserted[0]) "e\ntw" "and inserts what was removed"

            testCase "apply agrees with String"
            <| fun () ->
                forSeeds 3000 (fun rng ->
                    let source = randomString rng "ab\n" 40
                    let doc = Text.ofString source
                    let specs = randomSpecs rng source.Length
                    let changes = ChangeSet.ofSpecs doc specs
                    Expect.equal (ChangeSet.length changes) source.Length "length"
                    let result = ChangeSet.apply doc changes
                    expectText (applyToString specs source) result "apply"
                    Expect.equal (ChangeSet.newLength changes) (Text.length result) "new length"
                    expectTexts doc changes "ofSpecs")

            testCase "invert round-trips"
            <| fun () ->
                forSeeds 3000 (fun rng ->
                    let source = randomString rng "ab\n" 40
                    let doc = Text.ofString source
                    let changes = ChangeSet.ofSpecs doc (randomSpecs rng source.Length)
                    let changed = ChangeSet.apply doc changes
                    let inverse = ChangeSet.invert changes
                    expectTexts changed inverse "inverse"
                    expectText source (ChangeSet.apply changed inverse) "inverse applied")

            testCase "compose equals sequential apply"
            <| fun () ->
                forSeeds 5000 (fun rng ->
                    let source = randomString rng "ab\n" 40
                    let doc = Text.ofString source
                    let a = ChangeSet.ofSpecs doc (randomSpecs rng source.Length)
                    let middle = ChangeSet.apply doc a
                    let b = ChangeSet.ofSpecs middle (randomSpecs rng (Text.length middle))
                    let composed = ChangeSet.compose a b
                    expectText (Text.toString (ChangeSet.apply middle b)) (ChangeSet.apply doc composed) "composed"
                    expectTexts doc composed "composed"

                    Expect.sequenceEqual
                        (ChangeDesc.compose (ChangeSet.desc a) (ChangeSet.desc b)).Sections
                        composed.Sections
                        "the shapes compose as the sets do")

            testCase "map converges"
            <| fun () ->
                forSeeds 5000 (fun rng ->
                    let source = randomString rng "ab\n" 40
                    let doc = Text.ofString source
                    let a = ChangeSet.ofSpecs doc (randomSpecs rng source.Length)
                    let b = ChangeSet.ofSpecs doc (randomSpecs rng source.Length)
                    let afterA = ChangeSet.apply doc a
                    let afterB = ChangeSet.apply doc b
                    let bOverA = ChangeSet.map false (ChangeSet.desc a) b
                    let aOverB = ChangeSet.map true (ChangeSet.desc b) a
                    expectTexts afterA bOverA "b over a"
                    expectTexts afterB aOverB "a over b"
                    let left = ChangeSet.apply afterA bOverA
                    let right = ChangeSet.apply afterB aOverB
                    expectText (Text.toString left) right "both orders give one text"

                    Expect.sequenceEqual
                        (ChangeDesc.map false (ChangeSet.desc a) (ChangeSet.desc b)).Sections
                        bOverA.Sections
                        "the shapes map as the sets do")

            testCase "mapPos follows the text"
            <| fun () ->
                let doc = Text.ofString "0123456789"
                let insertion = ChangeSet.ofSpecs doc [ { From = 4; To = 4; Insert = Text.ofString "ab" } ]
                Expect.equal (ChangeSet.mapPos Assoc.Before 4 insertion) 4 "before an insertion at the position"
                Expect.equal (ChangeSet.mapPos Assoc.After 4 insertion) 6 "after an insertion at the position"
                Expect.equal (ChangeSet.mapPos Assoc.Before 3 insertion) 3 "before the insertion"
                Expect.equal (ChangeSet.mapPos Assoc.Before 5 insertion) 7 "after the insertion"

                let deletion = ChangeSet.desc (ChangeSet.ofSpecs doc [ { From = 2; To = 6; Insert = Text.empty } ])
                Expect.equal (ChangeDesc.mapPos Assoc.After 4 deletion) 2 "inside a deleted range"
                Expect.equal (ChangeDesc.mapPos Assoc.Before 8 deletion) 4 "after a deleted range"
                Expect.equal (ChangeDesc.mapPosMode Assoc.After MapMode.TrackDel 4 deletion) ValueNone "TrackDel inside"
                Expect.equal (ChangeDesc.mapPosMode Assoc.After MapMode.TrackDel 2 deletion) (ValueSome 2) "TrackDel at the start"
                Expect.equal (ChangeDesc.mapPosMode Assoc.After MapMode.TrackBefore 6 deletion) ValueNone "TrackBefore at the end"
                Expect.equal (ChangeDesc.mapPosMode Assoc.After MapMode.TrackAfter 2 deletion) ValueNone "TrackAfter at the start"

                let atStart = ChangeSet.ofSpecs doc [ { From = 0; To = 0; Insert = Text.ofString "x" } ]
                Expect.equal (ChangeSet.mapPos Assoc.Before 0 atStart) 0 "position 0, before"
                Expect.equal (ChangeSet.mapPos Assoc.After 0 atStart) 1 "position 0, after"
                Expect.equal (ChangeSet.mapPos Assoc.After 10 atStart) 11 "the end"

                let atEnd = ChangeSet.ofSpecs doc [ { From = 10; To = 10; Insert = Text.ofString "x" } ]
                Expect.equal (ChangeSet.mapPos Assoc.Before 10 atEnd) 10 "the end, before"
                Expect.equal (ChangeSet.mapPos Assoc.After 10 atEnd) 11 "the end, after"
                Expect.throwsT<ArgumentOutOfRangeException> (fun () -> ChangeSet.mapPos Assoc.After 11 atEnd |> ignore) "beyond the end"

            testCase "mapPos agrees with a mapped insertion"
            <| fun () ->
                forSeeds 2000 (fun rng ->
                    let source = randomString rng "ab" 30
                    let doc = Text.ofString source
                    let changes = ChangeSet.ofSpecs doc (randomSpecs rng source.Length)
                    let position = rng.Next (0, source.Length + 1)
                    let marker = ChangeSet.ofSpecs doc [ { From = position; To = position; Insert = Text.ofString "|" } ]
                    // An insertion moved over the change lands where its position maps, after text inserted there.
                    let moved = ChangeSet.map false (ChangeSet.desc changes) marker
                    let mutable landed = -1
                    moved |> ChangeSet.iterChanges (fun _ _ fromB _ _ -> landed <- fromB)
                    Expect.equal landed (ChangeSet.mapPos Assoc.After position changes) "mapped position")

            testCase "ofSpecs validates"
            <| fun () ->
                let doc = Text.ofString "0123456789"

                Expect.throwsT<ArgumentException>
                    (fun () ->
                        ChangeSet.ofSpecs doc [ { From = 0; To = 3; Insert = Text.empty }; { From = 2; To = 4; Insert = Text.empty } ]
                        |> ignore)
                    "overlap"

                Expect.throwsT<ArgumentException> (fun () -> ChangeSet.ofSpecs doc [ { From = 8; To = 11; Insert = Text.empty } ] |> ignore) "range"
                Expect.throwsT<ArgumentException> (fun () -> ChangeSet.ofSpecs doc [ { From = 5; To = 4; Insert = Text.empty } ] |> ignore) "reversed"
                Expect.throwsT<ArgumentException> (fun () -> ChangeSet.apply (Text.ofString "abc") (ChangeSet.empty 5) |> ignore) "length"

                let unordered =
                    ChangeSet.ofSpecs doc [ { From = 6; To = 7; Insert = Text.ofString "B" }; { From = 1; To = 2; Insert = Text.ofString "A" } ]

                expectText "0A2345B789" (ChangeSet.apply doc unordered) "any order"
                Expect.isTrue (ChangeSet.isEmpty (ChangeSet.ofSpecs doc [])) "no spec"
                Expect.isTrue (ChangeSet.isEmpty (ChangeSet.ofSpecs doc [ { From = 3; To = 3; Insert = Text.empty } ])) "no-op spec"

            testCase "changed ranges"
            <| fun () ->
                let doc = Text.ofString "0123456789"

                let changes =
                    ChangeSet.ofSpecs doc [ { From = 1; To = 3; Insert = Text.ofString "x" }; { From = 6; To = 6; Insert = Text.ofString "yz" } ]

                let ranges = ResizeArray ()
                ChangeDesc.iterChangedRanges (fun fromA toA fromB toB -> ranges.Add (fromA, toA, fromB, toB)) (ChangeSet.desc changes)
                Expect.sequenceEqual ranges [ (1, 3, 1, 2); (6, 6, 5, 7) ] "ranges"
                Expect.isTrue (ChangeDesc.touchesRange 2 2 (ChangeSet.desc changes)) "touches 2"
                Expect.isFalse (ChangeDesc.touchesRange 4 5 (ChangeSet.desc changes)) "between the changes"
                let inverse = ChangeDesc.invert (ChangeSet.desc changes)
                Expect.equal (ChangeDesc.length inverse) (ChangeSet.newLength changes) "inverse length"
                Expect.equal (ChangeDesc.newLength inverse) 10 "inverse new length"

            testCase "line edits round-trip"
            <| fun () ->
                let doc = Text.ofString "first\nsecond\r\nthird"

                let edits =
                    [
                        { StartLine = 0; StartCharacter = 2; EndLine = 1; EndCharacter = 3; NewText = "X\nY" }
                        { StartLine = 2; StartCharacter = 5; EndLine = 2; EndCharacter = 5; NewText = "!" }
                    ]

                let changes = ChangeSet.ofLineEdits doc edits
                expectText "fiX\nYond\r\nthird!" (ChangeSet.apply doc changes) "applied"
                Expect.equal (ChangeSet.toLineEdits doc changes) edits "round trip"
        ]
