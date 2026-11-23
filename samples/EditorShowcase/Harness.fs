/// The command line, and the scripted modes: a picture of the window, the frame benchmark, and the measurements of
/// history memory, loading and change application. A scripted mode creates its window hidden and never shows it.
module EditorShowcase.Harness

// The display's mode, SDL's events and the screen's pixels are read and written through native pointers.
#nowarn "9" "51"

open System
open System.Collections.Generic
open System.Diagnostics
open System.Globalization
open System.IO
open System.Text
open Ranvier
open Ranvier.Dom
open Ranvier.View
open Ranvier.View.Dom
open Ranvier.View.Dsl
open Ranvier.View.WebCore.Sdl
open Ranvier.Plume
open Ranvier.Plume.View
open EditorShowcase.Sample
open EditorShowcase.Model
open EditorShowcase.App

type Mode =
    | Interactive
    | Shot of path: string
    | Bench
    | Memory
    | Load
    | Apply
    | Profile of nativeTurn: bool
    | Writes
    | Tour of picture: string

type Options =
    { Mode: Mode
      Width: int
      Height: int
      /// Timed frames of each benchmark configuration.
      Frames: int
      /// Lines scrolled per benchmark frame.
      Lines: int
      /// The file the window opens with, as an index of the explorer.
      File: int
      /// False to open the window with the scroll tour stopped.
      Tour: bool
      /// `--tour` with its window shown on the desktop.
      Shown: bool
      /// `--tour --shown` that also moves the desktop's real pointer inside the window.
      Pointer: bool }

let usage =
    """Editor showcase on Ranvier.Plume

  dotnet run -c Release                 open the window
  dotnet run -- dev                     open it under the hot reload host (Debug build)
  dotnet run -c Release -- --shot <png> save a picture of the window, which stays hidden
  dotnet run -c Release -- --bench      time scrolling and typing under each renderer setting, in a hidden window
  dotnet run -c Release -- --memory     measure the memory 10,000 history items hold on the 1,000,000-line file
  dotnet run -c Release -- --load       time building the text of the 1,000,000-line file from UTF-8 bytes
  dotnet run -c Release -- --apply      time one change set of 10,000 ranges against 10,000 single replacements
  dotnet run -c Release -- --profile    time typing and scrolling with each feature of the text view switched off
  dotnet run -c Release -- --profile-turn  include host scheduling and native rendering phases in the profile
  dotnet run -c Release -- --writes     list the writes a keystroke and a scroll make to the page
  dotnet run -c Release -- --tour       run the app as its window runs it, tour on, with no window shown
  dotnet run -c Release -- --tour --shown <picture.png>
                                        the same with the window shown for the length of the run, and what the
                                        screen showed of it saved as a picture
  dotnet run -c Release -- --tour --shown <picture.png> --pointer
                                        and the desktop's real pointer moved inside the window: a press, a drag,
                                        the wheel and the scrollbar through Windows. It sends no keys, clicks only
                                        where the window is under the pointer, and puts the pointer back

Options
  --size <w>x<h>   window size in CSS pixels (default 1280x800)
  --frames <n>     timed frames per benchmark configuration (default 240)
  --lines <n>      lines scrolled per benchmark frame (default 3)
  --open <n>       the window opens file n of the explorer (0 to 2, default 0)
  --no-tour        the window opens with the scroll tour stopped"""

let parse (argv: string[]) : Result<Options, string> =
    let rec go (options: Options) (rest: string list) =
        match rest with
        | [] -> Ok options
        | "--shot" :: path :: tail -> go { options with Mode = Shot path } tail
        | "--bench" :: tail -> go { options with Mode = Bench } tail
        | "--memory" :: tail -> go { options with Mode = Memory } tail
        | "--load" :: tail -> go { options with Mode = Load } tail
        | "--apply" :: tail -> go { options with Mode = Apply } tail
        | "--profile" :: tail -> go { options with Mode = Profile false } tail
        | "--profile-turn" :: tail -> go { options with Mode = Profile true } tail
        | "--writes" :: tail -> go { options with Mode = Writes } tail
        | "--tour" :: "--shown" :: path :: "--pointer" :: tail -> go { options with Mode = Tour path; Shown = true; Pointer = true } tail
        | "--tour" :: "--shown" :: path :: tail -> go { options with Mode = Tour path; Shown = true } tail
        | "--tour" :: tail -> go { options with Mode = Tour "" } tail
        | "--size" :: size :: tail ->
            match size.Split 'x' with
            | [| w; h |] ->
                match Int32.TryParse w, Int32.TryParse h with
                | (true, width), (true, height) when width >= 640 && height >= 400 -> go { options with Width = width; Height = height } tail
                | _ -> Error $"--size takes <width>x<height> of at least 640x400, not '{size}'"
            | _ -> Error $"--size takes <width>x<height>, not '{size}'"
        | "--frames" :: count :: tail ->
            match Int32.TryParse count with
            | true, frames when frames >= 10 && frames <= 100000 -> go { options with Frames = frames } tail
            | _ -> Error $"--frames takes a number from 10 to 100000, not '{count}'"
        | "--lines" :: count :: tail ->
            match Int32.TryParse count with
            | true, lines when lines >= 1 && lines <= 100000 -> go { options with Lines = lines } tail
            | _ -> Error $"--lines takes a number from 1 to 100000, not '{count}'"
        | "--no-tour" :: tail -> go { options with Tour = false } tail
        | "--open" :: index :: tail ->
            match Int32.TryParse index with
            | true, file when file >= 0 && file <= 2 -> go { options with File = file } tail
            | _ -> Error $"--open takes 0, 1 or 2, not '{index}'"
        | other :: _ -> Error $"unknown argument '{other}'"

    go { Mode = Interactive; Width = WindowW; Height = WindowH; Frames = 240; Lines = 3; File = 0; Tour = true; Shown = false; Pointer = false } (List.ofArray argv)

/// A scripted run that found something wrong. The hot reload host evaluates this file's functions in F#
/// Interactive, where a private exception is out of reach and `Failed` is a case of Ranvier's `Reading`.
exception RunFailed of string

let private say (text: string) = Console.WriteLine text
let private milliseconds (ticks: int64) = float ticks * 1000.0 / float Stopwatch.Frequency
let private fixed3 (value: float) = value.ToString("F3", CultureInfo.InvariantCulture)

let private percentile (sorted: float[]) (fraction: float) =
    sorted[min (sorted.Length - 1) (int (fraction * float sorted.Length))]

/// Stops the run when an element carries a Ranvier.Css class that has no rule in the sheet the engine holds:
/// the class then came from a `styles { ... }` whose rules nothing handed to the sheet.
let private requireRules (window: Window) (session: Session) =
    let stylesheet = session.Stylesheet :?> CSSStyleSheet
    let sheet = LiveSheet.held stylesheet
    let nodes = window.Host.Document.querySelectorAll "[class]"
    let seen = HashSet<string>()
    let missing = ResizeArray<string>()

    for i in 0u .. nodes.length - 1u do
        match nodes.item i with
        | :? Element as element ->
            for name in (element.getAttribute "class").Split(' ', StringSplitOptions.RemoveEmptyEntries) do
                if name.StartsWith "r-" && seen.Add name && not (sheet.Contains("." + name)) then
                    missing.Add $"{name} on <{element.tagName.ToLowerInvariant()}>"
        | _ -> ()

    if missing.Count > 0 then
        let names = String.Join(", ", missing)
        raise (RunFailed $"{missing.Count} of the {seen.Count} Ranvier.Css classes on the page have no rule in the adopted stylesheet: {names}.")

    say $"  styles     {seen.Count} Ranvier.Css classes on the page, each with a rule in the constructed sheet the document adopts ({stylesheet.cssRules.length} rules read back from the engine; {editorView.Styles.Length} of the classes were handed over by Ranvier.Plume.View)"

/// Runs `body` on the app mounted in a hidden window. The run advances the frames.
let private hosted (options: Options) (body: Window -> State -> EditorView -> (unit -> unit) -> unit) : int =
    try
        use window = Window.Create(Title, options.Width, options.Height, hidden = true)
        let session = Session()
        session.Auto <- false
        session.Tour <- false
        session.Active <- options.File
        let props = { Window = window; Session = session }
        let graph = window.Graph
        let doc = window.Host.Document
        let container = doc.createElement "div"
        doc.body.appendChild container |> ignore

        let mounted =
            Mounting.mount graph None (fun errs -> raise (AggregateException errs)) container None (fun ctx ->
                Program.run (Component.render app props) ctx.Backend)

        try
            let state = session.State :?> State

            /// One frame of the app, then one turn of the window's loop.
            let step () =
                state.Frame(1.0 / 60.0)

                if not (window.Step(timeout = 0)) then
                    raise (RunFailed "the window closed during the run")

            // The first frames size the row pool and measure the font.
            for _ in 1..12 do
                step ()

            let view =
                match currentView state with
                | ValueSome view -> view
                | ValueNone -> raise (RunFailed "the text view did not mount")

            let struct (pixelW, pixelH) = window.PixelSize
            let height = graph.Run(fun () -> view.Height.Value)
            let charWidth = graph.Run(fun () -> view.CharWidth.Value)
            say $"  window     {options.Width} x {options.Height} CSS px asked for, {pixelW} x {pixelH} device px (Window.PixelSize), created hidden"
            say $"  compositor GPU compositing {window.IsGpuComposited} (Window.IsGpuComposited)"
            say $"  text view  {fixed3 height} CSS px high (clientHeight), {int (Math.Ceiling(height / view.Theme.LineHeight)) + 13} row elements, character advance {fixed3 charWidth} CSS px (64 characters measured with getBoundingClientRect)"
            requireRules window session
            body window state view step
            0
        finally
            graph.Run(fun () -> mounted.Dispose())
            container.remove ()
    with
    | RunFailed reason ->
        Console.Error.WriteLine $"FAIL {reason}"
        1

let private shot (options: Options) (path: string) =
    say "Editor showcase on Ranvier.Plume --shot"

    hosted options (fun window state view step ->
        let editor = state.Files[state.Active.Peek].Editor

        window.Graph.Run(fun () ->
            view.ScrollTo(21.0 * view.Theme.LineHeight)
            let text = editor.Text.Value
            let line = Text.line (min 35 (text.Lines - 1)) text
            editor.Dispatch [ Tx.selection (EditorSelection.single (min line.To (line.From + 8)) (min line.To (line.From + 14))) ])

        for _ in 1..4 do
            step ()

        window.Present()
        let struct (width, height, pixels) = window.ReadPixels()
        Png.PngEncoder.saveRgba8 path width height pixels
        say $"saved {Path.GetFullPath path} ({width} x {height} device px, the composited window read back with Window.ReadPixels)")

let private bench (options: Options) =
    say "Editor showcase on Ranvier.Plume --bench"

    hosted options (fun window state view step ->
        let graph = window.Graph
        let editor = state.Files[state.Active.Peek].Editor
        let lineHeight = view.Theme.LineHeight

        let scrollLines () =
            graph.Run(fun () -> view.ScrollBy(float options.Lines * lineHeight))

        let scrollPixels () = graph.Run(fun () -> view.ScrollBy 7.0)

        let typeCharacter () =
            graph.Run(fun () -> Commands.insertText "x" editor |> ignore)

        let runs =
            [ $"lines x{options.Lines}", scrollLines, true, true, true
              $"lines x{options.Lines}", scrollLines, false, true, true
              $"lines x{options.Lines}", scrollLines, true, false, true
              $"lines x{options.Lines}", scrollLines, false, false, true
              $"lines x{options.Lines}", scrollLines, true, true, false
              "7 px", scrollPixels, true, true, true
              "7 px", scrollPixels, true, true, false
              "typing", typeCharacter, true, true, true
              "typing", typeCharacter, true, false, true ]

        say $"  workloads  'lines x{options.Lines}' scrolls {options.Lines} line(s) a frame, '7 px' scrolls 7 CSS px a frame, 'typing' inserts one character a frame at the cursor;"
        say $"             all on the 1,000,000-line file, {options.Frames} timed frames after 30 untimed ones"
        say "  dispatch   Stopwatch around the workload's call: EditorView.ScrollBy (a signal write), or Commands.insertText (the transaction, the log, the fold, the row and its DOM write)"
        say "  app ms     Stopwatch around State.Frame: EditorView.RunFrame, then Api.flush: the scroll commit, the row lookup, the row effects, their DOM writes and the scrollTop write"
        say "  turn ms    Stopwatch around Window.Step(timeout = 0) after it: events, layout, paint and present of a hidden window, which no display paces"
        say "  rows       rows the lookup computed in the frame (EditorView.RowsWritten), mean over the timed frames"
        say ""
        say "  workload   recycling  cache  layer   rows/frame   dispatch ms mean  median     p95    app ms mean  median     p95   turn ms mean  median     p95   total mean"

        for name, work, recycle, cache, layer in runs do
            graph.Run(fun () ->
                if state.Recycle.Peek <> recycle then setRecycle state recycle
                if state.Cached.Peek <> cache then setCached state cache
                if state.Layer.Peek <> layer then setLayer state layer
                view.ResetMarkup()
                view.ScrollTo(1000.0 * lineHeight)
                let start = (Text.line 1010 editor.Text.Value).From
                editor.Dispatch [ Tx.selection (EditorSelection.single start start) ])

            let dispatch = Array.zeroCreate<float> options.Frames
            let app = Array.zeroCreate<float> options.Frames
            let turn = Array.zeroCreate<float> options.Frames
            let mutable rows = 0L

            for frame in -30 .. options.Frames - 1 do
                let written = view.RowsWritten()
                let t0 = Stopwatch.GetTimestamp()
                work ()
                let t1 = Stopwatch.GetTimestamp()
                state.Frame(1.0 / 60.0)
                let t2 = Stopwatch.GetTimestamp()

                if not (window.Step(timeout = 0)) then
                    raise (RunFailed "the window closed during the run")

                let t3 = Stopwatch.GetTimestamp()

                if frame >= 0 then
                    dispatch[frame] <- milliseconds (t1 - t0)
                    app[frame] <- milliseconds (t2 - t1)
                    turn[frame] <- milliseconds (t3 - t2)
                    rows <- rows + (view.RowsWritten() - written)

            let line (samples: float[]) =
                let sorted = Array.sort samples
                $"{fixed3 (Array.average samples), 12}  {fixed3 (percentile sorted 0.5), 6}  {fixed3 (percentile sorted 0.95), 6}"

            let onOff (value: bool) = if value then "on " else "off"
            let perFrame = (float rows / float options.Frames).ToString("F1", CultureInfo.InvariantCulture)
            let total = Array.average dispatch + Array.average app + Array.average turn
            say $"  {name, -9}  {onOff recycle}        {onOff cache}    {onOff layer}  {perFrame, 10}  {line dispatch}  {line app}  {line turn}  {fixed3 total, 10}"

        say ""
        say "One run on one machine. Compare two runs only when both came from this benchmark at the same window size.")

/// What each feature of the text view costs: typing and scrolling with one or more features switched off, in
/// rounds that take every configuration in turn, in one window.
let private profile (options: Options) nativeTurn =
    say (if nativeTurn then "Editor showcase on Ranvier.Plume --profile-turn" else "Editor showcase on Ranvier.Plume --profile")

    hosted options (fun window state view step ->
        let graph = window.Graph
        let editor = state.Files[state.Active.Peek].Editor
        let lineHeight = view.Theme.LineHeight

        let switch (name: string) (on: bool) =
            for feature, slot, extension in features do
                if feature = name then
                    graph.Run(fun () -> editor.Dispatch [ Tx.effect (slot.Reconfigure(if on then extension else Extension.many [])) ])

        let configurations =
            [ "every feature", []
              "no cursor", [ "cursor" ]
              "no active line", [ "active" ]
              "no selection", [ "selection" ]
              "no layer", [ "cursor"; "active"; "selection" ]
              "no line numbers", [ "numbers" ]
              "no language", [ "language" ]
              "text alone", [ "cursor"; "active"; "selection"; "numbers"; "language" ] ]

        // Exercise the focused native text-entry path in the same hidden window on either build.
        let nativeInput = Environment.GetEnvironmentVariable "PLUME_PROFILE_INPUT" = "1"
        if nativeInput then
            window.Host.SetFocused true
            graph.Run view.Focus

        let typeOne () =
            if nativeInput then window.Host.Type("x", "KeyX", "x")
            else graph.Run(fun () -> Commands.insertText "x" editor |> ignore)

        let workloads =
            [ "typing", typeOne
              $"lines x{options.Lines}", (fun () -> graph.Run(fun () -> view.ScrollBy(float options.Lines * lineHeight))) ]

        let rounds = 5
        let totals = Dictionary<string, ResizeArray<struct (float * float * float)>>()
        let phases = Dictionary<struct (string * string), struct (int * float)>()
        let record workload phase nanoseconds =
            let key = struct (workload, phase)
            let struct (events, duration) = phases.GetValueOrDefault key
            phases[key] <- struct (events + 1, duration + nanoseconds)

        if nativeTurn then
            window.Host.SetSchedulingTrace true
            window.Host.SetRenderingTrace true

        let drain keep workload =
            let struct (owner, lostOwner) = window.Host.DrainSchedulingTrace()
            let rendering, lostRendering = window.Host.DrainRenderingTrace()
            if lostOwner <> 0L || lostRendering <> 0UL then raise (RunFailed "native turn trace overflow")
            if keep then
                for event in owner do
                    record workload (string event.Phase) (float event.Duration * 1e9 / float Stopwatch.Frequency)
                for event in rendering do
                    let phase =
                        match event.phase with
                        | 1 -> "UpdateRequested"
                        | 2 -> "StyleLayout"
                        | 3 -> "Paint"
                        | 4 -> "RenderingUpdate"
                        | 5 -> "GpuSubmit"
                        | 6 -> "BufferCreate"
                        | 7 -> "CaptureRaster"
                        | 8 -> "ImageEncode"
                        | 9 -> "PixelCopy"
                        | other -> $"Native({other})"
                    record workload phase (float event.duration_ns)
        // PLUME_PROFILE names the one workload and configuration to run, as "typing|every feature": a CPU trace
        // of the run then holds that workload alone.
        let only = Environment.GetEnvironmentVariable "PLUME_PROFILE"
        let chosen (key: string) = String.IsNullOrEmpty only || key = only
        // Each typing run has 40 lines of the file to itself, so it types on a line no run has typed on, and a
        // scroll run starts after them all.
        let mutable typed = 0

        for round in 0 .. rounds - 1 do
            for workload, work in workloads do
                let run = configurations |> List.filter (fun (configuration, _) -> chosen (workload + "|" + configuration))

                // Each round starts with another configuration.
                for offset in 0 .. run.Length - 1 do
                    let configuration, off = run[(offset + round) % run.Length]
                    let key = workload + "|" + configuration

                    for feature in off do
                        switch feature false

                    graph.Run(fun () ->
                        let first = 1000 + 40 * typed
                        view.ScrollTo(float first * lineHeight)
                        let start = (Text.line (first + 4) editor.Text.Value).From
                        editor.Dispatch [ Tx.selection (EditorSelection.single start start) ])

                    let initialLength = editor.Text.Peek.Length
                    if workload = "typing" then
                        typed <- typed + 1

                    let mutable dispatch = 0L
                    let mutable app = 0L
                    let mutable turn = 0L

                    for frame in -30 .. options.Frames - 1 do
                        let t0 = Stopwatch.GetTimestamp()
                        work ()
                        let t1 = Stopwatch.GetTimestamp()
                        state.Frame(1.0 / 60.0)
                        let t2 = Stopwatch.GetTimestamp()
                        if nativeTurn then drain false ""
                        let turnStart = if nativeTurn then Stopwatch.GetTimestamp() else t2

                        if not (window.Step(timeout = 0)) then
                            raise (RunFailed "the window closed during the run")

                        let t3 = Stopwatch.GetTimestamp()

                        if frame >= 0 then
                            dispatch <- dispatch + (t1 - t0)
                            app <- app + (t2 - t1)
                            turn <- turn + (t3 - turnStart)

                        if nativeTurn then drain (frame >= 0) key

                    if nativeInput && workload = "typing" && editor.Text.Peek.Length <> initialLength + options.Frames + 30 then
                        raise (RunFailed "native typing did not insert one character per turn")

                    if not (totals.ContainsKey key) then
                        totals[key] <- ResizeArray()

                    let frames = float options.Frames
                    totals[key].Add(struct (milliseconds dispatch / frames, milliseconds app / frames, milliseconds turn / frames))

                    for feature in off do
                        switch feature true

        if nativeTurn then
            say "  Events completed during timed Window.Step calls. Durations are inclusive ms per timed turn. Phases can overlap."
            for entry in phases |> Seq.sortBy (fun entry -> entry.Key) do
                let struct (workload, phase) = entry.Key
                let struct (events, ns) = entry.Value
                say $"  {workload} {phase}: {events} events, {fixed3 (ns / (1e6 * float rounds * float options.Frames))} ms/turn"

        say $"  {rounds} rounds of {options.Frames} timed frames after 30 untimed ones; a row is the mean of its rounds, and the lowest and highest total of a round"
        say ""
        say "  workload   features           dispatch ms   app ms   turn ms   total ms   total of a round"

        for workload, _ in workloads do
            for configuration, _ in configurations |> List.filter (fun (configuration, _) -> chosen (workload + "|" + configuration)) do
                let runs = totals[workload + "|" + configuration]
                let mean (pick: struct (float * float * float) -> float) = runs |> Seq.averageBy pick
                let total (struct (d, a, t)) = d + a + t
                let lowest = runs |> Seq.map total |> Seq.min
                let highest = runs |> Seq.map total |> Seq.max
                say $"  {workload, -9}  {configuration, -16}  {fixed3 (mean (fun (struct (d, _, _)) -> d)), 10}  {fixed3 (mean (fun (struct (_, a, _)) -> a)), 7}  {fixed3 (mean (fun (struct (_, _, t)) -> t)), 8}  {fixed3 (mean total), 9}   {fixed3 lowest} to {fixed3 highest}")

/// The writes a keystroke and a scroll make to the page, read from a mutation observer on the document's body.
let private writes (options: Options) =
    say "Editor showcase on Ranvier.Plume --writes"

    hosted options (fun window state view step ->
        let graph = window.Graph
        let doc = window.Host.Document
        let editor = state.Files[state.Active.Peek].Editor
        let lineHeight = view.Theme.LineHeight

        graph.Run(fun () ->
            view.ScrollTo(1000.0 * lineHeight)
            let start = (Text.line 1010 editor.Text.Value).From
            editor.Dispatch [ Tx.selection (EditorSelection.single start start) ])

        for _ in 1..6 do
            step ()

        use service = Ranvier.View.WebCore.MutationObservation.forHost window.Host
        let seen = ResizeArray<MutationRecord>()
        use observer = service.Create(fun records _ -> seen.AddRange records)

        observer.Observe(
            doc.body,
            { ChildList = true
              Subtree = true
              Attributes = Some true
              CharacterData = Some true
              AttributeOldValue = None
              CharacterDataOldValue = None
              AttributeFilter = None }
        )

        let describe (node: Node) : string =
            match node with
            | :? Element as element ->
                let mark = element.getAttribute "data-pm"
                let name = element.tagName.ToLowerInvariant()
                if isNull mark then $"<{name}> with {element.childElementCount} child elements" else $"<{name} data-pm=\"{mark}\">"
            | other -> $"text node under <{other.parentElement.tagName.ToLowerInvariant()}>"

        let report (title: string) (count: int) (work: unit -> unit) =
            observer.TakeRecords() |> ignore
            seen.Clear()

            for _ in 1..count do
                work ()
                step ()

            seen.AddRange(observer.TakeRecords())
            let records = seen.ToArray()
            say $"  {title}: {records.Length} mutation records over {count} frames"

            records
            |> Array.countBy (fun record ->
                let kind =
                    match record.Type with
                    | MutationType.ChildList -> "children"
                    | MutationType.Attributes -> "attribute " + (defaultArg record.AttributeName "?")
                    | MutationType.CharacterData -> "text"

                kind + " of " + describe record.Target)
            |> Array.sortByDescending snd
            |> Array.iter (fun (what, times) -> say $"    {float times / float count, 6:F2} a frame   {what}")

        report "typing" 20 (fun () -> graph.Run(fun () -> Commands.insertText "x" editor |> ignore))
        report $"lines x{options.Lines}" 20 (fun () -> graph.Run(fun () -> view.ScrollBy(float options.Lines * lineHeight))))

/// The app's SDL window and the desktop around it: the mouse as SDL reports it, and what the screen shows.
/// Not private: the hot reload host evaluates this file's functions in F# Interactive, where a private module is
/// out of reach.
module Desktop =
    open System.Runtime.InteropServices
    open Microsoft.FSharp.NativeInterop
    open SDL

    /// The one SDL window the process has open.
    let private window () : nativeptr<SDL_Window> =
        let mutable count = 0
        let windows = SDL3.SDL_GetWindows &&count

        try
            if count <> 1 then
                failwithf "%d SDL windows are open; a scripted run has one" count

            NativePtr.read windows
        finally
            SDL3.SDL_free(NativePtr.toNativeInt windows)

    /// True when the desktop shows the window: it is neither hidden nor minimised.
    let isShown () : bool =
        let flags = SDL3.SDL_GetWindowFlags(window ())
        not (flags.HasFlag SDL_WindowFlags.SDL_WINDOW_HIDDEN) && not (flags.HasFlag SDL_WindowFlags.SDL_WINDOW_MINIMIZED)

    /// Where the window's content is on the screen, and its size, in pixels.
    let bounds () : struct (int * int * int * int) =
        let mutable x = 0
        let mutable y = 0
        let mutable width = 0
        let mutable height = 0
        SDL3.SDL_GetWindowPosition(window (), &&x, &&y) |> ignore
        SDL3.SDL_GetWindowSizeInPixels(window (), &&width, &&height) |> ignore
        struct (x, y, width, height)

    let private push (event: SDL_Event) =
        let mutable pushed = event
        SDL3.SDL_PushEvent &&pushed |> ignore

    /// The pointer at a point of the window, in pixels, with the left button held or not: an event on SDL's queue,
    /// where the desktop's mouse puts its own.
    let move (x: float32) (y: float32) (held: bool) =
        let mutable event = SDL_Event()
        event.motion.``type`` <- SDL_EventType.SDL_EVENT_MOUSE_MOTION
        event.motion.windowID <- SDL3.SDL_GetWindowID(window ())
        event.motion.state <- (if held then SDL_MouseButtonFlags.SDL_BUTTON_LMASK else Unchecked.defaultof<SDL_MouseButtonFlags>)
        event.motion.x <- x
        event.motion.y <- y
        push event

    /// The left button pressed or released at a point of the window.
    let button (x: float32) (y: float32) (down: bool) =
        let mutable event = SDL_Event()
        event.button.``type`` <- (if down then SDL_EventType.SDL_EVENT_MOUSE_BUTTON_DOWN else SDL_EventType.SDL_EVENT_MOUSE_BUTTON_UP)
        event.button.windowID <- SDL3.SDL_GetWindowID(window ())
        event.button.button <- 1uy
        event.button.down <- SDLBool.op_Implicit down
        event.button.clicks <- 1uy
        event.button.x <- x
        event.button.y <- y
        push event

    /// The wheel turned `notches` towards the user with the pointer at a point. SDL counts a turn away as positive.
    let wheel (x: float32) (y: float32) (notches: float32) =
        let mutable event = SDL_Event()
        event.wheel.``type`` <- SDL_EventType.SDL_EVENT_MOUSE_WHEEL
        event.wheel.windowID <- SDL3.SDL_GetWindowID(window ())
        event.wheel.y <- -notches
        event.wheel.mouse_x <- x
        event.wheel.mouse_y <- y
        push event

    [<Struct; StructLayout(LayoutKind.Sequential)>]
    type ScreenPoint =
        val mutable X: int
        val mutable Y: int

    [<DllImport("user32.dll")>]
    extern bool private GetCursorPos(ScreenPoint& point)

    [<DllImport("user32.dll")>]
    extern bool private SetCursorPos(int x, int y)

    [<DllImport("user32.dll")>]
    extern nativeint private WindowFromPoint(ScreenPoint point)

    [<DllImport("user32.dll")>]
    extern nativeint private GetAncestor(nativeint window, uint32 flags)

    [<DllImport("user32.dll")>]
    extern void private mouse_event(uint32 flags, uint32 dx, uint32 dy, uint32 data, unativeint extra)

    /// The window's handle on Windows.
    let private handle () : nativeint =
        SDL3.SDL_GetPointerProperty(SDL3.SDL_GetWindowProperties(window ()), "SDL.window.win32.hwnd", 0n)

    /// Where the desktop's pointer is, in pixels of the screen.
    let pointer () : struct (int * int) =
        let mutable point = ScreenPoint()
        GetCursorPos &point |> ignore
        struct (point.X, point.Y)

    /// Puts the desktop's pointer at a point of the screen. Windows reports the move to the window under it.
    let pointTo (x: int) (y: int) = SetCursorPos(x, y) |> ignore

    /// True when the window under a point of the screen is the app's.
    let isUnder (x: int) (y: int) : bool =
        let mutable point = ScreenPoint()
        point.X <- x
        point.Y <- y
        let found = WindowFromPoint point
        // GA_ROOT: the top-level window of whatever is under the point.
        found <> 0n && (found = handle () || GetAncestor(found, 2u) = handle ())

    /// The left button of the desktop's mouse, pressed where the pointer is. MOUSEEVENTF_LEFTDOWN.
    let press () = mouse_event (0x0002u, 0u, 0u, 0u, 0un)

    /// The left button released. MOUSEEVENTF_LEFTUP.
    let release () = mouse_event (0x0004u, 0u, 0u, 0u, 0un)

    /// The desktop's wheel turned `notches` towards the user. MOUSEEVENTF_WHEEL, 120 a notch, negative towards the user.
    let turnWheel (notches: int) = mouse_event (0x0800u, 0u, 0u, uint32 (-120 * notches), 0un)

    [<Struct; StructLayout(LayoutKind.Sequential)>]
    type private BitmapHeader =
        val mutable Size: uint32
        val mutable Width: int
        val mutable Height: int
        val mutable Planes: uint16
        val mutable BitCount: uint16
        val mutable Compression: uint32
        val mutable SizeImage: uint32
        val mutable XPelsPerMeter: int
        val mutable YPelsPerMeter: int
        val mutable ColorsUsed: uint32
        val mutable ColorsImportant: uint32

    [<DllImport("user32.dll")>]
    extern nativeint private GetDC(nativeint window)

    [<DllImport("user32.dll")>]
    extern int private ReleaseDC(nativeint window, nativeint context)

    [<DllImport("gdi32.dll")>]
    extern nativeint private CreateCompatibleDC(nativeint context)

    [<DllImport("gdi32.dll")>]
    extern nativeint private CreateCompatibleBitmap(nativeint context, int width, int height)

    [<DllImport("gdi32.dll")>]
    extern nativeint private SelectObject(nativeint context, nativeint item)

    [<DllImport("gdi32.dll")>]
    extern bool private BitBlt(nativeint target, int x, int y, int width, int height, nativeint source, int sourceX, int sourceY, uint32 operation)

    [<DllImport("gdi32.dll")>]
    extern int private GetDIBits(nativeint context, nativeint bitmap, uint32 start, uint32 lines, byte[] bits, BitmapHeader& header, uint32 usage)

    [<DllImport("gdi32.dll")>]
    extern bool private DeleteObject(nativeint item)

    [<DllImport("gdi32.dll")>]
    extern bool private DeleteDC(nativeint context)

    /// The pixels the screen shows in a rectangle, as RGBA rows from the top. Windows only.
    let screen (x: int) (y: int) (width: int) (height: int) : byte[] =
        let desktop = GetDC 0n
        let memory = CreateCompatibleDC desktop
        let bitmap = CreateCompatibleBitmap(desktop, width, height)
        let before = SelectObject(memory, bitmap)

        try
            // SRCCOPY
            BitBlt(memory, 0, 0, width, height, desktop, x, y, 0x00CC0020u) |> ignore
            SelectObject(memory, before) |> ignore
            let mutable header = BitmapHeader()
            header.Size <- 40u
            header.Width <- width
            // A negative height asks for rows from the top.
            header.Height <- -height
            header.Planes <- 1us
            header.BitCount <- 32us
            let pixels = Array.zeroCreate<byte> (width * height * 4)
            GetDIBits(memory, bitmap, 0u, uint32 height, pixels, &header, 0u) |> ignore

            // Blue, green, red to red, green, blue; the screen has no transparency.
            for offset in 0..4 .. pixels.Length - 4 do
                let blue = pixels[offset]
                pixels[offset] <- pixels[offset + 2]
                pixels[offset + 2] <- blue
                pixels[offset + 3] <- 255uy

            pixels
        finally
            DeleteObject bitmap |> ignore
            DeleteDC memory |> ignore
            ReleaseDC(0n, desktop) |> ignore

/// The refresh rate SDL reports for the primary display, in hertz; 0 when it reports none.
let private displayHertz () : float =
    let mode = SDL.SDL3.SDL_GetCurrentDisplayMode(SDL.SDL3.SDL_GetPrimaryDisplay())

    if Microsoft.FSharp.NativeInterop.NativePtr.isNullPtr mode then
        0.0
    else
        float (Microsoft.FSharp.NativeInterop.NativePtr.read mode).refresh_rate

/// The app as its window runs it, tour on. The window is hidden unless the run was asked to show it; a shown
/// window is closed when the run ends. The mouse, the wheel and the keys go in through SDL's queue and through
/// the host, as a window receives and sends them. Each line printed is something that held; the first that does
/// not ends the run.
let private tour (options: Options) (picture: string) =
    say (if options.Shown then "Editor showcase on Ranvier.Plume --tour --shown" else "Editor showcase on Ranvier.Plume --tour")

    try
        use window = Window.Create(Title, options.Width, options.Height, hidden = true)
        // A session as `dotnet run` starts it: the app drives its own frames, and the tour is on.
        let session = Session()
        session.Active <- options.File
        let props = { Window = window; Session = session }
        let graph = window.Graph
        let host = window.Host
        let doc = host.Document
        let container = doc.createElement "div"
        doc.body.appendChild container |> ignore

        let mounted =
            Mounting.mount graph None (fun errs -> raise (AggregateException errs)) container None (fun ctx ->
                Program.run (Component.render app props) ctx.Backend)

        try
            let state = session.State :?> State

            /// One refresh of a display: a frame when the page asked for one, then a turn of the window's loop.
            let refresh () : bool =
                let asked = host.FrameRequested

                if asked then
                    host.Frame()

                if not (window.Step(timeout = 0)) then
                    raise (RunFailed "the window closed during the run")

                asked

            /// `count` refreshes four milliseconds apart; the number that ran a frame.
            let refreshes (count: int) : int =
                let mutable ran = 0

                for _ in 1..count do
                    Threading.Thread.Sleep 4

                    if refresh () then
                        ran <- ran + 1

                ran

            let holds (what: string) (condition: bool) =
                if not condition then
                    raise (RunFailed $"did not hold: {what}")

                say $"  held     {what}"

            let button (label: string) : Element =
                let nodes = doc.querySelectorAll "button"

                let found =
                    seq {
                        for i in 0u .. nodes.length - 1u do
                            match nodes.item i with
                            | :? Element as element when element.textContent.StartsWith label -> yield element
                            | _ -> ()
                    }
                    |> Seq.tryHead

                match found with
                | Some element -> element
                | None -> raise (RunFailed $"no button reads '{label}'")

            let click (element: Element) =
                let rect = element.getBoundingClientRect ()
                host.Click(int (rect.left + rect.width / 2.0), int (rect.top + rect.height / 2.0)) |> ignore

            if options.Shown then
                window.Show()
                window.Present()

            // The first refreshes mount the text view and size its rows.
            refreshes 12 |> ignore

            if options.Shown then
                let struct (x, y, width, height) = Desktop.bounds ()
                holds $"the window is shown on the desktop: {width} x {height} pixels at ({x}, {y})" (Desktop.isShown ())

            let view () =
                match currentView state with
                | ValueSome view -> view
                | ValueNone -> raise (RunFailed "the text view did not mount")

            let top () = graph.Run(fun () -> (view ()).ScrollTop.Value)
            let text = (doc.querySelector "[data-plume]").getBoundingClientRect ()

            // The tour, on the frames the app asks for.
            let start = top ()
            let rows = (view ()).RowsWritten()
            let ran = refreshes 120
            holds $"with the tour on, each of 120 refreshes ran a frame the app asked for ({ran} did)" (ran = 120)
            holds $"and the text moved down, by {fixed3 (top () - start)} CSS px" (top () > start)
            holds $"rows were computed for the lines that came into view ({(view ()).RowsWritten() - rows})" ((view ()).RowsWritten() > rows)
            holds $"the statistics panel published: {state.Stats.Peek[0]} lines in the file" (state.Stats.Peek[0] <> "")

            // The window's own loop, as `dotnet run` runs it: `Window.Step()` alone decides when a frame runs.
            let own = top ()
            let clock = Stopwatch.StartNew()
            let mutable turns = 0

            while clock.ElapsedMilliseconds < 2000L do
                if not (window.Step()) then
                    raise (RunFailed "the window closed during the run")

                turns <- turns + 1

            // The window paces frames by the refresh rate of the display. With nothing presented on a screen it runs
            // a frame one and a half refresh intervals after the last.
            let rate = Double.Parse(state.Stats.Peek[7], CultureInfo.InvariantCulture)
            let hertz = displayHertz ()
            holds $"left to the window's own loop for 2 s ({turns} turns), the tour moved the text {fixed3 (top () - own)} CSS px" (top () > own)
            holds $"at {fixed3 rate} frames a second by the app's count; the display reports {fixed3 hertz} Hz, and a frame every one and a half refreshes is {fixed3 (hertz / 1.5)} a second" (hertz <= 0.0 || (rate >= hertz / 3.0 && rate <= hertz * 1.1))

            // A click on the tour's button stops it, and the app comes to rest.
            click (button "Scroll tour")
            refreshes 3 |> ignore
            holds "a click on the tour's button stops the tour" (not state.Tour.Peek && (button "Scroll tour").textContent = "Scroll tour: stopped")
            let rest = top ()
            let idle = refreshes 60
            holds "stopped, the text stays where it is" (top () = rest)
            holds $"and the app asks for few frames: {idle} of 60 refreshes ran one, for the cursor's blink and the statistics" (idle < 20)

            // The wheel, a click in the text and a key, as the window sends them.
            host.Wheel(int (text.left + text.width / 2.0), int (text.top + text.height / 2.0), 0.0f, 120.0f, 0) |> ignore
            refreshes 3 |> ignore
            holds "the wheel over the text scrolls it by the wheel's delta on the next frame" (top () = rest + 120.0)
            let editor = state.Files[state.Active.Peek].Editor
            let status () = (doc.querySelectorAll "span" |> fun nodes -> seq { for i in 0u .. nodes.length - 1u -> (nodes.item i).textContent }) |> Seq.find (fun label -> label.StartsWith "Ln ")
            let before = status ()
            host.Click(int text.left + 300, int text.top + 110) |> ignore
            refreshes 2 |> ignore
            holds $"a click in the text moves the cursor: the status line went from '{before}' to '{status ()}'" (status () <> before)
            let length = graph.Run(fun () -> editor.Text.Value.Length)
            host.Type("x", "KeyX", "x")
            refreshes 2 |> ignore
            holds "a key typed after the click inserts one character" (graph.Run(fun () -> editor.Text.Value.Length) = length + 1)

            // The desktop's mouse, as SDL reports it: events on SDL's queue, read by the window's own loop.
            let struct (pixelWidth, _) = window.PixelSize
            let scale = float32 pixelWidth / float32 options.Width
            let at (x: float) (y: float) = struct (float32 (text.left + x) * scale, float32 (text.top + y) * scale)

            /// Two turns of the window's loop more than a refresh apart: the window holds a move and a turn of the
            /// wheel until a refresh has passed since the one before.
            let turns () =
                refresh () |> ignore
                Threading.Thread.Sleep 40
                refresh () |> ignore

            let struct (pressX, pressY) = at 300.0 150.0
            let struct (dragX, dragY) = at 420.0 230.0
            let pressed = graph.Run(fun () -> (view ()).PosAtCoords 300.0 150.0)
            let dragged = graph.Run(fun () -> (view ()).PosAtCoords 420.0 230.0)
            Desktop.move pressX pressY false
            turns ()
            Desktop.button pressX pressY true
            turns ()
            Desktop.move dragX dragY true
            turns ()
            Desktop.button dragX dragY false
            turns ()
            let main = graph.Run(fun () -> editor.Selection.Value.Main)
            holds $"a press, a drag and a release from SDL's queue select from {pressed} to {dragged}" (main.Anchor = pressed && main.Head = dragged && pressed <> dragged)
            let wheeled = top ()
            Desktop.wheel pressX pressY 3.0f
            turns ()
            holds "three notches of the wheel from SDL's queue scroll 120 CSS px" (top () = wheeled + 120.0)
            let track = ((doc.querySelector "[data-plume]").parentElement.lastChild :?> Element).getBoundingClientRect ()
            let trackX = float32 (track.left + track.width / 2.0) * scale
            let trackY = float32 (track.top + track.height / 4.0) * scale
            Desktop.move trackX trackY false
            turns ()
            Desktop.button trackX trackY true
            turns ()
            Desktop.button trackX trackY false
            turns ()
            let lines = graph.Run(fun () -> editor.Text.Value.Lines)
            let share = top () / (float lines * (view ()).Theme.LineHeight)
            holds $"a press a quarter of the way down the scrollbar's track moves the text to {fixed3 (100.0 * share)} percent of the file" (share > 0.20 && share < 0.28)

            if options.Pointer then
                // The desktop's real pointer, through Windows: into the window, a press, a drag, a release, the wheel
                // and a press on the scrollbar's track. No key is sent, a click is made only where the window is under
                // the pointer, and the pointer goes back to where it was.
                let struct (originX, originY, _, _) = Desktop.bounds ()
                let onScreen (x: float) (y: float) = struct (originX + int (Math.Round(x * float scale)), originY + int (Math.Round(y * float scale)))
                let struct (homeX, homeY) = Desktop.pointer ()
                let mutable down = false

                let go (struct (x, y)) =
                    Desktop.pointTo x y
                    turns ()

                    if not (Desktop.isUnder x y) then
                        raise (RunFailed $"another window covers the sample at ({x}, {y}); the pointer clicked nothing there")

                try
                    let from = graph.Run(fun () -> (view ()).PosAtCoords 300.0 150.0)
                    let until = graph.Run(fun () -> (view ()).PosAtCoords 420.0 230.0)
                    go (onScreen (text.left + 300.0) (text.top + 150.0))
                    Desktop.press ()
                    down <- true
                    turns ()
                    go (onScreen (text.left + 420.0) (text.top + 230.0))
                    Desktop.release ()
                    down <- false
                    turns ()
                    let range = graph.Run(fun () -> editor.Selection.Value.Main)
                    holds $"the real pointer, pressed, dragged and released through Windows, selects from {from} to {until}" (range.Anchor = from && range.Head = until && from <> until)

                    let rolled = top ()
                    go (onScreen (text.left + 300.0) (text.top + 150.0))
                    Desktop.turnWheel 3
                    turns ()
                    holds "three notches of the real wheel scroll 120 CSS px" (top () = rolled + 120.0)

                    go (onScreen (track.left + track.width / 2.0) (track.top + track.height / 2.0))
                    Desktop.press ()
                    down <- true
                    turns ()
                    Desktop.release ()
                    down <- false
                    turns ()
                    let half = top () / (float (graph.Run(fun () -> editor.Text.Value.Lines)) * (view ()).Theme.LineHeight)
                    holds $"a real press half-way down the scrollbar's track moves the text to {fixed3 (100.0 * half)} percent of the file" (half > 0.46 && half < 0.54)
                finally
                    if down then
                        Desktop.release ()

                    Desktop.pointTo homeX homeY

                let struct (backX, backY) = Desktop.pointer ()
                holds $"the pointer is back where it was, at ({homeX}, {homeY})" (backX = homeX && backY = homeY)

            if options.Shown then
                // What the screen shows where the window is, beside what the window composited.
                window.Present()
                refreshes 6 |> ignore
                let struct (x, y, width, height) = Desktop.bounds ()
                let shown = Desktop.screen x y width height
                let struct (compositedWidth, compositedHeight, composited) = window.ReadPixels()
                Png.PngEncoder.saveRgba8 picture width height shown
                say $"  saved    {Path.GetFullPath picture} ({width} x {height} pixels, copied from the screen at ({x}, {y}))"

                if compositedWidth = width && compositedHeight = height then
                    let mutable close = 0

                    for pixel in 0 .. width * height - 1 do
                        let offset = pixel * 4

                        if
                            abs (int shown[offset] - int composited[offset]) <= 8
                            && abs (int shown[offset + 1] - int composited[offset + 1]) <= 8
                            && abs (int shown[offset + 2] - int composited[offset + 2]) <= 8
                        then
                            close <- close + 1

                    let share = float close / float (width * height)
                    holds $"the screen shows what the window composited: {fixed3 (100.0 * share)} percent of its pixels are within 8 of 255 on each channel" (share > 0.9)
                else
                    say $"  seen     the window composited {compositedWidth} x {compositedHeight} pixels and the screen copy is {width} x {height}; they are not compared"

            // F5 in the text starts the tour again.
            host.Key(0, "F5", "F5", null, 0, false) |> ignore
            host.Key(1, "F5", "F5", null, 0, false) |> ignore
            refreshes 3 |> ignore
            holds "F5 in the text starts the tour again" state.Tour.Peek
            let again = top ()
            let resumed = refreshes 60
            holds $"and the tour moves the text on its own frames again ({resumed} of 60 refreshes ran one)" (resumed = 60 && top () <> again)

            let reveal = doc.querySelector "[data-demo=diagnostic]" :?> HTMLElement
            reveal.click()
            refreshes 6 |> ignore
            let tip = doc.querySelector ".unit-diagnostic [role=tooltip]"
            holds "the diagnostic button stops the tour and reveals the unit diagnostic" (
                not state.Tour.Peek && not (isNull tip) && tip.textContent.Contains "UNIT")

            // A tab opens its file in a view of its own.
            click (button "Integrators.fs")
            refreshes 6 |> ignore
            holds "a click on a file opens it" (state.Active.Peek = 1)
            holds $"and its view is mounted with its text: {grouped (graph.Run(fun () -> (view ()).Editor.Text.Value.Lines))} lines" (graph.Run(fun () -> (view ()).Editor.Text.Value.Lines) = session.Files[1].Count)
            let toolsEditor = state.Files[1]
            let press label =
                (button label :?> HTMLElement).click()
                refreshes 3 |> ignore
            graph.Run(fun () ->
                toolsEditor.FindText.Value <- "Mu: float"
                toolsEditor.Replacement.Value <- "Mu: decimal")
            press "Find"
            let selection () = graph.Run(fun () -> toolsEditor.Editor.Selection.Value.Main)
            let first = selection ()
            let markCount = (doc.querySelectorAll ".search-match").length
            holds $"Find selects a result ({first.From}..{first.To}) and paints its visible highlight ({markCount})" (
                first.To > first.From && not (isNull (doc.querySelector ".search-match")))
            press "Next"
            holds "Next selects a later result" ((selection ()).From > first.From)
            press "Previous"
            holds "Previous returns to the original result" ((selection ()).From = first.From)
            press "Replace"
            let replaced = graph.Run(fun () -> Text.sliceString first.From (first.From + 11) toolsEditor.Editor.Text.Value)
            holds "Replace edits the selected match" (replaced = "Mu: decimal")
            graph.Run(fun () ->
                let doc = toolsEditor.Editor.Text.Value
                toolsEditor.Editor.Dispatch [ Tx.selection (EditorSelection.single 0 (Text.line 4 doc).To) ])
            press "Fold selection"
            holds "Fold selection hides the selected lines" (graph.Run(fun () -> (view ()).Projection.Value.Count = session.Files[1].Count - 4))
            press "Unfold"
            holds "Unfold restores the document rows" (graph.Run(fun () -> (view ()).Projection.Value.Count = session.Files[1].Count))
            say (if options.Shown then "  The window was shown for the length of this run and is closed with it." else "  The window was created hidden and never shown.")
            0
        finally
            graph.Run(fun () -> mounted.Dispose())
            container.remove ()
    with
    | RunFailed reason ->
        Console.Error.WriteLine $"FAIL {reason}"
        1

/// The lines of the 1,000,000-line file.
let private largeFile () : ResizeArray<string> = (Session()).Files[0].Lines

/// Dossier 7.2: what 10,000 history items hold.
let private memory () =
    say "Editor showcase on Ranvier.Plume --memory"
    let lines = largeFile ()
    use graph = new Graph()
    use _activation = graph.Activate()
    let count = 10_000

    let measure () =
        GC.Collect()
        GC.WaitForPendingFinalizers()
        GC.GetTotalMemory true

    let document =
        Document.createWith
            { Text = Text.ofLines lines
              History = { History.defaults with Depth = count * 2 }
              Clock = fun () -> Environment.TickCount64 }

    let editor = Editor.create document []
    // One edit and its undo first, so the measurement holds no start-up cost.
    editor.Dispatch [ Tx.change { From = 0; To = 0; Insert = Text.ofString "x" }; Tx.isolateHistory ]
    Editor.undo editor |> ignore
    let before = measure ()
    let watch = Stopwatch.StartNew()

    for index in 0 .. count - 1 do
        let line = Text.line (int (int64 index * 7919L % 1_000_000L)) document.Text.Value
        editor.Dispatch [ Tx.change { From = line.To; To = line.To; Insert = Text.ofString "x" }; Tx.isolateHistory; Tx.userEvent "input.type" ]

    watch.Stop()
    let afterEdits = measure ()
    document.MarkSaved()
    let afterSave = measure ()
    let items = document.UndoDepth.Value
    say $"  workload   {grouped count} single-character insertions at lines (i * 7919) mod 1,000,000 of the {grouped lines.Count}-line file, each its own history item ({grouped items} items held), in {fixed3 watch.Elapsed.TotalMilliseconds} ms"
    say "  measured   GC.GetTotalMemory(true) after a full collection, before the insertions, after them, and after Document.MarkSaved"
    let each (bytes: int64) = (float bytes / float count).ToString("F1", CultureInfo.InvariantCulture)
    say $"  with the text at the start still held as the saved text   {grouped (int (afterEdits - before))} bytes, {each (afterEdits - before)} bytes per insertion"
    say $"  after MarkSaved: the history, the log and the current text {grouped (int (afterSave - before))} bytes, {each (afterSave - before)} bytes per insertion"
    say "  The first figure holds the leaves of the saved text that the current text no longer shares; it is bounded by"
    say "  the size of the file, not by the number of edits. The second is what history costs."
    GC.KeepAlive editor
    0

/// Dossier 7.3: building the text from bytes, with the scalar and the vectorised line scan.
let private load () =
    say "Editor showcase on Ranvier.Plume --load"
    let lines = largeFile ()
    let bytes = Encoding.UTF8.GetBytes(String.Join('\n', lines))

    let time (build: unit -> Text) =
        for _ in 1..3 do
            build () |> ignore

        let samples =
            Array.init 10 (fun _ ->
                GC.Collect()
                let started = Stopwatch.GetTimestamp()
                let text = build ()
                let elapsed = milliseconds (Stopwatch.GetTimestamp() - started)

                if text.Lines <> lines.Count then
                    raise (RunFailed $"the text has {text.Lines} lines, not {lines.Count}")

                elapsed)
            |> Array.sort

        samples[samples.Length / 2], samples[0]

    let scalarMedian, scalarBest = time (fun () -> Text.ofUtf8Scalar (ReadOnlySpan<byte> bytes))
    let vectorMedian, vectorBest = time (fun () -> Text.ofUtf8 (ReadOnlySpan<byte> bytes))
    say $"  workload   Text.ofUtf8 over the {grouped bytes.Length} UTF-8 bytes of the {grouped lines.Count}-line file; 10 runs after 3 untimed ones"
    say $"  scalar scan       median {fixed3 scalarMedian} ms, best {fixed3 scalarBest} ms"
    say $"  vectorised scan   median {fixed3 vectorMedian} ms, best {fixed3 vectorBest} ms"
    let ratio = (scalarMedian / vectorMedian).ToString("F2", CultureInfo.InvariantCulture)
    say $"  ratio             scalar / vectorised = {ratio} (median)"
    0

/// One change set of many ranges against the same replacements made one at a time.
let private apply () =
    say "Editor showcase on Ranvier.Plume --apply"
    let lines = largeFile ()
    let doc = Text.ofLines lines
    let count = 10_000

    let specs =
        [ for index in 0 .. count - 1 do
              let line = Text.line (index * 100) doc
              { From = line.From; To = line.From + 1; Insert = Text.ofString "X" } ]

    let time (run: unit -> Text) =
        run () |> ignore

        let samples =
            Array.init 7 (fun _ ->
                GC.Collect()
                let started = Stopwatch.GetTimestamp()
                run () |> ignore
                milliseconds (Stopwatch.GetTimestamp() - started))
            |> Array.sort

        samples[samples.Length / 2]

    let changes = ChangeSet.ofSpecs doc specs
    let asSet = time (fun () -> ChangeSet.apply doc changes)

    let oneByOne =
        time (fun () ->
            let mutable text = doc

            for spec in specs do
                text <- Text.replace spec.From spec.To spec.Insert text

            text)

    let build = time (fun () -> ChangeSet.apply doc (ChangeSet.ofSpecs doc specs))

    if not (Text.contentEquals (ChangeSet.apply doc changes) (List.fold (fun text (spec: ChangeSpec) -> Text.replace spec.From spec.To spec.Insert text) doc specs)) then
        raise (RunFailed "the change set and the single replacements give different texts")

    say $"  workload   {grouped count} one-character replacements, one every 100 lines of the {grouped lines.Count}-line file; median of 7 runs after one untimed"
    say $"  ChangeSet.apply of one set of {grouped count} ranges   {fixed3 asSet} ms"
    say $"  ChangeSet.ofSpecs and apply                    {fixed3 build} ms"
    say $"  {grouped count} calls of Text.replace               {fixed3 oneByOne} ms"
    0

/// Runs a scripted mode and returns the exit code: 0 when the run finished, 1 when it found something wrong.
let run (options: Options) : int =
    try
        match options.Mode with
        | Shot path -> shot options path
        | Bench -> bench options
        | Memory -> memory ()
        | Load -> load ()
        | Apply -> apply ()
        | Profile nativeTurn -> profile options nativeTurn
        | Writes -> writes options
        | Tour picture -> tour options picture
        | Interactive -> invalidOp "the window is opened by Program"
    with
    | RunFailed reason ->
        Console.Error.WriteLine $"FAIL {reason}"
        1
