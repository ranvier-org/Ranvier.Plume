namespace Ranvier.Plume.View

open System
open Ranvier.Keyboard

/// The column grid of a view. A column is one character advance wide, and every character takes a whole number of
/// columns: a tab reaches to the next multiple of the tab size, a wide character takes two, and a letter with its
/// combining marks is one character. The markup of a line draws it by the same rule (`Html.columnsInto`), so the
/// place of a position is a multiplication.
module internal Columns =

    /// True when every UTF-16 unit of the line is a character of one column: printable ASCII and nothing else.
    let isPlain (line: string) : bool =
        line.AsSpan().IndexOfAnyExceptInRange (' ', '~') < 0

    // The characters drawn two columns wide, as ranges of code points: East Asian wide and fullwidth characters,
    // and the blocks of emoji.
    //FOR-REVIEW Written from general knowledge of Unicode's East Asian Width property, not checked against its data
    //FOR-REVIEW file. A wide character missing here is drawn in a box of one column and reaches into the next.
    let private wide: struct (int * int)[] =
        [|
            struct (0x1100, 0x115F)
            struct (0x2E80, 0x303E)
            struct (0x3041, 0x33FF)
            struct (0x3400, 0x4DBF)
            struct (0x4E00, 0x9FFF)
            struct (0xA000, 0xA4CF)
            struct (0xA960, 0xA97F)
            struct (0xAC00, 0xD7A3)
            struct (0xF900, 0xFAFF)
            struct (0xFE10, 0xFE19)
            struct (0xFE30, 0xFE6F)
            struct (0xFF00, 0xFF60)
            struct (0xFFE0, 0xFFE6)
            struct (0x1F300, 0x1F64F)
            struct (0x1F680, 0x1F6FF)
            struct (0x1F900, 0x1F9FF)
            struct (0x1FA70, 0x1FAFF)
            struct (0x20000, 0x3FFFD)
        |]

    let private isWide (codePoint: int) : bool =
        let mutable found = false
        let mutable index = 0

        while not found && index < wide.Length do
            let struct (first, last) = wide[index]
            found <- codePoint >= first && codePoint <= last
            index <- index + 1

        found

    /// The length in UTF-16 units of the character that starts at `at`: a grapheme cluster.
    let clusterLength (line: string) (at: int) : int =
        // A unit of ASCII that no combining mark follows is a character by itself.
        if line[at] < '\u0080' && (at + 1 = line.Length || line[at + 1] < '̀') then
            1
        else
            max 1 (Grapheme.nextClusterLength line at)

    /// The columns of the character of `length` units at `at`, when it starts at column `column`.
    let columnsOf (line: string) (at: int) (length: int) (column: int) (tabSize: int) : int =
        let first = line[at]

        if first = '\t' then
            let size = max 1 tabSize
            size - column % size
        elif first < '\u0080' then
            1
        elif Char.IsHighSurrogate first && length > 1 && Char.IsLowSurrogate line[at + 1] then
            if isWide (Char.ConvertToUtf32 (first, line[at + 1])) then 2 else 1
        elif isWide (int first) then
            2
        else
            1

    /// The column where the character holding unit `offset` of `line` starts; the line's width for an offset at
    /// or past its end.
    let columnOf (line: string) (tabSize: int) (offset: int) : int =
        if isPlain line then
            min offset line.Length
        else
            let mutable at = 0
            let mutable column = 0
            let mutable looking = true

            while looking && at < line.Length do
                let length = clusterLength line at

                if offset < at + length then
                    looking <- false
                else
                    column <- column + columnsOf line at length column tabSize
                    at <- at + length

            column

    /// The columns the character at `offset` takes; one at or past the line's end.
    let widthAt (line: string) (tabSize: int) (offset: int) : int =
        if offset >= line.Length || isPlain line then
            1
        else
            let mutable at = 0
            let mutable column = 0
            let mutable width = 1
            let mutable looking = true

            while looking && at < line.Length do
                let length = clusterLength line at
                let columns = columnsOf line at length column tabSize

                if offset < at + length then
                    width <- columns
                    looking <- false
                else
                    column <- column + columns
                    at <- at + length

            width

    /// The offset of the character boundary nearest to `column` columns from the line's start.
    let offsetAt (line: string) (tabSize: int) (column: float) : int =
        if isPlain line then
            Math.Clamp (int (Math.Round column), 0, line.Length)
        else
            let mutable at = 0
            let mutable start = 0
            let mutable looking = true

            while looking && at < line.Length do
                let length = clusterLength line at
                let columns = columnsOf line at length start tabSize

                // Left of the character's middle, the boundary before it is the nearer one.
                if column < float start + float columns / 2.0 then
                    looking <- false
                else
                    start <- start + columns
                    at <- at + length

            at
