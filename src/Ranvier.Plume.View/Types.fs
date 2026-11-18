namespace Ranvier.Plume.View

open System
open Ranvier
open Ranvier.Dom
open Ranvier.Plume

/// The lines a view shows, and the offsets of the first and last of them.
[<Struct>]
type VisibleRange =
    {
        FirstLine: int
        LastLine: int
        From: int
        To: int
    }

/// A rectangle in CSS pixels on the sheet the rows lie on.
[<Struct>]
type Rect =
    {
        Left: float
        Top: float
        Width: float
        Height: float
    }

/// A rectangle a layer draws. <c>Class</c> is a word a theme rule selects by.
[<NoEquality; NoComparison>]
type LayerMarker = { Rect: Rect; Class: string }

/// What a gutter shows beside a line.
[<Struct>]
type GutterMarker = { Text: string; Class: string }

/// A column beside the text.
[<NoEquality; NoComparison>]
type GutterSpec =
    {
        /// A word a theme rule selects the gutter's markers by.
        Class: string
        /// The column's width in CSS pixels.
        Width: float
        /// The marker of a line, or <c>ValueNone</c> for an empty cell.
        LineMarker: EditorState -> Line -> GutterMarker voption
        /// A value of the state the markers depend on beside the line itself. The markers of every row are computed
        /// again when it changes; a gutter that reads the line alone returns a constant.
        Key: EditorState -> obj
    }

/// The shape of the cursor.
type CursorShape =
    | Bar
    | Block
    | Underline

/// A mounted editor view: what a view plugin, a layer or a host reads and calls.
[<NoEquality; NoComparison>]
type EditorView =
    {
        Editor: Editor
        /// The host's frames and timers.
        Scheduler: FrameScheduler
        Theme: Theme
        /// The lines shown.
        Viewport: Memo<VisibleRange>
        HasFocus: Memo<bool>
        /// The distance scrolled from the document's top, in CSS pixels, as the rows are laid out for.
        ScrollTop: Memo<float>
        /// The height of the viewport in CSS pixels.
        Height: Memo<float>
        /// The advance of one character in CSS pixels, measured from the page.
        CharWidth: Memo<float>
        /// The width of the gutters, where the text starts.
        GutterWidth: Memo<float>
        /// Gives the view the keyboard focus.
        Focus: unit -> unit
        /// Scrolls to a distance from the document's top at the next frame.
        ScrollTo: float -> unit
        /// Scrolls by a distance at the next frame.
        ScrollBy: float -> unit
        /// The offset under a point of the viewport, in CSS pixels from its top-left corner.
        PosAtCoords: float -> float -> int
        /// The rectangle of the character at an offset, on the sheet. A tracked read: a layer that calls it follows
        /// the text, the font and the sheet.
        CoordsAtPos: int -> Rect
        /// Runs the work of a frame now: measures the viewport and lays the rows out for the scroll position asked
        /// for. A scripted run calls it in place of the frame callback.
        RunFrame: unit -> unit
        /// The number of rows computed since the view was mounted.
        RowsWritten: unit -> int64
        /// The value a view plugin made for the view, by the plugin's id. A tracked read.
        PluginValue: int -> obj voption
    }

/// Positioned rectangles drawn below or above the text.
and [<NoEquality; NoComparison>] LayerSpec =
    {
        /// True to draw above the text.
        Above: bool
        /// A word a theme rule selects the layer's markers by.
        Class: string
        /// The rectangles. Reads of memos and of <c>CoordsAtPos</c> are tracked.
        Markers: EditorView -> LayerMarker[]
    }

/// A view plugin, without its value type.
[<AbstractClass>]
type ViewPluginBase internal (id: int) =
    member internal _.Id = id
    abstract CreateBoxed: view: EditorView -> obj

/// Code that runs for as long as a view is mounted with it, and the value it made for the view.
[<Sealed>]
type ViewPlugin<'T> internal (id: int, create: EditorView -> 'T) =
    inherit ViewPluginBase (id)
    override _.CreateBoxed(view: EditorView) : obj = box (create view)

/// What an editor view is mounted with.
[<NoEquality; NoComparison>]
type EditorViewProps =
    {
        Editor: Editor
        /// The frames and timers of the host the view is mounted in.
        Scheduler: FrameScheduler
    }

[<RequireQualifiedAccess>]
module ViewPlugin =

    let mutable private lastId = 0

    /// The plugins of an editor's views.
    let facet: Facet<ViewPluginBase, ViewPluginBase[]> = Facet.define id

    /// A plugin. <c>create</c> runs when a view is mounted with the plugin, under an owner of its own: effects and
    /// cleanups it registers last until the view unmounts or the plugins of the editor change.
    let define (create: EditorView -> 'T) : ViewPlugin<'T> =
        ViewPlugin<'T> (Threading.Interlocked.Increment &lastId, create)

    /// The extension that adds <c>plugin</c>.
    let extension (plugin: ViewPlugin<'T>) : Extension = facet.Of (plugin :> ViewPluginBase)

    /// The value <c>plugin</c> made for <c>view</c>. A tracked read.
    let tryGet (plugin: ViewPlugin<'T>) (view: EditorView) : 'T voption =
        match view.PluginValue plugin.Id with
        | ValueSome value -> ValueSome (unbox<'T> value)
        | ValueNone -> ValueNone

[<RequireQualifiedAccess>]
module Gutter =

    /// The gutters of an editor's views, in order from the left.
    let facet: Facet<GutterSpec, GutterSpec[]> = Facet.define id

    /// The extension that adds a gutter.
    let define (spec: GutterSpec) : Extension = facet.Of spec

[<RequireQualifiedAccess>]
module Layer =

    /// The layers of an editor's views.
    let facet: Facet<LayerSpec, LayerSpec[]> = Facet.define id

    /// The extension that adds a layer.
    let define (spec: LayerSpec) : Extension = facet.Of spec
