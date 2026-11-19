#I "../tests/Ranvier.Plume.Tests/bin/Release/net10.0"
#r "Ranvier.dll"
#r "Ranvier.Keyboard.dll"
#r "Ranvier.Plume.dll"

open System
open System.Diagnostics
open Ranvier
open Ranvier.Plume

let run count slowLast =
    use graph = new Graph()
    use active = graph.Activate()
    let document = Document.create Text.empty
    let readers = Array.init count (fun _ -> Api.createRoot (fun owner -> owner, document.Reader()))
    document.Dispatch []
    if slowLast then
        for i in 0 .. count - 2 do
            (snd readers[i]).Read() |> ignore

    let watch = Stopwatch.StartNew()
    for _ in 1..10000 do document.Dispatch []
    watch.Stop()
    for owner, _ in readers do owner.Dispose()
    watch.Elapsed.TotalNanoseconds / 10000.
for count, slowLast in [0,false; 1,false; 64,false; 1024,false; 1024,true] do
    run count slowLast |> ignore
    let samples = Array.init 9 (fun _ -> run count slowLast)
    printfn "%d readers, slow last %b: %.1f ns/append" count slowLast ((Array.sort samples)[4])
