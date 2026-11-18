module Ranvier.Plume.View.Tests.ViewTests

open System
open Expecto
open Ranvier
open Ranvier.Dom
open Ranvier.Plume
open Ranvier.Plume.View
open Ranvier.Plume.View.Tests.Host

let private manyLines (count: int) = Seq.init count (fun index -> "line " + string index)

let private standard: Extension list =
    [
        Keymap.extension Keymap.defaults
        LineNumbers.extension LineNumbers.defaults
        ActiveLine.extension
        DrawSelection.extension
        Cursor.extension { Cursor.defaults with BlinkRate = 0.0 }
    ]

let private insert (at: int) (text: string) : TxSpec =
    Tx.change { From = at; To = at; Insert = Text.ofString text }

let private gutterOf (row: Element) : string =
    match row.querySelector "[data-pm~=gutter]" with
    | null -> ""
    | cell -> cell.textContent

/// The line a row shows: its text after the gutter markers.
let private textOf (row: Element) : string =
    row.textContent.Substring (gutterOf row).Length

/// The elements of `selector` that are laid out with an area.
let private shown (mounted: Mounted) (selector: string) : DOMRect[] =
    mounted.Query selector
    |> Array.map (fun element -> element.getBoundingClientRect ())
    |> Array.filter (fun rect -> rect.width > 0.0 && rect.height > 0.0)

let private viewportOf (mounted: Mounted) : Element = (mounted.Query "[data-plume]")[0]

[<Tests>]
let tests =
    testSequenced
    <| testList
        "EditorView"
        [
            testCase "mounts a 1,000,000-line document with a row pool"
            <| fun () ->
                withView (manyLines 1_000_000) standard 800 (fun mounted ->
                    let view = mounted.View
                    Expect.floatClose Accuracy.medium view.Height.Value 800.0 "viewport height"
                    Expect.isGreaterThan view.CharWidth.Value 1.0 "character width measured"
                    let rows = mounted.Rows
                    Expect.equal rows.Length (int (Math.Ceiling (800.0 / 20.0)) + 13) "row pool"
                    Expect.equal (textOf rows[0]) "line 0" "first row"
                    Expect.equal (gutterOf rows[0]) "1" "first line number"
                    Expect.equal (textOf rows[20]) "line 20" "row 20"
                    Expect.equal view.Viewport.Value.FirstLine 0 "first line shown"
                    Expect.equal view.Viewport.Value.LastLine 40 "last line shown"
                    Expect.floatClose Accuracy.medium view.GutterWidth.Value 72.0 "gutter width"
                    Expect.equal mounted.Editor.PageLines.Value 39 "page lines")

            testCase "scrolling three lines computes three rows"
            <| fun () ->
                withView (manyLines 1_000_000) standard 800 (fun mounted ->
                    let view = mounted.View
                    let viewport = viewportOf mounted
                    // Away from the document's top, where the rows above the first line shown are in the pool.
                    mounted.Run (fun () -> view.ScrollTo (1000.0 * 20.0))
                    mounted.Step ()
                    let before = view.RowsWritten ()
                    let offset = viewport.scrollTop
                    mounted.Run (fun () -> view.ScrollBy 60.0)
                    Expect.equal (view.RowsWritten ()) before "a scroll request alone computes nothing"
                    mounted.Step ()
                    Expect.equal (view.RowsWritten () - before) 3L "rows computed"
                    Expect.equal (viewport.scrollTop - offset) 60 "the viewport scrolled"
                    Expect.equal view.Viewport.Value.FirstLine 1003 "first line shown"

                    let lines = mounted.Rows |> Array.map textOf |> Set.ofArray
                    Expect.equal lines.Count 53 "every row shows a line of its own"
                    Expect.isTrue (lines.Contains "line 997" && lines.Contains "line 1049") "the pool holds the overscan on both sides"
                    Expect.isFalse (lines.Contains "line 996") "a line that left the pool is gone")

            testCase "several scroll requests in one turn lay the rows out once"
            <| fun () ->
                withView (manyLines 1_000_000) standard 800 (fun mounted ->
                    let view = mounted.View
                    mounted.Run (fun () -> view.ScrollTo (1000.0 * 20.0))
                    mounted.Step ()
                    let before = view.RowsWritten ()

                    mounted.Run (fun () ->
                        view.ScrollBy 20.0
                        view.ScrollBy 20.0
                        view.ScrollBy 20.0)

                    mounted.Step ()
                    Expect.equal (view.RowsWritten () - before) 3L "three rows, computed once"
                    Expect.equal view.ScrollTop.Value 20060.0 "scrolled by the sum")

            testCase "a scroll request reaches the rows through the frame callback"
            <| fun () ->
                let scheduler = new ManualScheduler ()

                withViewOn (fun _ -> scheduler :> FrameScheduler) (manyLines 10_000) standard 800 (fun mounted ->
                    let view = mounted.View
                    Expect.equal scheduler.Pending 0 "a view at rest asks for no frame"

                    mounted.Run (fun () ->
                        view.ScrollTo 2000.0
                        view.ScrollBy 20.0)

                    Expect.equal scheduler.Pending 1 "one frame callback for two requests"
                    Expect.equal view.ScrollTop.Value 0.0 "the rows wait for the frame"
                    let before = view.RowsWritten ()

                    // No RunFrame here: the frame callback carries the request.
                    mounted.Run (fun () -> scheduler.RunFrames ())
                    Expect.equal view.ScrollTop.Value 2020.0 "committed by the frame"
                    Expect.equal view.Viewport.Value.FirstLine 101 "first line shown"
                    Expect.equal (view.RowsWritten () - before) 53L "the rows are laid out once, for the last request"
                    Expect.equal scheduler.Pending 0 "and the view rests again")

            testCase "an edit computes the rows of the lines it changed"
            <| fun () ->
                withView (manyLines 1_000_000) standard 800 (fun mounted ->
                    let view = mounted.View
                    let editor = mounted.Editor
                    let line = Text.line 5 editor.Text.Value
                    let before = view.RowsWritten ()
                    mounted.Run (fun () -> editor.Dispatch [ insert line.To "!" ])
                    Expect.equal (view.RowsWritten () - before) 1L "one row for a character"
                    Expect.equal (textOf mounted.Rows[5]) "line 5!" "the row shows the edit"

                    let again = view.RowsWritten ()
                    mounted.Run (fun () -> editor.Dispatch [ insert line.From "\n" ])
                    Expect.equal (view.RowsWritten () - again) 48L "a new line moves the rows from it to the pool's end"
                    Expect.equal (textOf mounted.Rows[5]) "" "the new empty line"
                    Expect.equal (textOf mounted.Rows[6]) "line 5!" "the line after it"
                    Expect.equal (gutterOf mounted.Rows[6]) "7" "its number")

            testCase "the cursor, the selection and the active line are drawn where the positions are"
            <| fun () ->
                withView (manyLines 1000) (EditorState.allowMultipleSelections.Of true :: standard) 800 (fun mounted ->
                    let view = mounted.View
                    let editor = mounted.Editor
                    let position = (Text.line 3 editor.Text.Value).From + 4
                    mounted.Run (fun () -> editor.Dispatch [ Tx.selection (EditorSelection.single position position) ])
                    mounted.Step ()

                    let rect = mounted.Run (fun () -> view.CoordsAtPos position)
                    Expect.floatClose Accuracy.medium rect.Left (72.0 + 4.0 * view.CharWidth.Value) "left"
                    Expect.equal (mounted.Run (fun () -> view.PosAtCoords (rect.Left + 0.5) (3.0 * 20.0 + 5.0))) position "PosAtCoords round-trips"
                    Expect.equal (mounted.Run (fun () -> view.PosAtCoords 5000.0 (3.0 * 20.0 + 5.0))) (Text.line 3 editor.Text.Value).To "a point past the line gives its end"

                    let cursors = shown mounted "[data-pm~=cursor]"
                    Expect.equal cursors.Length 1 "one cursor"
                    // The engine lays out in sixty-fourths of a pixel.
                    Expect.isLessThan (abs (cursors[0].left - rect.Left)) 0.05 "cursor left"
                    Expect.floatClose Accuracy.medium cursors[0].top 60.0 "cursor top"
                    Expect.floatClose Accuracy.medium cursors[0].width 2.0 "a bar"

                    let bands = shown mounted "[data-pm~=active-line]"
                    Expect.equal bands.Length 1 "one active line band"
                    Expect.floatClose Accuracy.medium bands[0].top 60.0 "band top"

                    let first = Text.line 1 editor.Text.Value
                    let second = Text.line 6 editor.Text.Value

                    mounted.Run (fun () ->
                        editor.Dispatch
                            [
                                Tx.selection (EditorSelection.create [ EditorSelection.range first.From (first.From + 3); EditorSelection.range second.From (Text.line 8 editor.Text.Value).To ] 0)
                            ])

                    mounted.Step ()
                    let selected = shown mounted "[data-pm~=selection]"
                    Expect.equal selected.Length 4 "one rectangle for the first range and one per line of the second"
                    Expect.equal (shown mounted "[data-pm~=cursor]").Length 2 "a cursor per range")

            testCase "keys run the keymap and printable keys insert"
            <| fun () ->
                withView [ "hello" ] standard 800 (fun mounted ->
                    let editor = mounted.Editor
                    let root = (viewportOf mounted).parentElement
                    use events = Ranvier.View.WebCore.EventConstruction.forHost mounted.Window.Host

                    let press (key: string) (modifiers: EventModifiers) =
                        let event =
                            events.Create (
                                "keydown",
                                Ranvier.Dom.EventConstruction.Keyboard (
                                    { EventOptions.Default with
                                        Bubbles = true
                                        Cancelable = true
                                    },
                                    { KeyboardFields.Default with
                                        Key = key
                                        Modifiers = modifiers
                                    }
                                )
                            )

                        root.dispatchEvent event |> ignore
                        mounted.Step ()

                    mounted.Run (fun () -> mounted.View.Focus ())
                    press "a" EventModifiers.None
                    Expect.equal (Text.toString editor.Text.Value) "ahello" "a printable key inserts"
                    press "ArrowRight" EventModifiers.None
                    press "Backspace" EventModifiers.None
                    Expect.equal (Text.toString editor.Text.Value) "aello" "bound keys run commands"
                    press "z" EventModifiers.Control
                    Expect.equal (Text.toString editor.Text.Value) "ahello" "Ctrl-z undoes"
                    press "End" EventModifiers.Shift
                    Expect.equal (editor.Selection.Value.Main.Anchor, editor.Selection.Value.Main.Head) (2, 6) "Shift-End extends"
                    press " " EventModifiers.None
                    Expect.equal (Text.toString editor.Text.Value) "ah " "the space key replaces the selection")

            testCase "a read-only editor ignores typing"
            <| fun () ->
                withView [ "hello" ] (EditorState.readOnly.Of true :: standard) 800 (fun mounted ->
                    let root = (viewportOf mounted).parentElement
                    use events = Ranvier.View.WebCore.EventConstruction.forHost mounted.Window.Host

                    let event =
                        events.Create (
                            "keydown",
                            Ranvier.Dom.EventConstruction.Keyboard (
                                { EventOptions.Default with
                                    Bubbles = true
                                    Cancelable = true
                                },
                                { KeyboardFields.Default with Key = "a" }
                            )
                        )

                    root.dispatchEvent event |> ignore
                    mounted.Step ()
                    Expect.equal (Text.toString mounted.Editor.Text.Value) "hello" "text")

            testCase "a transaction that asks for it scrolls the cursor into view"
            <| fun () ->
                withView (manyLines 10_000) standard 800 (fun mounted ->
                    let editor = mounted.Editor
                    let far = (Text.line 5000 editor.Text.Value).From
                    mounted.Run (fun () -> editor.Dispatch [ Tx.selection (EditorSelection.single far far); Tx.scrollIntoView ])
                    mounted.Step ()
                    let shown = mounted.View.Viewport.Value
                    Expect.isTrue (shown.FirstLine <= 5000 && 5000 <= shown.LastLine) "the line is shown"
                    mounted.Run (fun () -> editor.Dispatch [ Tx.effect (EditorView.scrollIntoView.Of 0) ])
                    mounted.Step ()
                    Expect.equal mounted.View.Viewport.Value.FirstLine 0 "the effect scrolls to a position")

            testCase "a viewport without height"
            <| fun () ->
                withView (manyLines 100) standard 0 (fun mounted ->
                    Expect.equal mounted.Rows.Length 0 "no row in a viewport without height"
                    mounted.Run (fun () -> mounted.Editor.Dispatch [ insert 0 "x" ])
                    mounted.Step ())

            testCase "a document shorter than the row pool"
            <| fun () ->
                withView [ "one"; "two"; "three" ] standard 800 (fun mounted ->
                    let texts = mounted.Rows |> Array.map textOf
                    Expect.equal (texts |> Array.filter (fun text -> text.Length > 0)) [| "one"; "two"; "three" |] "three lines, the other rows blank"
                    mounted.Run (fun () -> mounted.View.ScrollTo 1e12)
                    mounted.Step ()
                    Expect.equal mounted.View.ScrollTop.Value 0.0 "nothing to scroll in a short document")

            testCase "a scroll beyond the end is clamped"
            <| fun () ->
                withView (manyLines 1000) standard 800 (fun mounted ->
                    mounted.Run (fun () -> mounted.View.ScrollTo 1e12)
                    mounted.Step ()
                    Expect.equal mounted.View.ScrollTop.Value (1000.0 * 20.0 - 800.0 + 40.0) "clamped to the scroll range"
                    Expect.equal mounted.View.Viewport.Value.LastLine 999 "the last line is shown")

            testCase "an edit, a read of the view and a selection in one batch reach the row"
            <| fun () ->
                withView (manyLines 1000) standard 800 (fun mounted ->
                    let editor = mounted.Editor
                    let line = Text.line 3 editor.Text.Value

                    mounted.Run (fun () ->
                        Api.batch (fun () ->
                            editor.Dispatch [ insert line.To "!" ]
                            // The read lays a window out between the two transactions.
                            mounted.View.Viewport.Value |> ignore
                            editor.Dispatch [ Tx.selection (EditorSelection.single 0 0) ]))

                    mounted.Step ()
                    Expect.equal (textOf mounted.Rows[3]) "line 3!" "the row shows the edit")

            testCase "rows stay on the sheet whatever the line height"
            <| fun () ->
                let state =
                    EditorState.create
                        {
                            Doc = Text.ofLines (manyLines 20_000)
                            Selection = ValueNone
                            Extensions = []
                        }

                let update: Update =
                    {
                        StartState = state
                        State = state
                        Transactions = Array.empty
                        Changes = ChangeSet.empty state.Doc.Length
                        Effects = Array.empty
                    }

                let config = RowConfig (Array.empty, Array.empty, ValueNone, true, true)
                let markup = MarkupCache (Theme.dark, 16)

                for lineHeight in [ 20.0; 24.0; 40.0 ] do
                    let mutable previous = ValueNone
                    let mutable line = 15_000

                    // Scrolling up by 37 lines at a time crosses the anchor's reach many times.
                    while line > 0 do
                        let window = Rows.compute previous update config (float line * lineHeight) 800.0 lineHeight 6 700 markup (ref 0L)
                        Expect.isGreaterThanOrEqual (Rows.Lead + window.Top - float window.Anchor * lineHeight) 0.0 "the scroll offset is positive"
                        Expect.isGreaterThanOrEqual (float (window.Start - window.Anchor) * lineHeight + Rows.Lead) 0.0 "the first row is on the sheet"
                        Expect.isLessThanOrEqual (float (window.Start + window.Pool - window.Anchor) * lineHeight + Rows.Lead) Rows.SheetHeight "the last row is on the sheet"
                        previous <- ValueSome window
                        line <- line - 37

            testCase "reconfiguring another compartment leaves the plugins and the rows alone"
            <| fun () ->
                let log = ResizeArray<string> ()
                let tabs = Compartment.create ()

                let plugin =
                    ViewPlugin.define (fun _ ->
                        log.Add "created"
                        Api.onCleanup (fun () -> log.Add "disposed")
                        0)

                withView (manyLines 100) (tabs.Of (EditorState.tabSize.Of 2) :: ViewPlugin.extension plugin :: standard) 800 (fun mounted ->
                    let before = mounted.View.RowsWritten ()
                    mounted.Run (fun () -> mounted.Editor.Dispatch [ Tx.effect (tabs.Reconfigure (EditorState.tabSize.Of 8)) ])
                    mounted.Step ()
                    Expect.sequenceEqual log [ "created" ] "the plugin stays"
                    Expect.equal (mounted.View.RowsWritten () - before) 0L "no row is computed again"
                    Expect.equal (shown mounted "[data-pm~=cursor]").Length 1 "the cursor is still drawn")

            testCase "scrolling after the document shrank starts from where the view is"
            <| fun () ->
                withView (manyLines 1000) standard 800 (fun mounted ->
                    let view = mounted.View
                    let editor = mounted.Editor
                    mounted.Run (fun () -> view.ScrollTo 15000.0)
                    mounted.Step ()
                    let from = (Text.line 100 editor.Text.Value).From
                    mounted.Run (fun () -> editor.Dispatch [ Tx.change { From = from; To = Text.length editor.Text.Value; Insert = Text.empty } ])
                    mounted.Step ()
                    let top = view.ScrollTop.Value
                    Expect.equal top (101.0 * 20.0 - 800.0 + 40.0) "the view is at the end of the shorter document"
                    mounted.Run (fun () -> view.ScrollBy -100.0)
                    mounted.Step ()
                    Expect.equal view.ScrollTop.Value (top - 100.0) "the first request moves it")

            testCase "a compartment adds and removes a gutter"
            <| fun () ->
                let numbers = Compartment.create ()

                withView (manyLines 100) [ numbers.Of (LineNumbers.extension LineNumbers.defaults); Cursor.extension { Cursor.defaults with BlinkRate = 0.0 } ] 800 (fun mounted ->
                    Expect.equal (gutterOf mounted.Rows[3]) "4" "numbers shown"
                    mounted.Run (fun () -> mounted.Editor.Dispatch [ Tx.effect (numbers.Reconfigure (Extension.many [])) ])
                    mounted.Step ()
                    Expect.equal (gutterOf mounted.Rows[3]) "" "numbers gone"
                    Expect.equal mounted.View.GutterWidth.Value 0.0 "no gutter width"
                    Expect.equal (textOf mounted.Rows[3]) "line 3" "the text stays"

                    mounted.Run (fun () ->
                        mounted.Editor.Dispatch [ Tx.effect (numbers.Reconfigure (LineNumbers.extension { LineNumbers.defaults with Format = fun number -> "#" + string number })) ])

                    mounted.Step ()
                    Expect.equal (gutterOf mounted.Rows[3]) "#4" "numbers back, in another format")

            testCase "a view plugin runs while mounted and is disposed with its compartment"
            <| fun () ->
                let log = ResizeArray<string> ()
                let slot = Compartment.create ()

                let plugin =
                    ViewPlugin.define (fun view ->
                        log.Add "created"
                        Api.onCleanup (fun () -> log.Add "disposed")
                        view.Editor.Id)

                withView [ "x" ] [ slot.Of (ViewPlugin.extension plugin) ] 800 (fun mounted ->
                    Expect.sequenceEqual log [ "created" ] "created at mount"
                    Expect.equal (mounted.Run (fun () -> ViewPlugin.tryGet plugin mounted.View)) (ValueSome mounted.Editor.Id) "its value"
                    mounted.Run (fun () -> mounted.Editor.Dispatch [ Tx.effect (slot.Reconfigure (Extension.many [])) ])
                    mounted.Step ()
                    Expect.sequenceEqual log [ "created"; "disposed" ] "disposed when its compartment drops it"
                    Expect.equal (mounted.Run (fun () -> ViewPlugin.tryGet plugin mounted.View)) ValueNone "no value")

            testCase "tokens are marked up with the theme's classes"
            <| fun () ->
                let language =
                    Language.ofLineLexer "demo" (fun line ->
                        if line.StartsWith "let " then
                            [| { From = 0; To = 3; Tag = Tag.keyword } |]
                        else
                            Array.empty)

                withView [ "let x = 1 < 2"; "plain & simple" ] [ Language.extension language ] 800 (fun mounted ->
                    Expect.equal mounted.Rows[0].innerHTML "<span class=\"t1\">let</span> x = 1 &lt; 2" "a keyword span, and escaped text"
                    Expect.equal (textOf mounted.Rows[1]) "plain & simple" "escaped text reads back")
        ]
