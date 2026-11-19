// Pass --lib:<Release output directory> to fsi to select the assemblies.
#r "Ranvier.dll"
#r "Ranvier.Keyboard.dll"
#r "Ranvier.Plume.dll"

open System
open System.Diagnostics
open Ranvier
open Ranvier.Plume

let sample batchSize =
    use graph = new Graph()
    use active = graph.Activate()
    let document = Document.create Text.empty
    let readers = Array.init 256 (fun _ -> document.Reader())
    let mutable ticks = 0L
    let mutable bytes = 0L
    let mutable observed = 0
    for _ in 1..16 do
        for _ in 1..batchSize do document.Dispatch []
        let allocated = GC.GetAllocatedBytesForCurrentThread()
        let start = Stopwatch.GetTimestamp()
        for reader in readers do observed <- observed + reader.Read().Length
        ticks <- ticks + (Stopwatch.GetTimestamp() - start)
        bytes <- bytes + (GC.GetAllocatedBytesForCurrentThread() - allocated)
    let reads = 16 * readers.Length
    if observed <> reads * batchSize then failwith "unexpected batch size"
    float ticks * 1e9 / float Stopwatch.Frequency / float reads, bytes / int64 reads

for size in [1; 16; 256] do
    sample size |> ignore
    let results = Array.init 7 (fun _ -> sample size)
    let ns = (results |> Array.map fst |> Array.sort).[3]
    let bytes = (results |> Array.map snd |> Array.sort).[3]
    printfn "%d tx/read: %.1f ns/read, %d B/read" size ns bytes
