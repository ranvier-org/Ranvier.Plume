module Ranvier.Plume.Tests.HistoryTests

open System
open Expecto
open Ranvier.Plume
open Ranvier.Plume.Tests.Helpers

/// An editor state and a history folded side by side, with no graph.
type private Sim =
    {
        State: EditorState
        History: HistoryState
        Clock: int64
    }

let private start (config: HistoryConfig) (text: string) : Sim =
    {
        State =
            EditorState.create
                {
                    Doc = Text.ofString text
                    Selection = ValueNone
                    Extensions = []
                }
        History = HistoryState.empty config
        Clock = 1000L
    }

let private step (specs: TxSpec list) (sim: Sim) : Sim =
    let built = EditorState.transaction specs sim.State

    let transaction =
        { built with
            Source = 1
            Annotations = Array.append built.Annotations [| Annotations.time.Of sim.Clock |]
        }

    { sim with
        State = EditorState.apply transaction sim.State
        History = HistoryState.apply transaction sim.History
    }

let private wait (milliseconds: int64) (sim: Sim) : Sim =
    { sim with Clock = sim.Clock + milliseconds }

let private insert (at: int) (text: string) : TxSpec =
    Tx.change { From = at; To = at; Insert = Text.ofString text }

/// Typing: an insertion at `at` that leaves the cursor after it.
let private typeAt (at: int) (text: string) : TxSpec list =
    [ insert at text; Tx.selection (EditorSelection.single (at + text.Length) (at + text.Length)); Tx.userEvent "input.type" ]

let private undo (sim: Sim) : Sim option =
    match HistoryState.undoSpec 1 sim.State.Selection sim.History with
    | ValueSome specs -> Some (step specs sim)
    | ValueNone -> None

let private redo (sim: Sim) : Sim option =
    match HistoryState.redoSpec 1 sim.State.Selection sim.History with
    | ValueSome specs -> Some (step specs sim)
    | ValueNone -> None

let private text (sim: Sim) = Text.toString sim.State.Doc
let private head (sim: Sim) = sim.State.Selection.Main.Head

[<Tests>]
let tests =
    testList
        "History"
        [
            testCase "nothing to undo or redo"
            <| fun () ->
                let sim = start History.defaults "hello"
                Expect.isNone (undo sim) "undo"
                Expect.isNone (redo sim) "redo"
                Expect.equal (HistoryState.undoDepth sim.History) 0 "undo depth"

            testCase "undo then redo restores text and selection"
            <| fun () ->
                let edited = start History.defaults "hello world" |> step [ Tx.selection (EditorSelection.single 5 5) ] |> step (typeAt 5 ",")
                Expect.equal (text edited) "hello, world" "edited"
                Expect.equal (head edited) 6 "cursor after typing"
                let undone = (undo edited).Value
                Expect.equal (text undone) "hello world" "undone"
                Expect.equal (head undone) 5 "the selection before the edit"
                Expect.equal (HistoryState.undoDepth undone.History, HistoryState.redoDepth undone.History) (0, 1) "depths"
                let redone = (redo undone).Value
                Expect.equal (text redone) "hello, world" "redone"
                Expect.equal (head redone) 6 "the selection before the undo"
                Expect.equal (HistoryState.undoDepth redone.History, HistoryState.redoDepth redone.History) (1, 0) "depths after redo"
                Expect.equal (text (undo redone).Value) "hello world" "undone again"

            testCase "typing groups by time"
            <| fun () ->
                let typed =
                    start History.defaults ""
                    |> step (typeAt 0 "a")
                    |> wait 100L
                    |> step (typeAt 1 "b")
                    |> wait 100L
                    |> step (typeAt 2 "c")
                    |> wait 600L
                    |> step (typeAt 3 "d")

                Expect.equal (HistoryState.undoDepth typed.History) 2 "two items"
                let once = (undo typed).Value
                Expect.equal (text once) "abc" "the late character"
                Expect.equal (text (undo once).Value) "" "the group"

            testCase "edits apart do not join"
            <| fun () ->
                let typed = start History.defaults "0123456789" |> step (typeAt 0 "a") |> wait 10L |> step (typeAt 8 "b")
                Expect.equal (HistoryState.undoDepth typed.History) 2 "two items"

            testCase "a group id joins edits apart and in time"
            <| fun () ->
                let group = History.newGroupId ()
                Expect.notEqual group (History.newGroupId ()) "ids differ"

                let grouped =
                    start History.defaults "0123456789"
                    |> step [ insert 0 "a"; Tx.group group ]
                    |> wait 5000L
                    |> step [ insert 9 "b"; Tx.group group ]
                    |> step [ insert 5 "c" ]

                Expect.equal (HistoryState.undoDepth grouped.History) 2 "the group, and the edit after it"
                let once = (undo grouped).Value
                Expect.equal (text once) "a01234567b89" "the edit after the group"
                Expect.equal (text (undo once).Value) "0123456789" "the group as one item"

            testCase "isolateHistory splits"
            <| fun () ->
                let typed =
                    start History.defaults ""
                    |> step (typeAt 0 "a")
                    |> step (Tx.isolateHistory :: typeAt 1 "b")
                    |> step (typeAt 2 "c")

                Expect.equal (HistoryState.undoDepth typed.History) 3 "three items"

            testCase "a new edit clears redo"
            <| fun () ->
                let sim = start History.defaults "x" |> step (typeAt 1 "a")
                let undone = (undo sim).Value
                Expect.equal (HistoryState.redoDepth undone.History) 1 "redo available"
                let edited = undone |> wait 1000L |> step (typeAt 0 "b")
                Expect.equal (HistoryState.redoDepth edited.History) 0 "redo cleared"
                Expect.isNone (redo edited) "nothing to redo"

            testCase "an edit history leaves out moves the undo"
            <| fun () ->
                let sim =
                    start History.defaults "0123456789"
                    |> step [ insert 5 "abc" ]
                    |> step [ insert 0 "XX"; Tx.addToHistory false ]

                Expect.equal (text sim) "XX01234abc56789" "both edits"
                Expect.equal (HistoryState.undoDepth sim.History) 1 "one item"
                let undone = (undo sim).Value
                Expect.equal (text undone) "XX0123456789" "the recorded edit is undone where it now is"
                Expect.equal (text (redo undone).Value) "XX01234abc56789" "and redone"

            testCase "items below a moved item move when it is undone"
            <| fun () ->
                let sim =
                    start History.defaults "0123456789"
                    |> step (Tx.isolateHistory :: [ insert 2 "a" ])
                    |> step (Tx.isolateHistory :: [ insert 8 "b" ])
                    |> step [ insert 0 "XX"; Tx.addToHistory false ]

                Expect.equal (text sim) "XX01a23456b789" "all edits"
                let once = (undo sim).Value
                Expect.equal (text once) "XX01a23456789" "the top item"
                let twice = (undo once).Value
                Expect.equal (text twice) "XX0123456789" "the item below"
                Expect.equal (text (redo (redo twice).Value).Value) "XX01a23456b789" "both redone"

            testCase "an edit whose text was deleted has nothing to undo"
            <| fun () ->
                let sim =
                    start History.defaults "0123456789"
                    |> step [ insert 5 "abc" ]
                    |> step [ Tx.change { From = 3; To = 10; Insert = Text.empty }; Tx.addToHistory false ]

                Expect.equal (text sim) "012789" "the insertion is gone"
                Expect.equal (HistoryState.undoDepth sim.History) 0 "no item left"
                Expect.isNone (undo sim) "undo"

            testCase "depth bounds the stack"
            <| fun () ->
                let mutable sim = start { History.defaults with Depth = 3 } ""

                for index in 0..4 do
                    sim <- sim |> step (Tx.isolateHistory :: [ insert index "x" ])

                Expect.equal (HistoryState.undoDepth sim.History) 3 "three items"
                let mutable undone = 0
                let mutable current = Some sim

                while current.IsSome do
                    match undo current.Value with
                    | Some next ->
                        undone <- undone + 1
                        current <- Some next
                    | None ->
                        sim <- current.Value
                        current <- None

                Expect.equal undone 3 "three undos"
                Expect.equal (text sim) "xx" "the oldest edits stay"

            testCase "the position returns after undo"
            <| fun () ->
                let sim = start History.defaults "hello"
                let origin = HistoryState.position sim.History
                let edited = sim |> step (typeAt 0 "a")
                Expect.notEqual (HistoryState.position edited.History) origin "moved"
                let undone = (undo edited).Value
                Expect.equal (HistoryState.position undone.History) origin "back"
                Expect.equal (HistoryState.position (redo undone).Value.History) (HistoryState.position edited.History) "forward"

                let leftOut = sim |> step [ insert 0 "z"; Tx.addToHistory false ]
                Expect.notEqual (HistoryState.position leftOut.History) origin "an edit history leaves out moves the position"
                let selectionOnly = sim |> step [ Tx.selection (EditorSelection.single 2 2) ]
                Expect.equal (HistoryState.position selectionOnly.History) origin "a selection leaves the position"

            testCase "undoing everything restores the text, and redoing everything the edits"
            <| fun () ->
                forSeeds 500 (fun rng ->
                    let source = randomString rng "ab\n" 30
                    let mutable sim = start { History.defaults with Depth = 100 } source
                    let mutable edits = 0

                    for _ in 0 .. rng.Next 12 do
                        let specs = ChangeSetTests.randomSpecs rng (Text.length sim.State.Doc)

                        if not (List.isEmpty specs) then
                            let before = sim.History
                            sim <- sim |> wait (int64 (rng.Next 900)) |> step [ Tx.changes specs; Tx.userEvent "input.type" ]

                            if HistoryState.undoDepth sim.History > HistoryState.undoDepth before then
                                edits <- edits + 1

                    let final = text sim
                    let mutable back = sim
                    let mutable undone = 0
                    let mutable more = true

                    while more do
                        match undo back with
                        | Some next ->
                            back <- next
                            undone <- undone + 1
                        | None -> more <- false

                    Expect.equal (text back) source "every edit undone"
                    Expect.equal undone (HistoryState.undoDepth sim.History) "one undo per item"
                    more <- true

                    while more do
                        match redo back with
                        | Some next -> back <- next
                        | None -> more <- false

                    Expect.equal (text back) final "every edit redone")

            testCase "undo and redo with edits history leaves out in between"
            <| fun () ->
                forSeeds 500 (fun rng ->
                    // A recorded edit and an edit left out commute: undoing the recorded one gives the text the
                    // edit left out alone would have given, when the two do not overlap.
                    let source = randomString rng "ab" 30 + "0123456789"
                    let at = rng.Next (0, 6)
                    let later = source.Length - rng.Next (0, 5)
                    let recorded = start History.defaults source |> step [ insert at "REC" ]
                    let both = recorded |> step [ insert (later + 3) "out"; Tx.addToHistory false ]
                    let alone = start History.defaults source |> step [ insert later "out"; Tx.addToHistory false ]
                    Expect.equal (text (undo both).Value) (text alone) "undone under the other edit")
        ]
