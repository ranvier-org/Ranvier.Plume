module EditorShowcase.Program

open System
open Ranvier
open Ranvier.View
open Ranvier.View.Dom
open Ranvier.View.Dsl
open Ranvier.View.WebCore.Sdl
open EditorShowcase.Model
open EditorShowcase.App
open EditorShowcase.Harness

let private propsOf (window: Window) = { Window = window; Session = Session() }

/// Opens the app's window and runs it until it is closed. A window is shown here, and by `--tour --shown` for
/// the length of its scripted run.
let private runWindow (options: Options) =
    use window = Window.Create(Title, options.Width, options.Height, hidden = true)
    let graph = window.Graph
    let doc = window.Host.Document
    let container = doc.createElement "div"
    doc.body.appendChild container |> ignore
    let props = propsOf window
    props.Session.Active <- options.File
    props.Session.Tour <- options.Tour

    let mounted =
        Mounting.mount graph None (fun errs -> raise (AggregateException errs)) container None (fun ctx ->
            Program.run (Component.render app props) ctx.Backend)

    try
        window.Show()
        window.Present()

        while window.Step() do
            ()
    finally
        graph.Run(fun () -> mounted.Dispose())
        container.remove ()

    0

/// Runs the app outside the hot reload host: its window, or one of the scripted modes of `Harness`.
/// An argument the app does not know is an error with exit code 2.
let private runApp (argv: string[]) =
    if Array.contains "--help" argv then
        Console.WriteLine usage
        0
    else
        match parse argv with
        | Error reason ->
            Console.Error.WriteLine $"error: {reason}"
            Console.Error.WriteLine()
            Console.Error.WriteLine usage
            2
        | Ok options when options.Mode = Interactive -> runWindow options
        | Ok options -> run options

[<EntryPoint>]
let main argv =
#if DEBUG
    // dotnet run -- dev: the hot reload host keeps the window open and patches saved edits into it.
    if Array.contains "dev" argv then
        Ranvier.View.WebCore.Hmr.Dev.RunWith(argv, Title, WindowW, WindowH, app, propsOf)
    else
        runApp argv
#else
    runApp argv
#endif
