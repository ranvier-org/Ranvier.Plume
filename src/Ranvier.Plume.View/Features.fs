namespace Ranvier.Plume.View

open System
open Ranvier
open Ranvier.View.Dom
open Ranvier.Plume

/// How line numbers are shown.
[<NoEquality; NoComparison>]
type LineNumbersConfig =
    {
        /// The text of a 1-based line number.
        Format: int -> string
        /// The width of the column in CSS pixels.
        Width: float
    }

/// A gutter of line numbers.
[<RequireQualifiedAccess>]
module LineNumbers =

    /// Decimal numbers in a column of 48 CSS pixels.
    let defaults: LineNumbersConfig = { Format = string; Width = 48.0 }

    let extension (config: LineNumbersConfig) : Extension =
        Gutter.define
            {
                Class = "line-number"
                Width = config.Width
                LineMarker = fun _ line -> ValueSome { Text = config.Format (line.Number + 1); Class = "" }
                Key = fun _ -> null
            }

module internal Geometry =

    /// A width that reaches past the right edge of any viewport.
    [<Literal>]
    let FullWidth = 100000.0

/// A band behind the line of the main cursor, and the active mark on that line's gutter cell.
[<RequireQualifiedAccess>]
module ActiveLine =

    let extension: Extension =
        Extension.many
            [
                ViewFacets.activeLineGutter.Of true
                Layer.define
                    {
                        Above = false
                        Class = "active-line"
                        Markers =
                            fun view ->
                                let head = view.Editor.Selection.Value.Main.Head
                                let range = view.Viewport.Value
                                let line = Text.lineAt head view.Editor.Text.Value

                                if line.Number < range.FirstLine || line.Number > range.LastLine || not (view.Projection.Value.IsVisible line.Number) then
                                    Array.empty
                                else
                                    let at = view.CoordsAtPos line.From

                                    [|
                                        {
                                            Rect =
                                                {
                                                    Left = 0.0
                                                    Top = at.Top
                                                    Width = Geometry.FullWidth
                                                    Height = at.Height
                                                }
                                            Class = ""
                                        }
                                    |]
                    }
            ]

/// Rectangles behind the text of every selected range.
[<RequireQualifiedAccess>]
module DrawSelection =

    let extension: Extension =
        Layer.define
            {
                Above = false
                Class = "selection"
                Markers =
                    fun view ->
                        let selection = view.Editor.Selection.Value

                        if selection.Ranges |> Array.forall (fun range -> range.IsEmpty) then
                            Array.empty
                        else
                            let text = view.Editor.Text.Value
                            let shown = view.Viewport.Value
                            let width = view.CharWidth.Value
                            let markers = ResizeArray<LayerMarker> ()

                            for range in selection.Ranges do
                                if not range.IsEmpty && range.To >= shown.From && range.From <= shown.To then
                                    let first = Text.lineAt (max range.From shown.From) text
                                    let last = Text.lineAt (min range.To shown.To) text

                                    let projection = view.Projection.Value
                                    for row in projection.VisualRow first.Number .. projection.VisualRow last.Number do
                                        let number = projection.DocumentLine row
                                        let line =
                                            if number = first.Number then first
                                            elif number = last.Number then last
                                            else Text.line number text

                                        let from = Math.Clamp(range.From, line.From, line.To)
                                        let at = view.CoordsAtPos from
                                        // A line selected through its line break shows one column more.
                                        let right =
                                            (view.CoordsAtPos (min range.To line.To)).Left
                                            + (if range.From <= line.To && range.To > line.To then width else 0.0)

                                        if right > at.Left then
                                            markers.Add
                                                {
                                                    Rect = { at with Width = right - at.Left }
                                                    Class = ""
                                                }

                            markers.ToArray ()
            }

/// How the cursor is drawn.
type CursorConfig =
    {
        Shape: CursorShape
        /// Milliseconds the cursor stays lit and then dark; 0 for a cursor that stays lit.
        BlinkRate: float
    }

/// A cursor at the head of every range.
[<RequireQualifiedAccess>]
module Cursor =

    /// A bar that blinks every 530 ms.
    let defaults: CursorConfig = { Shape = Bar; BlinkRate = 530.0 }

    let extension (config: CursorConfig) : Extension =
        // The lit half of the blink, and the shape. The blink starts lit again whenever the selection changes.
        let blink: ViewPlugin<struct (Signal<bool> * Memo<CursorShape>)> =
            ViewPlugin.define (fun view ->
                let lit = Api.createSignal true
                let shape = view.Editor.Facet ViewFacets.cursorShape

                if config.BlinkRate > 0.0 then
                    let owner = Api.getOwner ()
                    // When the selection last changed, on the scheduler's clock.
                    let moved = ref (view.Scheduler.Now ())

                    Api.createEffect (fun () ->
                        view.Editor.Selection.Value |> ignore
                        moved.Value <- view.Scheduler.Now ()

                        if not lit.Peek then
                            lit.Value <- true)

                    // One timer for the view's life: a change of selection restarts the lit half without touching it.
                    let rec arm () =
                        Api.runWithOwner owner (fun () ->
                            Frames.timeout view.Scheduler config.BlinkRate (fun () ->
                                let rested = view.Scheduler.Now () - moved.Value >= config.BlinkRate
                                lit.Value <- (if rested then not lit.Peek else true)
                                arm ())
                            |> ignore)

                    arm ()

                struct (lit, shape))

        Extension.many
            [
                ViewPlugin.extension blink
                ViewFacets.cursorShape.Of config.Shape
                Layer.define
                    {
                        Above = true
                        Class = "cursor"
                        Markers =
                            fun view ->
                                let struct (lit, shape) =
                                    match ViewPlugin.tryGet blink view with
                                    | ValueSome (struct (lit, shape)) -> struct (lit.Value, shape.Value)
                                    | ValueNone -> struct (true, config.Shape)

                                if not lit then
                                    Array.empty
                                else
                                    let shown = view.Viewport.Value

                                    view.Editor.Selection.Value.Ranges
                                    |> Array.choose (fun range ->
                                        if range.Head < shown.From || range.Head > shown.To then
                                            None
                                        else
                                            let at = view.CoordsAtPos range.Head

                                            let rect =
                                                match shape with
                                                | Bar -> { at with Width = 2.0 }
                                                | Block -> at
                                                | Underline ->
                                                    { at with
                                                        Top = at.Top + at.Height - 2.0
                                                        Height = 2.0
                                                    }

                                            Some { Rect = rect; Class = "" })
                    }
            ]
