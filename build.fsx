// Build entry point. Run with: dotnet fsi --exec build.fsx -- <target>
//   build   builds the solution in Release
//   test    runs every test project on each of its target frameworks, unfiltered
//   ci      build, then test
//   check   builds the solution in Debug, then runs rfs check on it (rfs is the Ranvier.FSharp.Agent global tool)
//   traced  builds both libraries on the traced core (-p:RanvierTrace=true) and runs the state tests on them, in a
//           copy of the tree: a traced restore in place would replace what the untraced build and rfs read
//   dev     runs the sample under the hot reload host with its window off screen, on a copy of its sources:
//           the steps of samples/EditorShowcase/hot-reload.json edit the copy and read the page after each edit
open System
open System.Diagnostics
open System.IO
open System.Text.Json

let root = __SOURCE_DIRECTORY__

let runIn (directory: string) (file: string) (arguments: string) =
    let info = ProcessStartInfo (file, arguments, WorkingDirectory = directory, UseShellExecute = false)
    use started = Process.Start info
    started.WaitForExit ()

    if started.ExitCode <> 0 then
        eprintfn "'%s %s' exited with code %d" file arguments started.ExitCode
        exit started.ExitCode

let run (file: string) (arguments: string) = runIn root file arguments

let build () =
    run "dotnet" "build Ranvier.Plume.slnx -c Release --nologo"

/// The target frameworks a project file names.
let frameworksOf (project: string) : string[] =
    let text = File.ReadAllText project

    let between (opening: string) (closing: string) =
        match text.IndexOf opening with
        | -1 -> None
        | at -> Some (text.Substring (at + opening.Length, text.IndexOf (closing, at) - at - opening.Length))

    match between "<TargetFrameworks>" "</TargetFrameworks>" with
    | Some list -> list.Split (';', StringSplitOptions.RemoveEmptyEntries ||| StringSplitOptions.TrimEntries)
    | None ->
        match between "<TargetFramework>" "</TargetFramework>" with
        | Some one -> [| one.Trim () |]
        | None -> failwithf "%s names no target framework" project

let test () =
    for project in Directory.GetFiles (Path.Combine (root, "tests"), "*.fsproj", SearchOption.AllDirectories) do
        for framework in frameworksOf project do
            printfn "%s on %s" (Path.GetFileNameWithoutExtension project) framework
            run "dotnet" (String.Concat ("run -c Release --no-build --framework ", framework, " --project \"", project, "\" -- --summary"))

/// Copies a tree without its build outputs and its repository data.
let rec copyTree (source: string) (target: string) =
    Directory.CreateDirectory target |> ignore

    for file in Directory.GetFiles source do
        File.Copy (file, Path.Combine (target, Path.GetFileName file))

    for directory in Directory.GetDirectories source do
        match Path.GetFileName directory with
        | "bin"
        | "obj"
        | ".git"
        | ".idea"
        | "bench" -> ()
        | name -> copyTree directory (Path.Combine (target, name))

let traced () =
    let work = Path.Combine (Path.GetTempPath (), "ranvier-plume-traced-" + Guid.NewGuid().ToString "N")
    copyTree root work

    try
        let tests = Path.Combine (work, "tests", "Ranvier.Plume.Tests", "Ranvier.Plume.Tests.fsproj")
        runIn work "dotnet" "build src/Ranvier.Plume.View/Ranvier.Plume.View.fsproj -c Release -p:RanvierTrace=true --nologo"
        runIn work "dotnet" (String.Concat ("build \"", tests, "\" -c Release -p:RanvierTrace=true --nologo"))

        for framework in frameworksOf tests do
            printfn "Ranvier.Plume.Tests on %s, traced" framework
            runIn work "dotnet" (String.Concat ("run -c Release --no-build -p:RanvierTrace=true --framework ", framework, " --project \"", tests, "\" -- --summary"))
    finally
        Directory.Delete (work, true)

// rfs reads the Debug configuration: the outputs of the projects a project references, and the Debug restore.
let check () =
    run "dotnet" "build Ranvier.Plume.slnx -c Debug --nologo"
    run "rfs" "status --reload"
    run "rfs" "check --fail-on-error"

// The hot reload host ends with exit code 0 whatever its steps found, so the steps are read from its result.
let dev () =
    let sample = Path.Combine (root, "samples", "EditorShowcase")
    run "dotnet" (String.Concat ("build \"", sample, "\" -c Debug --nologo"))
    let work = Path.Combine (Path.GetTempPath (), "ranvier-plume-dev-" + Guid.NewGuid().ToString "N")
    let copy = Path.Combine (work, "EditorShowcase")
    Directory.CreateDirectory copy |> ignore

    try
        for file in Directory.GetFiles sample do
            match Path.GetExtension file with
            | ".fs"
            | ".fsproj" -> File.Copy (file, Path.Combine (copy, Path.GetFileName file))
            | _ -> ()

        let scenario = Path.Combine (work, "hot-reload.json")
        File.Copy (Path.Combine (sample, "hot-reload.json"), scenario)
        run "dotnet" (String.Concat ("run -c Debug --no-build --project \"", sample, "\" -- dev -hidden -sources \"", copy, "\" -bench \"", scenario, "\""))
        let results = Path.Combine (work, "results")

        if not (Directory.Exists results) || Directory.GetFiles(results, "*.json").Length = 0 then
            eprintfn "the hot reload run wrote no result"
            exit 1

        use document = JsonDocument.Parse (File.ReadAllText (Directory.GetFiles(results, "*.json")[0]))
        let mutable failed = 0
        let mutable steps = 0

        for step in document.RootElement.EnumerateArray () do
            let ok = step.GetProperty("ok").GetBoolean ()
            steps <- steps + 1

            if not ok then
                failed <- failed + 1

            let detail = step.GetProperty("detail").GetString ()
            printfn "  %s  %s  %s" (if ok then "ok  " else "FAIL") (step.GetProperty("step").GetString ()) (if detail.Length > 90 then detail.Substring (0, 90) else detail)

        if failed > 0 then
            eprintfn "%d of %d hot reload steps failed" failed steps
            exit 1

        printfn "%d of %d hot reload steps passed; the window was never shown" steps steps
    finally
        Directory.Delete (work, true)

match fsi.CommandLineArgs |> Array.skip 1 |> Array.filter (fun a -> a <> "--") |> Array.tryHead with
| Some "build" -> build ()
| Some "test" -> test ()
| Some "check" -> check ()
| Some "dev" -> dev ()
| Some "traced" -> traced ()
| Some "ci"
| None ->
    build ()
    test ()
| Some other ->
    eprintfn "unknown target '%s' (build, test, ci, check, dev, traced)" other
    exit 2
