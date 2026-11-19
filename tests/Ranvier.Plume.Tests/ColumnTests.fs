module Ranvier.Plume.Tests.ColumnTests

open System
open Expecto
open Ranvier
open Ranvier.Keyboard
open Ranvier.Plume

let private withEditor (text: string) (extensions: Extension list) (body: Editor -> unit) =
    let graph = new Graph ()
    let activation = graph.Activate ()

    try
        body (Editor.create (Document.create (Text.ofString text)) extensions)
    finally
        activation.Dispose ()
        graph.Dispose ()

let private at (position: int) (editor: Editor) =
    editor.Dispatch [ Tx.selection (EditorSelection.single position position) ]

let private head (editor: Editor) = editor.Selection.Value.Main.Head

/// Moving up and down a line keeps the column a reader sees, where a tab reaches to its stop and a wide character
/// takes two columns.
[<Tests>]
let verticalTests =
    testList
        "Vertical motion"
        [
            testCase "down from a line indented with a tab lands under the same column on a line of spaces"
            <| fun () ->
                withEditor "\tab\n    ab" [] (fun editor ->
                    // After the tab and one letter: column 5.
                    at 2 editor
                    Commands.cursorLineDown editor |> ignore
                    Expect.equal (head editor) (4 + 5) "column 5 of the line of spaces"
                    Commands.cursorLineUp editor |> ignore
                    Expect.equal (head editor) 2 "and back under it")

            testCase "down from a line of spaces lands under the same column on a line indented with a tab"
            <| fun () ->
                withEditor "    ab\n\tab" [] (fun editor ->
                    at 5 editor
                    Commands.cursorLineDown editor |> ignore
                    Expect.equal (head editor) (7 + 2) "after the tab and one letter")

            testCase "a column inside a tab goes to the nearer edge of the tab"
            <| fun () ->
                withEditor "abcd\n\tx" [] (fun editor ->
                    at 1 editor
                    Commands.cursorLineDown editor |> ignore
                    Expect.equal (head editor) 5 "column 1 is nearer the tab's start"
                    at 3 editor
                    Commands.cursorLineDown editor |> ignore
                    Expect.equal (head editor) 6 "column 3 is nearer its end")

            testCase "the tab size of the state decides the column"
            <| fun () ->
                withEditor "\tab\n        ab" [ EditorState.tabSize.Of 8 ] (fun editor ->
                    // With tabs of eight, after the tab and one letter is column 9.
                    at 2 editor
                    Commands.cursorLineDown editor |> ignore
                    Expect.equal (head editor) (4 + 9) "column 9")

            testCase "wide characters count two columns"
            <| fun () ->
                withEditor "漢字x\nabcdef" [] (fun editor ->
                    // After two ideographs: column 4.
                    at 2 editor
                    Commands.cursorLineDown editor |> ignore
                    Expect.equal (head editor) (4 + 4) "column 4"
                    Commands.cursorLineUp editor |> ignore
                    Expect.equal (head editor) 2 "back after the ideographs"
                    // Column 3 is the middle of the second ideograph.
                    at (4 + 3) editor
                    Commands.cursorLineUp editor |> ignore
                    Expect.equal (head editor) 2 "a column at an ideograph's middle goes after it")

            testCase "the goal column is kept across a short line"
            <| fun () ->
                withEditor "\tabc\nx\n      abc" [] (fun editor ->
                    // After the tab and two letters: column 6.
                    at 3 editor
                    Commands.cursorLineDown editor |> ignore
                    Expect.equal (head editor) 6 "the short line's end"
                    Commands.cursorLineDown editor |> ignore
                    Expect.equal (head editor) (7 + 6) "column 6 again")

            testCase "selecting down extends to the same column"
            <| fun () ->
                withEditor "\tab\n    ab" [] (fun editor ->
                    at 2 editor
                    Commands.selectLineDown editor |> ignore
                    Expect.equal (editor.Selection.Value.Main.Anchor, head editor) (2, 9) "from the anchor to column 5")

            testCase "a page down keeps the column"
            <| fun () ->
                let text = String.Join ("\n", Array.concat [ [| "\tab" |]; Array.create 9 "filler"; [| "    ab" |] ])

                withEditor text [] (fun editor ->
                    editor.PageLines.Value <- 10
                    at 2 editor
                    Commands.cursorPageDown editor |> ignore
                    let line = Text.lineAt (head editor) editor.Text.Value
                    Expect.equal (line.Number, head editor - line.From) (10, 5) "line 10, column 5 of its spaces")
        ]

/// The arithmetic of the column grid.
[<Tests>]
let columnTests =
    testList
        "Columns"
        [
            testCase "a plain line has a column for each character"
            <| fun () ->
                Expect.equal (Columns.columnOf "hello" 4 3) 3 "column"
                Expect.equal (Columns.columnOf "hello" 4 9) 5 "past the end is the line's width"
                Expect.equal (Columns.widthAt "hello" 4 2) 1 "width"
                Expect.equal (Columns.offsetAt "hello" 4 2.4) 2 "nearest boundary"
                Expect.equal (Columns.offsetAt "hello" 4 2.6) 3 "nearest boundary, above the half"
                Expect.equal (Columns.offsetAt "hello" 4 -3.0) 0 "left of the line"
                Expect.equal (Columns.offsetAt "hello" 4 80.0) 5 "right of the line"

            testCase "a tab reaches to the next multiple of the tab size"
            <| fun () ->
                Expect.equal [ for offset in 0..3 -> Columns.columnOf "\txx" 4 offset ] [ 0; 4; 5; 6 ] "a tab at the start"
                Expect.equal [ for offset in 0..5 -> Columns.columnOf "ab\txx" 4 offset ] [ 0; 1; 2; 4; 5; 6 ] "a tab after two characters"
                Expect.equal (Columns.columnOf "abcd\tx" 4 5) 8 "a tab at a stop takes a whole stop"
                Expect.equal (Columns.columnOf "\t\tx" 8 2) 16 "two tabs of eight"
                Expect.equal (Columns.widthAt "ab\tx" 4 2) 2 "the tab's own width"
                Expect.equal (Columns.widthAt "\tx" 2 0) 2 "a tab of two"

            testCase "a wide character takes two columns, and a combined one is one character"
            <| fun () ->
                Expect.equal [ for offset in 0..3 -> Columns.columnOf "漢字x" 4 offset ] [ 0; 2; 4; 5 ] "ideographs"
                Expect.equal [ for offset in 0..3 -> Columns.columnOf "😀x" 4 offset ] [ 0; 0; 2; 3 ] "a unit inside a surrogate pair is at the pair's start"
                Expect.equal [ for offset in 0..3 -> Columns.columnOf "e\u0301x" 4 offset ] [ 0; 0; 1; 2 ] "a combining mark belongs to its letter"
                Expect.equal (Columns.columnOf "éλx" 4 2) 2 "narrow letters outside ASCII take one"
                Expect.equal (Columns.widthAt "漢x" 4 0, Columns.widthAt "😀x" 4 0, Columns.widthAt "éx" 4 0) (2, 2, 1) "widths"
                Expect.equal (Columns.columnOf "漢\tx" 4 2) 4 "a tab after a wide character"

            testCase "a column finds the nearest character boundary"
            <| fun () ->
                Expect.equal [ for column in [ 0.0; 1.9; 2.1; 3.9; 4.4; 4.6 ] -> Columns.offsetAt "\txx" 4 column ] [ 0; 0; 1; 1; 1; 2 ] "inside and after a tab"
                Expect.equal [ for column in [ 0.9; 1.1; 2.9; 3.1; 4.6 ] -> Columns.offsetAt "漢字x" 4 column ] [ 0; 1; 1; 2; 3 ] "wide characters"
                Expect.equal (Columns.offsetAt "😀x" 4 1.2) 2 "never inside a surrogate pair"

            testCase "every character boundary is found again from its column"
            <| fun () ->
                let pieces = [| "a"; " "; "\t"; "漢"; "😀"; "e\u0301"; "é"; "x" |]
                let random = Random 7

                for _ in 1..500 do
                    let line = String.Concat [ for _ in 1 .. random.Next (0, 12) -> pieces[random.Next pieces.Length] ]
                    let tabSize = [| 2; 4; 8 |][random.Next 3]
                    let mutable at = 0

                    while at <= line.Length do
                        let column = Columns.columnOf line tabSize at
                        Expect.equal (Columns.offsetAt line tabSize (float column)) at (sprintf "offset %d of a line of %d units at tab size %d" at line.Length tabSize)
                        at <- if at < line.Length then at + Grapheme.nextClusterLength line at else at + 1
        ]
