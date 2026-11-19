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

    /// Appends the markup of the characters of `line` from `from` up to `to'`, the first of them standing at
    /// column `column`, and gives the column after the last. A tab is spaces to its stop, a character of ASCII is
    /// itself, and any other is in an element as wide as its columns (`Columns`).
    let columnsInto (builder: StringBuilder) (line: string) (from: int) (to': int) (column: int) (tabSize: int) : int =
        let mutable at = from
        let mutable column = column

        while at < to' do
            let length = min (Columns.clusterLength line at) (to' - at)
            let columns = Columns.columnsOf line at length column tabSize
            let first = line[at]

            if first = '\t' then
                builder.Append (' ', columns) |> ignore
            elif length = 1 && first < '\u0080' then
                escapeInto builder line at (at + 1)
            else
                builder.Append (if columns = 2 then "<span class=\"pm-c2\">" else "<span class=\"pm-c1\">") |> ignore
                escapeInto builder line at (at + length)
                builder.Append "</span>" |> ignore

            column <- column + columns
            at <- at + length

        column

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
    let lines = Dictionary<string, string> (StringComparer.Ordinal)
    let builder = StringBuilder 256
    let mutable held: Language voption = ValueNone
    let mutable heldTabSize = 4
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

    member private _.Build(language: Language voption, tabSize: int, line: string) : string =
        if line.Length = 0 then
            ""
        else
            builder.Clear () |> ignore
            let mutable at = 0
            let mutable spans = 0
            // A plain line is its text. Any other is laid on the column grid, and the column runs on across tokens.
            let plain = Columns.isPlain line
            let mutable column = 0

            match language with
            | ValueNone -> ()
            | ValueSome language ->
                for token in language.Tokenize line do
                    // A token outside the line or before the one before it is left out.
                    if token.From >= at && token.To <= line.Length && token.To > token.From then
                        if plain then
                            Html.escapeInto builder line at token.From
                        else
                            column <- Html.columnsInto builder line at token.From column tabSize

                        let mutable name = null
                        let classed = classes.TryGetValue (token.Tag.Id, &name)

                        if classed then
                            spans <- spans + 1
                            builder.Append("<span class=\"").Append(name).Append "\">" |> ignore

                        if plain then
                            Html.escapeInto builder line token.From token.To
                        else
                            column <- Html.columnsInto builder line token.From token.To column tabSize

                        if classed then
                            builder.Append "</span>" |> ignore

                        at <- token.To

            if plain then
                Html.escapeInto builder line at line.Length
            else
                Html.columnsInto builder line at line.Length column tabSize |> ignore

            // Markup of text alone ends with an empty comment: without it, rows of such lines cost the engine
            // more to scroll (`ACCEPTANCE.md`, 7.1).
            if spans = 0 then
                builder.Append "<!---->" |> ignore

            builder.ToString ()

    /// The markup of `line`: its text escaped, with a span for each token whose tag the theme gives a class, and
    /// an empty comment after a line without such a span. A line with a tab or a character outside ASCII is laid on
    /// the column grid for `tabSize`.
    member this.Markup(language: Language voption, enabled: bool, tabSize: int, line: string) : string =
        let same =
            tabSize = heldTabSize
            && (match held, language with
                | ValueSome a, ValueSome b -> obj.ReferenceEquals (a, b)
                | ValueNone, ValueNone -> true
                | _ -> false)

        if not same then
            lines.Clear ()
            held <- language
            heldTabSize <- tabSize

        if not enabled then
            misses <- misses + 1L
            this.Build (language, tabSize, line)
        else
            let mutable markup: string = null

            if lines.TryGetValue (line, &markup) then
                hits <- hits + 1L
                markup
            else
                misses <- misses + 1L

                if lines.Count >= capacity then
                    lines.Clear ()

                let made = this.Build (language, tabSize, line)
                lines[line] <- made
                made

/// What the rows are computed from beside the text, the state and the scroll position. A new value recomputes
/// every row.
[<Sealed>]
type internal RowConfig(gutters: GutterSpec[], keys: obj[], language: Language voption, tabSize: int, recycle: bool, cache: bool) =
    member _.Gutters = gutters
    /// `Key state` of each gutter.
    member _.Keys = keys
    member _.Language = language
    /// The columns between tab stops.
    member _.TabSize = tabSize
    member _.Recycle = recycle
    member _.Cache = cache
    /// A buffer for the markup of a row's gutter markers.
    member val Builder = StringBuilder 128

    /// The start of each gutter's marker element, up to the words a marker adds to its class.
    member val GutterOpen: string[] =
        gutters
        |> Array.mapi (fun index gutter ->
            String.Concat (
                "<span data-pm=\"gutter",
                (if index = gutters.Length - 1 then " gutter-last" else ""),
                (if String.IsNullOrEmpty gutter.Class then "" else " " + Html.escape gutter.Class)
            ))

    /// The rest of each gutter's opening tag: its width.
    member val GutterStyle: string[] =
        gutters
        |> Array.map (fun gutter -> String.Concat ("\" style=\"width:", gutter.Width.ToString ("0.##", CultureInfo.InvariantCulture), "px\">"))

    /// True when a configuration of these values would compute the rows this one computes.
    member _.Holds(specs: GutterSpec[], state: EditorState, tokenizer: Language voption, tabs: int, recycling: bool, caching: bool) : bool =
        let mutable same =
            recycle = recycling
            && cache = caching
            && tabSize = tabs
            && obj.ReferenceEquals (gutters, specs)
            && (match language, tokenizer with
                | ValueSome a, ValueSome b -> obj.ReferenceEquals (a, b)
                | ValueNone, ValueNone -> true
                | _ -> false)

        let mutable index = 0

        while same && index < gutters.Length do
            same <- Object.Equals (keys[index], gutters[index].Key state)
            index <- index + 1

        same

/// What a row draws.
[<CustomEquality; NoComparison>]
type internal RowContent =
    {
        /// The line shown, or -1 for a row past the end of the document.
        Line: int
        /// The distance from the sheet's top in CSS pixels.
        Top: float
        /// The markers of the gutters, as markup.
        GutterHtml: string
        /// The line, as markup.
        Html: string
        /// `GutterHtml`, as the template takes it.
        Gutter: UnsafeHtml
        /// `Html`, as the template takes it.
        Markup: UnsafeHtml
    }

    override this.Equals(other: obj) =
        match other with
        | :? RowContent as content ->
            content.Line = this.Line
            && content.Top = this.Top
            && String.Equals (content.Html, this.Html, StringComparison.Ordinal)
            && String.Equals (content.GutterHtml, this.GutterHtml, StringComparison.Ordinal)
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
        /// The number of this window among the windows of its view.
        Generation: int64
        /// The edits of the last windows, newest first: the window's number, the first and the last line changed in
        /// that window's text, and whether the lines after them moved.
        Edits: struct (int64 * int * int * bool) list
        /// The number of the newest window whose edit `Edits` no longer holds; -1 when it holds them all.
        EditsFloor: int64
        /// The update the last edit was read from; compared by reference.
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

    /// Edits a window carries for a lookup that skipped windows. A lookup further behind computes every row.
    [<Literal>]
    let MaxEdits = 8

    let private emptyHtml = UnsafeHtml.Trust ""

    let private blank (lineHeight: float) : RowContent =
        {
            Line = -1
            Top = -lineHeight
            GutterHtml = ""
            Html = ""
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

        // The rows above and below the first line shown stay on the sheet: the anchor moves before the lead or
        // the room after it is used up, whatever the line height.
        let reach =
            max 1 (min rebase (min (int (Lead / lineHeight) - overscan - 2) (int ((SheetHeight - Lead) / lineHeight) - pool - 2)))

        let anchor =
            match previous with
            | ValueSome before when abs (first - before.Anchor) <= reach -> before.Anchor
            | _ -> first

        let generation =
            match previous with
            | ValueSome before -> before.Generation + 1L
            | ValueNone -> 0L

        // The lookup compares the window it last saw with this one, and may have skipped windows in between, so
        // a window carries the edits of the windows before it.
        let struct (edits, editsFloor) =
            match previous with
            | ValueSome before when not (obj.ReferenceEquals (before.Update, update)) && update.DocChanged ->
                let dirty = [| Int32.MaxValue; -1 |]

                ChangeDesc.iterChangedRanges
                    (fun _ _ fromB toB ->
                        dirty[0] <- min dirty[0] (Text.lineAt fromB text).Number
                        dirty[1] <- max dirty[1] (Text.lineAt toB text).Number)
                    (ChangeSet.desc update.Changes)

                let edit = struct (generation, dirty[0], dirty[1], total <> before.Lines)

                if List.length before.Edits >= MaxEdits then
                    let struct (dropped, _, _, _) = List.item (MaxEdits - 1) before.Edits
                    struct (edit :: List.truncate (MaxEdits - 1) before.Edits, dropped)
                else
                    struct (edit :: before.Edits, before.EditsFloor)
            | ValueSome before -> struct (before.Edits, before.EditsFloor)
            | ValueNone -> struct ([], -1L)

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
            Generation = generation
            Edits = edits
            EditsFloor = editsFloor
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
            || before.Generation < next.EditsFloor
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

            // Lines whose text changed since the window the lookup saw, and the lines after them when lines were
            // added or removed.
            for struct (generation, from, to', shifted) in next.Edits do
                if generation > before.Generation then
                    let last =
                        if shifted then
                            next.Start + pool - 1
                        else
                            min to' (next.Start + pool - 1)

                    for line in max from next.Start .. last do
                        slots.Add (slotOf line)

            slots

    /// Appends the markers of the gutters, each in a box of its gutter's width. A theme rule lays the boxes out and
    /// puts the gap before the text after the last one.
    let private appendGutters (builder: StringBuilder) (window: RowWindow) (line: Line) =
        let gutters = window.Config.Gutters

        for index in 0 .. gutters.Length - 1 do
            let gutter = gutters[index]
            builder.Append (window.Config.GutterOpen[index]) |> ignore

            match gutter.LineMarker window.State line with
            | ValueSome marker ->
                if not (String.IsNullOrEmpty marker.Class) then
                    builder.Append(' ').Append (Html.escape marker.Class) |> ignore

                builder.Append(window.Config.GutterStyle[index]).Append (Html.escape marker.Text) |> ignore
            | ValueNone -> builder.Append (window.Config.GutterStyle[index]) |> ignore

            builder.Append "</span>" |> ignore

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

                let html = window.Markup.Markup (window.Config.Language, window.Config.Cache, window.Config.TabSize, line.Text)

                let gutter =
                    if window.Config.Gutters.Length = 0 then
                        ""
                    else
                        let builder = window.Config.Builder
                        builder.Clear () |> ignore
                        appendGutters builder window line
                        builder.ToString ()

                {
                    Line = number
                    Top = float (number - window.Anchor) * window.LineHeight + Lead
                    GutterHtml = gutter
                    Html = html
                    Gutter = (if gutter.Length = 0 then emptyHtml else UnsafeHtml.Trust gutter)
                    Markup = UnsafeHtml.Trust html
                }
