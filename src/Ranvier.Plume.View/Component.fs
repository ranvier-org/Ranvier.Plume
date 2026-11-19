namespace Ranvier.Plume.View

open System
open System.Collections.Generic
open System.Runtime.CompilerServices
open System.Threading
open Ranvier
open Ranvier.Dom
open Ranvier.View
open Ranvier.View.Dom
open Ranvier.View.Dsl
open Ranvier.Css
open Ranvier.Keyboard
open Ranvier.Plume

/// How an editor view is built.
type ViewConfig =
    {
        Theme: Theme
        /// Rows kept above and below the lines shown.
        Overscan: int
        /// Lines the first line shown may move from the sheet's anchor line before the anchor moves to it.
        RebaseLines: int
    }

/// An editor view ready to mount, and the CSS it needs.
[<NoEquality; NoComparison>]
type EditorViewDefinition =
    {
        Component: Component<EditorViewProps>
        /// The classes the view applies, each with its rules. A host adds them to the stylesheet it serves.
        Styles: StyleDefinition list
        /// The theme's rules for tokens, layers and gutters. A host adds them to the same stylesheet.
        Rules: Rule list
    }

/// A slot of a layer's pool of rectangles.
[<ReferenceEquality>]
type internal MarkerSlot =
    {
        Index: int
        Markers: Memo<LayerMarker[]>
        Class: string
    }

[<ReferenceEquality>]
type internal LayerInstance =
    {
        Id: int
        Above: bool
        Slots: Memo<MarkerSlot[]>
    }

/// The instance of a mounted view: what its template reads, and the nodes it holds.
[<Sealed; AllowNullLiteral>]
type internal Surface() =
    member val View: EditorView = Unchecked.defaultof<EditorView> with get, set
    member val Input: HTMLTextAreaElement = null with get, set
    member val Preedit: Signal<string> = Unchecked.defaultof<_> with get, set
    member val InputActive: Signal<bool> = Unchecked.defaultof<_> with get, set
    member val Viewport: HTMLElement = null with get, set
    member val Probe: HTMLElement = null with get, set
    member val Rows: Lookup<int, RowContent> = Unchecked.defaultof<_> with get, set
    member val RowSlots: Memo<RowSlot[]> = Unchecked.defaultof<_> with get, set
    member val IsActive: Lookup<int, bool> = Unchecked.defaultof<_> with get, set
    member val Below: Memo<LayerInstance[]> = Unchecked.defaultof<_> with get, set
    member val Above: Memo<LayerInstance[]> = Unchecked.defaultof<_> with get, set
    member val ThumbTop: Memo<float> = Unchecked.defaultof<_> with get, set
    member val ThumbHeight: Memo<float> = Unchecked.defaultof<_> with get, set
    member val Promoted: Memo<bool> = Unchecked.defaultof<_> with get, set
    /// Raised when the viewport element is set or cleared.
    member val Mounts: Signal<int> = Unchecked.defaultof<_> with get, set
    member val Focused: Signal<bool> = Unchecked.defaultof<_> with get, set
    member val ScrollToFraction: float -> unit = ignore with get, set
    member val Measure: unit -> unit = ignore with get, set
    /// True while the scrollbar holds the pointer.
    member val DraggingThumb = false with get, set
    /// True while a pointer drag extends the selection.
    member val Selecting = false with get, set
    /// The strokes of a key sequence so far.
    member val Pending: KeyChord list = [] with get, set

/// A row element of the pool. The element lives as long as the pool; scrolling changes its content.
and [<ReferenceEquality>] internal RowSlot = { Slot: int; Surface: Surface }

module internal Surface =

    let private mounted = ConditionalWeakTable<Editor, Surface> ()
    let mutable private lastLayer = 0

    let find (editor: Editor) : Surface voption =
        match mounted.TryGetValue editor with
        | true, surface -> ValueSome surface
        | _ -> ValueNone

    let current (editor: Editor) : EditorView voption =
        find editor |> ValueOption.map (fun surface -> surface.View)

    /// `scheduler` with every callback run on `graph`.
    //FOR-REVIEW Ranvier.View.Dom.Frames.frame and .timeout call Api.runWithOwner in the host's callback, and
    //FOR-REVIEW WebCoreHost.runDueTimers runs that callback with no ambient graph, so the pair raises "No ambient
    //FOR-REVIEW graph on this thread" (seen with Ranvier.View.Dom 0.1.50 and Ranvier.View.WebCore 0.1.2). The view
    //FOR-REVIEW enters its graph here. A fix in Ranvier.View would remove this adapter.
    let private onGraph (graph: Graph) (scheduler: FrameScheduler) : FrameScheduler =
        { new FrameScheduler with
            member _.Now() = scheduler.Now ()
            member _.Frame(callback: float -> unit) = scheduler.Frame (fun time -> graph.Run (fun () -> callback time))
            member _.Timeout(milliseconds: float, callback: unit -> unit) = scheduler.Timeout (milliseconds, (fun () -> graph.Run callback))
            member _.Microtask(callback: unit -> unit) = scheduler.Microtask (fun () -> graph.Run callback)
            // The host owns its scheduler.
            member _.Dispose() = ()
        }

    let create (config: ViewConfig) (ctx: Setup) (props: EditorViewProps) : Surface =
        let editor = props.Editor
        let scheduler = onGraph Graph.Current props.Scheduler
        let lineHeight = config.Theme.LineHeight
        let owner = Api.getOwner ()
        let surface = Surface ()

        // ---- Inputs ----
        let scrollTarget = Api.createSignal 0.0
        let scroll = Api.createSignal 0.0
        let height = Api.createSignal 0.0
        // Until the probe is measured: a character of a monospaced face advances about 0.55 of the font size.
        let charWidth = Api.createSignal (config.Theme.FontSize * 0.55)
        let focused = Api.createSignal false
        let mounts = Api.createSignal 0
        let written = ref 0L
        let markup = MarkupCache (config.Theme, 8192)

        let gutters = editor.Facet Gutter.facet
        let language = editor.Facet Language.facet
        let tabSize = editor.Facet EditorState.tabSize
        let recycle = editor.Facet ViewFacets.recycleRows
        let cache = editor.Facet ViewFacets.markupCache
        let promoted = editor.Facet ViewFacets.compositorLayer
        let activeGutter = editor.Facet ViewFacets.activeLineGutter
        let layers = editor.Facet Layer.facet
        let plugins = editor.Facet ViewPlugin.facet

        // ---- Rows ----
        let updates = editor.Updates ()

        let rowConfig =
            Api.createMemo (fun (previous: RowConfig voption) ->
                let specs = gutters.Value
                let state = editor.State.Value

                match previous with
                | ValueSome before when before.Holds (specs, state, language.Value, tabSize.Value, recycle.Value, cache.Value) -> before
                | _ -> RowConfig (specs, specs |> Array.map (fun gutter -> gutter.Key state), language.Value, tabSize.Value, recycle.Value, cache.Value))

        let window =
            Api.createMemo (fun (previous: RowWindow voption) ->
                Rows.compute previous (updates.Read ()) rowConfig.Value scroll.Value height.Value lineHeight config.Overscan config.RebaseLines markup written)

        let rows = Api.createLookup Rows.content Rows.affected (fun () -> window.Value)
        let pool = Api.createMemo (fun (_: int voption) -> window.Value.Pool)

        let rowSlots =
            Api.createMemo (fun (previous: RowSlot[] voption) ->
                let before = ValueOption.defaultValue Array.empty previous
                let count = pool.Value

                if count = before.Length then
                    before
                else
                    Array.init count (fun slot ->
                        if slot < before.Length then
                            before[slot]
                        else
                            { Slot = slot; Surface = surface }))

        // ---- Geometry ----
        let anchor = Api.createMemo (fun (_: int voption) -> window.Value.Anchor)
        let top = Api.createMemo (fun (_: float voption) -> window.Value.Top)

        let gutterWidth =
            Api.createMemo (fun (_: float voption) ->
                let total = gutters.Value |> Array.sumBy (fun gutter -> gutter.Width)
                // The text starts 24 CSS pixels after the last gutter.
                if total > 0.0 then total + 24.0 else 0.0)

        let viewport =
            Api.createMemo (fun (_: VisibleRange voption) ->
                let shown = window.Value
                let last = max 0 (min (shown.Lines - 1) (int ((shown.Top + max 0.0 height.Value) / lineHeight)))
                let first = min shown.First last

                {
                    FirstLine = first
                    LastLine = last
                    From = (Text.line first shown.Text).From
                    To = (Text.line last shown.Text).To
                })

        let thumbHeight =
            Api.createMemo (fun (_: float voption) ->
                let visible = height.Value
                let range = Rows.maxScroll window.Value.Lines visible lineHeight
                Math.Clamp (visible * visible / (range + max 1.0 visible), 28.0, max 28.0 visible))

        let thumbTop =
            Api.createMemo (fun (_: float voption) ->
                let visible = height.Value
                let shown = window.Value
                let range = Rows.maxScroll shown.Lines visible lineHeight

                if range > 0.0 then
                    (visible - thumbHeight.Value) * shown.Top / range
                else
                    0.0)

        let activeLine =
            Api.createSelector (fun () ->
                if activeGutter.Value then
                    (Text.lineAt editor.Selection.Value.Main.Head editor.Text.Value).Number
                else
                    -1)

        // ---- Scrolling ----
        let limit () =
            Rows.maxScroll (Api.untrack (fun () -> editor.Text.Value)).Lines height.Peek lineHeight

        let scrollTo (offset: float) =
            let target = Math.Clamp (offset, 0.0, limit ())

            if target <> scrollTarget.Peek then
                scrollTarget.Value <- target

        let probedAt = ref Double.NegativeInfinity

        let measure () =
            let element = surface.Viewport

            if not (isNull element) then
                let measured = float element.clientHeight

                if abs (measured - height.Peek) > 0.5 then
                    height.Value <- measured

                let probe = surface.Probe
                let now = scheduler.Now ()

                // The advance of a character is measured again once a second: a font may have arrived since.
                if not (isNull probe) && now - probedAt.Value >= 1000.0 then
                    probedAt.Value <- now
                    let width = (probe.getBoundingClientRect ()).width / 64.0

                    if width > 0.0 && abs (width - charWidth.Peek) > 1e-6 then
                        charWidth.Value <- width

        let commit () =
            let target = Math.Clamp (scrollTarget.Peek, 0.0, limit ())

            if target <> scroll.Peek then
                scroll.Value <- target

        // Several scroll requests in one frame lay the rows out once: a request replaces the frame callback of the
        // request before it.
        Api.createEffect (fun () ->
            if scrollTarget.Value <> scroll.Value then
                Frames.frame scheduler (fun _ -> commit ()) |> ignore)

        // Scrolling moves the viewport over the sheet: the sheet and its rows keep their places and their styles.
        let applied = ref Int32.MinValue

        Api.createEffect (fun () ->
            mounts.Value |> ignore
            let offset = int (Rows.Lead + top.Value - float anchor.Value * lineHeight)
            let element = surface.Viewport

            if isNull element then
                applied.Value <- Int32.MinValue
            elif applied.Value <> offset then
                applied.Value <- offset
                element.scrollTop <- offset)

        Api.createEffect (fun () ->
            let visible = height.Value

            if visible > 0.0 then
                editor.PageLines.Value <- max 1 (int (visible / lineHeight) - 1))


        // The viewport is measured when it is mounted, soon after, and then twice a second. A frame measures
        // nothing: a read of the layout after the writes of a transaction would lay the page out a second time.
        let rec poll (delay: float) =
            Api.runWithOwner owner (fun () ->
                Frames.timeout scheduler delay (fun () ->
                    measure ()
                    poll 500.0)
                |> ignore)

        poll 50.0

        let reveal (position: int) =
            let text = Api.untrack (fun () -> editor.Text.Value)
            let line = Text.lineAt (Math.Clamp (position, 0, text.Length)) text
            let lineTop = float line.Number * lineHeight
            let current = scrollTarget.Peek
            let visible = height.Peek

            if lineTop < current then
                scrollTo lineTop
            elif lineTop + lineHeight > current + visible then
                scrollTo (lineTop + lineHeight - visible)

        let requests = editor.Updates ()

        Api.createEffect (fun () ->
            let update = requests.Read ()
            let mutable target = -1

            for transaction in update.Transactions do
                if transaction.ScrollIntoView then
                    target <- update.State.Selection.Main.Head

                for effect in transaction.Effects do
                    match StateEffect.tryValue ViewFacets.scrollIntoView effect with
                    | ValueSome position -> target <- position
                    | ValueNone -> ()

            if target >= 0 then
                reveal target)

        // ---- Layers and plugins ----
        let layerInstances =
            Api.createMemoWith (fun (_: LayerInstance[] voption) ->
                layers.Value
                |> Array.map (fun spec ->
                    let markers =
                        Api.createMemo (fun (previous: LayerMarker[] voption) ->
                            let next = spec.Markers surface.View

                            // The same rectangles are the same value, so their elements are left alone.
                            match previous with
                            | ValueSome before when
                                before.Length = next.Length
                                && Array.forall2 (fun (a: LayerMarker) (b: LayerMarker) -> a.Rect = b.Rect && a.Class = b.Class) before next
                                ->
                                before
                            | _ -> next)

                    let rec slots: Memo<MarkerSlot[]> =
                        Api.createMemo (fun (previous: MarkerSlot[] voption) ->
                            let before = ValueOption.defaultValue Array.empty previous
                            let count = markers.Value.Length

                            // The pool of rectangles grows and stays: a rectangle without a marker is hidden.
                            if count <= before.Length then
                                before
                            else
                                Array.init count (fun index ->
                                    if index < before.Length then
                                        before[index]
                                    else
                                        {
                                            Index = index
                                            Markers = markers
                                            Class = spec.Class
                                        }))

                    {
                        Id = Interlocked.Increment &lastLayer
                        Above = spec.Above
                        Slots = slots
                    }))

        let below =
            Api.createMemo (fun (_: LayerInstance[] voption) -> layerInstances.Value |> Array.filter (fun layer -> not layer.Above))

        let above =
            Api.createMemo (fun (_: LayerInstance[] voption) -> layerInstances.Value |> Array.filter (fun layer -> layer.Above))

        let pluginValues =
            Api.createMemoWith (fun (_: Dictionary<int, obj> voption) ->
                let values = Dictionary<int, obj> ()

                for plugin in plugins.Value do
                    if not (values.ContainsKey plugin.Id) then
                        values[plugin.Id] <- Api.untrack (fun () -> plugin.CreateBoxed surface.View)

                values)

        // ---- The view ----
        surface.View <-
            {
                Editor = editor
                Scheduler = scheduler
                Theme = config.Theme
                Viewport = viewport
                HasFocus = Api.createMemo (fun (_: bool voption) -> focused.Value)
                ScrollTop = top
                Height = Api.createMemo (fun (_: float voption) -> height.Value)
                CharWidth = Api.createMemo (fun (_: float voption) -> charWidth.Value)
                GutterWidth = gutterWidth
                Focus =
                    fun () ->
                        if not (isNull surface.Input) then
                            surface.InputActive.Value <- true
                            surface.Input.focus ()
                ScrollTo = scrollTo
                // From where the view is: a target beyond a document that has shrunk counts as its end.
                ScrollBy = fun delta -> scrollTo (Math.Clamp (scrollTarget.Peek, 0.0, limit ()) + delta)
                PosAtCoords =
                    fun x y ->
                        Api.untrack (fun () ->
                            let text = editor.Text.Value
                            let line = Text.line (Math.Clamp (int ((top.Value + y) / lineHeight), 0, text.Lines - 1)) text
                            line.From + Columns.offsetAt line.Text tabSize.Value ((x - gutterWidth.Value) / charWidth.Value))
                CoordsAtPos =
                    fun position ->
                        let text = editor.Text.Value
                        let line = Text.lineAt (Math.Clamp (position, 0, text.Length)) text
                        let width = charWidth.Value
                        let tabs = tabSize.Value
                        let within = min (position - line.From) line.Text.Length

                        {
                            Left = gutterWidth.Value + float (Columns.columnOf line.Text tabs within) * width
                            Top = float (line.Number - anchor.Value) * lineHeight + Rows.Lead
                            Width = float (Columns.widthAt line.Text tabs within) * width
                            Height = lineHeight
                        }
                RunFrame = fun () -> Api.batch commit
                RowsWritten = fun () -> written.Value
                MarkupStats = fun () -> struct (markup.Hits, markup.Misses)
                ResetMarkup = fun () -> markup.Reset ()
                PluginValue =
                    fun id ->
                        match pluginValues.Value.TryGetValue id with
                        | true, value -> ValueSome value
                        | _ -> ValueNone
            }

        surface.Rows <- rows
        surface.RowSlots <- rowSlots
        surface.IsActive <- activeLine
        surface.Below <- below
        surface.Above <- above
        surface.ThumbTop <- thumbTop
        surface.ThumbHeight <- thumbHeight
        surface.Promoted <- promoted
        surface.Mounts <- mounts
        surface.Focused <- focused
        surface.Preedit <- Api.createSignal ""
        surface.InputActive <- Api.createSignal false
        surface.Measure <- measure
        surface.ScrollToFraction <- fun fraction -> scrollTo (Math.Clamp (fraction, 0.0, 1.0) * limit ())

        // The plugins run while the view is mounted, whether or not anything reads their values.
        Api.createEffect (fun () -> pluginValues.Value |> ignore)

        mounted.AddOrUpdate (editor, surface)
        // A second view of the editor may have taken the entry since.
        ctx.OnCleanup (fun () ->
            match mounted.TryGetValue editor with
            | true, held when obj.ReferenceEquals (held, surface) -> mounted.Remove editor |> ignore
            | _ -> ())
        surface

/// What the pointer and the wheel do, by the values their events carry. A point is in CSS pixels from the left and
/// the top of the viewport.
module internal PointerInput =

    let private select (surface: Surface) (anchor: int voption) (head: int) =
        let editor = surface.View.Editor
        let main = (Api.untrack (fun () -> editor.Selection.Value)).Main
        let anchor = ValueOption.defaultValue main.Anchor anchor

        if anchor <> main.Anchor || head <> main.Head then
            editor.Dispatch [ Tx.selection (EditorSelection.single anchor head); Tx.userEvent "select.pointer" ]

    /// A press at a point puts the cursor there, or with `extend` moves the head of the main range there. The moves
    /// that follow extend the selection until the release.
    let press (surface: Surface) (x: float) (y: float) (extend: bool) =
        let head = surface.View.PosAtCoords x y
        select surface (if extend then ValueNone else ValueSome head) head
        surface.Selecting <- true

    let move (surface: Surface) (x: float) (y: float) =
        if surface.Selecting then
            select surface ValueNone (surface.View.PosAtCoords x y)

    let release (surface: Surface) = surface.Selecting <- false

    let wheel (surface: Surface) (deltaY: float) = surface.View.ScrollBy deltaY

    /// Scrolls so that the scrollbar's thumb is centred `y` CSS pixels down a track `height` high.
    let thumbTo (surface: Surface) (y: float) (height: float) =
        let thumb = Api.untrack (fun () -> surface.ThumbHeight.Value)
        let room = height - thumb

        if room > 0.0 then
            surface.ScrollToFraction ((y - thumb / 2.0) / room)

module internal Handlers =

    let wheel (surface: Surface) (ev: WheelEvent) =
        ev.preventDefault ()
        PointerInput.wheel surface (float ev.deltaY)

    let pointerDown (surface: Surface) (pe: PointerEvent) =
        match box pe.currentTarget with
        | :? HTMLElement as element ->
            let rect = element.getBoundingClientRect ()
            pe.preventDefault ()
            surface.View.Focus ()
            PointerInput.press surface (float pe.clientX - rect.left) (float pe.clientY - rect.top) pe.shiftKey

            // Without the capture no release is sure to arrive, so the press selects and no drag follows.
            try
                element.setPointerCapture pe.pointerId
            with _ ->
                PointerInput.release surface
                reraise ()
        | _ -> ()

    let pointerMove (surface: Surface) (pe: PointerEvent) =
        if surface.Selecting then
            match box pe.currentTarget with
            | :? HTMLElement as element ->
                let rect = element.getBoundingClientRect ()
                PointerInput.move surface (float pe.clientX - rect.left) (float pe.clientY - rect.top)
            | _ -> ()

    let pointerEnd (surface: Surface) (_: PointerEvent) = PointerInput.release surface

    /// Scrolls to where the pointer is on the scrollbar track, with the thumb centred on it.
    let private dragTo (surface: Surface) (pe: PointerEvent) =
        match box pe.currentTarget with
        | :? HTMLElement as track ->
            let rect = track.getBoundingClientRect ()
            PointerInput.thumbTo surface (float pe.clientY - rect.top) rect.height
        | _ -> ()

    let trackDown (surface: Surface) (pe: PointerEvent) =
        match box pe.currentTarget with
        | :? HTMLElement as track ->
            pe.preventDefault ()
            track.setPointerCapture pe.pointerId
            surface.DraggingThumb <- true
            dragTo surface pe
        | _ -> ()

    let trackMove (surface: Surface) (pe: PointerEvent) =
        if surface.DraggingThumb then
            dragTo surface pe

    let trackEnd (surface: Surface) (_: PointerEvent) = surface.DraggingThumb <- false

    let keyDown (surface: Surface) (ev: KeyboardEvent) =
        let chord = Ranvier.Keyboard.Dom.KeyboardEvent.toKeyChord ev

        if not ev.isComposing && not (KeyChord.isModifierOnly chord) then
            let editor = surface.View.Editor

            match Keymap.run editor surface.Pending chord with
            | Handled ->
                ev.preventDefault ()
                surface.Pending <- []
            | Pending strokes ->
                ev.preventDefault ()
                surface.Pending <- strokes
            | Unhandled ->
                surface.Pending <- []

                // A printable key without a binding inserts its text.
                if not (ev.target :? HTMLTextAreaElement) && not ev.ctrlKey && not ev.altKey && not ev.metaKey then
                    match KeyChord.tryPlainText chord with
                    | ValueSome text ->
                        if Commands.insertText text editor then
                            ev.preventDefault ()
                    | ValueNone -> ()

[<RequireQualifiedAccess; CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module EditorView =

    /// The dark theme, six rows of overscan, and an anchor that moves every 700 lines.
    let defaults: ViewConfig =
        {
            Theme = Theme.dark
            Overscan = 6
            RebaseLines = 700
        }

    /// True when a line keeps its row element for as long as it stays in the row pool. Default true.
    let recycleRows: Facet<bool, bool> = ViewFacets.recycleRows
    /// The accessible name of the native editing control.
    let accessibleName = ViewFacets.accessibleName
    /// True when the sheet is promoted to a compositor layer. Default false.
    let compositorLayer: Facet<bool, bool> = ViewFacets.compositorLayer
    /// True when the markup of a line is kept by the line's text. Default true.
    let markupCache: Facet<bool, bool> = ViewFacets.markupCache
    /// An effect that scrolls a position into view in the views of the editor that dispatches it.
    let scrollIntoView: StateEffectType<int> = ViewFacets.scrollIntoView

    /// The view mounted on <c>editor</c>.
    let current (editor: Editor) : EditorView voption = Surface.current editor

    let mutable private lastScope = 0

    /// The view component for a theme, with the classes and rules it needs. The component is defined once and
    /// mounted any number of times; each mount takes an editor and the frame scheduler of its host.
    let create (config: ViewConfig) : EditorViewDefinition =
        let theme = Theme.define config.Theme
        let recorder = StyleRecorder ()
        let classOf = RecordingClassBuilder recorder
        // The theme's rules select inside this view's elements by an attribute of the viewport.
        let scope = String.Concat ("v", string (Interlocked.Increment &lastScope))

        let unselectable =
            utility {
                Css.userSelect CssValue.UserSelect.none
                Css.webkitUserSelect CssValue.WebkitUserSelect.none
            }

        let root =
            utility {
                Css.position CssValue.Position.relative
                Css.flexGrow (Css.number 1.)
                Css.minHeight (Css.px 0.)
                Declaration.create "height" "100%"
                Css.overflow CssValue.Overflow.hidden
                Declaration.create "outline" "none"
                Declaration.create "background-color" theme.Background
            }

        // The clipped window on the document.
        let viewport =
            utility {
                Css.position CssValue.Position.absolute
                Css.left (Css.px 0.)
                Css.top (Css.px 0.)
                Css.bottom (Css.px 0.)
                Css.right (Css.px 14.)
                Css.overflow CssValue.Overflow.hidden
                Css.contain CssValue.Contain.strict
                Css.cursor CssValue.Cursor.text
                Declaration.create "font-family" theme.FontFamily
                Css.fontSize (Css.px theme.FontSize)
                Css.lineHeight (Css.px theme.LineHeight)
                Css.whiteSpace CssValue.WhiteSpace.pre
                // Kerning and ligatures off: every glyph of the font then advances by the same amount. A tab and a
                // character outside ASCII are drawn on the same grid by the line's markup (`Columns`), so a column is
                // a multiplication.
                Css.fontKerning CssValue.Font.Kerning.none
                Css.fontVariantLigatures CssValue.Font.Variant.Ligatures.none
                Declaration.create "color" theme.Ink
                unselectable
            }

        // The strip behind the gutters.
        let gutterStrip =
            utility {
                Css.position CssValue.Position.absolute
                Css.left (Css.px 0.)
                Css.top (Css.px 0.)
                Css.bottom (Css.px 0.)
                Css.borderRightWidth (Css.px 1.)
                Css.borderRightStyle CssValue.Border.Style.solid
                Declaration.create "border-right-color" theme.Edge
            }

        // A class holds what is fixed on an element; what changes is one text in its style attribute.
        //FOR-REVIEW The view had these as Ranvier.Css reader values (Css.top (fun slot -> ...), whenActive). An
        //FOR-REVIEW instance renders every binding when one value changes, and each reader value is lowered again on
        //FOR-REVIEW each render: a CPU trace of typing had NumberText.format, GrammarRuntime.lookup and
        //FOR-REVIEW Declaration.isCssWideWith under Style.variableDeclarations (Ranvier.Css 0.1.1). A cache of the
        //FOR-REVIEW lowered value by reader result in Ranvier.Css would let reader values back in here.
        let gutterStripStyle (surface: Surface) : string =
            let width = surface.View.GutterWidth.Value

            if width <= 0.0 then
                "display:none"
            else
                String.Concat ("width:", Px.text (max 0.0 (width - 12.0)))

        // The sheet the rows lie on. The viewport scrolls over it by its own scroll offset; a row keeps its place
        // on the sheet for as long as it shows the same line.
        let sheet =
            utility {
                Css.position CssValue.Position.absolute
                Css.left (Css.px 0.)
                Css.right (Css.px 0.)
                Css.top (Css.px 0.)
                Css.height (Css.px Rows.SheetHeight)
            }

        let sheetStyle (surface: Surface) : string option =
            if surface.Promoted.Value then Some "will-change:transform" else None

        // One line of text: a box of fixed size that contains its own layout and paint.
        let row =
            utility {
                Css.position CssValue.Position.absolute
                Css.left (Css.px 0.)
                Css.right (Css.px 0.)
                Css.height (Css.px theme.LineHeight)
                Css.overflow CssValue.Overflow.hidden
                Css.contain CssValue.Contain.strict
            }

        let activeInk = String.Concat (";--pm-gutter-ink:", theme.Ink)

        // The gutter markers of the main cursor's line take the text colour through a variable of the row.
        let rowStyle (slot: RowSlot) : string =
            let content = slot.Surface.Rows.Get slot.Slot

            if content.Line >= 0 && slot.Surface.IsActive.Get content.Line then
                String.Concat ("top:", Px.text content.Top, activeInk)
            else
                String.Concat ("top:", Px.text content.Top)

        let markerOf (slot: MarkerSlot) : LayerMarker voption =
            let markers = slot.Markers.Value

            if slot.Index < markers.Length then
                ValueSome markers[slot.Index]
            else
                ValueNone

        // A rectangle of a layer.
        let marker =
            utility {
                Css.position CssValue.Position.absolute
                Css.pointerEvents CssValue.PointerEvents.none
            }

        let markerStyle (slot: MarkerSlot) : string =
            match markerOf slot with
            | ValueSome marker ->
                let rect = marker.Rect
                String.Concat ("left:", Px.text rect.Left, ";top:", Px.text rect.Top, ";width:", Px.text rect.Width, ";height:", Px.text rect.Height)
            | ValueNone -> "display:none"

        // A hidden run of 64 characters, measured to find the advance of one.
        let probe =
            utility {
                Css.position CssValue.Position.absolute
                Css.left (Css.px 0.)
                Css.top (Css.px -40.)
                Declaration.create "visibility" "hidden"
            }

        let scrollTrack =
            utility {
                Css.position CssValue.Position.absolute
                Css.top (Css.px 0.)
                Css.bottom (Css.px 0.)
                Css.right (Css.px 0.)
                Css.width (Css.px 14.)
                Css.borderLeftWidth (Css.px 1.)
                Css.borderLeftStyle CssValue.Border.Style.solid
                Declaration.create "border-left-color" theme.Edge
                Css.cursor CssValue.Cursor.``default``
                Declaration.create "touch-action" "none"
            }

        let scrollThumb =
            utility {
                Css.position CssValue.Position.absolute
                Css.left (Css.px 2.)
                Css.right (Css.px 2.)
                Css.borderRadius (Css.px 4.)
                Declaration.create "background-color" theme.Thumb
                Css.pointerEvents CssValue.PointerEvents.none
            }

        let thumbStyle (surface: Surface) : string =
            String.Concat ("top:", Px.text surface.ThumbTop.Value, ";height:", Px.text surface.ThumbHeight.Value)

        // A row of the pool: a markup slot for the gutter markers and one for the line. A row is an instance with its
        // own render effect, so a change to its content re-reads the bindings of this row alone, and an edit to the
        // line writes the line's slot and leaves the markers' elements in place.
        let rowView: TemplateView<RowSlot> =
            div (classOf { row }, div.attribute ("data-pm", "row"), div.attribute ("aria-hidden", "true"), div.style rowStyle) {
                span (span.unsafeHtml (fun (slot: RowSlot) -> (slot.Surface.Rows.Get slot.Slot).Gutter)) { () }
                span (span.unsafeHtml (fun (slot: RowSlot) -> (slot.Surface.Rows.Get slot.Slot).Markup)) { () }
            }

        let markerView: TemplateView<MarkerSlot> =
            div (
                classOf { marker },
                div.style markerStyle,
                div.attribute (
                    "data-pm",
                    fun (slot: MarkerSlot) ->
                        match markerOf slot with
                        | ValueSome found when found.Class.Length > 0 -> Some (String.Concat (slot.Class, " ", found.Class))
                        | _ -> Some slot.Class
                )
            ) {
                ()
            }

        let layerView: TemplateView<LayerInstance> =
            fragment [| forEachFixed (fun (slot: MarkerSlot) -> slot.Index) (fun (layer: LayerInstance) -> layer.Slots.Value) markerView |]

        let caretPosition (surface: Surface) =
            let view = surface.View
            let rect = view.CoordsAtPos view.Editor.Selection.Value.Main.Head
            String.Concat("left:", Px.text rect.Left, ";top:", Px.text rect.Top, ";height:", Px.text rect.Height)

        let overlay = view (fun (struct (spec: DecorationOverlay, editorView: EditorView)) -> Component.render spec.Body editorView)

        let template: TemplateView<Surface> =
            div (
                classOf { root },
                div.onKeydown Handlers.keyDown
            ) {
                div (
                    classOf { viewport },
                    div.attribute ("data-plume", scope),
                    div.mounted (fun (surface: Surface) (element: HTMLDivElement) ->
                        surface.Viewport <- element
                        surface.Mounts.Value <- surface.Mounts.Peek + 1
                        surface.Measure ()

                        fun () ->
                            surface.Viewport <- null
                            surface.Mounts.Value <- surface.Mounts.Peek + 1),
                    div.onWheel Handlers.wheel,
                    div.onPointerdown Handlers.pointerDown,
                    div.onPointermove Handlers.pointerMove,
                    div.onPointerup Handlers.pointerEnd,
                    div.onPointercancel Handlers.pointerEnd
                ) {
                    div (classOf { gutterStrip }, div.style gutterStripStyle) { () }

                    div (classOf { sheet }, div.style sheetStyle) {
                        forEachFixed (fun (layer: LayerInstance) -> layer.Id) (fun (surface: Surface) -> surface.Below.Value) layerView
                        forEachFixed (fun (slot: RowSlot) -> slot.Slot) (fun (surface: Surface) -> surface.RowSlots.Value) rowView
                        forEachFixed (fun (layer: LayerInstance) -> layer.Id) (fun (surface: Surface) -> surface.Above.Value) layerView
                        forEachFixed (fun (struct (spec: DecorationOverlay, _)) -> spec.Id)
                            (fun (s: Surface) -> s.View.Editor.Facet(Decoration.overlays).Value |> Array.map (fun spec -> struct (spec, s.View))) overlay
                        span (
                            span.attribute ("aria-hidden", "true"),
                            span.style (fun (s: Surface) ->
                                if String.IsNullOrEmpty s.Preedit.Value then "display:none"
                                else "position:absolute;pointer-events:none;text-decoration:underline;background:" + theme.Background + ";" + caretPosition s)
                        ) { fun (s: Surface) -> s.Preedit.Value }
                    }

                    span (
                        classOf { probe },
                        span.mounted (fun (surface: Surface) (element: HTMLSpanElement) ->
                            surface.Probe <- element
                            fun () -> surface.Probe <- null)
                    ) {
                        String ('0', 64)
                    }
                }

                textarea (
                    textarea.attribute ("aria-label", fun (s: Surface) -> Some (s.View.Editor.Facet(accessibleName).Value)),
                    textarea.attribute ("wrap", "off"),
                    textarea.style (fun (s: Surface) ->
                        let pos =
                            if s.Focused.Value then
                                let v = s.View
                                let head = v.Editor.Selection.Value.Main.Head
                                let rect = v.CoordsAtPos head
                                let line = Text.lineAt head v.Editor.Text.Value
                                let y = Math.Clamp(float line.Number * rect.Height - v.ScrollTop.Value, 0., max 0. (v.Height.Value - rect.Height))
                                String.Concat("left:min(", Px.text rect.Left, ", 99%);top:", Px.text y, ";height:", Px.text rect.Height)
                            else "left:0;top:0"
                        "position:absolute;opacity:0;pointer-events:none;overflow:hidden;resize:none;width:1px;padding:0;border:0;" + pos),
                    textarea.mounted (fun (s: Surface) area ->
                        s.Input <- area
                        let release = NativeInput.mount s.View s.Focused s.InputActive s.Preedit area
                        fun () -> release (); s.Input <- null)
                ) { () }
                div (
                    classOf { scrollTrack },
                    div.onPointerdown Handlers.trackDown,
                    div.onPointermove Handlers.trackMove,
                    div.onPointerup Handlers.trackEnd,
                    div.onPointercancel Handlers.trackEnd
                ) {
                    div (classOf { scrollThumb }, div.style thumbStyle) { () }
                }
            }

        let under (selector: string) : Selector =
            Selector.unsafeFromText (String.Concat ("[data-plume=\"", scope, "\"] ", selector))

        let rules =
            [
                yield Rule.style (under "[data-pm=decoration]") [
                    Declaration.create "position" "absolute"
                    Declaration.create "box-sizing" "border-box"
                ]
                yield Rule.style (under "[data-decoration=underline]") [
                    Declaration.create "border-bottom" "2px dotted currentColor"
                ]
                yield Rule.style (under "[data-pm=decoration-hover]") [
                    Declaration.create "display" "none"
                    Declaration.create "position" "absolute"
                    Declaration.create "top" "100%"
                    Declaration.create "left" "0"
                    Declaration.create "width" "max-content"
                    Declaration.create "background-color" theme.Background
                    Declaration.create "color" theme.Ink
                    Declaration.create "pointer-events" "none"
                ]
                yield Rule.style (under "[data-pm=decoration]:hover") [ Declaration.create "z-index" "1" ]
                yield Rule.style (under "[data-pm=decoration]:hover > [data-pm=decoration-hover]") [ Declaration.create "display" "block" ]
                yield Rule.style (under "[data-pm~=\"cursor\"]") [ Declaration.create "background-color" theme.Accent ]
                yield Rule.style (under "[data-pm~=\"selection\"]") [ Declaration.create "background-color" theme.Selection ]
                yield Rule.style (under "[data-pm~=\"active-line\"]") [ Declaration.create "background-color" theme.ActiveLine ]

                yield
                    Rule.style
                        (under "[data-pm~=\"gutter\"]")
                        [
                            Declaration.create "display" "inline-block"
                            Declaration.create "text-align" "right"
                            Declaration.create "color" (String.Concat ("var(--pm-gutter-ink, ", theme.Faint, ")"))
                        ]

                // The text starts 24 CSS pixels after the last gutter.
                yield Rule.style (under "[data-pm~=\"gutter-last\"]") [ Declaration.create "margin-right" "24px" ]

                // A character outside ASCII is drawn in a box of one column or of two. The boxes are found by class:
                // an engine tests a class rule against the elements of that class alone.
                yield Rule.style (under ".pm-c1") [ Declaration.create "display" "inline-block"; Declaration.create "width" "1ch" ]
                yield Rule.style (under ".pm-c2") [ Declaration.create "display" "inline-block"; Declaration.create "width" "2ch" ]

                for _, name, declarations in theme.Tokens do
                    yield
                        Rule.style
                            (under ("." + name))
                            (declarations |> List.map (fun (property, value) -> Declaration.create property value))
            ]

        {
            Component = Component.template (Surface.create { config with Theme = theme }) template
            Styles = recorder.Definitions
            Rules = rules
        }
