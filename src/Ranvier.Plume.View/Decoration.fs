namespace Ranvier.Plume.View

open Ranvier
open Ranvier.Plume
open Ranvier.View
open Ranvier.View.Dsl

type internal DecorationOverlay =
    { Id: int
      Body: Component<EditorView> }

/// Document-anchored marks. Ranges use half-open UTF-16 offsets.
[<RequireQualifiedAccess>]
module Decoration =
    let internal overlays: Facet<DecorationOverlay, DecorationOverlay[]> = Facet.define id
    let mutable private nextId = 0

    /// Carries ranges across disjoint edits and removes a mark when its text is edited.
    /// Insertions at the start or end are outside the range.
    let mapUntouched (changes: ChangeDesc) (ranges: RangeSet<'T>) =
        if ranges.Count = 0 || ChangeDesc.isEmpty changes then ranges
        else
            let keep left right _ =
                let mutable intact = true
                ChangeDesc.iterChangedRanges (fun a b _ _ ->
                    if not (InputGeometry.untouched left right a b) then intact <- false) changes
                intact
            ranges |> RangeSet.update [] keep |> RangeSet.map changes

    type private Segment<'T> =
        { From: int
          Until: int
          Occurrence: int
          Line: int
          Rect: Rect
          Payload: 'T }

    let private visible (ranges: RangeSet<'T>) (view: EditorView) =
        let shown = view.Viewport.Value
        let doc = view.Editor.Text.Value
        let segments = ResizeArray<Segment<'T>>()
        let mutable previous = struct (-1, -1)
        let mutable occurrence = 0
        RangeSet.between shown.From shown.To (fun first last payload ->
            let bounds = struct (first, last)
            occurrence <- if bounds = previous then occurrence + 1 else 0
            previous <- bounds
            let left, right = max first shown.From, min last shown.To
            if left < right then
                let finalLine = (Text.lineAt (right - 1) doc).Number
                for n in (Text.lineAt left doc).Number .. finalLine do
                    let line = Text.line n doc
                    let struct (a, b) = InputGeometry.segment left right line.From line.To
                    if a < b then
                        let start, finish = view.CoordsAtPos a, view.CoordsAtPos b
                        segments.Add {
                            From = first; Until = last; Line = n; Occurrence = occurrence
                            Rect = { start with Width = finish.Left - start.Left }
                            Payload = payload
                        }) ranges
        segments.ToArray()

    let private positioned kind (className: string) (ranges: EditorView -> RangeSet<'T>) (template: TemplateView<unit -> 'T>) : Extension =
        let body = Component.template (fun _ (read: unit -> Segment<'T>) -> fun () -> (read()).Payload) template
        let segment =
            span (
                span.attribute ("data-pm", "decoration"),
                span.attribute ("data-decoration", (kind: string)),
                span.attribute ("class", className),
                span.style (fun (read: unit -> Segment<'T>) ->
                    let r = (read()).Rect
                    System.String.Concat("left:", Px.text r.Left, ";top:", Px.text r.Top, ";width:", Px.text r.Width, ";height:", Px.text r.Height))
            ) {
                render body id
            }
        let content =
            forEach (fun s -> struct (s.From, s.Until, s.Line, s.Occurrence))
                (fun view -> visible (ranges view) view) segment
        overlays.Of {
            Id = System.Threading.Interlocked.Increment &nextId
            Body = Component.template (fun _ view -> view) content
        }

    /// Places a typed template over each visible line segment of a range.
    let mark className ranges template = positioned "mark" className ranges template

    /// Underlines each visible segment and shows its typed template on pointer hover.
    let underline className ranges (hover: TemplateView<unit -> 'T>) =
        let tooltip =
            span (span.attribute ("role", "tooltip"), span.attribute ("data-pm", "decoration-hover")) {
                hover
            }
        positioned "underline" className ranges tooltip
