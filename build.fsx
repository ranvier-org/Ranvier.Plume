// Build entry point. Run with: dotnet fsi --exec build.fsx -- <target>
//   build   builds the solution in Release
//   test    runs every test project, unfiltered
//   ci      build, then test
//   check   builds the solution in Debug, then runs rfs check on it (rfs is the Ranvier.FSharp.Agent global tool)
open System
open System.Diagnostics
open System.IO

let root = __SOURCE_DIRECTORY__

let run (file: string) (arguments: string) =
    let info = ProcessStartInfo (file, arguments, WorkingDirectory = root, UseShellExecute = false)
    use started = Process.Start info
    started.WaitForExit ()

    if started.ExitCode <> 0 then
        eprintfn "'%s %s' exited with code %d" file arguments started.ExitCode
        exit started.ExitCode

let build () =
    run "dotnet" "build Ranvier.Plume.slnx -c Release --nologo"

let test () =
    for project in Directory.GetFiles (Path.Combine (root, "tests"), "*.fsproj", SearchOption.AllDirectories) do
        run "dotnet" (String.Concat ("run -c Release --no-build --project \"", project, "\" -- --summary"))

// rfs reads the Debug configuration: the outputs of the projects a project references, and the Debug restore.
let check () =
    run "dotnet" "build Ranvier.Plume.slnx -c Debug --nologo"
    run "rfs" "status --reload"
    run "rfs" "check --fail-on-error"

match fsi.CommandLineArgs |> Array.skip 1 |> Array.filter (fun a -> a <> "--") |> Array.tryHead with
| Some "build" -> build ()
| Some "test" -> test ()
| Some "check" -> check ()
| Some "ci"
| None ->
    build ()
    test ()
| Some other ->
    eprintfn "unknown target '%s' (build, test, ci, check)" other
    exit 2
