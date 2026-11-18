/// The command line, and the scripted modes: a picture of the window, the frame benchmark, and the measurements of
/// history memory, loading and change application. A scripted mode creates its window hidden and never shows it.
module EditorShowcase.Harness

// The display's mode is read through a native pointer.
#nowarn "9"

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
    | Profile
    | Writes
    | Tour

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
      Tour: bool }

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
  dotnet run -c Release -- --writes     list the writes a keystroke and a scroll make to the page
  dotnet run -c Release -- --tour       run the app as its window runs it, tour on, with no window shown

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
        | "--profile" :: tail -> go { options with Mode = Profile } tail
        | "--writes" :: tail -> go { options with Mode = Writes } tail
        | "--tour" :: tail -> go { options with Mode = Tour } tail
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

    go { Mode = Interactive; Width = WindowW; Height = WindowH; Frames = 240; Lines = 3; File = 0; Tour = true } (List.ofArray argv)

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
let private profile (options: Options) =
    say "Editor showcase on Ranvier.Plume --profile"

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

        let workloads =
            [ "typing", (fun () -> graph.Run(fun () -> Commands.insertText "x" editor |> ignore))
              $"lines x{options.Lines}", (fun () -> graph.Run(fun () -> view.ScrollBy(float options.Lines * lineHeight))) ]

        let rounds = 5
        let totals = Dictionary<string, ResizeArray<struct (float * float * float)>>()
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

                    for feature in off do
                        switch feature false

                    graph.Run(fun () ->
                        let first = 1000 + 40 * typed
                        view.ScrollTo(float first * lineHeight)
                        let start = (Text.line (first + 4) editor.Text.Value).From
                        editor.Dispatch [ Tx.selection (EditorSelection.single start start) ])

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

                        if not (window.Step(timeout = 0)) then
                            raise (RunFailed "the window closed during the run")

                        let t3 = Stopwatch.GetTimestamp()

                        if frame >= 0 then
                            dispatch <- dispatch + (t1 - t0)
                            app <- app + (t2 - t1)
                            turn <- turn + (t3 - t2)

                    let key = workload + "|" + configuration

                    if not (totals.ContainsKey key) then
                        totals[key] <- ResizeArray()

                    let frames = float options.Frames
                    totals[key].Add(struct (milliseconds dispatch / frames, milliseconds app / frames, milliseconds turn / frames))

                    for feature in off do
                        switch feature true

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

/// The refresh rate SDL reports for the primary display, in hertz; 0 when it reports none.
let private displayHertz () : float =
    let mode = SDL.SDL3.SDL_GetCurrentDisplayMode(SDL.SDL3.SDL_GetPrimaryDisplay())

    if Microsoft.FSharp.NativeInterop.NativePtr.isNullPtr mode then
        0.0
    else
        float (Microsoft.FSharp.NativeInterop.NativePtr.read mode).refresh_rate

/// The app as its window runs it, tour on, with no window shown. The page is given a frame whenever it asks for
/// one, as a shown window gives one at each refresh of its display, and the mouse, the wheel and the keys go in
/// through the host as a window sends them. Each line printed is something that held; the first that does not
/// ends the run.
let private tour (options: Options) =
    say "Editor showcase on Ranvier.Plume --tour"

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

            // The first refreshes mount the text view and size its rows.
            refreshes 12 |> ignore

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

            // F5 in the text starts the tour again.
            host.Key(0, "F5", "F5", null, 0, false) |> ignore
            host.Key(1, "F5", "F5", null, 0, false) |> ignore
            refreshes 3 |> ignore
            holds "F5 in the text starts the tour again" state.Tour.Peek
            let again = top ()
            let resumed = refreshes 60
            holds $"and the tour moves the text on its own frames again ({resumed} of 60 refreshes ran one)" (resumed = 60 && top () <> again)

            // A tab opens its file in a view of its own.
            click (button "Integrators.fs")
            refreshes 6 |> ignore
            holds "a click on a file opens it" (state.Active.Peek = 1)
            holds $"and its view is mounted with its text: {grouped (graph.Run(fun () -> (view ()).Editor.Text.Value.Lines))} lines" (graph.Run(fun () -> (view ()).Editor.Text.Value.Lines) = session.Files[1].Count)
            say "  The window was created hidden and never shown."
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
        | Profile -> profile options
        | Writes -> writes options
        | Tour -> tour options
        | Interactive -> invalidOp "the window is opened by Program"
    with
    | RunFailed reason ->
        Console.Error.WriteLine $"FAIL {reason}"
        1
