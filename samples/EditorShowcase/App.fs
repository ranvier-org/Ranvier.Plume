/// The view: state, handlers and the component tree. Kept apart from the entry point so hot reload can patch it.
module EditorShowcase.App

open System
open System.Diagnostics
open System.Globalization
open Ranvier
open Ranvier.Dom
open Ranvier.View
open Ranvier.View.Dom
open Ranvier.View.Dsl
open Ranvier.Css
open Ranvier.Css.View
open Ranvier.View.WebCore
open Ranvier.View.WebCore.Sdl
open Ranvier.Plume
open Ranvier.Keyboard
open Ranvier.Plume.View
open EditorShowcase.Sample
open EditorShowcase.Model
// Last, so that `styles` is the one that records the rules it applies.
open EditorShowcase.LiveSheet

/// The labels of the statistics panel, in the order of `State.Stats`.
let StatLabels =
    [| "lines in file"
       "row elements"
       "rows computed / frame"
       "rows computed / s"
       "markup cache hits"
       "lines lexed / s"
       "frame work"
       "frames / s" |]

let private diagnosticHover: TemplateView<unit -> Diagnostic> =
    span (span.style "padding:3px 8px;border:1px solid currentColor") {
        strong () { fun read -> (read()).Code }
        "  "
        fun read -> (read()).Message
    }

let private diagnosticMarks =
    Decoration.underline "unit-diagnostic" (fun view -> view.Editor.Field(diagnostics).Value) diagnosticHover

/// The text view, defined once and mounted for each file.
let editorView = EditorView.create EditorView.defaults

// The renderer settings are compartments, so a toggle reconfigures one facet of every open editor.
let private recycleSlot = Compartment.create ()
let private cacheSlot = Compartment.create ()
let private layerSlot = Compartment.create ()

/// The features of the text view, each in a compartment of its own, so a measurement can switch one off.
let features: (string * Compartment * Extension) list =
    [ "numbers", Compartment.create (), LineNumbers.extension LineNumbers.defaults
      "active", Compartment.create (), ActiveLine.extension
      "selection", Compartment.create (), DrawSelection.extension
      "cursor", Compartment.create (), Cursor.extension Cursor.defaults
      "language", Compartment.create (), Language.extension Lexer.language ]

/// An open file: its editor, and the frames of the window it is shown in.
[<ReferenceEquality>]
type FileSlot =
    { Index: int
      Editor: Editor
      Scheduler: FrameScheduler
      FindText: Signal<string>
      Replacement: Signal<string> }

/// The shell's instance.
[<ReferenceEquality>]
type State =
    { Props: Props
      /// The open files by index; null where a file is not open yet.
      Files: FileSlot[]
      /// Opens a file if it is not open, and gives its slot.
      Ensure: int -> FileSlot
      Active: Signal<int>
      Tour: Signal<bool>
      Recycle: Signal<bool>
      Cached: Signal<bool>
      Layer: Signal<bool>
      Stats: Signal<string[]>
      Status: Memo<string>
      /// The number of lines of the active file.
      Lines: Memo<int>
      /// Advances the app by one frame of `dt` seconds; a scripted run calls it in place of the frame callback.
      Frame: float -> unit
      /// Starts the frame loop when the tour runs.
      Wake: unit -> unit
      RevealDiagnostic: unit -> unit }

let private activeSlot (s: State) : FileSlot = s.Files[s.Active.Value]

/// The view of the active file, once it is mounted.
let currentView (s: State) : EditorView voption =
    let slot = s.Files[s.Active.Peek]

    if isNull (box slot) then
        ValueNone
    else
        EditorView.current slot.Editor

// ---- Handlers of the shell ----

let private reconfigure (s: State) (slot: Compartment) (content: Extension) =
    for file in s.Files do
        if not (isNull (box file)) then
            file.Editor.Dispatch [ Tx.effect (slot.Reconfigure content) ]

let openFile (s: State) (index: int) =
    s.Ensure index |> ignore
    s.Props.Session.Active <- index
    s.Active.Value <- index

let toggleTour (s: State) =
    s.Props.Session.Tour <- not s.Props.Session.Tour
    s.Tour.Value <- s.Props.Session.Tour
    s.Wake ()

let setRecycle (s: State) (value: bool) =
    s.Props.Session.Recycle <- value
    s.Recycle.Value <- value
    reconfigure s recycleSlot (EditorView.recycleRows.Of value)

let setCached (s: State) (value: bool) =
    s.Props.Session.Cached <- value
    s.Cached.Value <- value
    reconfigure s cacheSlot (EditorView.markupCache.Of value)

let setLayer (s: State) (value: bool) =
    s.Props.Session.Layer <- value
    s.Layer.Value <- value
    reconfigure s layerSlot (EditorView.compositorLayer.Of value)

let private documentY (view: EditorView) position =
    let line = (Text.lineAt position view.Editor.Text.Value).Number
    float (view.Projection.Value.VisualRow line) * view.Theme.LineHeight

let private showDiagnostic (view: EditorView voption) stopTour =
    match view with
    | ValueSome view ->
        let marks = view.Editor.Field(diagnostics).Value
        if marks.Count > 0 then
            stopTour ()
            Folding.reveal marks.From[0] view.Editor |> ignore
            view.ScrollTo(max 0.0 (documentY view marks.From[0] - 2.0 * view.Theme.LineHeight))
            view.Editor.Dispatch [Tx.selection (EditorSelection.single marks.From[0] marks.From[0])]
            view.RunFrame()
            view.Focus()
    | ValueNone -> ()

let private keepFocus (_: State) (ev: MouseEvent) = ev.preventDefault ()

let private milliseconds (ticks: int64) = float ticks * 1000.0 / float Stopwatch.Frequency
let private fixed1 (value: float) = value.ToString("F1", CultureInfo.InvariantCulture)

// ---- Setup: the editors, the frame loop and the statistics ----

let private setup (ctx: Setup) (props: Props) : State =
    let session = props.Session
    let host = props.Window.Host
    let graph = Graph.Current
    let owner = Api.getOwner ()
    let scheduler = Frames.forHost host
    let watch = Stopwatch.StartNew()
    let files: FileSlot[] = Array.zeroCreate session.Files.Length
    let active = Api.createSignal session.Active
    let tour = Api.createSignal session.Tour
    let recycle = Api.createSignal session.Recycle
    let cached = Api.createSignal session.Cached
    let layer = Api.createSignal session.Layer
    let stats: Signal<string[]> = Api.createSignal (Array.create StatLabels.Length "")
    // Set once the state exists; the editors' key bindings call it.
    let onToggleTour: (unit -> unit) ref = ref ignore

    // The stylesheet is one constructed sheet per window, adopted by the document. The editor view hands over the
    // classes it applies and the rules of its theme; the shell's own classes are recorded where they are applied.
    add editorView.Styles
    let stylesheet = attach host (Styles.page @ editorView.Rules) session.Stylesheet
    session.Stylesheet <- box stylesheet

    /// Restores the place a file was left at, and remembers it when the view goes.
    let scrollMemory (index: int) =
        ViewPlugin.define (fun view ->
            session.MountedView <- ValueSome view
            view.ScrollTo session.Scrolls[index]
            Api.onCleanup (fun () ->
                match session.MountedView with
                | ValueSome current when obj.ReferenceEquals(current, view) -> session.MountedView <- ValueNone
                | _ -> ()
                session.Scrolls[index] <- Api.untrack (fun () -> view.ScrollTop.Value)))

    let extensionsFor (index: int) : Extension list =
        [ Keymap.extension
              [ Keymap.bindChords [ chord { key (FunctionKey 5) } ] (fun _ ->
                    onToggleTour.Value ()
                    true) ]
          Keymap.extension Keymap.defaults
          SearchView.highlight "search-match"
          Folding.extension
          Brackets.autoClose
          BracketMatching.extension
          StateField.extension diagnostics
          diagnosticMarks
          EditorView.accessibleName.Of session.Files[index].Name
          Extension.many (features |> List.map (fun (_, slot, extension) -> slot.Of extension))
          recycleSlot.Of (EditorView.recycleRows.Of session.Recycle)
          cacheSlot.Of (EditorView.markupCache.Of session.Cached)
          layerSlot.Of (EditorView.compositorLayer.Of session.Layer)
          ViewPlugin.extension (scrollMemory index) ]

    let ensure (index: int) : FileSlot =
        if isNull (box files[index]) then
            Api.runWithOwner owner (fun () ->
                let text =
                    if isNull (box session.Texts[index]) then
                        Text.ofLines session.Files[index].Lines
                    else
                        session.Texts[index]

                session.Texts[index] <- text

                files[index] <-
                    { Index = index
                      Editor = Editor.create (Document.create text) (extensionsFor index)
                      Scheduler = scheduler
                      FindText = Api.createSignal ""
                      Replacement = Api.createSignal "" })

        files[index]

    ensure session.Active |> ignore

    // A reload opens the text each file had.
    ctx.OnCleanup(fun () ->
        for file in files do
            if not (isNull (box file)) then
                session.Texts[file.Index] <- Api.untrack (fun () -> file.Editor.Text.Value))

    let status =
        Api.createMemo (fun (_: string voption) ->
            let file = files[active.Value]
            let text = file.Editor.Text.Value
            let struct (line, column) = Text.positionOf file.Editor.Selection.Value.Main.Head text
            $"Ln {grouped (line + 1)}, Col {column + 1}")

    let lines = Api.createMemo (fun (_: int voption) -> files[active.Value].Editor.Text.Value.Lines)

    let view () : EditorView voption = EditorView.current files[active.Peek].Editor

    // What the statistics panel shows is counted over a quarter of a second.
    let mutable windowStart = watch.Elapsed.TotalSeconds
    let mutable frames = 0
    let mutable frameTicks = 0L
    let mutable rowsAtStart = 0L
    let mutable lexedAtStart = 0L
    let mutable tourClock = 0.0

    let publish (now: float) =
        match view () with
        | ValueSome view ->
            let span = max 1e-3 (now - windowStart)
            let rows = view.RowsWritten()
            let struct (hits, misses) = view.MarkupStats()
            let lookups = hits + misses
            let height = Api.untrack (fun () -> view.Height.Value)

            stats.Value <-
                [| grouped (Api.untrack (fun () -> lines.Value))
                   string (if height > 0.0 then int (Math.Ceiling(height / view.Theme.LineHeight)) + 13 else 0)
                   (if frames > 0 then fixed1 (float (rows - rowsAtStart) / float frames) else "-")
                   grouped (int (float (rows - rowsAtStart) / span))
                   (if not session.Cached then "off"
                    elif lookups = 0L then "-"
                    else fixed1 (100.0 * float hits / float lookups) + " %")
                   grouped (int (float (misses - lexedAtStart) / span))
                   (if frames > 0 then (milliseconds frameTicks / float frames).ToString("F3", CultureInfo.InvariantCulture) + " ms" else "-")
                   fixed1 (float frames / span) |]

            rowsAtStart <- rows
            lexedAtStart <- misses
        | ValueNone -> ()

        windowStart <- now
        frames <- 0
        frameTicks <- 0L

    /// One frame: advance the tour, then lay the rows out for the scroll position asked for.
    let frame (dt: float) =
        match view () with
        | ValueSome view ->
            let started = Stopwatch.GetTimestamp()

            graph.Run(fun () ->
                if session.Tour then
                    // The tour cruises and speeds up in bursts, and starts again from the top at the end.
                    tourClock <- tourClock + dt
                    let burst = Math.Pow(max 0.0 (sin (tourClock * 0.8)), 6.0)
                    let lineHeight = view.Theme.LineHeight
                    let range = max 0.0 (float view.Projection.Value.Count * lineHeight - view.Height.Value + 2.0 * lineHeight)
                    let next = view.ScrollTop.Value + (160.0 + 5200.0 * burst) * dt
                    view.ScrollTo(if next >= range then 0.0 else next)

                view.RunFrame()
                Api.flush ())

            frameTicks <- frameTicks + (Stopwatch.GetTimestamp() - started)
            frames <- frames + 1
        | ValueNone -> ()

        // A view built by this frame's writes may have applied a class for the first time.
        flush stylesheet

        // A scripted run has no timer: its frames publish the statistics.
        if not session.Auto then
            let now = watch.Elapsed.TotalSeconds

            if stats.Peek[0] = "" || now - windowStart >= 0.25 then
                graph.Run(fun () -> publish now)

    let mutable last = watch.Elapsed.TotalSeconds
    let mutable alive = true
    /// True while a frame callback is requested.
    let mutable armed = false

    // The app drives frames only while the tour runs. A view at rest asks for its own frames when it scrolls.
    let rec tick (_: float) =
        armed <- false

        if alive && session.Auto && session.Tour then
            let now = watch.Elapsed.TotalSeconds
            let dt = min 0.1 (now - last)
            last <- now
            frame dt
            wake ()

    and wake () =
        if alive && session.Auto && session.Tour && not armed then
            armed <- true
            last <- watch.Elapsed.TotalSeconds
            host.RequestAnimationFrame tick |> ignore

    let rec report () =
        if alive then
            graph.Run(fun () -> publish watch.Elapsed.TotalSeconds)
            host.SetTimeout(report, 250.0) |> ignore

    let state =
        { Props = props
          Files = files
          Ensure = ensure
          Active = active
          Tour = tour
          Recycle = recycle
          Cached = cached
          Layer = layer
          Stats = stats
          Status = status
          Lines = lines
          Frame = frame
          Wake = wake
          RevealDiagnostic = fun () -> graph.Run(fun () -> showDiagnostic session.MountedView (fun () -> if tour.Peek then onToggleTour.Value ())) }

    onToggleTour.Value <- fun () -> toggleTour state

    // The text view takes the keyboard as soon as it is mounted.
    host.SetTimeout(
        (fun () ->
            match view () with
            | ValueSome view -> view.Focus()
            | ValueNone -> ()),
        0.0
    )
    |> ignore

    wake ()
    host.SetTimeout(report, 250.0) |> ignore
    ctx.OnCleanup(fun () -> alive <- false)
    session.State <- box state
    state

// ---- The statistics panel ----

let private hud: Component<Signal<string[]>> =
    Component.template
        (fun _ (stats: Signal<string[]>) -> stats)
        (div (styles { Styles.hud }) {
            for index in 0 .. StatLabels.Length - 1 do
                div (styles { Styles.hudRow }) {
                    span (styles { Styles.hudLabel }) { StatLabels[index] }

                    span (styles { Styles.hudValue }) {
                        fun (stats: Signal<string[]>) ->
                            let values = stats.Value
                            if index < values.Length then values[index] else ""
                    }
                }
        })

// ---- The shell ----

let private fileName (s: State) (index: int) = s.Props.Session.Files[index].Name

let private sidebarView: TemplateView<State> =
    div (styles { Styles.sidebar }) {
        div (styles { Styles.sideTitle }) { "Explorer" }

        for index in 0..2 do
            button (
                button.batch (styles { Styles.fileItem (fun (s: State) -> s.Active.Value = index) }),
                button.onMousedown keepFocus,
                button.onClick (fun (s: State) _ -> openFile s index)
            ) {
                span () { fun (s: State) -> fileName s index }
                span (styles { Styles.fileSize }) { fun (s: State) -> grouped s.Props.Session.Files[index].Count + " lines" }
            }

        div (styles { Styles.sideTitle }) { "Text renderer" }

        button (button.batch (styles { Styles.toggle (fun (s: State) -> s.Tour.Value) }), button.onMousedown keepFocus, button.onClick (fun (s: State) _ -> toggleTour s)) {
            fun (s: State) -> if s.Tour.Value then "Scroll tour: running" else "Scroll tour: stopped"
        }

        button (
            button.batch (styles { Styles.toggle (fun (s: State) -> s.Recycle.Value) }),
            button.onMousedown keepFocus,
            button.onClick (fun (s: State) _ -> setRecycle s (not s.Recycle.Peek))
        ) {
            fun (s: State) -> if s.Recycle.Value then "Row recycling: on" else "Row recycling: off"
        }

        button (
            button.batch (styles { Styles.toggle (fun (s: State) -> s.Cached.Value) }),
            button.onMousedown keepFocus,
            button.onClick (fun (s: State) _ -> setCached s (not s.Cached.Peek))
        ) {
            fun (s: State) -> if s.Cached.Value then "Markup cache: on" else "Markup cache: off"
        }

        button (
            button.batch (styles { Styles.toggle (fun (s: State) -> s.Layer.Value) }),
            button.onMousedown keepFocus,
            button.onClick (fun (s: State) _ -> setLayer s (not s.Layer.Peek))
        ) {
            fun (s: State) -> if s.Layer.Value then "Compositor layer: on" else "Compositor layer: off"
        }

        button (
            button.batch (styles { Styles.toggle (fun (_: State) -> false) }),
            button.attribute ("data-demo", "diagnostic"),
            button.attribute ("disabled", fun (s: State) -> if (activeSlot s).Editor.Field(diagnostics).Value.Count = 0 then Some "" else None),
            button.onMousedown keepFocus,
            button.onClick (fun (s: State) _ -> s.RevealDiagnostic())
        ) { "Go to diagnostic" }

        render hud (fun (s: State) -> s.Stats)

        div (styles { Styles.hint }) {
            "Hover the underlined Mu: float for its unit diagnostic. Select multiple lines to fold them. Type an opening bracket to insert its mate. Ctrl+Z undoes an edit. F5 starts or stops the tour."
        }
    }

let private tabsView: TemplateView<State> =
    div (styles { Styles.tabs }) {
        for index in 0..2 do
            button (
                button.batch (styles { Styles.tab (fun (s: State) -> s.Active.Value = index) }),
                button.onMousedown keepFocus,
                button.onClick (fun (s: State) _ -> openFile s index)
            ) {
                fun (s: State) -> fileName s index
            }
    }

let private editAction (action: FileSlot -> unit) (s: State) _ =
    if s.Tour.Peek then toggleTour s
    let slot = activeSlot s
    action slot
    match s.Props.Session.MountedView with
    | ValueSome view ->
        let head = slot.Editor.Selection.Value.Main.Head
        Folding.reveal head slot.Editor |> ignore
        let y = documentY view head
        if y < view.ScrollTop.Value || y + view.Theme.LineHeight > view.ScrollTop.Value + view.Height.Value then
            view.ScrollTo(max 0.0 (y - 2.0 * view.Theme.LineHeight))
        view.RunFrame()
        view.Focus()
    | ValueNone -> ()

let private find (slot: FileSlot) =
    let query = slot.FindText.Peek
    Search.setQuery (if query = "" then ValueNone else ValueSome (Search.literal query false)) slot.Editor |> ignore
    if query <> "" then Search.next slot.Editor |> ignore

let private foldSelection (slot: FileSlot) =
    let editor = slot.Editor
    let doc = editor.Text.Value
    let range = editor.Selection.Value.Main
    let first = (Text.lineAt range.From doc).Number
    let last = (Text.lineAt (max range.From (range.To - 1)) doc).Number
    if last > first then
        Folding.fold first last editor |> ignore

let private toolButton (label: string) action : TemplateView<State> =
    button (button.batch (styles { Styles.toolButton }), button.onMousedown keepFocus, button.onClick (editAction action)) { label }

let private toolField (label: string) (field: FileSlot -> Signal<string>) =
    input (
        input.batch (styles { Styles.toolField }),
        input.attribute ("aria-label", label),
        input.attribute ("placeholder", label),
        input.value (fun (s: State) -> (field (activeSlot s)).Value),
        input.on ("input", fun (s: State) ev ->
            match box ev.target with
            | :? HTMLInputElement as element -> (field (activeSlot s)).Value <- element.value
            | _ -> ())
    )

let private editingTools: TemplateView<State> =
    div (styles { Styles.toolBar }) {
        toolField "Find text" (fun slot -> slot.FindText)
        toolButton "Find" find
        toolButton "Previous" (fun slot -> Search.previous slot.Editor |> ignore)
        toolButton "Next" (fun slot -> Search.next slot.Editor |> ignore)
        toolField "Replacement" (fun slot -> slot.Replacement)
        toolButton "Replace" (fun slot -> Search.replaceCurrent slot.Replacement.Peek slot.Editor |> ignore)
        toolButton "Replace all" (fun slot -> Search.replaceAll slot.Replacement.Peek slot.Editor |> ignore)
        toolButton "Fold selection" foldSelection
        toolButton "Unfold" (fun slot ->
            let line = Text.lineAt slot.Editor.Selection.Value.Main.Head slot.Editor.Text.Value
            Folding.unfold line.Number slot.Editor |> ignore)
    }

let private statusView: TemplateView<State> =
    div (styles { Styles.statusBar }) {
        span (styles { Styles.statusItem }) { fun (s: State) -> s.Status.Value }
        span (styles { Styles.statusItem }) { fun (s: State) -> grouped s.Lines.Value + " lines" }
        span (styles { Styles.statusItem }) { "Spaces: 4" }
        span (styles { Styles.statusItem }) { "UTF-8" }
        span (styles { Styles.statusItem }) { "F#" }

        span (styles { Styles.statusMode }) {
            fun (s: State) ->
                String.Join(
                    "  |  ",
                    [ (if s.Tour.Value then "tour" else "manual")
                      (if s.Recycle.Value then "recycled rows" else "rows rewritten in place")
                      (if s.Cached.Value then "cached markup" else "lexing every row")
                      (if s.Layer.Value then "layer" else "no layer") ]
                )
        }
    }

/// The text view of one file. Another file is another instance, mounted in its place.
let private fileView: TemplateView<FileSlot> =
    render editorView.Component (fun (file: FileSlot) -> { Editor = file.Editor; Scheduler = file.Scheduler })

let app: Component<Props> =
    Component.template
        setup
        (div (styles { Styles.app }) {
            div (styles { Styles.body }) {
                sidebarView

                div (styles { Styles.main }) {
                    tabsView
                    editingTools
                    div (styles { Styles.crumbs }) { fun (s: State) -> "src  >  kernels  >  " + fileName s s.Active.Value }

                    div (styles { Styles.editorArea }) {
                        forEachFixed (fun (file: FileSlot) -> file.Index) (fun (s: State) -> [| activeSlot s |]) fileView
                    }
                }
            }

            statusView
        })
