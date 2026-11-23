module Ranvier.Plume.Tests.FoldTests

open Expecto
open Ranvier
open Ranvier.Plume

[<Tests>]
let tests =
    testCase "folded rows, local selections and shared edits" <| fun () ->
        use graph = new Graph()
        use active = graph.Activate()
        let doc = Document.create (Text.ofString "A\r\nB\r\nC\r\nD")
        let a = Editor.create doc [Folding.extension]
        let b = Editor.create doc [Folding.extension]
        let rows (editor: Editor) =
            let projection = Folding.projection editor.State.Value
            [for row in 0 .. projection.Count - 1 -> projection.DocumentLine row]
        Expect.isTrue (Folding.fold 0 2 a) "collapse"
        Expect.equal (rows a) [0;3] "body rows leave the layout"
        Expect.equal (rows b) [0;1;2;3] "editor-local"
        let p = Folding.projection a.State.Value
        Expect.equal (p.VisualRow 2, p.VisualRow 3) (0,1) "caret and line after fold"
        b.Dispatch [Tx.selection (EditorSelection.single 3 3)]
        Expect.equal (rows a) [0;3] "foreign selection leaves fold closed"
        b.Dispatch [Tx.change {From=0; To=0; Insert=Text.ofString "X\n"}]
        Expect.equal (rows a) [0;1;4] "disjoint edits map both ends"
        a.Dispatch [Tx.selection (EditorSelection.single 5 5)]
        Expect.equal (rows a) [0;1;2;3;4] "entering a hidden line reveals it"
        Expect.isTrue (Folding.fold 1 3 a) "recollapse with cursor in body"
        Expect.equal a.Selection.Value.Main.Head 3 "move hidden head to header end"
        Expect.isTrue (Folding.unfold 1 a) "expand"
        Expect.equal (rows a) [0;1;2;3;4] "original lines return"

[<Tests>]
let boundaryTests =
    testCase "fold boundaries follow foreign edits and keep every CRLF offset visible on entry" <| fun () ->
        use graph = new Graph()
        use active = graph.Activate()
        let doc = Document.create (Text.ofString "H\r\nB\r\nC\r\nX")
        let a = Editor.create doc [Folding.extension]
        let b = Editor.create doc []
        let count () = (Folding.projection a.State.Value).Count
        let insert p text = b.Dispatch [Tx.change {From=p; To=p; Insert=Text.ofString text}]
        let close () = Expect.isTrue (Folding.fold 0 2 a) "collapse header through C"
        close ()
        a.Dispatch [Tx.selection (EditorSelection.single 2 2)]
        Expect.equal (count ()) 2 "the interior of the header CRLF still belongs to its header"
        a.Dispatch [Tx.selection (EditorSelection.single 8 8)]
        Expect.equal (count ()) 4 "the interior of the last hidden CRLF belongs to its body"
        close ()
        insert 1 "!"
        Expect.equal (count ()) 2 "typing at the end of the header extends the header"
        insert (Text.line 2 a.Text.Value).To "!"
        Expect.equal (count ()) 4 "typing at the end of hidden text opens the fold"
        close ()
        insert (Text.line 3 a.Text.Value).From "!"
        Expect.equal (count ()) 2 "typing at the next visible line leaves the fold closed"
        b.Dispatch [Tx.change {From=0; To=a.Text.Value.Length; Insert=Text.ofString "replacement"}]
        Expect.equal (count ()) 1 "replacing across both fold boundaries removes it"


