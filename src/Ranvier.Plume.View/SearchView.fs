namespace Ranvier.Plume.View

open Ranvier.Plume
open Ranvier.View.Dsl

/// Search state and a document-anchored mark on each visible match.
[<RequireQualifiedAccess>]
module SearchView =
    let highlight className =
        let ranges (view: EditorView) =
            let shown = view.Viewport.Value
            let projection = view.Projection.Value
            let state = view.Editor.State.Value
            let mutable items = []
            let add first last =
                match items with
                | (previous, _, ()) :: _ when previous = first -> ()
                | _ -> items <- (first, last, ()) :: items
            if projection.HasFolds then
                for row in projection.VisualRow shown.FirstLine .. projection.VisualRow shown.LastLine do
                    let line = Text.line (projection.DocumentLine row) state.Doc
                    Search.between (max shown.From line.From) (min shown.To line.To) add state
            else
                Search.between shown.From shown.To add state
            RangeSet.ofRanges items

        Extension.many [
            Search.extension
            Decoration.mark className ranges (fragment [||])
        ]
