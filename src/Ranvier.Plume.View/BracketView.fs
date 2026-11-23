namespace Ranvier.Plume.View

open System.Collections.Generic
open Ranvier.Plume

/// Highlights both characters of each matched pair adjacent to an empty selection.
[<RequireQualifiedAccess>]
module BracketMatching =
    let extension =
        Layer.define {
            Above = false
            Class = "matching-bracket"
            Markers = fun view ->
                let doc = view.Editor.Text.Value
                let viewport = view.Viewport.Value
                let projection = view.Projection.Value
                let mutable positions: HashSet<int> = null
                let visible p =
                    p >= viewport.From && p < viewport.To &&
                    (not projection.HasFolds || projection.IsVisible((Text.lineAt p doc).Number))
                let add p =
                    if visible p then
                        if isNull positions then positions <- HashSet<int>()
                        positions.Add p |> ignore
                for range in view.Editor.Selection.Value.Ranges do
                    if range.IsEmpty && range.Head >= viewport.From && range.Head <= viewport.To then
                        match Brackets.matchAt doc range.Head with
                        | ValueSome(struct (a, b)) -> add a; add b
                        | ValueNone -> ()
                if isNull positions then Array.empty
                else positions |> Seq.map (fun p -> { Rect = view.CoordsAtPos p; Class = "matched" }) |> Seq.toArray
        }
