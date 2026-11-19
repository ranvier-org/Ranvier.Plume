#I "../tests/Ranvier.Plume.Tests/bin/Release/net10.0"
#r "Ranvier.dll"
#r "Ranvier.Keyboard.dll"
#r "Ranvier.Plume.dll"

open System
open System.IO
open Ranvier
open Ranvier.Plume

let lines = ResizeArray<string>()
lines.Add "From Stdlib Require Import List NArith."
lines.Add "From Plume Require Import Edits Retention."
lines.Add "Import ListNotations."

let list xs = "[" + String.concat "; " xs + "]"
let units (s: string) = s |> Seq.map (fun c -> string (int c) + "%N") |> list
let check name expr =
    lines.Add $"Example {name} : {expr}. Proof. reflexivity. Qed."

// Every ordered pair of disjoint ranges, including empty and adjacent edits.
// Represent the witness with the original spans, independent of the F# section encoder.
let materializationCases =
    [ [("", "")]
      [("plain", "")]
      [("🙂" + string (char 0xDE42), "")]
      [("left", "\r\n"); ("right", "")]
      [("a", "\r"); ("", "\n"); ("", "")] ]
for index, spans in List.indexed materializationCases do
    let raw = spans |> List.map (fun (line, ending) -> line + ending) |> String.concat ""
    let model = spans |> List.map (fun (line, ending) -> "(" + units line + ", " + units ending + ")") |> list
    let actual = Text.toString (Text.ofString raw)
    check $"materialize_{index}" $"@flatten N {model} = {units actual}"

let manyLines = String.replicate 260 "λ\r\n" + string (char 0xD83D)
let materialized = Text.toString (Text.ofString manyLines)
lines.Add "Definition line_block : list (list N * list N) := repeat ([955%N], [13%N;10%N]) 130."
lines.Add "Definition tree_fixture := branch (leaf line_block) (leaf (line_block ++ [([55357%N], [])]))."
check "materialize_tree" ("fill tree_fixture 0 (repeat 0%N (size tree_fixture)) = " + units materialized)

let source = "aλ🙂b"
let doc = Text.ofString source
let positions = [0..source.Length]
for a in positions do
    for b in positions |> List.filter ((<=) a) do
        for c in positions |> List.filter ((<=) b) do
            for d in positions |> List.filter ((<=) c) do
                let sub x y = source.Substring(x, y - x)
                let change x y ins = { From = x; To = y; Insert = Text.ofString ins }
                let specs = [change a b "x"; change c d ""]
                let set = ChangeSet.ofSpecs doc specs
                let edited = ChangeSet.apply doc set
                let undone = ChangeSet.apply edited (ChangeSet.invert set)
                let keep x y = "keep " + units (sub x y)
                let replace x y ins = "replace " + units (sub x y) + " " + units ins
                let parts = list [keep 0 a; replace a b "x"; keep b c; replace c d ""; keep d source.Length]
                let name = $"edit_{a}_{b}_{c}_{d}"
                lines.Add $"Definition {name} : list (@span N) := {parts}."
                check (name + "_forward") $"back 0 {name} {units source} = {units (Text.toString edited)}"
                check (name + "_undo") $"back 0 (map flip {name}) {units (Text.toString edited)} = {units (Text.toString undone)}"

// Two independent owners, with the slower reader detached while behind.
// Recompute the model trim at each append, feeding the F# observed sequence IDs
// into executable Rocq equalities. A memo can return its cached batch twice.
let readers () =
    use graph = new Graph()
    use activation = graph.Activate()
    let document = Document.create Text.empty
    let register () = Api.createRoot (fun owner -> owner, document.Reader())
    let fastOwner, fast = register ()
    let slowOwner, slow = register ()
    let mutable fastAt = 0
    let mutable slowAt = 0
    lines.Add "Definition log0 : list nat := []."
    lines.Add "Definition cut0 := 0."
    for head in 1 .. 16 do
        if head = 10 then slowOwner.Dispose()
        document.Dispatch [ Tx.userEvent "proof.tick" ]
        let cursors = if head < 10 then [fastAt;slowAt] else [fastAt]
        let rs = cursors |> List.map string |> list
        lines.Add $"Definition cut{head} := oldest {head} {rs}."
        lines.Add $"Definition log{head} := trim cut{head-1} cut{head} (log{head-1} ++ [{head}])."
        let read name (reader: LogReader) at =
            let expect batch = batch |> Array.map (fun (tx: Transaction) -> string tx.Seq) |> list
            let expr = $"since cut{head} {at} log{head}"
            check $"{name}_{head}" (expr + " = " + expect (reader.Read()))
            check $"{name}_{head}_cached" (expr + " = " + expect (reader.Read()))
        if head % 2 = 0 then
            read "fast" fast fastAt
            fastAt <- head
        if head = 6 then
            read "slow" slow slowAt
            slowAt <- head
    fastOwner.Dispose()
    document.Dispatch [Tx.userEvent "proof.trim"]
    try
        document.DispatchAt(15L, [])
        failwith "Expired revision was accepted after the last reader detached"
    with :? InvalidOperationException -> ()
readers()

// Probe DispatchAt on a fresh document per revision because accepted probes append.
// Reading or disposing a cursor changes the next trim; neither trims immediately.
let probe slowLast disposeSlow revision =
    use graph = new Graph()
    use activation = graph.Activate()
    let doc = Document.create Text.empty
    let makeReader () = Api.createRoot (fun owner -> owner, doc.Reader())
    let a, b = makeReader(), makeReader()
    let (first, slow), (second, fast) = if slowLast then b, a else a, b
    for seq in 1..5 do
        doc.Dispatch [Tx.userEvent "proof.tick"]
        if seq = 2 then slow.Read() |> ignore
        if seq = 4 then
            fast.Read() |> ignore
            if disposeSlow then first.Dispose()
    let accepted =
        try doc.DispatchAt(int64 revision, []); true
        with :? InvalidOperationException -> false
    second.Dispose()
    if not disposeSlow then first.Dispose()
    let positions = if disposeSlow then "[4]" else "[2; 4]"
    let outcome = if accepted then "true" else "false"
    let group = if disposeSlow then "released" else "live"
    let order = if slowLast then "last" else "first"
    check $"base_{order}_{group}_{revision}" $"Nat.leb (oldest 5 {positions}) {revision} = {outcome}"
for slowLast in [false; true] do
    for disposed in [false; true] do
        for revision in 0..5 do probe slowLast disposed revision
File.WriteAllLines(fsi.CommandLineArgs[1], lines)
