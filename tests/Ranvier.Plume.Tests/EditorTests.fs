module Ranvier.Plume.Tests.EditorTests

open System
open System.Threading
open Expecto
open Ranvier
open Ranvier.Plume

/// Runs `body` with a graph of its own as the ambient graph.
let private withGraph (body: Graph -> unit) =
    let graph = new Graph ()
    let activation = graph.Activate ()

    try
        body graph
    finally
        activation.Dispose ()
        graph.Dispose ()

let private insert (at: int) (text: string) : TxSpec =
    Tx.change { From = at; To = at; Insert = Text.ofString text }

let private textOf (document: Document) = Text.toString document.Text.Value

[<Tests>]
let documentTests =
    testList
        "Document"
        [
            testCase "the text follows dispatch"
            <| fun () ->
                withGraph (fun _ ->
                    let document = Document.create (Text.ofString "hello")
                    Expect.equal document.Head.Value 0L "head"
                    document.Dispatch [ insert 5 " world" ]
                    Expect.equal (textOf document) "hello world" "text"
                    Expect.equal document.Head.Value 1L "head after one transaction"
                    document.Dispatch [ Tx.changes [ { From = 0; To = 1; Insert = Text.ofString "J" }; { From = 6; To = 11; Insert = Text.ofString "there" } ] ]
                    Expect.equal (textOf document) "Jello there" "two ranges at once")

            testCase "two dispatches in one batch wake an effect once and lose nothing"
            <| fun () ->
                withGraph (fun _ ->
                    let document = Document.create (Text.ofString "")
                    let reader = document.Reader ()
                    let mutable runs = 0
                    let seen = ResizeArray<int64> ()

                    Api.createEffect (fun () ->
                        runs <- runs + 1

                        for transaction in reader.Read () do
                            seen.Add transaction.Seq)

                    Api.flush ()
                    Expect.equal runs 1 "the first run"

                    Api.batch (fun () ->
                        document.Dispatch [ insert 0 "a" ]
                        document.Dispatch [ insert 1 "b" ])

                    Api.flush ()
                    Expect.equal runs 2 "one run for two dispatches"
                    Expect.sequenceEqual seen [ 1L; 2L ] "both transactions, in order"
                    Expect.equal (textOf document) "ab" "text")

            testCase "a transaction against an older head is mapped"
            <| fun () ->
                withGraph (fun _ ->
                    let document = Document.create (Text.ofString "0123456789")
                    let reader = document.Reader ()
                    document.Dispatch [ insert 0 "ab" ]
                    document.Dispatch [ insert 3 "c" ]
                    Expect.equal (textOf document) "ab0c123456789" "two transactions landed"
                    // Written against the document before both: deletes "1" and inserts at the end.
                    document.DispatchAt (0L, [ Tx.changes [ { From = 1; To = 2; Insert = Text.empty }; { From = 10; To = 10; Insert = Text.ofString "!" } ]; Tx.isolateHistory ])
                    Expect.equal (textOf document) "ab0c23456789!" "the same character is deleted"
                    Expect.equal document.Head.Value 3L "head"
                    let last = (reader.Read ()) |> Array.last
                    Expect.equal last.Base 2L "the transaction is recorded against the head it landed on"
                    Expect.equal (Text.toString last.Changes.Removed[0]) "1" "its removed text is the current document's"
                    Expect.isTrue (Editor.undo (Editor.create document [])) "the mapped transaction can be undone"
                    Expect.equal (textOf document) "ab0c123456789" "undone")

            testCase "a position the log dropped raises"
            <| fun () ->
                withGraph (fun _ ->
                    let document = Document.create (Text.ofString "x")
                    document.Dispatch [ insert 0 "a" ]
                    document.Dispatch [ insert 0 "b" ]
                    document.Dispatch [ insert 0 "c" ]
                    Expect.throwsT<InvalidOperationException> (fun () -> document.DispatchAt (0L, [ insert 0 "z" ])) "position 0 is gone")

            testCase "the dirty flag across the saved position"
            <| fun () ->
                withGraph (fun _ ->
                    let document = Document.create (Text.ofString "x")
                    let editor = Editor.create document []
                    Expect.isFalse document.IsDirty.Value "clean at the start"
                    editor.Dispatch [ insert 0 "a"; Tx.isolateHistory ]
                    Expect.isTrue document.IsDirty.Value "dirty after an edit"
                    document.MarkSaved ()
                    Expect.isFalse document.IsDirty.Value "clean after saving"
                    Expect.equal (Text.toString document.Saved.Value) "ax" "saved text"
                    editor.Dispatch [ insert 0 "b"; Tx.isolateHistory ]
                    Expect.isTrue document.IsDirty.Value "dirty again"
                    Expect.isTrue (Editor.undo editor) "undo"
                    Expect.isFalse document.IsDirty.Value "clean at the saved position"
                    Expect.isTrue (Editor.undo editor) "undo past it"
                    Expect.isTrue document.IsDirty.Value "dirty before the saved position"
                    Expect.isTrue (Editor.redo editor) "redo"
                    Expect.isFalse document.IsDirty.Value "clean again"
                    Expect.equal (document.UndoDepth.Value, document.RedoDepth.Value) (1, 1) "depths")

            testCase "one group across two documents is one item in each"
            <| fun () ->
                withGraph (fun _ ->
                    let first = Document.create (Text.ofString "one")
                    let second = Document.create (Text.ofString "two")
                    let group = History.newGroupId ()
                    first.Dispatch [ insert 0 "a"; Tx.group group ]
                    second.Dispatch [ insert 0 "b"; Tx.group group ]
                    first.Dispatch [ insert 4 "c"; Tx.group group ]
                    Expect.equal (first.UndoDepth.Value, second.UndoDepth.Value) (1, 1) "one item in each file"
                    Expect.isTrue (Editor.undo (Editor.create first [])) "undo in the first"
                    Expect.equal (textOf first) "one" "the first file's part is undone"
                    Expect.equal (textOf second) "btwo" "the second file is left alone")

            testCase "a transaction posted against an older head lands mapped when the graph pumps"
            <| fun () ->
                withGraph (fun graph ->
                    let document = Document.create (Text.ofString "0123456789")
                    let reader = document.Reader ()
                    document.Dispatch [ insert 0 "ab" ]
                    // Written against the document before the insertion, and posted from another thread.
                    let thread = Thread (fun () -> document.PostAt (0L, [ Tx.changes [ { From = 1; To = 2; Insert = Text.empty } ]; Tx.isolateHistory ]))
                    thread.Start ()
                    thread.Join ()
                    Expect.equal (textOf document) "ab0123456789" "not yet"
                    graph.Pump () |> ignore
                    Expect.equal (textOf document) "ab023456789" "the character it named is deleted in the current text"
                    Expect.equal (reader.Read ()).Length 2 "the reader has both transactions")

            testCase "Post from another thread lands when the graph pumps"
            <| fun () ->
                withGraph (fun graph ->
                    let document = Document.create (Text.ofString "x")
                    let thread = Thread (fun () -> document.Post [ insert 1 "y" ])
                    thread.Start ()
                    thread.Join ()
                    Expect.equal (textOf document) "x" "not yet"
                    graph.Pump () |> ignore
                    Expect.equal (textOf document) "xy" "after the pump")

            testCase "the log keeps only what a reader has yet to read"
            <| fun () ->
                withGraph (fun _ ->
                    let document = Document.create (Text.ofString "")

                    for index in 0..9 do
                        document.Dispatch [ insert index "x" ]

                    Expect.equal document.LogLength 0 "without a reader the log holds nothing"

                    let owner =
                        Api.createRoot (fun owner ->
                            let reader = document.Reader ()

                            for index in 10..19 do
                                document.Dispatch [ insert index "x" ]

                            Expect.equal document.LogLength 10 "a reader that has not read keeps its transactions"
                            Expect.equal (reader.Read ()).Length 10 "and reads them"
                            document.Dispatch [ insert 0 "y" ]
                            Expect.equal document.LogLength 1 "once read, they go"
                            document.Dispatch [ insert 0 "y" ]
                            document.Dispatch [ insert 0 "y" ]
                            Expect.equal document.LogLength 3 "kept again"
                            owner)

                    owner.Dispose ()
                    document.Dispatch [ insert 0 "z" ]
                    Expect.equal document.LogLength 0 "a disposed reader keeps nothing")
        ]

/// A document over a text of its own, with an editor and an update reader, and a weak reference to that text.
/// A method of its own, so no local of the caller keeps the text alive.
[<System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)>]
let private documentOverOwnText () : struct (WeakReference * Document * Editor * UpdateReader) =
    let original = Text.ofLines (Seq.init 2000 (fun index -> "line " + string index))
    let document = Document.create original
    let editor = Editor.create document []
    let updates = editor.Updates ()
    updates.Read () |> ignore
    struct (WeakReference original, document, editor, updates)

let private collect () =
    GC.Collect ()
    GC.WaitForPendingFinalizers ()
    GC.Collect ()

/// True when the library was built on the traced core (`-p:RanvierTrace=true`). A traced graph's event log holds
/// the payload of each event it records, so a text the graph has seen lives as long as the graph.
let private traced =
    typeof<Document>.Assembly.GetCustomAttributes (typeof<System.Reflection.AssemblyMetadataAttribute>, false)
    |> Array.exists (fun attribute ->
        match attribute with
        | :? System.Reflection.AssemblyMetadataAttribute as metadata -> metadata.Key = "RanvierTrace" && metadata.Value = "true"
        | _ -> false)

[<Tests>]
let retentionTests =
    testList
        "Retention"
        [
            testCase "a document holds its saved text and its current text, and no other"
            <| fun () ->
                withGraph (fun _ ->
                    let struct (original, document, editor, updates) = documentOverOwnText ()

                    for index in 0..40 do
                        editor.Dispatch [ insert (index * 50) "x"; Tx.isolateHistory ]
                        updates.Read () |> ignore

                    collect ()
                    Expect.isTrue original.IsAlive "the text at the start is the saved text"
                    document.MarkSaved ()
                    // Two more transactions move the readers' last values past the text at the start.
                    editor.Dispatch [ insert 0 "y"; Tx.isolateHistory ]
                    updates.Read () |> ignore
                    editor.Dispatch [ insert 0 "y"; Tx.isolateHistory ]
                    updates.Read () |> ignore
                    collect ()

                    if traced then
                        Expect.isTrue original.IsAlive "the traced graph's event log holds the text at the start"
                    else
                        Expect.isFalse original.IsAlive "once another text is saved, nothing holds the text at the start"

                    Expect.equal document.UndoDepth.Value 43 "the history still undoes every edit"
                    Expect.isTrue (Editor.undo editor) "undo"
                    GC.KeepAlive updates)
        ]

let private editCount =
    StateField.define
        {
            Create = fun _ -> 0
            Update = fun count transaction -> if transaction.DocChanged then count + 1 else count
        }

[<Tests>]
let editorTests =
    testList
        "Editor"
        [
            testCase "the dossier's sketch"
            <| fun () ->
                withGraph (fun _ ->
                    let document = Document.create (Text.ofString "let answer = 42\n")
                    let editor = Editor.create document [ EditorState.tabSize.Of 4; StateField.extension editCount ]
                    let edits = editor.Field editCount
                    let mutable caret = -1
                    Api.createEffect (fun () -> caret <- editor.Selection.Value.Main.Head)
                    Api.flush ()
                    Expect.equal caret 0 "caret"

                    editor.Dispatch
                        [
                            Tx.change { From = 4; To = 10; Insert = Text.ofString "result" }
                            Tx.selection (EditorSelection.single 10 10)
                            Tx.userEvent "input.type"
                            Tx.scrollIntoView
                        ]

                    Api.flush ()
                    Expect.equal edits.Value 1 "edit count"
                    Expect.equal (Text.toString editor.Text.Value) "let result = 42\n" "text"
                    Expect.equal caret 10 "the effect followed the selection"
                    Expect.equal (editor.Facet EditorState.tabSize).Value 4 "facet"
                    Expect.isTrue (obj.ReferenceEquals (editor.Field editCount, edits)) "one memo per field"

                    let other = Editor.create document []
                    Expect.equal (Text.toString other.Text.Value) "let result = 42\n" "a second editor has the same text"
                    Expect.equal other.Selection.Value.Main.Head 0 "and its own selection")

            testCase "the selection memo sleeps through an edit after the cursor"
            <| fun () ->
                withGraph (fun _ ->
                    let document = Document.create (Text.ofString "0123456789")
                    let editor = Editor.create document []
                    let other = Editor.create document []
                    editor.Dispatch [ Tx.selection (EditorSelection.single 3 3) ]
                    let mutable runs = 0

                    Api.createEffect (fun () ->
                        editor.Selection.Value |> ignore
                        runs <- runs + 1)

                    Api.flush ()
                    Expect.equal runs 1 "first run"
                    other.Dispatch [ insert 8 "zz" ]
                    Api.flush ()
                    Expect.equal runs 1 "an edit after the cursor"
                    other.Dispatch [ insert 0 "zz" ]
                    Api.flush ()
                    Expect.equal runs 2 "an edit before the cursor"
                    Expect.equal editor.Selection.Value.Main.Head 5 "moved")

            testCase "two editors converge with separate selections"
            <| fun () ->
                withGraph (fun _ ->
                    let document = Document.create (Text.ofString "0123456789abcdef")
                    let a = Editor.create document []
                    let b = Editor.create document []
                    a.Dispatch [ Tx.selection (EditorSelection.single 3 3) ]
                    b.Dispatch [ Tx.selection (EditorSelection.single 10 10) ]
                    a.Dispatch [ insert 3 "XY"; Tx.selection (EditorSelection.single 5 5) ]
                    Expect.equal (Text.toString a.State.Value.Doc) "012XY3456789abcdef" "a's text"
                    Expect.isTrue (obj.ReferenceEquals (a.State.Value.Doc, b.State.Value.Doc)) "one text for both"
                    Expect.equal a.Selection.Value.Main.Head 5 "a's cursor"
                    Expect.equal b.Selection.Value.Main.Head 12 "b's cursor moved with the text")

            testCase "the fold under coalesced dispatch"
            <| fun () ->
                withGraph (fun _ ->
                    let document = Document.create (Text.ofString "")
                    let editor = Editor.create document [ StateField.extension editCount ]
                    let updates = editor.Updates ()
                    let mutable runs = 0
                    let mutable last = Unchecked.defaultof<Update>

                    Api.createEffect (fun () ->
                        last <- updates.Read ()
                        runs <- runs + 1)

                    Api.flush ()
                    Expect.equal runs 1 "first run"
                    Expect.equal last.Transactions.Length 0 "an empty update first"

                    Api.batch (fun () ->
                        editor.Dispatch [ insert 0 "a" ]
                        editor.Dispatch [ insert 1 "b" ]
                        editor.Dispatch [ insert 0 "c"; Tx.selection (EditorSelection.single 1 1) ])

                    Api.flush ()
                    Expect.equal runs 2 "one run for three dispatches"
                    Expect.equal last.Transactions.Length 3 "three transactions"
                    Expect.equal (Text.toString last.StartState.Doc) "" "start state"
                    Expect.equal (Text.toString last.State.Doc) "cab" "state"
                    Expect.equal (Text.toString (ChangeSet.apply last.StartState.Doc last.Changes)) "cab" "the changes are composed"
                    Expect.isTrue last.DocChanged "doc changed"
                    Expect.isTrue last.SelectionSet "selection set"
                    Expect.equal (editor.Field editCount).Value 3 "the field saw each transaction")

            testCase "a dispatch from an effect"
            <| fun () ->
                withGraph (fun _ ->
                    let document = Document.create (Text.ofString "abc")
                    let editor = Editor.create document []
                    let reacting = editor.Updates ()
                    let watching = editor.Updates ()
                    let seen = ResizeArray<int64> ()

                    Api.createEffect (fun () ->
                        let update = reacting.Read ()

                        if update.DocChanged then
                            editor.Dispatch [ Tx.selection (EditorSelection.single 0 0) ])

                    Api.createEffect (fun () ->
                        for transaction in (watching.Read ()).Transactions do
                            seen.Add transaction.Seq)

                    Api.flush ()
                    editor.Dispatch [ insert 3 "d"; Tx.selection (EditorSelection.single 4 4) ]
                    Api.flush ()
                    Expect.sequenceEqual seen [ 1L; 2L ] "both transactions reach the other reader once, in order"
                    Expect.equal editor.Selection.Value.Main.Head 0 "the effect's transaction applied"
                    Expect.equal document.Head.Value 2L "the effect dispatched once")

            testCase "a transaction of another editor carries no selection or effect"
            <| fun () ->
                withGraph (fun _ ->
                    let document = Document.create (Text.ofString "abc")
                    let a = Editor.create document []
                    let b = Editor.create document []
                    let mark = StateEffect.defineSimple<int> ()
                    let updates = b.Updates ()
                    updates.Read () |> ignore
                    a.Dispatch [ insert 0 "x"; Tx.selection (EditorSelection.single 1 1); Tx.effect (mark.Of 1); Tx.scrollIntoView ]
                    let fromA = (updates.Read ()).Transactions[0]
                    Expect.equal fromA.Selection ValueNone "selection"
                    Expect.equal fromA.Effects.Length 0 "effects"
                    Expect.isFalse fromA.ScrollIntoView "scroll"
                    document.Dispatch [ insert 0 "y"; Tx.effect (mark.Of 2) ]
                    let outside = (updates.Read ()).Transactions[0]
                    Expect.equal outside.Effects.Length 1 "an outside source's effects reach every editor")

            testCase "a compartment reconfigured through dispatch"
            <| fun () ->
                withGraph (fun _ ->
                    let tabs = Compartment.create ()
                    let editor = Editor.create (Document.create (Text.ofString "")) [ tabs.Of (EditorState.tabSize.Of 2) ]
                    let tabSize = editor.Facet EditorState.tabSize
                    Expect.equal tabSize.Value 2 "before"
                    editor.Dispatch [ Tx.effect (tabs.Reconfigure (EditorState.tabSize.Of 8)) ]
                    Expect.equal tabSize.Value 8 "after")

            testCase "undo in either editor undoes the document's last edit"
            <| fun () ->
                withGraph (fun _ ->
                    let document = Document.create (Text.ofString "0123456789")
                    let a = Editor.create document []
                    let b = Editor.create document []
                    a.Dispatch [ Tx.selection (EditorSelection.single 2 2) ]
                    b.Dispatch [ Tx.selection (EditorSelection.single 8 8) ]
                    a.Dispatch [ insert 2 "aa"; Tx.selection (EditorSelection.single 4 4); Tx.isolateHistory ]
                    Expect.equal b.Selection.Value.Main.Head 10 "b moved"
                    Expect.isTrue (Editor.undo b) "b undoes a's edit"
                    Expect.equal (Text.toString document.Text.Value) "0123456789" "undone"
                    Expect.equal b.Selection.Value.Main.Head 8 "the undoing editor's selection is mapped"
                    Expect.equal a.Selection.Value.Main.Head 2 "the other editor's selection is mapped"
                    Expect.isTrue (Editor.redo a) "a redoes"
                    Expect.equal (Text.toString document.Text.Value) "01aa23456789" "redone"
                    Expect.isTrue (Editor.undo a) "a undoes its own edit"
                    Expect.equal a.Selection.Value.Main.Head 2 "and gets back the selection it had before the edit"
                    Expect.isFalse (Editor.undo a) "nothing left")

            testCase "an edit and a selection in one batch leave every editor on the document's text"
            <| fun () ->
                withGraph (fun _ ->
                    let document = Document.create (Text.ofString "abc")
                    let a = Editor.create document []
                    let b = Editor.create document []

                    Api.batch (fun () ->
                        a.Dispatch [ insert 0 "x" ]
                        a.Dispatch [ Tx.selection (EditorSelection.single 2 2) ])

                    Expect.isTrue (obj.ReferenceEquals (b.State.Value.Doc, document.Text.Value)) "the other editor"
                    Expect.isTrue (obj.ReferenceEquals (a.State.Value.Doc, document.Text.Value)) "the editor that made them")

            testCase "a fold that fails is taken up again from where it started"
            <| fun () ->
                withGraph (fun _ ->
                    let failing = ref true

                    let fragile =
                        StateField.define
                            {
                                Create = fun _ -> 0
                                Update =
                                    fun value transaction ->
                                        if failing.Value then
                                            failing.Value <- false
                                            failwith "once"
                                        elif transaction.DocChanged then
                                            value + 1
                                        else
                                            value
                            }

                    let document = Document.create (Text.ofString "abc")
                    let editor = Editor.create document [ StateField.extension fragile ]
                    document.Dispatch [ insert 0 "x" ]
                    Expect.throws (fun () -> editor.State.Value |> ignore) "the first fold fails"
                    document.Dispatch [ insert 0 "y" ]
                    Expect.equal (Text.toString editor.State.Value.Doc) "yxabc" "the next fold takes both transactions"
                    Expect.equal (EditorState.field fragile editor.State.Value) 2 "and the field sees both")

            testCase "read-only drops user edits"
            <| fun () ->
                withGraph (fun _ ->
                    let document = Document.create (Text.ofString "abc")
                    let editor = Editor.create document [ EditorState.readOnly.Of true ]
                    editor.Dispatch [ insert 0 "x"; Tx.userEvent "input.type" ]
                    Expect.equal (Text.toString document.Text.Value) "abc" "a user edit is dropped"
                    editor.Dispatch [ Tx.selection (EditorSelection.single 2 2); Tx.userEvent "select" ]
                    Expect.equal editor.Selection.Value.Main.Head 2 "a selection applies"
                    editor.Dispatch [ insert 0 "x" ]
                    Expect.equal (Text.toString document.Text.Value) "xabc" "an edit without a user event applies"
                    Expect.isFalse (Editor.undo editor) "undo is a user edit")

            testCase "another editor's deletion leaves no selection beyond the document"
            <| fun () ->
                withGraph (fun _ ->
                    let document = Document.create (Text.ofString "0123456789")
                    let first = Editor.create document []
                    let second = Editor.create document [ EditorState.allowMultipleSelections.Of true ]
                    // One range at the end and one across what the other editor is about to delete.
                    second.Dispatch [ Tx.selection (EditorSelection.create [ EditorSelection.range 10 10; EditorSelection.range 4 9 ] 0) ]
                    first.Dispatch [ Tx.change { From = 6; To = 10; Insert = Text.empty } ]
                    Expect.equal (Text.toString second.Text.Value) "012345" "the tail is gone in both"

                    for range in second.Selection.Value.Ranges do
                        Expect.isLessThanOrEqual range.To 6 "a range ends inside the document"
                        Expect.isGreaterThanOrEqual range.From 0 "and starts inside it"

                    Expect.equal second.Selection.Value.Main.Head 6 "the cursor at the end is at the new end"
                    second.Dispatch [ Tx.change { From = 6; To = 6; Insert = Text.ofString "!" } ]
                    Expect.equal (Text.toString first.Text.Value) "012345!" "and an edit there lands")

            testCase "an invalid change raises and leaves the log alone"
            <| fun () ->
                withGraph (fun _ ->
                    let document = Document.create (Text.ofString "abc")
                    let editor = Editor.create document []
                    Expect.throwsT<ArgumentException> (fun () -> editor.Dispatch [ Tx.change { From = 2; To = 9; Insert = Text.empty } ]) "range"
                    Expect.equal document.Head.Value 0L "head"
                    editor.Dispatch [ Tx.selection (EditorSelection.single 99 99) ]
                    Expect.equal editor.Selection.Value.Main.Head 3 "a selection beyond the document is clamped")

            testCase "disposing the owner releases the editor's readers"
            <| fun () ->
                withGraph (fun _ ->
                    let document = Document.create (Text.ofString "")

                    let owner =
                        Api.createRoot (fun owner ->
                            let editor = Editor.create document []
                            editor.Updates () |> ignore

                            for index in 0..4 do
                                document.Dispatch [ insert index "x" ]

                            Expect.equal document.LogLength 5 "kept for the editor"
                            owner)

                    owner.Dispose ()
                    document.Dispatch [ insert 0 "y" ]
                    Expect.equal document.LogLength 0 "released")
        ]
