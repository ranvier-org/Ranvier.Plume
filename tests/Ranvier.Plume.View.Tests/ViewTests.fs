module Ranvier.Plume.View.Tests.ViewTests

open System
open Expecto
open Ranvier.View.Dsl
open Ranvier
open Ranvier.Dom
open Ranvier.Plume
open Ranvier.Plume.View
open Ranvier.Plume.View.Tests.Host

module private AX =
    open System.Runtime.InteropServices

    [<DllImport("RanvierWebCoreHost")>]
    extern int rv_host_set_accessibility(nativeint host, int enabled, nativeint callback, nativeint context)
    [<DllImport("RanvierWebCoreHost")>]
    extern nativeint rv_host_ax_focus(nativeint host)
    [<DllImport("RanvierWebCoreHost")>]
    extern int rv_ax_text_selection(nativeint node, int& first, int& last)
    [<DllImport("RanvierWebCoreHost")>]
    extern int rv_ax_text_set_selection(nativeint node, int first, int last)
    [<DllImport("RanvierWebCoreHost")>]
    extern void rv_ax_release(nativeint node)

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

/// The runs of 'x' in a line, each as a token.
let private tokensOfMarks (line: string) : Token[] =
    let tokens = ResizeArray<Token> ()
    let mutable at = 0

    while at < line.Length do
        if line[at] = 'x' then
            let from = at

            while at < line.Length && line[at] = 'x' do
                at <- at + 1

            tokens.Add ({ From = from; To = at; Tag = Tag.punctuation }: Token)
        else
            at <- at + 1

    tokens.ToArray ()

/// A language that tags every run of 'x', so the page holds an element whose edges are where that text is drawn.
let private marks: Extension = Language.extension (Language.ofLineLexer "marks" tokensOfMarks)

/// Where the page drew the tagged text of a row: the element of the theme's class for punctuation.
let private markOf (row: Element) : DOMRect = ((lineOf row).querySelector "span.t8").getBoundingClientRect ()

/// The cursor put before the first 'x' of line `number` as the page drew it, and that text as the page drew it.
let private cursorAndMark (mounted: Mounted) (number: int) : struct (DOMRect * DOMRect) =
    let line = Text.line number mounted.Editor.Text.Value
    let position = line.From + line.Text.IndexOf 'x'
    mounted.Run (fun () -> mounted.Editor.Dispatch [ Tx.selection (EditorSelection.single position position) ])
    mounted.Step ()
    mounted.Step ()
    let cursors = shown mounted "[data-pm~=cursor]"
    Expect.equal cursors.Length 1 "one cursor"
    struct (cursors[0], markOf mounted.Rows[number])

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

            testCase "the classes the view hands over style its elements through the sheet a host adopts"
            <| fun () ->
                withView (manyLines 100) standard 800 (fun mounted ->
                    let styles = Ranvier.View.WebCore.ComputedStyles.forHost mounted.Window.Host
                    let computed (element: Element) (property: string) = (styles.Get (element, None)).getPropertyValue property
                    let handed = definition.Styles |> List.map (fun style -> style.ClassName) |> Set.ofList
                    Expect.isNonEmpty handed "the definition hands classes over"
                    let root = (viewportOf mounted).parentElement

                    // Every class of Ranvier.Css on an element of the view is one the definition handed over.
                    let applied =
                        Array.append [| root |] (mounted.Query "[data-plume], [data-plume] *")
                        |> Array.collect (fun element ->
                            match element.getAttribute "class" with
                            | null -> Array.empty
                            | classes -> classes.Split (' ', StringSplitOptions.RemoveEmptyEntries))
                        |> Array.filter (fun name -> name.StartsWith "r-")
                        |> Set.ofArray

                    Expect.isNonEmpty applied "the view's elements carry classes"
                    Expect.isTrue (Set.isSubset applied handed) "each of them is handed over with its rules"

                    // The rules took effect: an element without them would be laid out in the flow, at its content's height.
                    let row = mounted.Rows[0]
                    Expect.equal (computed row "position") "absolute" "a row is placed on the sheet"
                    Expect.equal (computed row "height") "20px" "at the theme's line height"
                    Expect.equal (computed (viewportOf mounted) "overflow-x") "hidden" "the viewport clips"
                    Expect.equal (computed (viewportOf mounted) "font-size") "14px" "in the theme's font size"
                    let marker = (mounted.Query "[data-pm~=cursor]")[0]
                    Expect.equal (computed marker "pointer-events") "none" "a layer's rectangle takes no pointer")

            testCase "a view whose character width is not measured yet lays out on an estimate from the theme's font size"
            <| fun () ->
                // The manual scheduler's timers never fire, so the view never measures after it is mounted.
                let scheduler = new ManualScheduler ()

                withViewOn (fun _ -> scheduler :> FrameScheduler) [ "hello world"; "second" ] standard 800 (fun mounted ->
                    let view = mounted.View
                    let width = view.CharWidth.Value
                    Expect.floatClose Accuracy.high width (0.55 * EditorView.defaults.Theme.FontSize) "the estimate: 0.55 of the theme's font size"
                    Expect.equal (textOf mounted.Rows[0]) "hello world" "the rows are drawn"
                    let at = mounted.Run (fun () -> view.CoordsAtPos 4)
                    Expect.floatClose Accuracy.medium at.Left (view.GutterWidth.Value + 4.0 * width) "a position is columns of that width"
                    Expect.equal (mounted.Run (fun () -> view.PosAtCoords (at.Left + 0.5) 5.0)) 4 "and a point finds its position back"
                    Expect.equal (mounted.Run (fun () -> view.PosAtCoords 5000.0 25.0)) (Text.line 1 mounted.Editor.Text.Value).To "a point past a line is its end"
                    mounted.Run (fun () -> mounted.Editor.Dispatch [ Tx.selection (EditorSelection.single 4 4) ])
                    mounted.Step ()
                    let cursors = shown mounted "[data-pm~=cursor]"
                    Expect.equal cursors.Length 1 "the cursor is drawn"
                    Expect.isLessThan (abs (cursors[0].left - at.Left)) 0.05 "where the position is")

            testCase "a scroll request reaches the rows through the frame callback"
            <| fun () ->
                let scheduler = new ManualScheduler ()

                withViewOn (fun _ -> scheduler :> FrameScheduler) (manyLines 10_000) standard 800 (fun mounted ->
                    let view = mounted.View
                    Expect.equal scheduler.Pending 0 "a view at rest asks for no frame"

                    // Three writes to the scroll target, each its own run of the effect that asks for a frame.
                    mounted.Run (fun () -> view.ScrollTo 2000.0)
                    mounted.Run (fun () -> view.ScrollBy 20.0)
                    mounted.Run (fun () -> view.ScrollBy 20.0)

                    Expect.equal scheduler.Pending 1 "one frame callback for three requests"
                    Expect.equal view.ScrollTop.Value 0.0 "the rows wait for the frame"
                    let before = view.RowsWritten ()

                    // No RunFrame here: the frame callback carries the request.
                    mounted.Run (fun () -> scheduler.RunFrames ())
                    Expect.equal view.ScrollTop.Value 2040.0 "committed by the frame"
                    Expect.equal view.Viewport.Value.FirstLine 102 "first line shown"
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

            testCase "the cursor after a tab stands where the text after the tab is drawn"
            <| fun () ->
                withView [ "plain xx"; "\txx"; "ab\txx"; "\t\txx"; "abcd\txx" ] (marks :: standard) 800 (fun mounted ->
                    let width = mounted.View.CharWidth.Value
                    let start = mounted.View.GutterWidth.Value

                    // The page draws a tab to the next multiple of four columns.
                    for number, columns in [ 0, 6; 1, 4; 2, 4; 3, 8; 4, 8 ] do
                        let struct (cursor, mark) = cursorAndMark mounted number
                        Expect.isLessThan (abs (mark.left - (start + float columns * width))) 0.75 (sprintf "line %d: the page draws the text at column %d" number columns)
                        Expect.isLessThan (abs (cursor.left - mark.left)) 0.75 (sprintf "line %d: the cursor is where the text is" number))

            testCase "the cursor after wide and combined characters stands where the next text is drawn"
            <| fun () ->
                withView [ "漢字xx"; "a漢b字xx"; "😀xx"; "e\u0301xx"; "ｆｕｌｌxx" ] (marks :: standard) 800 (fun mounted ->
                    let width = mounted.View.CharWidth.Value
                    let start = mounted.View.GutterWidth.Value

                    // A wide character is drawn in two columns, and a letter with its combining mark in one.
                    for number, columns in [ 0, 4; 1, 6; 2, 2; 3, 1; 4, 8 ] do
                        let struct (cursor, mark) = cursorAndMark mounted number
                        Expect.isLessThan (abs (mark.left - (start + float columns * width))) 0.75 (sprintf "line %d: the page draws the text at column %d" number columns)
                        Expect.isLessThan (abs (cursor.left - mark.left)) 0.75 (sprintf "line %d: the cursor is where the text is" number)

                    Expect.floatClose Accuracy.medium (mounted.Rows[0].getBoundingClientRect ()).height 20.0 "a row of wide characters is one line high")

            testCase "a point on text after a tab or a wide character finds that text"
            <| fun () ->
                withView [ "\txx"; "漢字xx"; "ab\txx" ] (marks :: standard) 800 (fun mounted ->
                    let view = mounted.View
                    let text = mounted.Editor.Text.Value
                    mounted.Step ()
                    mounted.Step ()

                    for number in 0..2 do
                        let line = Text.line number text
                        let first = line.From + line.Text.IndexOf 'x'
                        let mark = markOf mounted.Rows[number]
                        let y = mark.top + 5.0
                        Expect.equal (mounted.Run (fun () -> view.PosAtCoords (mark.left + 1.0) y)) first (sprintf "line %d: a point on the first tagged character is before it" number)
                        Expect.equal (mounted.Run (fun () -> view.PosAtCoords (mark.right - 1.0) y)) (first + 2) (sprintf "line %d: a point at the end of the tagged text is after it" number)
                        Expect.equal (mounted.Run (fun () -> view.PosAtCoords (mark.right + 400.0) y)) line.To (sprintf "line %d: a point past the line is its end" number)

                    // Inside a tab, the nearer edge.
                    let start = view.GutterWidth.Value
                    let width = view.CharWidth.Value
                    Expect.equal (mounted.Run (fun () -> view.PosAtCoords (start + 1.2 * width) 5.0)) 0 "the left part of a tab is before it"
                    Expect.equal (mounted.Run (fun () -> view.PosAtCoords (start + 3.2 * width) 5.0)) 1 "the right part of a tab is after it")

            testCase "a selection over a tab or wide characters covers the text it selects"
            <| fun () ->
                withView [ "\txx"; "漢字xx" ] (marks :: standard) 800 (fun mounted ->
                    let text = mounted.Editor.Text.Value
                    let start = mounted.View.GutterWidth.Value

                    for number in 0..1 do
                        let line = Text.line number text
                        mounted.Run (fun () -> mounted.Editor.Dispatch [ Tx.selection (EditorSelection.single line.From line.To) ])
                        mounted.Step ()
                        mounted.Step ()
                        let selected = shown mounted "[data-pm~=selection]"
                        Expect.equal selected.Length 1 "one rectangle"
                        let mark = markOf mounted.Rows[number]
                        Expect.isLessThan (abs (selected[0].left - start)) 0.75 (sprintf "line %d: from the start of the line" number)
                        Expect.isLessThan (abs (selected[0].right - mark.right)) 0.75 (sprintf "line %d: to the end of its text" number))

            testCase "the rectangle of a tab or a wide character is as wide as the page draws it"
            <| fun () ->
                withView [ "\txx"; "漢xx" ] (marks :: standard) 800 (fun mounted ->
                    let start = mounted.View.GutterWidth.Value
                    mounted.Step ()
                    mounted.Step ()

                    for number in 0..1 do
                        let line = Text.line number mounted.Editor.Text.Value
                        let rect = mounted.Run (fun () -> mounted.View.CoordsAtPos line.From)
                        let mark = markOf mounted.Rows[number]
                        Expect.isLessThan (abs (rect.Left - start)) 0.05 (sprintf "line %d: it starts the line" number)
                        Expect.isLessThan (abs (rect.Width - (mark.left - start))) 0.75 (sprintf "line %d: and reaches the text after it" number))

            testCase "a tab is drawn and placed by the tab size of the state"
            <| fun () ->
                withView [ "\txx"; "ab\txx" ] (EditorState.tabSize.Of 8 :: marks :: standard) 800 (fun mounted ->
                    let width = mounted.View.CharWidth.Value
                    let start = mounted.View.GutterWidth.Value

                    for number in 0..1 do
                        let struct (cursor, mark) = cursorAndMark mounted number
                        Expect.isLessThan (abs (mark.left - (start + 8.0 * width))) 0.75 (sprintf "line %d: the page draws the tab to the eighth column" number)
                        Expect.isLessThan (abs (cursor.left - mark.left)) 0.75 (sprintf "line %d: and the cursor is there" number))

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

            testCase "mouse and wheel events on SDL's queue reach the listeners through the window's own loop"
            <| fun () ->
                withView (manyLines 1000) standard 800 (fun mounted ->
                    let view = mounted.View
                    let editor = mounted.Editor
                    // SDL reports the mouse in pixels of the window; the view starts at the window's corner.
                    let struct (pixels, _) = mounted.Window.PixelSize
                    let scale = float32 pixels / 1280.0f
                    let x (column: int) = float32 (Math.Round (view.GutterWidth.Value + float column * view.CharWidth.Value)) * scale
                    let y (line: int) = float32 (line * 20 + 10) * scale
                    let at (line: int) (column: int) = (Text.line line editor.Text.Value).From + column
                    let ends () = let main = editor.Selection.Value.Main in main.Anchor, main.Head

                    /// Two turns of the window's loop more than a display's refresh apart: the window holds a move and
                    /// a turn of the wheel until a refresh has passed since the one before.
                    let turns () =
                        mounted.Step ()
                        Threading.Thread.Sleep 40
                        mounted.Step ()

                    Desktop.move (x 2) (y 3) false
                    turns ()
                    Expect.equal (ends ()) (0, 0) "a move without a button selects nothing"
                    Desktop.button (x 2) (y 3) true
                    turns ()
                    Expect.equal (ends ()) (at 3 2, at 3 2) "a press puts the cursor under the pointer"
                    Desktop.move (x 4) (y 5) true
                    turns ()
                    Expect.equal (ends ()) (at 3 2, at 5 4) "a move with the button down takes the head with it"
                    Desktop.button (x 4) (y 5) false
                    turns ()
                    Desktop.move (x 0) (y 0) false
                    turns ()
                    Expect.equal (ends ()) (at 3 2, at 5 4) "a move after the release selects nothing"

                    Desktop.wheel (x 10) (y 10) 3.0f
                    turns ()
                    Expect.equal view.ScrollTop.Value 120.0 "three notches of the wheel scroll 120 CSS px"
                    Desktop.wheel (x 10) (y 10) -3.0f
                    turns ()
                    Expect.equal view.ScrollTop.Value 0.0 "and three back return"

                    let range = 1000.0 * 20.0 - 800.0 + 40.0
                    let thumb = 800.0 * 800.0 / (range + 800.0)
                    let place (py: float) = Math.Round (range * Math.Clamp ((py - thumb / 2.0) / (800.0 - thumb), 0.0, 1.0))
                    Desktop.move (1273.0f * scale) (200.0f * scale) false
                    turns ()
                    Desktop.button (1273.0f * scale) (200.0f * scale) true
                    turns ()
                    Expect.equal view.ScrollTop.Value (place 200.0) "a press on the scrollbar's track centres the thumb there"
                    Desktop.move (1273.0f * scale) (600.0f * scale) true
                    turns ()
                    Expect.equal view.ScrollTop.Value (place 600.0) "a move with the button down drags it"
                    Desktop.button (1273.0f * scale) (600.0f * scale) false
                    turns ()
                    Desktop.move (1273.0f * scale) (100.0f * scale) false
                    turns ()
                    Expect.equal view.ScrollTop.Value (place 600.0) "a move after the release drags nothing"
                    Expect.equal (ends ()) (at 3 2, at 5 4) "and the selection is as the pointer left it")

            testCase "a turn of the window's own loop runs the frame a scroll request asked for"
            <| fun () ->
                withView (manyLines 1000) standard 800 (fun mounted ->
                    let view = mounted.View
                    mounted.Step ()
                    mounted.Run (fun () -> view.ScrollTo (300.0 * 20.0))
                    Expect.equal view.ScrollTop.Value 0.0 "a request alone moves nothing"

                    // Turns of the window alone, for at most five seconds: no frame is run by the test. A turn returns
                    // at once while the window has work, so the turns are bounded by time and not by their number.
                    let clock = Diagnostics.Stopwatch.StartNew ()

                    while view.ScrollTop.Value = 0.0 && clock.ElapsedMilliseconds < 5000L do
                        mounted.Window.Step (timeout = 5) |> ignore

                    Expect.equal view.ScrollTop.Value (300.0 * 20.0) "the window's loop ran the frame"
                    Expect.equal (textOf mounted.Rows[300 % mounted.Rows.Length]) "line 300" "and the rows show the new place")

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

            testCase "native selection, composition and clipboard share the editor transaction path"
            <| fun () ->
                withView [ "abc" ] standard 800 (fun mounted ->
                    let host = mounted.Window.Host
                    host.SetFocused true
                    mounted.Run(fun () ->
                        mounted.Editor.Dispatch [ Tx.selection (EditorSelection.single 2 1) ]
                        mounted.View.Focus())
                    let area = mounted.Dom.activeElement :?> HTMLTextAreaElement
                    Expect.equal (area.value, area.selectionStart, area.selectionEnd, area.selectionDirection) ("abc", 1u, 2u, "backward") "accessible selection"
                    let plain () = Text.toString mounted.Editor.Text.Value
                    host.SetComposition("仮", 1, 1)
                    Expect.equal (plain ()) "abc" "preedit stays provisional"
                    host.CommitComposition "仮名"
                    mounted.Step()
                    Expect.equal (plain ()) "a仮名c" "commit once"
                    let copied = ResizeArray<string>()
                    host.SetClipboard((fun kind data _ -> if kind = "text/plain" then copied.Add data), (fun () -> [struct ("text/plain", "xy")]))
                    mounted.Run(fun () -> mounted.Editor.Dispatch [Tx.selection (EditorSelection.single 1 3)])
                    host.Key(0, "c", "KeyC", null, 2, false) |> ignore
                    Expect.contains copied "仮名" "native copy"
                    host.Key(0, "x", "KeyX", null, 2, false) |> ignore
                    mounted.Step()
                    Expect.equal (plain ()) "ac" "cut"
                    host.Key(0, "v", "KeyV", null, 2, false) |> ignore
                    mounted.Step()
                    Expect.equal (plain ()) "axyc" "paste")

            testCase "the native accessibility tree reads and sets the Plume selection"
            <| fun () ->
                withView ["abcdef"] standard 800 (fun mounted ->
                    let host = mounted.Window.Host
                    host.SetFocused true
                    mounted.Run(fun () ->
                        mounted.Editor.Dispatch [Tx.selection (EditorSelection.single 1 4)]
                        mounted.View.Focus())
                    let existing = AX.rv_host_ax_focus host.Handle
                    let enable = existing = 0n
                    if enable then
                        Expect.equal (AX.rv_host_set_accessibility(host.Handle, 1, 0n, 0n)) 0 "enable accessibility"
                    let node = if enable then AX.rv_host_ax_focus host.Handle else existing
                    try
                        Expect.notEqual node 0n "focused accessible text control"
                        let mutable first, last = 0, 0
                        let status = AX.rv_ax_text_selection(node, &first, &last)
                        Expect.isGreaterThanOrEqual status 0 "text selection available"
                        Expect.equal (first, last) (1, 4) "reported selection"
                        Expect.equal (AX.rv_ax_text_set_selection(node, 2, 5)) 0 "assistive selection action"
                        mounted.Step()
                        let main = mounted.Editor.Selection.Value.Main
                        Expect.equal (main.From, main.To) (2, 5) "selection reaches Plume"
                    finally
                        if node <> 0n then AX.rv_ax_release node
                        if enable then AX.rv_host_set_accessibility(host.Handle, 0, 0n, 0n) |> ignore)

            testCase "assistive selection maps native LF offsets back into a CRLF document"
            <| fun () ->
                withView [""] standard 800 (fun mounted ->
                    mounted.Window.Host.SetFocused true
                    mounted.Run(fun () ->
                        mounted.Editor.Dispatch [ insert 0 "a\r\nb\r\nc"; Tx.selection (EditorSelection.single 3 3) ]
                        mounted.View.Focus())
                    let area = mounted.Dom.activeElement :?> HTMLTextAreaElement
                    Expect.equal area.value "a\nb\nc" "native context"
                    area.setSelectionRange(1u, 4u, "backward")
                    mounted.Step()
                    let main = mounted.Editor.Selection.Value.Main
                    Expect.equal (main.Anchor, main.Head) (6, 1) "selection returned to the editor")

            testCase "multi-range cut puts every removed range on the clipboard"
            <| fun () ->
                withView ["abcdef"] (EditorState.allowMultipleSelections.Of true :: standard) 800 (fun mounted ->
                    let host = mounted.Window.Host
                    host.SetFocused true
                    let copied = ResizeArray<string>()
                    host.SetClipboard((fun kind data _ -> if kind = "text/plain" then copied.Add data), (fun () -> []))
                    mounted.Run(fun () ->
                        mounted.Editor.Dispatch [ Tx.selection (EditorSelection.create [EditorSelection.range 0 1; EditorSelection.range 3 5] 1) ]
                        mounted.View.Focus())
                    host.Key(0, "x", "KeyX", null, 2, false) |> ignore
                    mounted.Step()
                    Expect.contains copied "a\nde" "joined selections"
                    Expect.equal (Text.toString mounted.Editor.Text.Value) "bcf" "both cuts"
                    host.SetClipboard((fun _ _ _ -> ()), (fun () -> [struct ("text/plain", copied[0])]))
                    host.Key(0, "v", "KeyV", null, 2, false) |> ignore
                    mounted.Step()
                    Expect.equal (Text.toString mounted.Editor.Text.Value) "abcdef" "paste distributes one line to each caret")

            testCase "a read-only native selection can be copied without changing the document"
            <| fun () ->
                withView ["abc"] (EditorState.readOnly.Of true :: standard) 800 (fun mounted ->
                    let host = mounted.Window.Host
                    host.SetFocused true
                    let written = ResizeArray<string>()
                    host.SetClipboard((fun mime value _ -> if mime = "text/plain" then written.Add value), (fun () -> [struct ("text/plain", "x")]))
                    mounted.Run(fun () ->
                        mounted.Editor.Dispatch [Tx.selection (EditorSelection.single 0 2)]
                        mounted.View.Focus())
                    let area = mounted.Dom.activeElement :?> HTMLTextAreaElement
                    Expect.isTrue area.readOnly "native accessibility reports read-only"
                    host.Key(0, "c", "KeyC", null, 2, false) |> ignore
                    host.Key(0, "v", "KeyV", null, 2, false) |> ignore
                    host.Type("z", "KeyZ", "z")
                    mounted.Step()
                    Expect.contains written "ab" "copy"
                    Expect.equal (Text.toString mounted.Editor.Text.Value) "abc" "editing suppressed")

            testCase "composition cancellation and concurrent edits do not insert the preedit"
            <| fun () ->
                withView [ "abc" ] standard 800 (fun mounted ->
                    let host = mounted.Window.Host
                    host.SetFocused true
                    mounted.Run(fun () -> mounted.View.Focus())
                    host.SetComposition("仮", 1, 1)
                    host.CancelComposition()
                    mounted.Step()
                    Expect.equal (Text.toString mounted.Editor.Text.Value) "abc" "cancel"
                    host.SetComposition("仮", 1, 1)
                    mounted.Run(fun () -> mounted.Editor.Dispatch [insert 3 "!"])
                    host.CommitComposition "名"
                    mounted.Step()
                    Expect.equal (Text.toString mounted.Editor.Text.Value) "abc!" "stale commit")

            testCase "typed decoration hover follows its visible range"
            <| fun () ->
                let field = StateField.define {
                    Create = fun _ -> RangeSet.ofRanges [1, 4, "first diagnostic"]
                    Update = fun old tx -> Decoration.mapUntouched (ChangeSet.desc tx.Changes) old
                }
                let tip = span () { fun (read: unit -> string) -> read() }
                let underline = Decoration.underline "sample" (fun v -> v.Editor.Field(field).Value) tip
                withView (manyLines 500) [StateField.extension field; underline] 800 (fun mounted ->
                    let mark () = mounted.Query "[data-pm=decoration]"
                    let selector = "[data-pm=decoration-hover]"
                    Expect.equal (mark()).Length 1 "one visible segment"
                    let rect = ((mark())[0]).getBoundingClientRect()
                    Expect.equal (shown mounted selector).Length 0 "hover starts hidden"
                    mounted.Window.Host.Mouse(0, int rect.left + 1, int rect.top + 2, -1, 0, 0) |> ignore
                    mounted.Step()
                    Expect.equal (shown mounted selector).Length 1 "pointer reveals the template"
                    let tooltip = (mounted.Query selector)[0]
                    Expect.equal tooltip.textContent "first diagnostic" "typed payload"
                    mounted.Run(fun () -> mounted.View.ScrollTo 4000.)
                    mounted.Step()
                    Expect.equal (mark()).Length 0 "recycling removes the mark and its hover"
                    mounted.Run(fun () -> mounted.Editor.Dispatch [insert 1 "xx"]; mounted.View.ScrollTo 0.)
                    mounted.Step()
                    let moved = ((mark())[0]).getBoundingClientRect()
                    Expect.isLessThan (abs (moved.left - rect.left - 2. * mounted.View.CharWidth.Value)) 0.04 "boundary insertion moves the underline within layout rounding"
                    mounted.Run(fun () -> mounted.Editor.Dispatch [insert 4 "x"])
                    mounted.Step()
                    Expect.equal (mark()).Length 0 "an interior edit invalidates the diagnostic")

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

                let config = RowConfig (Array.empty, Array.empty, ValueNone, 4, true, true)
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
                // The indent unit is a facet no view reads.
                let indent = Compartment.create ()

                let plugin =
                    ViewPlugin.define (fun _ ->
                        log.Add "created"
                        Api.onCleanup (fun () -> log.Add "disposed")
                        0)

                withView (manyLines 100) (indent.Of (EditorState.indentUnit.Of "  ") :: ViewPlugin.extension plugin :: standard) 800 (fun mounted ->
                    let before = mounted.View.RowsWritten ()
                    mounted.Run (fun () -> mounted.Editor.Dispatch [ Tx.effect (indent.Reconfigure (EditorState.indentUnit.Of "\t")) ])
                    mounted.Step ()
                    Expect.sequenceEqual log [ "created" ] "the plugin stays"
                    Expect.equal (mounted.View.RowsWritten () - before) 0L "no row is computed again"
                    Expect.equal (shown mounted "[data-pm~=cursor]").Length 1 "the cursor is still drawn")

            testCase "reconfiguring the tab size draws the tabs again"
            <| fun () ->
                let tabs = Compartment.create ()

                withView [ "\txx" ] (tabs.Of (EditorState.tabSize.Of 2) :: marks :: standard) 800 (fun mounted ->
                    let width = mounted.View.CharWidth.Value
                    let start = mounted.View.GutterWidth.Value
                    let struct (_, narrow) = cursorAndMark mounted 0
                    Expect.isLessThan (abs (narrow.left - (start + 2.0 * width))) 0.75 "a tab of two columns"
                    mounted.Run (fun () -> mounted.Editor.Dispatch [ Tx.effect (tabs.Reconfigure (EditorState.tabSize.Of 8)) ])
                    mounted.Step ()
                    let struct (cursor, wide) = cursorAndMark mounted 0
                    Expect.isLessThan (abs (wide.left - (start + 8.0 * width))) 0.75 "the same tab at eight columns"
                    Expect.isLessThan (abs (cursor.left - wide.left)) 0.75 "and the cursor with it")

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

/// The markup of a line on the column grid, without a window.
[<Tests>]
let markupTests =
    testList
        "Markup"
        [
            testCase "the markup of a line draws a tab as spaces and boxes a character outside ASCII"
            <| fun () ->
                let cache = MarkupCache (EditorView.defaults.Theme, 16)
                Expect.equal (cache.Markup (ValueNone, true, 4, "a<b")) "a&lt;b<!---->" "a plain line is its text"
                Expect.equal (cache.Markup (ValueNone, true, 4, "ab\tc")) "ab  c<!---->" "a tab to the next stop"
                Expect.equal (cache.Markup (ValueNone, true, 8, "ab\tc")) "ab      c<!---->" "the same line at another tab size"
                Expect.equal (cache.Markup (ValueNone, true, 4, "漢é")) "<span class=\"pm-c2\">漢</span><span class=\"pm-c1\">é</span><!---->" "a box of two columns and a box of one"
                Expect.equal (cache.Markup (ValueNone, true, 4, "<\u0301")) "<span class=\"pm-c1\">&lt;\u0301</span><!---->" "a boxed character is escaped"

                let language = Language.ofLineLexer "marks" tokensOfMarks
                Expect.equal (cache.Markup (ValueSome language, true, 4, "a\txx\tb")) "a   <span class=\"t8\">xx</span>  b" "tab stops count across a token"
        ]
