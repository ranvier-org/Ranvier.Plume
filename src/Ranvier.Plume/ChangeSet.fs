namespace Ranvier.Plume

open System

/// A replacement of [From, To) by Insert.
[<Struct>]
type ChangeSpec = { From: int; To: int; Insert: Text }

/// The side a position stays on when text is inserted at it.
type Assoc =
    | Before = -1
    | After = 1

/// When mapping a position reports the deletion of its surroundings.
type MapMode =
    /// The position always maps.
    | Simple = 0
    /// Reports a deletion across the position.
    | TrackDel = 1
    /// Reports a deletion of the character before the position.
    | TrackBefore = 2
    /// Reports a deletion of the character after the position.
    | TrackAfter = 3

/// An edit addressed by 0-based line and UTF-16 character, as the Language Server Protocol does.
type LineEdit =
    {
        StartLine: int
        StartCharacter: int
        EndLine: int
        EndCharacter: int
        NewText: string
    }

/// The shape of a change: which ranges of a document are replaced, and by how many characters.
/// <c>Sections</c> holds pairs of a length in the document and the number of characters replacing it,
/// -1 for a range left alone.
[<Struct; NoEquality; NoComparison>]
type ChangeDesc = { Sections: int[] }

/// A change to a document of one length: the shape, and for each replaced range the text inserted and the text
/// removed.
[<Sealed>]
type ChangeSet internal (sections: int[], inserted: Text[], removed: Text[]) =
    /// Pairs of a length in the document and the number of characters replacing it, -1 for a range left alone.
    member _.Sections = sections
    /// The text inserted by each replaced range, in order.
    member _.Inserted = inserted
    /// The text removed by each replaced range, in order.
    member _.Removed = removed

module internal Sections =

    let noTexts: Text[] = [||]

    let length (sections: int[]) : int =
        let mutable total = 0
        let mutable i = 0

        while i < sections.Length do
            total <- total + sections[i]
            i <- i + 2

        total

    let newLength (sections: int[]) : int =
        let mutable total = 0
        let mutable i = 0

        while i < sections.Length do
            let inserted = sections[i + 1]
            total <- total + (if inserted < 0 then sections[i] else inserted)
            i <- i + 2

        total

    let isEmpty (sections: int[]) : bool =
        sections.Length = 0
        || (sections.Length = 2 && sections[1] < 0)

    let mismatch () : exn =
        ArgumentException "The change sets apply to documents of different lengths."

    [<Sealed>]
    type Builder(texts: bool) =
        let sections = ResizeArray<int> ()
        let inserted = if texts then ResizeArray<Text> () else null
        let removed = if texts then ResizeArray<Text> () else null

        member _.SectionCount = sections.Count

        /// Adds a section, joined to the last one when both leave the text alone, both only delete or both only
        /// insert, or when `forceJoin` is set.
        member _.Add(length: int, ins: int, forceJoin: bool) =
            if not (length = 0 && ins <= 0) then
                let last = sections.Count - 2

                if last >= 0 && ins <= 0 && ins = sections[last + 1] then
                    sections[last] <- sections[last] + length
                elif last >= 0 && length = 0 && sections[last] = 0 then
                    sections[last + 1] <- sections[last + 1] + ins
                elif forceJoin && last >= 0 && sections[last + 1] >= 0 then
                    sections[last] <- sections[last] + length
                    sections[last + 1] <- sections[last + 1] + ins
                else
                    sections.Add length
                    sections.Add ins

                    if texts && ins >= 0 then
                        inserted.Add Text.empty
                        removed.Add Text.empty

        /// Appends to the text inserted by the last section.
        member _.AddInsert(text: Text) =
            if texts && text.Length > 0 then
                let last = inserted.Count - 1
                inserted[last] <- Text.append inserted[last] text

        /// Appends to the text removed by the last section.
        member _.AddRemoved(text: Text) =
            if texts && text.Length > 0 then
                let last = removed.Count - 1
                removed[last] <- Text.append removed[last] text

        member _.Sections = sections.ToArray ()

        member _.ToSet() : ChangeSet =
            ChangeSet (sections.ToArray (), inserted.ToArray (), removed.ToArray ())

    /// A cursor over sections that can be consumed in parts.
    [<Sealed>]
    type Iter(sections: int[], inserted: Text[], removed: Text[]) =
        let mutable i = 0
        let mutable change = -1
        let mutable length = 0
        let mutable ins = 0
        let mutable off = 0

        /// The index after the current section; identifies it.
        member _.I = i
        /// What is left of the current section's length in the document.
        member _.Len = length
        /// What is left of the current section's inserted length; -1 for a range left alone, -2 at the end.
        member _.Ins = ins
        /// How much of the current section is consumed.
        member _.Off = off
        member _.Done = ins = -2
        member _.Len2 = if ins < 0 then length else ins

        member _.Next() =
            if i < sections.Length then
                length <- sections[i]
                ins <- sections[i + 1]
                i <- i + 2

                if ins >= 0 then
                    change <- change + 1
            else
                length <- 0
                ins <- -2

            off <- 0

        /// The whole text the current section inserts.
        member _.Text = if ins >= 0 then inserted[change] else Text.empty
        /// The next `count` characters the current section inserts.
        member _.TextBit(count: int) = Text.slice off (off + count) inserted[change]
        /// The whole text the current section removes.
        member _.Removed = removed[change]

        member this.Forward(count: int) =
            if count = length then
                this.Next ()
            else
                length <- length - count
                off <- off + count

        member this.Forward2(count: int) =
            if ins = -1 then
                this.Forward count
            elif count = ins then
                this.Next ()
            else
                ins <- ins - count
                off <- off + count

    let iter (sections: int[]) (inserted: Text[]) (removed: Text[]) : Iter =
        let cursor = Iter (sections, inserted, removed)
        cursor.Next ()
        cursor

    /// Adds to `builder` the sections of `a` followed by `b`, where `b` applies to the result of `a`.
    let compose (texts: bool) (a: Iter) (b: Iter) (builder: Builder) =
        let mutable opened = false
        let mutable running = true

        while running do
            if a.Done && b.Done then
                running <- false
            elif a.Ins = 0 then
                builder.Add (a.Len, 0, opened)

                if texts then
                    builder.AddRemoved a.Removed

                a.Next ()
            elif b.Len = 0 && not b.Done then
                builder.Add (0, b.Ins, opened)

                if texts then
                    builder.AddInsert b.Text

                b.Next ()
            elif a.Done || b.Done then
                raise (mismatch ())
            else
                let length = min a.Len2 b.Len
                let before = builder.SectionCount

                if a.Ins = -1 then
                    let insB =
                        if b.Ins = -1 then -1
                        elif b.Off > 0 then 0
                        else b.Ins

                    builder.Add (length, insB, opened)

                    if texts && insB >= 0 then
                        if insB > 0 then
                            builder.AddInsert b.Text

                        builder.AddRemoved (Text.slice b.Off (b.Off + length) b.Removed)
                elif b.Ins = -1 then
                    builder.Add ((if a.Off > 0 then 0 else a.Len), length, opened)

                    if texts then
                        builder.AddInsert (a.TextBit length)

                        if a.Off = 0 then
                            builder.AddRemoved a.Removed
                else
                    builder.Add ((if a.Off > 0 then 0 else a.Len), (if b.Off > 0 then 0 else b.Ins), opened)

                    if texts then
                        if b.Off = 0 then
                            builder.AddInsert b.Text

                        if a.Off = 0 then
                            builder.AddRemoved a.Removed

                opened <-
                    (a.Ins > length || (b.Ins >= 0 && b.Len > length))
                    && (opened || builder.SectionCount > before)

                a.Forward2 length
                b.Forward length

    /// Adds to `builder` the sections of `a` moved over `b`: both apply to one document, and the result applies to
    /// the document `b` produces. Where both insert at one position, `a`'s text comes first when `before` is set.
    let map (texts: bool) (before: bool) (a: Iter) (b: Iter) (builder: Builder) =
        // The section of `a` whose inserted text is already in the result.
        let mutable insertedAt = -1
        let mutable running = true

        while running do
            if (a.Done && b.Len > 0) || (b.Done && a.Len > 0) then
                raise (mismatch ())
            elif a.Ins = -1 && b.Ins = -1 then
                let length = min a.Len b.Len
                builder.Add (length, -1, false)
                a.Forward length
                b.Forward length
            elif
                b.Ins >= 0
                && (a.Ins < 0
                    || insertedAt = a.I
                    || (a.Off = 0 && (b.Len < a.Len || (b.Len = a.Len && not before))))
            then
                // A change of `b` comes first: its inserted text stays, and what `a` did to its range is dropped,
                // except for text `a` inserts there.
                let mutable left = b.Len
                builder.Add (b.Ins, -1, false)

                while left > 0 do
                    if a.Done then
                        raise (mismatch ())

                    let piece = min a.Len left

                    if a.Ins >= 0 && insertedAt < a.I && a.Len <= piece then
                        builder.Add (0, a.Ins, false)

                        if texts then
                            builder.AddInsert a.Text

                        insertedAt <- a.I

                    a.Forward piece
                    left <- left - piece

                b.Next ()
            elif a.Ins >= 0 then
                // A change of `a`, up to the next change of `b` that is not a deletion inside it.
                let mutable kept = 0
                let mutable left = a.Len
                let mutable removed = Text.empty
                let mutable scanning = true

                while scanning && left > 0 do
                    if b.Ins = -1 then
                        let piece = min left b.Len

                        if texts then
                            let at = a.Off + a.Len - left
                            removed <- Text.append removed (Text.slice at (at + piece) a.Removed)

                        kept <- kept + piece
                        left <- left - piece
                        b.Forward piece
                    elif b.Ins = 0 && b.Len < left then
                        left <- left - b.Len
                        b.Next ()
                    else
                        scanning <- false

                builder.Add (kept, (if insertedAt < a.I then a.Ins else 0), false)

                if texts then
                    if insertedAt < a.I then
                        builder.AddInsert a.Text

                    builder.AddRemoved removed

                insertedAt <- a.I
                a.Forward (a.Len - left)
            elif a.Done && b.Done then
                running <- false
            else
                raise (mismatch ())

    let mapPos (assoc: Assoc) (mode: MapMode) (position: int) (sections: int[]) : int voption =
        let mutable posA = 0
        let mutable posB = 0
        let mutable i = 0
        let mutable result = ValueNone
        let mutable found = false

        while not found && i < sections.Length do
            let length = sections[i]
            let ins = sections[i + 1]
            let endA = posA + length
            i <- i + 2

            if ins < 0 then
                if endA > position then
                    result <- ValueSome (posB + (position - posA))
                    found <- true
                else
                    posB <- posB + length
            else
                if
                    mode <> MapMode.Simple
                    && endA >= position
                    && ((mode = MapMode.TrackDel && posA < position && endA > position)
                        || (mode = MapMode.TrackBefore && posA < position)
                        || (mode = MapMode.TrackAfter && endA > position))
                then
                    found <- true
                elif
                    endA > position
                    || (endA = position && assoc = Assoc.Before && length = 0)
                then
                    result <-
                        ValueSome (
                            if position = posA || assoc = Assoc.Before then
                                posB
                            else
                                posB + ins
                        )

                    found <- true
                else
                    posB <- posB + ins

            posA <- endA

        if found then
            result
        elif position > posA then
            raise (
                ArgumentOutOfRangeException (
                    "position",
                    String.Concat ("Position ", string position, " is outside a change set of length ", string posA, ".")
                )
            )
        else
            ValueSome posB

[<RequireQualifiedAccess>]
module ChangeDesc =

    /// The length of the document the change applies to.
    let length (desc: ChangeDesc) : int = Sections.length desc.Sections
    /// The length of the document the change produces.
    let newLength (desc: ChangeDesc) : int = Sections.newLength desc.Sections
    /// True when the change replaces no range.
    let isEmpty (desc: ChangeDesc) : bool = Sections.isEmpty desc.Sections

    /// The shape of the change that undoes <c>desc</c>.
    let invert (desc: ChangeDesc) : ChangeDesc =
        let sections = Array.copy desc.Sections
        let mutable i = 0

        while i < sections.Length do
            let ins = sections[i + 1]

            if ins >= 0 then
                sections[i + 1] <- sections[i]
                sections[i] <- ins

            i <- i + 2

        { Sections = sections }

    /// The shape of <c>a</c> followed by <c>b</c>, where <c>b</c> applies to the document <c>a</c> produces.
    let compose (a: ChangeDesc) (b: ChangeDesc) : ChangeDesc =
        let builder = Sections.Builder false
        Sections.compose false (Sections.iter a.Sections null null) (Sections.iter b.Sections null null) builder
        { Sections = builder.Sections }

    /// <c>desc</c> moved over <c>other</c>. Both apply to one document; the result applies to the document
    /// <c>other</c> produces. Where both insert at one position, <c>desc</c> comes first when <c>before</c> is true.
    let map (before: bool) (other: ChangeDesc) (desc: ChangeDesc) : ChangeDesc =
        let builder = Sections.Builder false
        Sections.map false before (Sections.iter desc.Sections null null) (Sections.iter other.Sections null null) builder
        { Sections = builder.Sections }

    /// Where <c>position</c> lies after the change. A position inside a replaced range maps to the range's start
    /// with <c>Assoc.Before</c> and to the end of the inserted text with <c>Assoc.After</c>.
    let mapPos (assoc: Assoc) (position: int) (desc: ChangeDesc) : int =
        (Sections.mapPos assoc MapMode.Simple position desc.Sections).Value

    /// <c>mapPos</c>, or <c>ValueNone</c> when <c>mode</c> reports a deletion.
    let mapPosMode (assoc: Assoc) (mode: MapMode) (position: int) (desc: ChangeDesc) : int voption =
        Sections.mapPos assoc mode position desc.Sections

    /// True when a replaced range touches [<c>from</c>, <c>to'</c>].
    let touchesRange (from: int) (to': int) (desc: ChangeDesc) : bool =
        let sections = desc.Sections
        let mutable position = 0
        let mutable i = 0
        let mutable touches = false

        while not touches && i < sections.Length && position <= to' do
            let finish = position + sections[i]

            if sections[i + 1] >= 0 && finish >= from then
                touches <- true

            position <- finish
            i <- i + 2

        touches

    /// Calls <c>f fromA toA fromB toB</c> for each replaced range, adjacent ranges as one: the range in the
    /// document the change applies to, and the range in the document it produces.
    let iterChangedRanges (f: int -> int -> int -> int -> unit) (desc: ChangeDesc) : unit =
        let sections = desc.Sections
        let mutable posA = 0
        let mutable posB = 0
        let mutable i = 0

        while i < sections.Length do
            let length = sections[i]
            let ins = sections[i + 1]
            i <- i + 2

            if ins < 0 then
                posA <- posA + length
                posB <- posB + length
            else
                let mutable endA = posA + length
                let mutable endB = posB + ins

                while i < sections.Length && sections[i + 1] >= 0 do
                    endA <- endA + sections[i]
                    endB <- endB + sections[i + 1]
                    i <- i + 2

                f posA endA posB endB
                posA <- endA
                posB <- endB

[<RequireQualifiedAccess>]
module ChangeSet =

    /// The change that leaves a document of <c>length</c> characters as it is.
    let empty (length: int) : ChangeSet =
        ChangeSet ((if length = 0 then [||] else [| length; -1 |]), Sections.noTexts, Sections.noTexts)

    let desc (changes: ChangeSet) : ChangeDesc = { Sections = changes.Sections }
    /// The length of the document the change applies to.
    let length (changes: ChangeSet) : int = Sections.length changes.Sections
    /// The length of the document the change produces.
    let newLength (changes: ChangeSet) : int = Sections.newLength changes.Sections
    /// True when the change replaces no range.
    let isEmpty (changes: ChangeSet) : bool = Sections.isEmpty changes.Sections

    /// The change <c>specs</c> make to <c>doc</c>. Specs address <c>doc</c> and may come in any order; specs at one
    /// position apply in list order.
    /// <exception cref="T:System.ArgumentException">A spec lies outside <c>doc</c>, ends before it starts, or overlaps
    /// another.</exception>
    let ofSpecs (doc: Text) (specs: ChangeSpec list) : ChangeSet =
        let total = doc.Length
        let builder = Sections.Builder true
        let mutable position = 0

        for spec in List.sortBy (fun (spec: ChangeSpec) -> spec.From) specs do
            if spec.From < 0 || spec.To > total || spec.From > spec.To then
                raise (
                    ArgumentException (
                        String.Concat (
                            "The change [",
                            string spec.From,
                            ", ",
                            string spec.To,
                            ") lies outside a document of length ",
                            string total,
                            "."
                        )
                    )
                )

            if spec.From < position then
                raise (ArgumentException (String.Concat ("The change at ", string spec.From, " overlaps the change before it.")))

            if spec.To > spec.From || spec.Insert.Length > 0 then
                builder.Add (spec.From - position, -1, false)
                builder.Add (spec.To - spec.From, spec.Insert.Length, false)
                builder.AddInsert spec.Insert
                builder.AddRemoved (Text.slice spec.From spec.To doc)
                position <- spec.To

        builder.Add (total - position, -1, false)
        builder.ToSet ()

    /// <c>doc</c> with the change applied.
    /// <exception cref="T:System.ArgumentException"><c>doc</c> has another length than the change applies to.</exception>
    let apply (doc: Text) (changes: ChangeSet) : Text =
        let sections = changes.Sections

        if doc.Length <> Sections.length sections then
            raise (
                ArgumentException (
                    String.Concat (
                        "A change set for a document of length ",
                        string (Sections.length sections),
                        " was applied to one of length ",
                        string doc.Length,
                        "."
                    )
                )
            )

        // Back to front, so the positions of the ranges still to replace stay valid.
        let mutable text = doc
        let mutable position = doc.Length
        let mutable change = changes.Inserted.Length - 1
        let mutable i = sections.Length - 2

        while i >= 0 do
            let length = sections[i]
            position <- position - length

            if sections[i + 1] >= 0 then
                text <- Text.replace position (position + length) changes.Inserted[change] text
                change <- change - 1

            i <- i - 2

        text

    /// The change that undoes <c>changes</c>.
    let invert (changes: ChangeSet) : ChangeSet =
        ChangeSet ((ChangeDesc.invert { Sections = changes.Sections }).Sections, changes.Removed, changes.Inserted)

    /// <c>a</c> followed by <c>b</c>, where <c>b</c> applies to the document <c>a</c> produces.
    let compose (a: ChangeSet) (b: ChangeSet) : ChangeSet =
        let builder = Sections.Builder true
        Sections.compose true (Sections.iter a.Sections a.Inserted a.Removed) (Sections.iter b.Sections b.Inserted b.Removed) builder
        builder.ToSet ()

    /// <c>changes</c> moved over <c>other</c>. Both apply to one document; the result applies to the document
    /// <c>other</c> produces. Where both insert at one position, <c>changes</c> comes first when <c>before</c> is true.
    let map (before: bool) (other: ChangeDesc) (changes: ChangeSet) : ChangeSet =
        let builder = Sections.Builder true
        Sections.map true before (Sections.iter changes.Sections changes.Inserted changes.Removed) (Sections.iter other.Sections null null) builder
        builder.ToSet ()

    /// Where <c>position</c> lies after the change.
    let mapPos (assoc: Assoc) (position: int) (changes: ChangeSet) : int =
        (Sections.mapPos assoc MapMode.Simple position changes.Sections).Value

    /// Calls <c>f fromA toA fromB toB inserted</c> for each replaced range, adjacent ranges as one.
    let iterChanges (f: int -> int -> int -> int -> Text -> unit) (changes: ChangeSet) : unit =
        let sections = changes.Sections
        let mutable posA = 0
        let mutable posB = 0
        let mutable change = 0
        let mutable i = 0

        while i < sections.Length do
            let length = sections[i]
            let ins = sections[i + 1]
            i <- i + 2

            if ins < 0 then
                posA <- posA + length
                posB <- posB + length
            else
                let mutable endA = posA + length
                let mutable endB = posB + ins
                let mutable text = changes.Inserted[change]
                change <- change + 1

                while i < sections.Length && sections[i + 1] >= 0 do
                    endA <- endA + sections[i]
                    endB <- endB + sections[i + 1]
                    text <- Text.append text changes.Inserted[change]
                    change <- change + 1
                    i <- i + 2

                f posA endA posB endB text
                posA <- endA
                posB <- endB

    /// The change <c>edits</c> make to <c>doc</c>.
    let ofLineEdits (doc: Text) (edits: LineEdit list) : ChangeSet =
        edits
        |> List.map (fun edit ->
            {
                From = Text.offsetOf edit.StartLine edit.StartCharacter doc
                To = Text.offsetOf edit.EndLine edit.EndCharacter doc
                Insert = Text.ofString edit.NewText
            })
        |> ofSpecs doc

    /// The change as edits addressing <c>doc</c>, the document it applies to.
    let toLineEdits (doc: Text) (changes: ChangeSet) : LineEdit list =
        let edits = ResizeArray<LineEdit> ()

        changes
        |> iterChanges (fun fromA toA _ _ inserted ->
            let struct (startLine, startCharacter) = Text.positionOf fromA doc
            let struct (endLine, endCharacter) = Text.positionOf toA doc

            edits.Add
                {
                    StartLine = startLine
                    StartCharacter = startCharacter
                    EndLine = endLine
                    EndCharacter = endCharacter
                    NewText = Text.toString inserted
                })

        List.ofSeq edits
