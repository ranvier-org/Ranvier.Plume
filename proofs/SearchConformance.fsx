module SearchConformance

open Ranvier
open Ranvier.Plume

let observations () =
    use graph = new Graph()
    use active = graph.Activate()
    let rows = ResizeArray<string>()
    let list offsets = "[" + String.concat ";" (List.map string offsets) + "]"
    for caseId, (source, needle, starts, ends) in List.indexed [
        "aaa😀aaa", "aa", [0;5], [2;7]
        "abaaba", "aba", [0;3], [3;6]
        "x\r\ny", "\r\n", [1], [3]
    ] do
        let editor = Editor.create (Document.create (Text.ofString source)) [Search.extension]
        Search.setQuery (ValueSome (Search.literal needle true)) editor |> ignore
        for left in 0 .. source.Length do
            for right in left .. source.Length do
                for forward in [true;false] do
                    editor.Dispatch [Tx.selection (EditorSelection.single left right)]
                    let model, offsets, name =
                        if forward then
                            Search.next editor |> ignore
                            $"search_next {list starts} {right}", starts, "next"
                        else
                            Search.previous editor |> ignore
                            $"search_prev {list ends} {left}", starts, "prev"
                    let actual = List.findIndex ((=) editor.Selection.Value.Main.From) offsets
                    rows.Add $"Example search_{name}_{caseId}_{left}_{right} : {model} = {actual}. Proof. reflexivity. Qed."
    List.ofSeq rows
