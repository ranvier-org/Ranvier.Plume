// Build entry point. Run with: dotnet fsi --exec build.fsx -- <target>
//   build   builds the solution in Release
//   test    runs every test project on each of its target frameworks, unfiltered
//   ci      build, then test
//   check   builds the solution in Debug, then runs rfs check on it (rfs is the Ranvier.FSharp.Agent global tool)
//   traced  builds both libraries on the traced core (-p:RanvierTrace=true) and runs the state tests on them, in a
//           copy of the tree: a traced restore in place would replace what the untraced build and rfs read
//   acceptance  recomputes what section 7 of the dossier asks: 7.1 from the recorded runs in docs/bench, 7.2 and
//           7.3 from runs made now, and checks the plan's boxes and the commits the acceptance record names
//   dev     runs the sample under the hot reload host with its window off screen, on a copy of its sources:
//           the steps of samples/EditorShowcase/hot-reload.json edit the copy and read the page after each edit
open System
open System.Diagnostics
open System.Globalization
open System.IO
open System.Text.Json
open System.Text.RegularExpressions

let root = __SOURCE_DIRECTORY__

let runIn (directory: string) (file: string) (arguments: string) =
    let info = ProcessStartInfo (file, arguments, WorkingDirectory = directory, UseShellExecute = false)
    use started = Process.Start info
    started.WaitForExit ()

    if started.ExitCode <> 0 then
        eprintfn "'%s %s' exited with code %d" file arguments started.ExitCode
        exit started.ExitCode

let run (file: string) (arguments: string) = runIn root file arguments

/// What a command run from the root prints, and its exit code.
let capture (file: string) (arguments: string) : struct (string * int) =
    let info = ProcessStartInfo (file, arguments, WorkingDirectory = root, UseShellExecute = false, RedirectStandardOutput = true)
    use started = Process.Start info
    let output = started.StandardOutput.ReadToEnd ()
    started.WaitForExit ()
    struct (output, started.ExitCode)

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

/// The acceptance criteria of the dossier, section 7, as verdicts a reader can run for themselves.
let acceptance () =
    build ()
    let mutable failed = 0

    let verdict (holds: bool) (what: string) =
        if not holds then
            failed <- failed + 1

        printfn "  %s  %s" (if holds then "pass" else "FAIL") what

    let number (text: string) = Double.Parse (text, CultureInfo.InvariantCulture)
    let docs = Path.Combine (root, "docs")

    // 7.1: a row passes when the mean of the sample's run totals is inside the range of the baseline's runs, or
    // below it. The runs are the recorded ones: a new run needs the baseline app, which is outside this repository.
    let row = Regex @"^  (lines x\d+|7 px|typing)\s+(on|off)\s+(on|off)\s+(on|off)\s+.*?([\d.]+)\s*$"

    let totals (name: string) : (string * float) list =
        [
            for line in File.ReadAllLines (Path.Combine (docs, "bench", name)) do
                let found = row.Match line

                if found.Success then
                    let key = String.Join (", ", [ for group in 1..4 -> found.Groups[group].Value ])
                    yield key, number found.Groups[5].Value
        ]

    let baseline = [ for run in 1..5 -> totals (sprintf "baseline-%d.txt" run) ]
    let sample = [ for run in 1..5 -> totals (sprintf "plume-%d.txt" run) ]
    printfn "7.1 Frame cost, from the ten recorded runs in docs/bench (workload, recycling, cache, layer):"

    for index in 0 .. baseline.Head.Length - 1 do
        let key = fst baseline.Head[index]
        let theirs = baseline |> List.map (fun run -> snd run[index])
        let ours = sample |> List.map (fun run -> snd run[index])
        let mean = List.average ours
        verdict (mean <= List.max theirs) (sprintf "%s: the sample's mean total %.3f ms; the baseline's runs %.3f to %.3f ms" key mean (List.min theirs) (List.max theirs))

    verdict (baseline.Head.Length = 9) (sprintf "%d rows in a run" baseline.Head.Length)

    // 7.2: under 512 bytes an insertion once the document is saved.
    let struct (memory, memoryExit) = capture "dotnet" "run -c Release --no-build --project samples/EditorShowcase -- --memory"
    let held = Regex.Match (memory, @"after MarkSaved.*?([\d.]+) bytes per insertion")
    printfn "7.2 History memory, from a run made now:"
    verdict (memoryExit = 0 && held.Success && number held.Groups[1].Value < 512.0) (sprintf "%s bytes an insertion after MarkSaved; the bound is 512" (if held.Success then held.Groups[1].Value else "?"))

    // 7.3: the vectorised scan stays only while it is the faster one.
    let struct (load, loadExit) = capture "dotnet" "run -c Release --no-build --project samples/EditorShowcase -- --load"
    let scalar = Regex.Match (load, @"scalar scan\s+median ([\d.]+) ms")
    let vectorised = Regex.Match (load, @"vectorised scan\s+median ([\d.]+) ms")
    printfn "7.3 Loading, from a run made now:"

    verdict
        (loadExit = 0 && scalar.Success && vectorised.Success && number vectorised.Groups[1].Value < number scalar.Groups[1].Value)
        (sprintf "the vectorised scan's median %s ms against the scalar scan's %s ms" (if vectorised.Success then vectorised.Groups[1].Value else "?") (if scalar.Success then scalar.Groups[1].Value else "?"))

    // The plan's boxes, and the commits the acceptance record's table names.
    let plan = File.ReadAllText (Path.Combine (docs, "superpowers", "plans", "2026-11-18-plume-phase-1.md"))
    let ticked = Regex.Matches(plan, @"(?m)^- \[x\] \*\*Step").Count
    let unticked = Regex.Matches(plan, @"(?m)^- \[ \] ").Count
    printfn "The plan and the record:"
    verdict (unticked = 0 && ticked = 41) (sprintf "%d steps ticked and %d not, of the plan's 41" ticked unticked)
    let record = File.ReadAllText (Path.Combine (docs, "ACCEPTANCE.md"))
    let from = record.IndexOf "## The plan, task by task"
    let until = record.IndexOf "## 7.1 Frame cost"

    let commits =
        if from < 0 || until < from then
            []
        else
            [ for found in Regex.Matches (record.Substring (from, until - from), @"`([0-9a-f]{7})`") -> found.Groups[1].Value ]
            |> List.distinct

    let missing = commits |> List.filter (fun commit -> let struct (_, code) = capture "git" (String.Concat ("cat-file -e ", commit, "^{commit}")) in code <> 0)
    verdict (not commits.IsEmpty && missing.IsEmpty) (sprintf "%d commits named by the record's table, %d of them not in the repository" commits.Length missing.Length)

    if failed > 0 then
        eprintfn "%d acceptance checks failed" failed
        exit 1

    printfn "every acceptance check passed"

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
| Some "acceptance" -> acceptance ()
| Some "ci"
| None ->
    build ()
    test ()
| Some other ->
    eprintfn "unknown target '%s' (build, test, ci, check, dev, traced, acceptance)" other
    exit 2
