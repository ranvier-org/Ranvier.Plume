module Ranvier.Plume.Tests.BracketTests

open Expecto
open Ranvier
open Ranvier.Plume

let private inEditor text extensions test =
    use graph = new Graph()
    use activation = graph.Activate()
    test (Editor.create (Document.create (Text.ofString text)) extensions)

[<Tests>]
let tests =
    testList "Brackets" [
        testCase "match typed nesting in both directions over UTF-16 and line endings" <| fun () ->
            let doc = Text.ofString "(😀\r\n[{x}])"
            let expected = ValueSome(struct (0, doc.Length - 1))
            Expect.equal (Brackets.matchAt doc 0) expected "opening"
            Expect.equal (Brackets.matchAt doc doc.Length) expected "closing"
            Expect.equal (Brackets.matchAt (Text.ofString "([)]") 0) ValueNone "crossed pairs"
            Expect.equal (Brackets.matchWithin 2 (Text.ofString "(  )") 0) ValueNone "bounded search"
            Expect.equal (Brackets.matchWithin 3 (Text.ofString "(  )") 0) (ValueSome(struct (0,3))) "at limit"

        testCase "wraps reversed selections and pairs at other cursors in one undo step" <| fun () ->
            inEditor "abc!" [ Brackets.autoClose; EditorState.allowMultipleSelections.Of true ] (fun editor ->
                editor.Dispatch [ Tx.selection (EditorSelection.create [EditorSelection.range 3 0; EditorSelection.cursor 4] 1) ]
                Expect.isTrue (Brackets.typeText "[" editor) "handled"
                Expect.equal (Text.toString editor.Text.Value) "[abc]![]" "wrapped and paired"
                let sel = editor.Selection.Value
                Expect.equal (sel.Ranges |> Array.map (fun r -> r.Anchor,r.Head)) [|4,1;7,7|] "selection directions"
                Expect.equal sel.MainIndex 1 "main cursor"
                Commands.undo editor |> ignore
                Expect.equal (Text.toString editor.Text.Value) "abc!" "single undo")

        testCase "mixed close skips and replacements have independent resulting offsets" <| fun () ->
            inEditor "ab)" [ Brackets.autoClose; EditorState.allowMultipleSelections.Of true ] (fun editor ->
                Brackets.typeText "(" editor |> ignore
                editor.Dispatch [ Tx.selection (EditorSelection.create [EditorSelection.cursor 1; EditorSelection.range 2 4; EditorSelection.cursor 5] 0) ]
                Brackets.typeText ")" editor |> ignore
                Expect.equal (Text.toString editor.Text.Value) "())))" "skip, replace, insert"
                Expect.equal (editor.Selection.Value.Ranges |> Array.map (fun r -> r.Head)) [|2;3;5|] "heads")

        testCase "a pre-existing closer is unrelated and accepts a literal closer" <| fun () ->
            inEditor ")" [ Brackets.autoClose ] (fun editor ->
                Brackets.typeText ")" editor |> ignore
                Expect.equal (Text.toString editor.Text.Value) "))" "literal closer")

        testCase "nested generated closers follow typing and lose ownership after leaving the pair" <| fun () ->
            inEditor "" [ Brackets.autoClose ] (fun editor ->
                for s in ["("; "["; "xy"; "]"; ")"] do Brackets.typeText s editor |> ignore
                Expect.equal (Text.toString editor.Text.Value) "([xy])" "both mapped closers skipped"
                Brackets.typeText "{" editor |> ignore
                editor.Dispatch [ Tx.selection (EditorSelection.single 0 0) ]
                editor.Dispatch [ Tx.selection (EditorSelection.single 7 7) ]
                Brackets.typeText "}" editor |> ignore
                Expect.equal (Text.toString editor.Text.Value) "([xy]){}}" "revisited closer is ordinary")

        testCase "disabled, literal and read-only typing" <| fun () ->
            inEditor "" [] (fun editor ->
                Brackets.typeText "(" editor |> ignore
                Expect.equal (Text.toString editor.Text.Value) "(" "plain typing")
            inEditor "" [ Brackets.autoClose ] (fun editor ->
                Brackets.typeText "(xyz" editor |> ignore
                Expect.equal (Text.toString editor.Text.Value) "(xyz" "bulk input")
            inEditor "" [ Brackets.autoClose; EditorState.readOnly.Of true ] (fun editor ->
                Expect.isFalse (Brackets.typeText "(" editor) "read only"
                Expect.equal editor.Text.Value.Length 0 "unchanged")
    ]
