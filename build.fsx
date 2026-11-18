// Build entry point. Run with: dotnet fsi --exec build.fsx -- <target>
//   build   builds the solution in Release
//   test    runs every test project, unfiltered
//   ci      build, then test
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

match fsi.CommandLineArgs |> Array.skip 1 |> Array.filter (fun a -> a <> "--") |> Array.tryHead with
| Some "build" -> build ()
| Some "test" -> test ()
| Some "ci"
| None ->
    build ()
    test ()
| Some other ->
    eprintfn "unknown target '%s' (build, test, ci)" other
    exit 2
