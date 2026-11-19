/// Properties run by FsCheck over texts with every kind of line break and with characters outside ASCII.
module Ranvier.Plume.Tests.PropertyTests

open System
open Expecto
open FsCheck
open Ranvier.Plume
open Ranvier.Plume.Tests.Helpers

/// What a generated text is made of: ASCII, each line break, a two-unit line break, a letter outside ASCII, a
/// CJK character and an astral character of two UTF-16 units.
let private pieces = [| "a"; "b"; " "; "\n"; "\r"; "\r\n"; "é"; "漢"; "😀" |]

/// A text as its pieces. A position between two pieces splits no surrogate pair, and one between "\r" and "\n"
/// splits a line break.
let private genPieces (most: int) : Gen<string list> =
    gen {
        let! count = Gen.choose (0, most)
        return! Gen.listOfLength count (Gen.elements pieces)
    }

/// The offsets between the pieces of a text, the start and the end among them.
let private offsetsOf (parts: string list) : int[] =
    parts |> List.scan (fun at (part: string) -> at + part.Length) 0 |> Array.ofList

/// A text, a range of it between pieces, and a text to put there.
type Replacement =
    {
        Source: string
        From: int
        To: int
        Insert: string
    }

/// A text and up to four ranges of it, in order and apart, each with a text to put there.
type Changes =
    {
        Doc: string
        Ranges: (int * int * string) list
    }

/// A text and a run of edits to it. An edit names its ends as thousandths of the way through the positions the
/// text then has between its characters.
type Edits =
    {
        Start: string
        Steps: (int * int * string) list
    }

let private genReplacement: Gen<Replacement> =
    gen {
        let! parts = genPieces 40
        let! insert = genPieces 8
        let offsets = offsetsOf parts
        let! first = Gen.choose (0, offsets.Length - 1)
        let! last = Gen.choose (first, offsets.Length - 1)

        return
            {
                Source = String.concat "" parts
                From = offsets[first]
                To = offsets[last]
                Insert = String.concat "" insert
            }
    }

let private genChanges: Gen<Changes> =
    gen {
        let! parts = genPieces 40
        let offsets = offsetsOf parts
        let! count = Gen.choose (0, 4)
        let! cuts = Gen.listOfLength (count * 2) (Gen.choose (0, offsets.Length - 1))
        let! inserts = Gen.listOfLength count (genPieces 3)
        let sorted = cuts |> List.sort |> Array.ofList

        return
            {
                Doc = String.concat "" parts
                Ranges = inserts |> List.mapi (fun index insert -> offsets[sorted[index * 2]], offsets[sorted[index * 2 + 1]], String.concat "" insert)
            }
    }

let private genEdits: Gen<Edits> =
    gen {
        let! parts = genPieces 60
        let! count = Gen.choose (1, 12)

        let! steps =
            Gen.listOfLength
                count
                (gen {
                    let! first = Gen.choose (0, 1000)
                    let! second = Gen.choose (0, 1000)
                    let! insert = genPieces 6
                    return first, second, String.concat "" insert
                })

        return
            {
                Start = String.concat "" parts
                Steps = steps
            }
    }

type Generators =
    static member Replacement() : Arbitrary<Replacement> = Arb.fromGen genReplacement
    static member Changes() : Arbitrary<Changes> = Arb.fromGen genChanges
    static member Edits() : Arbitrary<Edits> = Arb.fromGen genEdits

let private config =
    { FsCheckConfig.defaultConfig with
        maxTest = 500
        arbitrary = [ typeof<Generators> ]
    }

/// The offsets of `text` that split no surrogate pair.
let private between (text: string) : int[] =
    [|
        for index in 0 .. text.Length do
            if index = 0 || index = text.Length || not (Char.IsLowSurrogate text[index] && Char.IsHighSurrogate text[index - 1]) then
                index
    |]

let private replaced (from: int) (to': int) (insert: string) (source: string) =
    source.Remove(from, to' - from).Insert (from, insert)

[<Tests>]
let tests =
    testList
        "Properties"
        [
            testPropertyWithConfig config "a text holds the characters and the lines it was made from"
            <| fun (case: Replacement) -> expectText case.Source (Text.ofString case.Source) "ofString"

            testPropertyWithConfig config "replace agrees with String over line breaks and characters outside ASCII"
            <| fun (case: Replacement) ->
                let result = Text.replace case.From case.To (Text.ofString case.Insert) (Text.ofString case.Source)
                expectText (replaced case.From case.To case.Insert case.Source) result "replace"

            testPropertyWithConfig config "a run of edits agrees with String"
            <| fun (case: Edits) ->
                let mutable expected = case.Start
                let mutable text = Text.ofString case.Start

                for first, second, insert in case.Steps do
                    let positions = between expected
                    let a = positions[first * (positions.Length - 1) / 1000]
                    let b = positions[second * (positions.Length - 1) / 1000]
                    let from = min a b
                    let to' = max a b
                    text <- Text.replace from to' (Text.ofString insert) text
                    expected <- replaced from to' insert expected

                expectText expected text "edits"

            testPropertyWithConfig config "a change set applies as String does, and its inverse restores the text"
            <| fun (case: Changes) ->
                let doc = Text.ofString case.Doc
                let specs = case.Ranges |> List.map (fun (from, to', insert) -> { From = from; To = to'; Insert = Text.ofString insert })
                let changes = ChangeSet.ofSpecs doc specs
                let changed = ChangeSet.apply doc changes
                // Applied last to first, each range leaves the positions of the ones before it alone.
                let expected = case.Ranges |> List.rev |> List.fold (fun (text: string) (from, to', insert) -> replaced from to' insert text) case.Doc
                expectText expected changed "apply"
                expectText case.Doc (ChangeSet.apply changed (ChangeSet.invert changes)) "inverse"

            testPropertyWithConfig config "the line of an offset is the line a scan finds"
            <| fun (case: Replacement) ->
                let text = Text.ofString case.Source
                // The start of each line: after "\n", after "\r\n", and after a "\r" that no "\n" follows.
                let starts =
                    [|
                        0
                        for index in 0 .. case.Source.Length - 1 do
                            let character = case.Source[index]

                            if character = '\n' || (character = '\r' && (index + 1 = case.Source.Length || case.Source[index + 1] <> '\n')) then
                                index + 1
                    |]

                Expect.equal (Text.lines text) starts.Length "lines"

                for number in 0 .. starts.Length - 1 do
                    Expect.equal (Text.line number text).From starts[number] "the start of a line"
                    Expect.equal (Text.lineAt starts[number] text).Number number "the line of its start"
        ]
