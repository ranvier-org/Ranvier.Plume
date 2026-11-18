namespace Ranvier.Plume.View

open System
open System.Collections.Generic
open System.Globalization
open System.Text
open Ranvier
open Ranvier.View.Dsl
open Ranvier.Plume

/// The facets of an editor that its views read.
[<RequireQualifiedAccess>]
module ViewFacets =

    let private first (fallback: 'T) : 'T[] -> 'T =
        fun values -> if values.Length > 0 then values[0] else fallback

    /// True when a line keeps its row element for as long as it stays in the row pool. Default true.
    let recycleRows: Facet<bool, bool> = Facet.define (first true)
    /// True when the sheet is promoted to a compositor layer. Default false.
    let compositorLayer: Facet<bool, bool> = Facet.define (first false)
    /// True when the markup of a line is kept by the line's text. Default true.
    let markupCache: Facet<bool, bool> = Facet.define (first true)
    /// True when the gutter cell of the main cursor's line is marked active. Default false.
    let activeLineGutter: Facet<bool, bool> = Facet.define (Array.exists id)
    /// The shape of the cursor. Default a bar.
    let cursorShape: Facet<CursorShape, CursorShape> = Facet.define (first Bar)
    /// An effect that scrolls a position into view in the views of the editor that dispatches it.
    let scrollIntoView: StateEffectType<int> =
        StateEffect.define (fun position changes -> ValueSome (ChangeDesc.mapPos Assoc.After position changes))

module internal Html =

    let escapeInto (builder: StringBuilder) (text: string) (from: int) (to': int) =
        let mutable start = from

        for index in from .. to' - 1 do
            let replacement =
                match text[index] with
                | '&' -> "&amp;"
                | '<' -> "&lt;"
                | '>' -> "&gt;"
                | '"' -> "&quot;"
                | _ -> null

            if not (isNull replacement) then
                builder.Append(text, start, index - start).Append replacement |> ignore
                start <- index + 1

        builder.Append (text, start, to' - start) |> ignore

    let escape (text: string) : string =
        if text.AsSpan().IndexOfAny ("&<>\"".AsSpan ()) < 0 then
            text
        else
            let builder = StringBuilder (text.Length + 8)
            escapeInto builder text 0 text.Length
            builder.ToString ()

/// The markup of lines, kept by line text. When the cache holds `capacity` lines it starts again empty.
[<Sealed>]
type internal MarkupCache(theme: Theme, capacity: int) =
    let classes = Dictionary<int, string> ()
    let lines = Dictionary<string, UnsafeHtml> (StringComparer.Ordinal)
    let builder = StringBuilder 256
    let mutable held: Language voption = ValueNone
    let mutable hits = 0L
    let mutable misses = 0L

    do
        for tag, name, _ in theme.Tokens do
            classes[tag.Id] <- name

    member _.Hits = hits
    member _.Misses = misses

    member _.Reset() =
        lines.Clear ()
        hits <- 0L
        misses <- 0L

    member private _.Build(language: Language voption, line: string) : UnsafeHtml =
        match language with
        | ValueNone -> UnsafeHtml.Trust (Html.escape line)
        | ValueSome language ->
            builder.Clear () |> ignore
            let mutable at = 0

            for token in language.Tokenize line do
                // A token outside the line or before the one before it is left out.
                if token.From >= at && token.To <= line.Length && token.To > token.From then
                    Html.escapeInto builder line at token.From
                    let mutable name = null

                    if classes.TryGetValue (token.Tag.Id, &name) then
                        builder.Append("<span class=\"").Append(name).Append "\">" |> ignore
                        Html.escapeInto builder line token.From token.To
                        builder.Append "</span>" |> ignore
                    else
                        Html.escapeInto builder line token.From token.To

                    at <- token.To

            Html.escapeInto builder line at line.Length
            UnsafeHtml.Trust (builder.ToString ())

    /// The markup of `line`: its text escaped, with a span for each token whose tag the theme gives a class.
    member this.Markup(language: Language voption, enabled: bool, line: string) : UnsafeHtml =
        let same =
            match held, language with
            | ValueSome a, ValueSome b -> obj.ReferenceEquals (a, b)
            | ValueNone, ValueNone -> true
            | _ -> false

        if not same then
            lines.Clear ()
            held <- language

        if not enabled then
            misses <- misses + 1L
            this.Build (language, line)
        else
            let mutable markup = Unchecked.defaultof<UnsafeHtml>

            if lines.TryGetValue (line, &markup) then
                hits <- hits + 1L
                markup
            else
                misses <- misses + 1L

                if lines.Count >= capacity then
                    lines.Clear ()

                let made = this.Build (language, line)
                lines[line] <- made
                made

/// What the rows are computed from beside the text, the state and the scroll position. A new value recomputes
/// every row.
[<Sealed>]
type internal RowConfig(gutters: GutterSpec[], keys: obj[], language: Language voption, recycle: bool, cache: bool) =
    member _.Gutters = gutters
    /// `Key state` of each gutter.
    member _.Keys = keys
    member _.Language = language
    member _.Recycle = recycle
    member _.Cache = cache

    member _.Same(other: RowConfig) : bool =
        recycle = other.Recycle
        && cache = other.Cache
        && obj.ReferenceEquals (gutters, other.Gutters)
        && Array.forall2 (fun (a: obj) (b: obj) -> Object.Equals (a, b)) keys other.Keys
        && (match language, other.Language with
            | ValueSome a, ValueSome b -> obj.ReferenceEquals (a, b)
            | ValueNone, ValueNone -> true
            | _ -> false)

/// What a row draws.
[<CustomEquality; NoComparison>]
type internal RowContent =
    {
        /// The line shown, or -1 for a row past the end of the document.
        Line: int
        /// The distance from the sheet's top in CSS pixels.
        Top: float
        Gutter: UnsafeHtml
        Markup: UnsafeHtml
    }

    override this.Equals(other: obj) =
        match other with
        | :? RowContent as content ->
            content.Line = this.Line
            && content.Top = this.Top
            && obj.ReferenceEquals (content.Markup, this.Markup)
            && obj.ReferenceEquals (content.Gutter, this.Gutter)
        | _ -> false

    override this.GetHashCode() = this.Line

/// The rows a view shows: the source of the row lookup.
[<ReferenceEquality>]
type internal RowWindow =
    {
        Text: Text
        State: EditorState
        Config: RowConfig
        Lines: int
        /// The scroll position the rows are laid out for, in whole CSS pixels.
        Top: float
        /// The first line shown.
        First: int
        /// The first line of the row pool.
        Start: int
        Pool: int
        /// The line at the sheet's lead.
        Anchor: int
        LineHeight: float
        /// The lines of `Text` changed since the window before, or -1.
        DirtyFrom: int
        DirtyTo: int
        /// True when the lines from `DirtyFrom` on moved.
        Shifted: bool
        /// The update the dirty lines were read from; compared by reference.
        Update: obj
        Markup: MarkupCache
        Written: int64 ref
    }

module internal Rows =

    /// Where the anchor line lies on the sheet, in CSS pixels from its top. The viewport's scroll offset is this
    /// plus the distance scrolled from the anchor, so it stays positive on both sides of it.
    [<Literal>]
    let Lead = 16000.0

    /// The height of the sheet: the lead on both sides of the anchor, and room for the row pool.
    [<Literal>]
    let SheetHeight = 36000.0

    let private emptyHtml = UnsafeHtml.Trust ""

    let private blank (lineHeight: float) : RowContent =
        {
            Line = -1
            Top = -lineHeight
            Gutter = emptyHtml
            Markup = emptyHtml
        }

    /// The scroll range of a document of `lines` lines in a viewport of `height`.
    let maxScroll (lines: int) (height: float) (lineHeight: float) : float =
        max 0.0 (float lines * lineHeight - height + 2.0 * lineHeight)

    let compute
        (previous: RowWindow voption)
        (update: Update)
        (config: RowConfig)
        (scroll: float)
        (height: float)
        (lineHeight: float)
        (overscan: int)
        (rebase: int)
        (markup: MarkupCache)
        (written: int64 ref)
        : RowWindow =
        let text = update.State.Doc
        let total = text.Lines

        let pool =
            if height <= 0.0 then
                0
            else
                int (Math.Ceiling (height / lineHeight)) + 1 + 2 * overscan

        // Whole pixels keep the glyphs on the pixel grid.
        let top = Math.Round (Math.Clamp (scroll, 0.0, maxScroll total height lineHeight))
        let first = int (top / lineHeight)
        let start = max 0 (min (first - overscan) (total - pool))

        let anchor =
            match previous with
            | ValueSome before when abs (first - before.Anchor) <= rebase -> before.Anchor
            | _ -> first

        let dirty = [| -1; -1 |]
        let mutable shifted = false

        match previous with
        | ValueSome before when not (obj.ReferenceEquals (before.Update, update)) && update.DocChanged ->
            dirty[0] <- Int32.MaxValue

            ChangeDesc.iterChangedRanges
                (fun _ _ fromB toB ->
                    dirty[0] <- min dirty[0] (Text.lineAt fromB text).Number
                    dirty[1] <- max dirty[1] (Text.lineAt toB text).Number)
                (ChangeSet.desc update.Changes)

            shifted <- total <> before.Lines
        | _ -> ()

        {
            Text = text
            State = update.State
            Config = config
            Lines = total
            Top = top
            First = first
            Start = start
            Pool = pool
            Anchor = anchor
            LineHeight = lineHeight
            DirtyFrom = dirty[0]
            DirtyTo = dirty[1]
            Shifted = shifted
            Update = update
            Markup = markup
            Written = written
        }

    /// The slots whose content can differ between two windows.
    let affected (before: RowWindow) (next: RowWindow) : seq<int> =
        let pool = next.Pool

        if pool = 0 then
            Seq.empty
        elif
            before.Pool <> pool
            || before.Anchor <> next.Anchor
            || before.LineHeight <> next.LineHeight
            || not (obj.ReferenceEquals (before.Config, next.Config))
            || abs (next.Start - before.Start) >= pool
            || (not next.Config.Recycle && next.Start <> before.Start)
        then
            Seq.init pool id
        else
            let slots = ResizeArray<int> ()
            let recycle = next.Config.Recycle
            let inline slotOf (line: int) = if recycle then line % pool else line - next.Start

            // Lines that entered the pool.
            if next.Start > before.Start then
                for line in before.Start + pool .. next.Start + pool - 1 do
                    slots.Add (slotOf line)
            elif next.Start < before.Start then
                for line in next.Start .. before.Start - 1 do
                    slots.Add (slotOf line)

            // Lines whose text changed, and the lines after them when lines were added or removed.
            if next.DirtyFrom >= 0 then
                let last =
                    if next.Shifted then
                        next.Start + pool - 1
                    else
                        min next.DirtyTo (next.Start + pool - 1)

                for line in max next.DirtyFrom next.Start .. last do
                    slots.Add (slotOf line)

            slots

    let private gutterMarkup (window: RowWindow) (line: Line) : UnsafeHtml =
        let gutters = window.Config.Gutters

        if gutters.Length = 0 then
            emptyHtml
        elif gutters.Length = 1 then
            // One gutter fills the cell: its text needs no element unless it has a class to select by.
            match gutters[0].LineMarker window.State line with
            | ValueSome marker when String.IsNullOrEmpty marker.Class -> UnsafeHtml.Trust (Html.escape marker.Text)
            | ValueSome marker ->
                UnsafeHtml.Trust (
                    String.Concat ("<span data-pm=\"", Html.escape gutters[0].Class, " ", Html.escape marker.Class, "\">", Html.escape marker.Text, "</span>")
                )
            | ValueNone -> emptyHtml
        else
            let builder = StringBuilder 128

            for gutter in gutters do
                let width = gutter.Width.ToString ("0.##", CultureInfo.InvariantCulture)

                builder
                    .Append("<span data-pm=\"")
                    .Append(Html.escape gutter.Class)
                |> ignore

                match gutter.LineMarker window.State line with
                | ValueSome marker ->
                    builder
                        .Append(' ')
                        .Append(Html.escape marker.Class)
                        .Append("\" style=\"display:inline-block;width:")
                        .Append(width)
                        .Append("px\">")
                        .Append(Html.escape marker.Text)
                    |> ignore
                | ValueNone ->
                    builder
                        .Append("\" style=\"display:inline-block;width:")
                        .Append(width)
                        .Append "px\">"
                    |> ignore

                builder.Append "</span>" |> ignore

            UnsafeHtml.Trust (builder.ToString ())

    /// The content of a slot.
    let content (window: RowWindow) (slot: int) : RowContent =
        if slot >= window.Pool then
            blank window.LineHeight
        else
            let pool = window.Pool

            let number =
                if window.Config.Recycle then
                    // The line of the pool whose number is the slot modulo the pool size.
                    window.Start + ((slot - window.Start % pool) + pool) % pool
                else
                    window.Start + slot

            if number >= window.Lines then
                blank window.LineHeight
            else
                window.Written.Value <- window.Written.Value + 1L
                let line = Text.line number window.Text

                {
                    Line = number
                    Top = float (number - window.Anchor) * window.LineHeight + Lead
                    Gutter = gutterMarkup window line
                    Markup = window.Markup.Markup (window.Config.Language, window.Config.Cache, line.Text)
                }
