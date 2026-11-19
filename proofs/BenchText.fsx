#I "../tests/Ranvier.Plume.Tests/bin/Release/net10.0"
#r "Ranvier.Plume.dll"

open System
open System.Diagnostics
open Ranvier.Plume

let sample (input: string) =
    let iterations = min 100000 (max 100 (10000000 / input.Length))
    let text = Text.ofString input
    let mutable checksum = 0
    let watch = Stopwatch()
    let start = GC.GetAllocatedBytesForCurrentThread()
    watch.Start()
    for _ in 1..iterations do
        checksum <- checksum + (Text.toString text).Length
    watch.Stop()
    let allocated = GC.GetAllocatedBytesForCurrentThread() - start
    if checksum <> iterations * input.Length then failwith "unexpected text length"
    watch.Elapsed.TotalNanoseconds / float iterations, allocated / int64 iterations

for input in ["let answer = 42"; "let a = 1\r\nlet b = 2"; String.replicate 300 "alpha λ🙂\r\n"] do
    sample input |> ignore
    let time, bytes = (Array.init 9 (fun _ -> sample input) |> Array.sort)[4]
    printfn "%d chars: %.1f ns/read, %d B/read" input.Length time bytes
