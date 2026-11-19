/// What the window keeps for its whole life.
module EditorShowcase.Model

open Ranvier.View.WebCore.Sdl
open Ranvier.Plume
open EditorShowcase.Sample

type Diagnostic = { Code: string; Message: string }

// The sample flags the first unannotated gravitational parameter. Edits touching
// the reported text invalidate it. Unrelated edits move its anchor.
let diagnostics =
    StateField.define {
        Create = fun state ->
            [ for number in 0 .. min 31 (state.Doc.Lines - 1) do
                  let line = Text.line number state.Doc
                  let at = line.Text.IndexOf("Mu: float", System.StringComparison.Ordinal)
                  if at >= 0 then
                      yield line.From + at, line.From + at + 9,
                            { Code = "UNIT"; Message = "Give the gravitational parameter a unit of measure." } ]
            |> RangeSet.ofRanges
        Update = fun ranges tx ->
            if tx.DocChanged then Ranvier.Plume.View.Decoration.mapUntouched (ChangeSet.desc tx.Changes) ranges else ranges
    }

[<Literal>]
let Title = "Editor showcase (Ranvier.Plume)"

[<Literal>]
let WindowW = 1280

[<Literal>]
let WindowH = 800

/// What the window keeps for its whole life, so a hot reload returns to the same file, text, place and settings.
type Session() =
    let files = files ()
    member _.Files: SampleFile[] = files
    /// The text of each file once it was opened; null before.
    member val Texts: Text[] = Array.zeroCreate files.Length with get
    /// The distance each file was scrolled when its view was last unmounted, in CSS pixels.
    member val Scrolls: float[] = Array.zeroCreate files.Length with get
    member val Active = 0 with get, set
    member val MountedView: Ranvier.Plume.View.EditorView voption = ValueNone with get, set
    /// True while the view scrolls by itself.
    member val Tour = true with get, set
    /// True when a line keeps its row element while it stays in the pool.
    member val Recycle = true with get, set
    /// True when the markup of a line is kept by the line's text.
    member val Cached = true with get, set
    member val Layer = true with get, set
    /// False when a scripted run advances the frames itself.
    member val Auto = true with get, set
    /// The mounted view's state, for scripted runs.
    member val State: obj = null with get, set
    /// The constructed stylesheet the document adopts (a `CSSStyleSheet`); null until the view mounts.
    member val Stylesheet: obj = null with get, set

type Props = { Window: Window; Session: Session }
