#I "../tests/Ranvier.Plume.Tests/bin/Release/net10.0"
#r "Ranvier.dll"
#r "Ranvier.Keyboard.dll"
#r "Ranvier.Plume.dll"
#load "../samples/EditorShowcase/Sample.fs"

open System.Diagnostics
open Ranvier
open Ranvier.Plume

// Time the state API separately from native input and rendering.
let run (file: EditorShowcase.Sample.SampleFile) =
    use graph = new Graph()
    use active = graph.Activate()
    let editor = Editor.create (Document.create (Text.ofLines file.Lines)) [ Search.extension ]
    let median samples = Array.sort samples |> fun values -> values[Array.length values / 2]
    let time f =
        let watch = Stopwatch.StartNew()
        f ()
        watch.Elapsed.TotalMilliseconds
    let cold = ResizeArray<_>()
    let warm = ResizeArray<_>()
    let edited = ResizeArray<_>()
    for round in 0 .. 5 do
        let query = Search.literal "Mu: float" false
        let first = time (fun () ->
            Search.setQuery (ValueSome query) editor |> ignore
            if not (Search.next editor) then failwith "missing match")
        let next = time (fun () -> for _ in 1 .. 100 do Search.next editor |> ignore)
        let updated = time (fun () ->
            editor.Dispatch [Tx.change {From=0; To=0; Insert=Text.ofString " "}]
            if not (Search.next editor) then failwith "missing match after edit")
        if round > 0 then
            cold.Add first
            warm.Add (next / 100.)
            edited.Add updated
    printfn "%s (%d lines): query + first match %.3f ms; cached next %.4f ms; edit + next %.3f ms" file.Name file.Count (median (cold.ToArray())) (median (warm.ToArray())) (median (edited.ToArray()))

for file in EditorShowcase.Sample.files () |> Array.rev do run file
