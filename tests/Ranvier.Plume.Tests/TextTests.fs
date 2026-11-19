module Ranvier.Plume.Tests.TextTests

open System
open System.Diagnostics
open System.Text
open Expecto
open Ranvier.Plume
open Ranvier.Plume.Tests.Helpers

let private replaceString (from: int) (to': int) (insert: string) (source: string) =
    source.Remove(from, to' - from).Insert (from, insert)

let private editOfLargeText () =
    let builder = StringBuilder (1_000_000 * 12)

    for index in 0..999_999 do
        builder.Append("line ").Append(index).Append '\n' |> ignore

    let before = Text.ofString (builder.ToString ())
    Expect.equal (Text.lines before) 1_000_001 "lines"
    let first = Text.lineText 0 before
    let near = Text.lineText 500 before
    let last = Text.lineText 999_999 before
    Expect.equal (first, near, last) ("line 0", "line 500", "line 999999") "the lines read before the edit"

    // Warm-up of the edit path.
    let warm = Text.ofString "line 0\nline 1\nline 2"
    Text.replace 7 13 (Text.ofString "MODIFIED") warm |> ignore

    // The fastest of five edits: one of them may hold a garbage collection.
    let mutable fastest = Double.MaxValue
    let mutable after = before

    for attempt in 0..4 do
        let target = Text.line (500_000 + attempt) before
        let watch = Stopwatch.StartNew ()
        let edited = Text.replace target.From target.To (Text.ofString "MODIFIED LINE") before
        watch.Stop ()
        fastest <- min fastest watch.Elapsed.TotalMilliseconds

        if attempt = 0 then
            after <- edited

    Expect.equal (Text.lineText 500_000 after) "MODIFIED LINE" "edited line"
    Expect.isTrue (obj.ReferenceEquals (first, Text.lineText 0 after)) "line 0 identity"
    Expect.isTrue (obj.ReferenceEquals (near, Text.lineText 500 after)) "line 500 identity"
    Expect.isTrue (obj.ReferenceEquals (last, Text.lineText 999_999 after)) "line 999,999 identity"
    Expect.isLessThan fastest 5.0 "edit time"

let private memoryOfLargeText () =
    let lines = seq { for index in 0..999_999 -> String.Concat ("let orbitalState", string index, " = ", string (index * 3)) }
    let before = GC.GetTotalMemory true
    let text = Text.ofLines lines
    let after = GC.GetTotalMemory true
    let megabytes = float (after - before) / (1024.0 * 1024.0)
    Expect.equal (Text.lines text) 1_000_000 "lines"
    Expect.equal (Text.lineText 0 text) "let orbitalState0 = 0" "line 0"
    Expect.equal (Text.lineText 999_999 text) "let orbitalState999999 = 2999997" "the last line"
    let first = Text.line 0 text
    Expect.equal (first.To - first.From) 21 "length of line 0"
    // The bound Atelier's test sets for its rope of UTF-8 bytes.
    Expect.isLessThan megabytes 65.0 "megabytes held by 1,000,000 lines"
    GC.KeepAlive text

[<Tests>]
let tests =
    testList
        "Text"
        [
            // Ported from Atelier's TextSnapshotTests, with offsets in place of line and character ranges.
            testCase "multi-line insert, delete and line join across mixed LF and CRLF"
            <| fun () ->
                let initial = "line1\r\nline2\nline3\r\nline4"
                let text = Text.ofString initial
                Expect.equal (Text.lines text) 4 "lines"
                Expect.equal (Text.length text) initial.Length "length"
                Expect.equal [ for line in 0..3 -> Text.lineText line text ] [ "line1"; "line2"; "line3"; "line4" ] "the four lines"
                expectText initial text "the whole text"

                let at = Text.offsetOf 1 2 text
                let inserted = Text.replace at at (Text.ofString "A\r\nB\nC") text
                expectText (replaceString at at "A\r\nB\nC" initial) inserted "insert"
                Expect.equal (Text.lines inserted) 6 "lines after insert"

                let reverted = Text.replace at (at + 6) Text.empty inserted
                expectText initial reverted "insert reverted"

                // Joining lines 1 and 2 removes the "\n" between them.
                let breakAt = (Text.line 1 text).To
                expectText "line1\r\nline2line3\r\nline4" (Text.replace breakAt (breakAt + 1) Text.empty text) "join"

                // Deleting from inside line 0 to inside line 2.
                let from = Text.offsetOf 0 3 text
                let to' = Text.offsetOf 2 2 text
                expectText (replaceString from to' "" initial) (Text.replace from to' Text.empty text) "delete across lines"

                // The three edits of Atelier's test, as written there. An insert at the end of line 1.
                let added = "\r\ninsertedA\ninsertedB"
                let lineEnd = Text.offsetOf 1 5 text
                let grown = Text.replace lineEnd lineEnd (Text.ofString added) text
                expectText "line1\r\nline2\r\ninsertedA\ninsertedB\nline3\r\nline4" grown "insert at a line's end"
                Expect.equal (Text.lines grown) 6 "lines after it"
                expectText initial (Text.replace lineEnd (lineEnd + added.Length) Text.empty grown) "and taken out again"

                // A join across CRLF removes both units of the break.
                let joined = Text.replace (Text.offsetOf 0 5 text) (Text.offsetOf 1 0 text) Text.empty text
                Expect.equal (Text.lines joined) 3 "lines after the join"
                Expect.equal (Text.lineText 0 joined) "line1line2" "the joined line"
                expectText "line1line2\nline3\r\nline4" joined "join across CRLF"

                // From the middle of line 1 to the start of line 3.
                let cut = Text.replace (Text.offsetOf 1 2 text) (Text.offsetOf 3 0 text) Text.empty text
                Expect.equal (Text.lines cut) 2 "lines after the delete"
                expectText "line1\r\nliline4" cut "delete across three lines"

            testCase "unpaired surrogates survive materialization and leaf edits"
            <| fun () ->
                let high = string (char 0xD83D)
                let low = string (char 0xDE42)
                for line in [high; low; "a" + high + "b"; low + high] do
                    let lines = Array.init 300 (fun i -> if i % 31 = 0 then line else "abc")
                    let raw = String.concat "\n" lines
                    for text in [Text.ofString raw; Text.ofLines lines] do
                        Expect.equal (Text.toString text) raw "exact UTF-16 units"
                        let clipped = Text.slice 1 (raw.Length - 1) text
                        Expect.equal (Text.toString clipped) (raw.Substring(1, raw.Length - 2)) "split leaves"
                    Expect.isFalse (Text.contentEquals (Text.ofString line) (Text.ofString (line.Replace(high, "�").Replace(low, "�")))) "replacement differs"
                let repaired = Text.replace 1 1 (Text.ofString low) (Text.ofString high)
                Expect.equal (Text.toString repaired) (high + low) "pair restored"

            testCase "astral characters count two UTF-16 units"
            <| fun () ->
                let source = "let 🧪 = 1"
                let text = Text.ofString source
                Expect.equal (Text.lines text) 1 "lines"
                Expect.equal (Text.length text) 10 "length"
                Expect.equal (Text.lineText 0 text) source "line"

                for character in 0..10 do
                    let offset = Text.offsetOf 0 character text
                    Expect.equal offset character "offset"
                    Expect.equal (Text.positionOf offset text) (struct (0, character)) "position"

                Expect.equal (Text.sliceString 4 6 text) "🧪" "slice"

            // Run apart from the other tests: the strings a leaf keeps are counted over the whole process, and
            // texts read beside this one would push its leaves out.
            testSequenced (testCase "an edit of a 1,000,000-line text keeps other lines' strings and takes under 5 ms" editOfLargeText)

            // Run apart from the other tests: it reads the memory of the whole process.
            testSequenced (testCase "a text of 1,000,000 lines built from lines holds under 65 MB" memoryOfLargeText)

            testCase "offsets over CRLF and astral lines"
            <| fun () ->
                let text = Text.ofString "a\r\nb😀c\rd"
                Expect.equal (Text.length text) 9 "length"
                Expect.equal (Text.lines text) 3 "lines"
                let line = Text.line 1 text
                Expect.equal line.Number 1 "number"
                Expect.equal line.From 3 "from"
                Expect.equal line.To 7 "to"
                Expect.equal line.Text "b😀c" "text"
                Expect.equal (Text.lineAt 8 text).Number 2 "line at 8"
                Expect.equal (Text.lineAt 2 text).Number 0 "an offset inside a break belongs to its line"
                Expect.equal (Text.positionOf 5 text) (struct (1, 2)) "position of 5"
                Expect.equal (Text.offsetOf 2 0 text) 8 "offset of line 2"
                Expect.equal (Text.offsetOf 0 99 text) 1 "character clamped"

            testCase "empty text"
            <| fun () ->
                Expect.equal (Text.length Text.empty) 0 "length"
                Expect.equal (Text.lines Text.empty) 1 "lines"
                let line = Text.lineAt 0 Text.empty
                Expect.equal (line.Number, line.From, line.To, line.Text) (0, 0, 0, "") "line at 0"
                Expect.isTrue (obj.ReferenceEquals (Text.ofString "", Text.empty)) "one empty text"
                expectText "x" (Text.replace 0 0 (Text.ofString "x") Text.empty) "insert into empty"

            testCase "trailing newline"
            <| fun () ->
                Expect.equal (Text.lines (Text.ofString "a\nb")) 2 "no trailing newline"
                let text = Text.ofString "a\n"
                Expect.equal (Text.lines text) 2 "trailing newline"
                Expect.equal (Text.lineText 1 text) "" "last line"
                Expect.equal (Text.lineAt 2 text).Number 1 "line at length"

            testCase "out of range raises"
            <| fun () ->
                let text = Text.ofString "abc"
                Expect.throwsT<ArgumentOutOfRangeException> (fun () -> Text.lineAt 4 text |> ignore) "lineAt"
                Expect.throwsT<ArgumentOutOfRangeException> (fun () -> Text.line 1 text |> ignore) "line"
                Expect.throwsT<ArgumentOutOfRangeException> (fun () -> Text.replace 2 9 Text.empty text |> ignore) "replace"
                Expect.throwsT<ArgumentException> (fun () -> Text.replace 2 1 Text.empty text |> ignore) "reversed range"

            testCase "ofLines joins with a line feed"
            <| fun () ->
                expectText "a\nb\nc" (Text.ofLines [ "a"; "b"; "c" ]) "three lines"
                expectText "" (Text.ofLines []) "no line"
                expectText "a\r\nb\nc\r\nd" (Text.ofLines [ "a\r\nb"; "c\r"; "d" ]) "lines holding breaks"

                let many = Text.ofLines (Seq.init 1000 (fun index -> "row " + string index))
                Expect.equal (Text.lines many) 1000 "1000 lines"
                Expect.equal (Text.lineText 777 many) "row 777" "line 777"

            testCase "slice, append and iteration"
            <| fun () ->
                let source = String.Join ("\n", Seq.init 600 (fun index -> "line number " + string index))
                let text = Text.ofString source
                expectText (source.Substring (5, 9000)) (Text.slice 5 9005 text) "long slice"
                expectText (source.Substring (5, 3)) (Text.slice 5 8 text) "short slice"
                Expect.isTrue (obj.ReferenceEquals (Text.slice 0 (Text.length text) text, text)) "whole slice"
                expectText source (Text.append (Text.slice 0 7000 text) (Text.slice 7000 source.Length text)) "append"
                Expect.equal (Text.iterLines 2 4 text |> List.ofSeq) [ "line number 2"; "line number 3" ] "iterLines"
                Expect.equal (String.Concat (Text.iterRange 3 300 text)) (source.Substring (3, 297)) "iterRange"

            testCase "lines added one edit at a time fill their leaves"
            <| fun () ->
                let rec leaves (node: Node) : int =
                    match node with
                    | Leaf _ -> 1
                    | Branch branch -> leaves branch.Left + leaves branch.Right

                let mutable text = Text.ofString "first"
                let expected = StringBuilder "first"

                for index in 1..2000 do
                    let line = "\nline " + string index
                    text <- Text.replace (Text.length text) (Text.length text) (Text.ofString line) text
                    expected.Append line |> ignore

                expectText (expected.ToString ()) text "appended"
                Expect.isLessThan (leaves text.Root) 64 "a leaf holds 32 lines or more on average"

                // The same in the middle of a text: each new line splits a leaf, and the parts join their neighbours.
                let mutable middle = Text.ofLines (Seq.init 4000 (fun index -> "row " + string index))
                let at = (Text.line 2000 middle).From

                for _ in 1..2000 do
                    middle <- Text.replace at at (Text.ofString "x\n") middle

                Expect.equal (Text.lines middle) 6000 "lines"
                Expect.isLessThan (leaves middle.Root) 190 "a leaf holds 32 lines or more on average"
                Expect.equal (Text.lineText 2000 middle) "x" "the first inserted line"
                Expect.equal (Text.lineText 4000 middle) "row 2000" "the line the insertions pushed down"

            testCase "one-character texts are shared"
            <| fun () -> Expect.isTrue (obj.ReferenceEquals (Text.ofString "x", Text.ofString "x")) "x"

            testCase "replace agrees with String"
            <| fun () ->
                forSeeds 3000 (fun rng ->
                    let source = randomString rng "ab\n\r" 40
                    let insert = randomString rng "xy\n\r" 8
                    let from = rng.Next (0, source.Length + 1)
                    let to' = rng.Next (from, source.Length + 1)
                    let result = Text.replace from to' (Text.ofString insert) (Text.ofString source)
                    expectText (replaceString from to' insert source) result "replace")

            testCase "replace on a multi-leaf text agrees with String"
            <| fun () ->
                forSeeds 200 (fun rng ->
                    let source = String.Join ("\n", Seq.init 700 (fun index -> randomString rng "abc" 6 + string index))
                    let mutable expected = source
                    let mutable text = Text.ofString source

                    for _ in 1..12 do
                        let insert = randomString rng "xy\n" 30
                        let from = rng.Next (0, expected.Length + 1)
                        let to' = min expected.Length (from + rng.Next (0, 400))
                        text <- Text.replace from to' (Text.ofString insert) text
                        expected <- replaceString from to' insert expected

                    expectText expected text "replace sequence")

            testCase "slice and append agree with String"
            <| fun () ->
                forSeeds 2000 (fun rng ->
                    let source = randomString rng "ab\n\r" 40
                    let text = Text.ofString source
                    let from = rng.Next (0, source.Length + 1)
                    let to' = rng.Next (from, source.Length + 1)
                    expectText (source.Substring (from, to' - from)) (Text.slice from to' text) "slice"
                    Expect.equal (Text.sliceString from to' text) (source.Substring (from, to' - from)) "sliceString"
                    expectText source (Text.append (Text.slice 0 from text) (Text.slice from source.Length text)) "append")

            testCase "lineAt and positionOf agree with a scan"
            <| fun () ->
                forSeeds 1000 (fun rng ->
                    let source = randomString rng "ab\n\r" 40
                    let text = Text.ofString source

                    for offset in 0 .. source.Length do
                        let line = Text.lineAt offset text
                        Expect.isLessThanOrEqual line.From offset "line starts at or before the offset"
                        Expect.equal (Text.line line.Number text) line "line by number"
                        Expect.equal (source.Substring (line.From, line.To - line.From)) line.Text "line text"

                        if line.Number + 1 < Text.lines text then
                            Expect.isLessThan offset (Text.line (line.Number + 1) text).From "offset before the next line")

            testCase "vectorised scan equals scalar scan"
            <| fun () ->
                forSeeds 500 (fun rng ->
                    let pieces = [| "a"; "b"; "c"; "d"; " "; "é"; "😀"; "\n"; "\r"; "\r\n" |]
                    let source = String.Concat (Seq.init (rng.Next 300) (fun _ -> pieces[rng.Next pieces.Length]))
                    let bytes = Encoding.UTF8.GetBytes source
                    let vectorised = Text.ofUtf8 (ReadOnlySpan<byte> bytes)
                    let scalar = Text.ofUtf8Scalar (ReadOnlySpan<byte> bytes)
                    Expect.isTrue (Text.contentEquals vectorised scalar) "content"
                    expectText source vectorised "vectorised")

            testCase "ofUtf8 drops a byte order mark"
            <| fun () ->
                let bytes = Array.append [| 0xEFuy; 0xBBuy; 0xBFuy |] (Encoding.UTF8.GetBytes "a\nb")
                expectText "a\nb" (Text.ofUtf8 (ReadOnlySpan<byte> bytes)) "bom"

                // Atelier's case: the mark, both kinds of break, and characters of two and of four bytes.
                let source = "élan\r\n🧪 = λ\nfin"
                let text = Text.ofUtf8 (ReadOnlySpan<byte> (Array.append [| 0xEFuy; 0xBBuy; 0xBFuy |] (Encoding.UTF8.GetBytes source)))
                Expect.equal (Text.lines text) 3 "lines"
                let first = Text.line 0 text
                Expect.equal (first.Text, first.To - first.From) ("élan", 4) "line 0 and its length"
                let second = Text.line 1 text
                Expect.equal (second.Text, second.To - second.From) ("🧪 = λ", 6) "line 1: the astral character counts two"
                Expect.equal (Text.lineText 2 text) "fin" "line 2"
                expectText source text "the whole text"
        ]
