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

/// The element that holds a row's line.
let private lineOf (row: Element) : Element = row.lastChild :?> Element

/// The rows whose gutter markers take the text colour.
let private inked (mounted: Mounted) : string[] =
    mounted.Rows
    |> Array.filter (fun row -> (row.getAttribute "style").Contains "--pm-gutter-ink")
    |> Array.map gutterOf

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
                    Expect.floatClose Accuracy.medium (rows[20].getBoundingClientRect ()).top 400.0 "row 20 is twenty lines down"
                    Expect.floatClose Accuracy.medium (rows[20].getBoundingClientRect ()).height 20.0 "a row is one line high"
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
                    Expect.sequenceEqual (inked mounted) [ "4" ] "the gutter marker of the cursor's line takes the text colour"

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

            testCase "an edit to a line leaves its gutter marker's element in place"
            <| fun () ->
                withView (manyLines 1000) standard 800 (fun mounted ->
                    let editor = mounted.Editor
                    let line = Text.line 3 editor.Text.Value
                    (mounted.Rows[3].querySelector "[data-pm~=gutter]").setAttribute ("data-seen", "before")
                    mounted.Run (fun () -> editor.Dispatch [ insert line.To "!" ])
                    mounted.Step ()
                    Expect.equal (textOf mounted.Rows[3]) "line 3!" "the row shows the edit"
                    Expect.equal ((mounted.Rows[3].querySelector "[data-pm~=gutter]").getAttribute "data-seen") "before" "the marker is the element it was"

                    // A row that takes another line takes that line's markers.
                    mounted.Run (fun () -> mounted.View.ScrollTo (500.0 * 20.0))
                    mounted.Step ()
                    Expect.equal (mounted.Query "[data-seen]").Length 0 "a row given another line has new markers")

            testCase "a press places the cursor, a move selects, and a press with Shift extends"
            <| fun () ->
                withView (manyLines 1000) standard 800 (fun mounted ->
                    let view = mounted.View
                    let editor = mounted.Editor
                    let surface = (Surface.find editor).Value
                    let x (column: int) = view.GutterWidth.Value + float column * view.CharWidth.Value + 0.5
                    let y (line: int) = float line * 20.0 + 5.0
                    let at (line: int) (column: int) = (Text.line line editor.Text.Value).From + column
                    let ends () = let main = editor.Selection.Value.Main in main.Anchor, main.Head

                    mounted.Run (fun () -> PointerInput.press surface (x 2) (y 3) false)
                    Expect.equal (ends ()) (at 3 2, at 3 2) "the cursor is where the press was"
                    mounted.Run (fun () -> PointerInput.move surface (x 4) (y 5))
                    Expect.equal (ends ()) (at 3 2, at 5 4) "a move takes the head with it"
                    mounted.Run (fun () -> PointerInput.release surface)
                    mounted.Run (fun () -> PointerInput.move surface (x 0) (y 0))
                    Expect.equal (ends ()) (at 3 2, at 5 4) "a move after the release selects nothing"
                    mounted.Run (fun () -> PointerInput.press surface (x 1) (y 7) true)
                    Expect.equal (ends ()) (at 3 2, at 7 1) "a press with Shift keeps the anchor"
                    mounted.Run (fun () -> PointerInput.release surface)
                    mounted.Step ()
                    Expect.equal (shown mounted "[data-pm~=selection]").Length 5 "the selection is drawn on each of its lines"
                    Expect.sequenceEqual (inked mounted) [ "8" ] "the line of the head is the active one"

                    // A point in the gutter is the start of its line.
                    mounted.Run (fun () -> PointerInput.press surface 10.0 (y 9) false)
                    Expect.equal (ends ()) (at 9 0, at 9 0) "a press in the gutter"
                    mounted.Run (fun () -> PointerInput.release surface)
                    Expect.equal (HistoryState.undoDepth (mounted.Run (fun () -> editor.Document.History.Value))) 0 "a selection is no history item")

            testCase "a press below the last line lands in the last line"
            <| fun () ->
                withView [ "ab"; "cd" ] standard 800 (fun mounted ->
                    let view = mounted.View
                    let surface = (Surface.find mounted.Editor).Value
                    mounted.Run (fun () -> PointerInput.press surface (view.GutterWidth.Value + view.CharWidth.Value + 0.5) 500.0 false)
                    Expect.equal mounted.Editor.Selection.Value.Main.Head 4 "after the first character of the last line"
                    mounted.Run (fun () -> PointerInput.release surface))

            testCase "the wheel scrolls by its delta and the scrollbar by where its thumb is put"
            <| fun () ->
                withView (manyLines 1000) standard 800 (fun mounted ->
                    let view = mounted.View
                    let surface = (Surface.find mounted.Editor).Value
                    // The scroll range of 1,000 lines of 20 pixels in 800 pixels, with two lines of room after the last.
                    let range = 1000.0 * 20.0 - 800.0 + 40.0

                    mounted.Run (fun () -> PointerInput.wheel surface 120.0)
                    mounted.Step ()
                    Expect.equal view.ScrollTop.Value 120.0 "the wheel's delta"
                    mounted.Run (fun () -> PointerInput.wheel surface -500.0)
                    mounted.Step ()
                    Expect.equal view.ScrollTop.Value 0.0 "the document's top stops it"

                    let thumb = 800.0 * 800.0 / (range + 800.0)
                    mounted.Run (fun () -> PointerInput.thumbTo surface 800.0 800.0)
                    mounted.Step ()
                    Expect.equal view.ScrollTop.Value range "the thumb at the track's end is the document's end"
                    Expect.equal (textOf mounted.Rows[999 % mounted.Rows.Length]) "line 999" "the last line is in the pool"
                    mounted.Run (fun () -> PointerInput.thumbTo surface 400.0 800.0)
                    mounted.Step ()
                    Expect.equal view.ScrollTop.Value (Math.Round (range * (400.0 - thumb / 2.0) / (800.0 - thumb))) "the thumb centred on the track's middle"
                    // A quarter of the way down, the thumb's centre and its top give different places.
                    mounted.Run (fun () -> PointerInput.thumbTo surface 200.0 800.0)
                    mounted.Step ()
                    Expect.equal view.ScrollTop.Value (Math.Round (range * (200.0 - thumb / 2.0) / (800.0 - thumb))) "the thumb centred a quarter of the way down"
                    Expect.isLessThan view.ScrollTop.Value (range / 4.0 - 100.0) "which is above a quarter of the range"
                    mounted.Run (fun () -> PointerInput.thumbTo surface -50.0 800.0)
                    mounted.Step ()
                    Expect.equal view.ScrollTop.Value 0.0 "a point above the track is the document's top")

            testCase "the engine's mouse input reaches the pointer listeners"
            <| fun () ->
                withView (manyLines 1000) standard 800 (fun mounted ->
                    let host = mounted.Window.Host
                    let view = mounted.View
                    let editor = mounted.Editor
                    // The view starts at the window's corner, so a point of the window is a point of the viewport.
                    let x (column: int) = int (Math.Round (view.GutterWidth.Value + float column * view.CharWidth.Value))
                    let y (line: int) = line * 20 + 10
                    let at (line: int) (column: int) = (Text.line line editor.Text.Value).From + column
                    let ends () = let main = editor.Selection.Value.Main in main.Anchor, main.Head

                    // The host's actions are 0 for a move, 1 for a press and 2 for a release; button -1 is none and 0
                    // the left one; modifier 1 is Shift.
                    let mouse (action: int) (px: int) (py: int) (button: int) (modifiers: int) =
                        host.Mouse (action, px, py, button, (if action = 0 then 0 else 1), modifiers) |> ignore
                        mounted.Step ()

                    mouse 0 (x 2) (y 3) -1 0
                    Expect.equal (ends ()) (0, 0) "a move without a button selects nothing"
                    // A hidden window never has the desktop's focus. This is what a window tells the engine when it
                    // gains it; without it the engine sends no focus event.
                    host.SetFocused true
                    Expect.isFalse view.HasFocus.Value "the view starts without the keyboard"
                    mouse 1 (x 2) (y 3) 0 0
                    Expect.equal (ends ()) (at 3 2, at 3 2) "a press puts the cursor under the pointer"
                    Expect.isTrue view.HasFocus.Value "and gives the view the keyboard"
                    mouse 0 (x 4) (y 5) 0 0
                    Expect.equal (ends ()) (at 3 2, at 5 4) "a move with the button down takes the head with it"
                    // The viewport holds the pointer: over the scrollbar, the move still selects and scrolls nothing.
                    mouse 0 1275 (y 6) 0 0
                    Expect.equal (ends ()) (at 3 2, (Text.line 6 editor.Text.Value).To) "the viewport keeps the pointer that left it"
                    Expect.equal view.ScrollTop.Value 0.0 "and the scrollbar under it does not move"
                    mouse 2 1275 (y 6) 0 0
                    mouse 0 (x 0) (y 0) -1 0
                    Expect.equal (ends ()) (at 3 2, (Text.line 6 editor.Text.Value).To) "a move after the release selects nothing"
                    mouse 1 (x 1) (y 8) 0 1
                    Expect.equal (ends ()) (at 3 2, at 8 1) "a press with Shift moves the head and keeps the anchor"
                    mouse 2 (x 1) (y 8) 0 1
                    Expect.equal (shown mounted "[data-pm~=selection]").Length 6 "the selection is drawn on each of its lines"
                    Expect.sequenceEqual (inked mounted) [ "9" ] "and the line of its head is the active one"
                    host.SetFocused false
                    mounted.Step ()
                    Expect.isFalse view.HasFocus.Value "the view loses the keyboard with its window")

            testCase "the engine's wheel input scrolls the view"
            <| fun () ->
                withView (manyLines 1000) standard 800 (fun mounted ->
                    let host = mounted.Window.Host
                    let view = mounted.View
                    host.Wheel (400, 300, 0.0f, 120.0f, 0) |> ignore
                    mounted.Step ()
                    Expect.equal view.ScrollTop.Value 120.0 "a wheel over the text scrolls by its delta"
                    Expect.equal (textOf mounted.Rows[6]) "line 6" "the rows follow"
                    Expect.floatClose Accuracy.medium (mounted.Rows[6].getBoundingClientRect ()).top 0.0 "line 6 is at the top"
                    host.Wheel (400, 300, 0.0f, -500.0f, 0) |> ignore
                    mounted.Step ()
                    Expect.equal view.ScrollTop.Value 0.0 "the document's top stops it"

                    // Away from the document's top, where the rows above the first line shown are in the pool.
                    mounted.Run (fun () -> view.ScrollTo (500.0 * 20.0))
                    mounted.Step ()
                    let before = view.RowsWritten ()
                    host.Wheel (400, 300, 0.0f, 20.0f, 0) |> ignore
                    host.Wheel (400, 300, 0.0f, 40.0f, 0) |> ignore
                    Expect.equal (view.RowsWritten ()) before "wheel events alone compute no row"
                    mounted.Step ()
                    Expect.equal view.ScrollTop.Value (500.0 * 20.0 + 60.0) "the frame takes both deltas"
                    Expect.equal (view.RowsWritten () - before) 3L "and lays out the three lines once")

            testCase "the engine's mouse input drags the scrollbar"
            <| fun () ->
                withView (manyLines 1000) standard 800 (fun mounted ->
                    let host = mounted.Window.Host
                    let view = mounted.View
                    let range = 1000.0 * 20.0 - 800.0 + 40.0
                    let thumb = 800.0 * 800.0 / (range + 800.0)
                    // Where the document is when the thumb's centre is `py` pixels down the track.
                    let place (py: float) = Math.Round (range * Math.Clamp ((py - thumb / 2.0) / (800.0 - thumb), 0.0, 1.0))
                    let track = (viewportOf mounted).parentElement.lastChild :?> Element
                    let thumbTop () = ((track.firstChild :?> Element).getBoundingClientRect ()).top

                    let mouse (action: int) (px: int) (py: int) (button: int) =
                        host.Mouse (action, px, py, button, (if action = 0 then 0 else 1), 0) |> ignore
                        mounted.Step ()

                    mouse 0 1273 200 -1
                    mouse 1 1273 200 0
                    Expect.equal view.ScrollTop.Value (place 200.0) "a press on the track centres the thumb there"
                    Expect.floatClose Accuracy.low (thumbTop ()) (200.0 - thumb / 2.0) "and the thumb is drawn there"
                    mouse 0 1273 600 0
                    Expect.equal view.ScrollTop.Value (place 600.0) "a move with the button down drags it"
                    // The track holds the pointer: over the text, the move still drags and selects nothing.
                    mouse 0 640 700 0
                    Expect.equal view.ScrollTop.Value (place 700.0) "the track keeps the pointer that left it"
                    Expect.equal mounted.Editor.Selection.Value.Main.Head 0 "and the text under it is not selected"
                    mouse 2 640 700 0
                    mouse 0 1273 100 -1
                    Expect.equal view.ScrollTop.Value (place 700.0) "a move after the release drags nothing")

            testCase "the engine's key input types where a click put the cursor"
            <| fun () ->
                withView [ "hello" ] standard 800 (fun mounted ->
                    let host = mounted.Window.Host
                    let view = mounted.View
                    host.Click (int (Math.Round (view.GutterWidth.Value + 2.0 * view.CharWidth.Value)), 10) |> ignore
                    mounted.Step ()
                    Expect.equal mounted.Editor.Selection.Value.Main.Head 2 "the click puts the cursor after two characters"
                    host.Type ("a", "KeyA", "a")
                    mounted.Step ()
                    Expect.equal (Text.toString mounted.Editor.Text.Value) "heallo" "a key typed after the click inserts there"
                    Expect.equal (textOf mounted.Rows[0]) "heallo" "and the row shows it")

            testCase "the host's animation frame lays out what was asked for, and the view at rest asks for none"
            <| fun () ->
                withView (manyLines 1000) standard 800 (fun mounted ->
                    let host = mounted.Window.Host
                    let view = mounted.View
                    let viewport = viewportOf mounted

                    // What a shown window does at each refresh of its display: a frame, when the page asked for one.
                    let refresh () : bool =
                        let asked = host.FrameRequested

                        if asked then
                            host.Frame ()

                        asked

                    /// Refreshes until the page asks for no frame; the number that ran one, 10 at most.
                    let settle () : int =
                        let mutable ran = 0

                        while ran < 10 && refresh () do
                            ran <- ran + 1

                        ran

                    Expect.isLessThan (settle ()) 10 "the mounted view comes to rest"
                    Expect.isFalse host.FrameRequested "and at rest asks for no frame"

                    mounted.Run (fun () -> view.ScrollTo (500.0 * 20.0))
                    Expect.equal view.ScrollTop.Value 0.0 "a request alone moves nothing"
                    Expect.isTrue host.FrameRequested "the view asked the host for a frame"
                    host.Frame ()
                    Expect.equal view.ScrollTop.Value (500.0 * 20.0) "the host's frame lays the rows out"
                    Expect.equal (textOf mounted.Rows[500 % mounted.Rows.Length]) "line 500" "with the lines of the new place"
                    Expect.isLessThan (settle ()) 10 "the view comes to rest again"

                    // The wheel and the frame both come from the host, as a shown window sends them.
                    let rows = view.RowsWritten ()
                    let offset = viewport.scrollTop
                    host.Wheel (400, 300, 0.0f, 20.0f, 0) |> ignore
                    host.Wheel (400, 300, 0.0f, 40.0f, 0) |> ignore
                    Expect.equal (view.RowsWritten ()) rows "wheel events alone compute no row"
                    Expect.isTrue host.FrameRequested "they ask for a frame"
                    host.Frame ()
                    Expect.equal view.ScrollTop.Value (500.0 * 20.0 + 60.0) "one frame takes both deltas"
                    Expect.equal (view.RowsWritten () - rows) 3L "and lays out the three lines once"
                    Expect.equal (viewport.scrollTop - offset) 60 "the viewport moved over the sheet by as much"
                    Expect.isLessThan (settle ()) 10 "and the view comes to rest"
                    Expect.equal view.ScrollTop.Value (500.0 * 20.0 + 60.0) "where the wheel left it")

            testCase "the cursor blinks on the host's timers and a move of the cursor lights it"
            <| fun () ->
                // A blink long enough that a stalled turn of a busy machine does not pass for one.
                let blinking =
                    [
                        Keymap.extension Keymap.defaults
                        Cursor.extension { Cursor.defaults with BlinkRate = 200.0 }
                    ]

                withView (manyLines 10) blinking 800 (fun mounted ->
                    let lit () = (shown mounted "[data-pm~=cursor]").Length = 1

                    // Turns of the window until `wanted`, for at most five seconds.
                    let turnsUntil (wanted: unit -> bool) : bool =
                        let clock = Diagnostics.Stopwatch.StartNew ()

                        while not (wanted ()) && clock.ElapsedMilliseconds < 5000L do
                            Threading.Thread.Sleep 5
                            mounted.Step ()

                        wanted ()

                    mounted.Step ()
                    Expect.isTrue (turnsUntil lit) "lit"
                    Expect.isTrue (turnsUntil (lit >> not)) "dark after the blink rate"
                    Expect.isTrue (turnsUntil lit) "and lit again"
                    Expect.isTrue (turnsUntil (lit >> not)) "dark once more"
                    // Lit by the move itself: the timer's next turn is most of a blink away.
                    mounted.Run (fun () -> mounted.Editor.Dispatch [ Tx.selection (EditorSelection.single 3 3) ])
                    mounted.Step ()
                    Expect.isTrue (lit ()) "a move of the cursor lights it at once")

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

                withView [ "let x = 1 < 2"; "plain & simple"; "" ] [ Language.extension language ] 800 (fun mounted ->
                    Expect.equal (lineOf mounted.Rows[0]).innerHTML "<span class=\"t1\">let</span> x = 1 &lt; 2" "a keyword span, and escaped text"
                    Expect.equal (textOf mounted.Rows[1]) "plain & simple" "escaped text reads back"
                    Expect.equal (lineOf mounted.Rows[1]).innerHTML "plain &amp; simple<!---->" "a line without spans ends with an empty comment"
                    Expect.equal (lineOf mounted.Rows[2]).innerHTML "" "an empty line has no markup")

            testCase "a line without a language is its text and an empty comment"
            <| fun () ->
                withView [ "a < b"; "" ] [] 800 (fun mounted ->
                    Expect.equal (lineOf mounted.Rows[0]).innerHTML "a &lt; b<!---->" "escaped text and the comment"
                    Expect.equal (textOf mounted.Rows[0]) "a < b" "the row reads as the line"
                    Expect.equal (lineOf mounted.Rows[1]).innerHTML "" "an empty line"
                    mounted.Run (fun () -> mounted.Editor.Dispatch [ insert 5 "!" ])
                    mounted.Step ()
                    Expect.equal (lineOf mounted.Rows[0]).innerHTML "a &lt; b!<!---->" "after an edit"
                    Expect.equal (lineOf mounted.Rows[0]).childNodes.length 2u "a text node and a comment")
        ]
