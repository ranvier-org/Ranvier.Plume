/// A hidden window with one editor view mounted in it.
module Ranvier.Plume.View.Tests.Host

// SDL's events are native structures, pushed by address.
#nowarn "9" "51"

open System
open System.Text
open Ranvier
open Ranvier.Dom
open Ranvier.View
open Ranvier.View.Dom
open Ranvier.View.Dsl
open Ranvier.Css
open Ranvier.View.WebCore
open Ranvier.View.WebCore.Sdl
open Ranvier.Plume
open Ranvier.Plume.View

/// The native engine and its windows belong to one thread for the life of the process. Every test runs its
/// body on this thread.
module private NativeThread =
    let private queue = new System.Collections.Concurrent.BlockingCollection<unit -> unit> ()

    let private thread =
        let thread = System.Threading.Thread ((fun () -> for work in queue.GetConsumingEnumerable () do work ()), IsBackground = true)
        thread.Start ()
        thread

    let run (body: unit -> unit) =
        thread |> ignore
        use finished = new System.Threading.ManualResetEventSlim false
        let failure: exn ref = ref null

        queue.Add (fun () ->
            try
                try
                    body ()
                with caught ->
                    failure.Value <- caught
            finally
                finished.Set ())

        finished.Wait ()

        if not (isNull failure.Value) then
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure.Value).Throw ()

/// The desktop's mouse as SDL reports it: events put on SDL's queue for the test's window. The window's own loop
/// reads them on its next turn and sends them on to the engine, as it does with the events of a shown window.
/// A point is in pixels of the window.
module Desktop =
    open SDL
    open Microsoft.FSharp.NativeInterop

    /// The SDL id of the one window the process has open.
    let private windowId () : SDL_WindowID =
        let mutable count = 0
        let windows = SDL3.SDL_GetWindows &&count

        try
            if count <> 1 then
                failwithf "%d SDL windows are open; a view test has one" count

            SDL3.SDL_GetWindowID (NativePtr.read windows)
        finally
            SDL3.SDL_free (NativePtr.toNativeInt windows)

    let private push (event: SDL_Event) =
        let mutable pushed = event
        SDL3.SDL_PushEvent &&pushed |> ignore

    /// The pointer at a point, with the left button held or not.
    let move (x: float32) (y: float32) (held: bool) =
        let mutable event = SDL_Event ()
        event.motion.``type`` <- SDL_EventType.SDL_EVENT_MOUSE_MOTION
        event.motion.windowID <- windowId ()
        event.motion.state <- (if held then SDL_MouseButtonFlags.SDL_BUTTON_LMASK else Unchecked.defaultof<SDL_MouseButtonFlags>)
        event.motion.x <- x
        event.motion.y <- y
        push event

    /// The left button pressed or released at a point.
    let button (x: float32) (y: float32) (down: bool) =
        let mutable event = SDL_Event ()
        event.button.``type`` <- (if down then SDL_EventType.SDL_EVENT_MOUSE_BUTTON_DOWN else SDL_EventType.SDL_EVENT_MOUSE_BUTTON_UP)
        event.button.windowID <- windowId ()
        event.button.button <- 1uy
        event.button.down <- SDLBool.op_Implicit down
        event.button.clicks <- 1uy
        event.button.x <- x
        event.button.y <- y
        push event

    /// The wheel turned `notches` towards the user with the pointer at a point. SDL counts a turn away as positive.
    let wheel (x: float32) (y: float32) (notches: float32) =
        let mutable event = SDL_Event ()
        event.wheel.``type`` <- SDL_EventType.SDL_EVENT_MOUSE_WHEEL
        event.wheel.windowID <- windowId ()
        event.wheel.y <- -notches
        event.wheel.mouse_x <- x
        event.wheel.mouse_y <- y
        push event

/// A frame scheduler whose frames run when a test asks. Its timers never fire.
[<Sealed>]
type ManualScheduler() =
    let frames = ResizeArray<float -> unit> ()
    let mutable now = 0.0

    /// Frame callbacks waiting for the next frame.
    member _.Pending = frames.Count

    /// Runs the waiting frame callbacks as one frame.
    member _.RunFrames() =
        now <- now + 16.0
        let due = frames.ToArray ()
        frames.Clear ()

        for callback in due do
            callback now

    interface FrameScheduler with
        member _.Now() = now

        member _.Frame(callback: float -> unit) : IDisposable =
            frames.Add callback

            { new IDisposable with
                member _.Dispose() = frames.Remove callback |> ignore
            }

        member _.Timeout(_: float, _: unit -> unit) : IDisposable =
            { new IDisposable with
                member _.Dispose() = ()
            }

        member _.Microtask(callback: unit -> unit) : IDisposable =
            callback ()

            { new IDisposable with
                member _.Dispose() = ()
            }

        member _.Dispose() = ()

/// One definition for every test: the component is defined once and mounted many times.
let definition = EditorView.create EditorView.defaults

/// The CSS of the definition.
let private css =
    let text = StringBuilder ()
    text.Append("html,body{margin:0;padding:0;overflow:hidden}\n") |> ignore
    text.Append(Stylesheet.render definition.Rules).Append '\n' |> ignore

    for style in definition.Styles do
        for rule in style.Rules do
            text.Append((Stylesheet.render [ rule ]).TrimEnd ()).Append '\n' |> ignore

    text.ToString ()

type Mounted =
    {
        Window: Ranvier.View.WebCore.Sdl.Window
        Graph: Graph
        Dom: Ranvier.Dom.Document
        Editor: Editor
        View: EditorView
        /// One frame of the view, then one turn of the window.
        Step: unit -> unit
    }

    /// Runs `body` on the graph and flushes its effects.
    member this.Run(body: unit -> 'T) : 'T =
        this.Graph.Run (fun () ->
            let result = body ()
            Api.flush ()
            result)

    member this.Query(selector: string) : Element[] =
        let nodes = this.Dom.querySelectorAll selector

        if nodes.length = 0u then
            Array.empty
        else
            [|
                for index in 0u .. nodes.length - 1u do
                    match nodes.item index with
                    | :? Element as element -> element
                    | _ -> ()
            |]

    member this.Rows: Element[] = this.Query "[data-pm=row]"

/// Mounts a view of `lines` with `extensions` in a hidden window whose view is `height` CSS pixels high, and runs
/// `body` on it.
let withViewOn (schedulerOf: WebCoreHost -> FrameScheduler) (lines: seq<string>) (extensions: Extension list) (height: int) (body: Mounted -> unit) =
    NativeThread.run (fun () ->
        use window = Ranvier.View.WebCore.Sdl.Window.Create ("Ranvier.Plume view test", 1280, 800, hidden = true)
        let graph = window.Graph
        let host = window.Host
        let dom = host.Document

        use sheets = StyleSheets.forHost host
        let sheet = sheets.Create StyleSheetOptions.Default
        sheet.replaceSync css
        sheets.SetAdopted (dom, Array.append (sheets.GetAdopted dom) [| sheet |])

        let container = dom.createElement "div"
        container.setAttribute ("style", String.Concat ("position:absolute;left:0;top:0;width:1280px;height:", string height, "px;display:flex;flex-direction:column"))
        dom.body.appendChild container |> ignore

        let editor = graph.Run (fun () -> Editor.create (Ranvier.Plume.Document.create (Text.ofLines lines)) extensions)
        let props = { Editor = editor; Scheduler = schedulerOf host }

        let mounted =
            Mounting.mount graph None (fun errors -> raise (AggregateException errors)) container None (fun ctx ->
                Program.run (Component.render definition.Component props) ctx.Backend)

        try
            let view = (EditorView.current editor).Value

            let step () =
                graph.Run (fun () ->
                    view.RunFrame ()
                    Api.flush ())

                window.Step (timeout = 0) |> ignore

            // The first frames lay the page out, size the row pool and measure the font.
            for _ in 1..6 do
                step ()

            body
                {
                    Window = window
                    Graph = graph
                    Dom = dom
                    Editor = editor
                    View = view
                    Step = step
                }
        finally
            graph.Run (fun () -> mounted.Dispose ())
            container.remove ())

/// `withViewOn` with the frames and timers of the window's host.
let withView (lines: seq<string>) (extensions: Extension list) (height: int) (body: Mounted -> unit) =
    withViewOn Frames.forHost lines extensions height body
