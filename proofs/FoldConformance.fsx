open Ranvier
open Ranvier.Plume

let observations () =
    use graph = new Graph()
    use active = graph.Activate()
    let editor = Editor.create (Document.create (Text.ofString "a\r\nb\nc\rd\ne\nf\ng\nh")) [Folding.extension]
    [ for h in 0..5 do
          for last in h+1 .. 6 do
              Folding.fold h last editor |> ignore
              let p = Folding.projection editor.State.Value
              for line in 0..7 do
                  yield $"Example fold_{h}_{last}_{line} : fold_row {h} {last-h} {line} = {p.VisualRow line}. Proof. reflexivity. Qed."
              for row in 0..p.Count-1 do
                  yield $"Example expand_{h}_{last}_{row} : unfold_row {h} {last-h} {row} = {p.DocumentLine row}. Proof. reflexivity. Qed."
              Folding.unfold h editor |> ignore
      Folding.fold 1 3 editor |> ignore
      Folding.fold 5 6 editor |> ignore
      let p = Folding.projection editor.State.Value
      for row in 0..p.Count-1 do
          yield $"Example two_expand_{row} : rows_to_document [(3,1);(1,2)] {row} = {p.DocumentLine row}. Proof. reflexivity. Qed."
      for line in 0..7 do
          yield $"Example two_fold_{line} : document_to_rows [(3,1);(1,2)] {line} = {p.VisualRow line}. Proof. reflexivity. Qed." ]

