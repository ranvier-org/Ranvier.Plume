namespace Ranvier.Plume

open System
open Ranvier.Keyboard

/// The column grid of a line. A column is one character advance wide, and every character takes a whole number of
/// columns: a tab reaches to the next multiple of the tab size, a wide character takes two, and a letter with its
/// combining marks is one character. Moving up and down a line keeps a column of this grid, and a view draws a
/// line by it, so the place of a position is a multiplication.
[<RequireQualifiedAccess>]
module Columns =

    /// True when every UTF-16 unit of the line is a character of one column: printable ASCII and nothing else.
    let isPlain (line: string) : bool =
#if FABLE_COMPILER
        let mutable plain = true
        let mutable index = 0

        while plain && index < line.Length do
            plain <- line[index] >= ' ' && line[index] <= '~'
            index <- index + 1

        plain
#else
        line.AsSpan().IndexOfAnyExceptInRange (' ', '~') < 0
#endif

    // The characters two columns wide, as ranges of code points in order: the classes W (wide) and F (fullwidth)
    // of Unicode's East Asian Width property, with the defaults the data file gives the code points it does not
    // list. Derived from EastAsianWidth-18.0.0.txt of 2026-06-29.
    // `dotnet fsi --exec build.fsx -- widths <the data file>` compares this table with a data file, and prints the
    // table to put here when they differ.
    let private wide: struct (int * int)[] =
        [|
            struct (0x1100, 0x115F)
            struct (0x231A, 0x231B)
            struct (0x2329, 0x232A)
            struct (0x23E9, 0x23EC)
            struct (0x23F0, 0x23F0)
            struct (0x23F3, 0x23F3)
            struct (0x25FD, 0x25FE)
            struct (0x2614, 0x2615)
            struct (0x2630, 0x2637)
            struct (0x2648, 0x2653)
            struct (0x267F, 0x267F)
            struct (0x268A, 0x268F)
            struct (0x2693, 0x2693)
            struct (0x26A1, 0x26A1)
            struct (0x26AA, 0x26AB)
            struct (0x26BD, 0x26BE)
            struct (0x26C4, 0x26C5)
            struct (0x26CE, 0x26CE)
            struct (0x26D4, 0x26D4)
            struct (0x26EA, 0x26EA)
            struct (0x26F2, 0x26F3)
            struct (0x26F5, 0x26F5)
            struct (0x26FA, 0x26FA)
            struct (0x26FD, 0x26FD)
            struct (0x2705, 0x2705)
            struct (0x270A, 0x270B)
            struct (0x2728, 0x2728)
            struct (0x274C, 0x274C)
            struct (0x274E, 0x274E)
            struct (0x2753, 0x2755)
            struct (0x2757, 0x2757)
            struct (0x2795, 0x2797)
            struct (0x27B0, 0x27B0)
            struct (0x27BF, 0x27BF)
            struct (0x2B1B, 0x2B1C)
            struct (0x2B50, 0x2B50)
            struct (0x2B55, 0x2B55)
            struct (0x2E80, 0x2E99)
            struct (0x2E9B, 0x2EF3)
            struct (0x2F00, 0x2FD5)
            struct (0x2FF0, 0x303E)
            struct (0x3041, 0x3096)
            struct (0x3099, 0x30FF)
            struct (0x3105, 0x312F)
            struct (0x3131, 0x318E)
            struct (0x3190, 0x31E5)
            struct (0x31EF, 0x321E)
            struct (0x3220, 0x3247)
            struct (0x3250, 0xA48C)
            struct (0xA490, 0xA4C6)
            struct (0xA960, 0xA97C)
            struct (0xAC00, 0xD7A3)
            struct (0xF900, 0xFAFF)
            struct (0xFE10, 0xFE19)
            struct (0xFE30, 0xFE52)
            struct (0xFE54, 0xFE66)
            struct (0xFE68, 0xFE6B)
            struct (0xFF01, 0xFF60)
            struct (0xFFE0, 0xFFE6)
            struct (0x16FE0, 0x16FE4)
            struct (0x16FF0, 0x16FF6)
            struct (0x17000, 0x18CDA)
            struct (0x18CFF, 0x18D20)
            struct (0x18D80, 0x18DF2)
            struct (0x18E00, 0x19191)
            struct (0x191A0, 0x191D2)
            struct (0x1AFF0, 0x1AFF3)
            struct (0x1AFF5, 0x1AFFB)
            struct (0x1AFFD, 0x1AFFE)
            struct (0x1B000, 0x1B128)
            struct (0x1B132, 0x1B132)
            struct (0x1B150, 0x1B152)
            struct (0x1B155, 0x1B155)
            struct (0x1B164, 0x1B168)
            struct (0x1B170, 0x1B2FB)
            struct (0x1D300, 0x1D356)
            struct (0x1D360, 0x1D376)
            struct (0x1F004, 0x1F004)
            struct (0x1F0CF, 0x1F0CF)
            struct (0x1F18E, 0x1F18E)
            struct (0x1F191, 0x1F19A)
            struct (0x1F1AE, 0x1F1AE)
            struct (0x1F200, 0x1F202)
            struct (0x1F210, 0x1F23B)
            struct (0x1F240, 0x1F248)
            struct (0x1F250, 0x1F251)
            struct (0x1F260, 0x1F265)
            struct (0x1F300, 0x1F320)
            struct (0x1F32D, 0x1F335)
            struct (0x1F337, 0x1F37C)
            struct (0x1F37E, 0x1F393)
            struct (0x1F3A0, 0x1F3CA)
            struct (0x1F3CF, 0x1F3D3)
            struct (0x1F3E0, 0x1F3F0)
            struct (0x1F3F4, 0x1F3F4)
            struct (0x1F3F8, 0x1F43E)
            struct (0x1F440, 0x1F440)
            struct (0x1F442, 0x1F4FC)
            struct (0x1F4FF, 0x1F53D)
            struct (0x1F54B, 0x1F54E)
            struct (0x1F550, 0x1F567)
            struct (0x1F57A, 0x1F57A)
            struct (0x1F595, 0x1F596)
            struct (0x1F5A4, 0x1F5A4)
            struct (0x1F5FB, 0x1F64F)
            struct (0x1F680, 0x1F6C5)
            struct (0x1F6CC, 0x1F6CC)
            struct (0x1F6D0, 0x1F6D2)
            struct (0x1F6D5, 0x1F6D9)
            struct (0x1F6DC, 0x1F6DF)
            struct (0x1F6EB, 0x1F6EC)
            struct (0x1F6F4, 0x1F6FC)
            struct (0x1F7DA, 0x1F7DA)
            struct (0x1F7E0, 0x1F7EB)
            struct (0x1F7F0, 0x1F7F0)
            struct (0x1F90C, 0x1F93A)
            struct (0x1F93C, 0x1F945)
            struct (0x1F947, 0x1F9FF)
            struct (0x1FA70, 0x1FA7C)
            struct (0x1FA80, 0x1FAC6)
            struct (0x1FAC8, 0x1FAC8)
            struct (0x1FACC, 0x1FADD)
            struct (0x1FADF, 0x1FAEB)
            struct (0x1FAEF, 0x1FAFA)
            struct (0x20000, 0x2FFFD)
            struct (0x30000, 0x3FFFD)
        |]

    let private isWide (codePoint: int) : bool =
        // The ranges are in order and apart: a binary search for the one that could hold the code point.
        let mutable low = 0
        let mutable high = wide.Length - 1
        let mutable found = false

        while not found && low <= high do
            let middle = (low + high) / 2
            let struct (first, last) = wide[middle]

            if codePoint < first then high <- middle - 1
            elif codePoint > last then low <- middle + 1
            else found <- true

        found

    /// The length in UTF-16 units of the character that starts at <c>at</c>: a grapheme cluster.
    let clusterLength (line: string) (at: int) : int =
        // A unit of ASCII that no combining mark follows is a character by itself.
        if line[at] < '\u0080' && (at + 1 = line.Length || line[at + 1] < '̀') then
            1
        else
            max 1 (Grapheme.nextClusterLength line at)

    /// The columns of the character of <c>length</c> units at <c>at</c>, when it starts at column
    /// <c>column</c>. A tab size below 1 is taken as 1.
    let clusterColumns (line: string) (at: int) (length: int) (column: int) (tabSize: int) : int =
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

    /// The column where the character holding unit <c>offset</c> of the line starts; the line's width for an
    /// offset at or past its end.
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
                    column <- column + clusterColumns line at length column tabSize
                    at <- at + length

            column

    /// The columns the character at <c>offset</c> takes; one at or past the line's end.
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
                let columns = clusterColumns line at length column tabSize

                if offset < at + length then
                    width <- columns
                    looking <- false
                else
                    column <- column + columns
                    at <- at + length

            width

    /// The offset in the line of the character boundary nearest to <c>column</c> columns from the line's start.
    /// A column at a character's middle goes after the character.
    let offsetAt (line: string) (tabSize: int) (column: float) : int =
        if isPlain line then
            Math.Clamp (int (Math.Round column), 0, line.Length)
        else
            let mutable at = 0
            let mutable start = 0
            let mutable looking = true

            while looking && at < line.Length do
                let length = clusterLength line at
                let columns = clusterColumns line at length start tabSize

                // Left of the character's middle, the boundary before it is the nearer one.
                if column < float start + float columns / 2.0 then
                    looking <- false
                else
                    start <- start + columns
                    at <- at + length

            at
