/// A line lexer for the dummy F# source.
module EditorShowcase.Lexer

open System
open System.Collections.Generic

/// What a run of text is drawn as; the number is the index of its colour class.
module Kind =
    [<Literal>]
    let Plain = 0

    [<Literal>]
    let Keyword = 1

    [<Literal>]
    let Type = 2

    [<Literal>]
    let Function = 3

    [<Literal>]
    let String = 4

    [<Literal>]
    let Number = 5

    [<Literal>]
    let Comment = 6

    [<Literal>]
    let Operator = 7

    [<Literal>]
    let Punctuation = 8

    [<Literal>]
    let Attribute = 9

/// Consecutive characters of a line drawn in one colour. White space belongs to the run after it.
[<Struct>]
type Run = { Text: string; Kind: int }

let private keywords =
    HashSet<string>(
        [ "abstract"; "and"; "as"; "begin"; "do"; "done"; "downto"; "elif"; "else"; "end"; "exception"; "false"
          "finally"; "for"; "fun"; "function"; "if"; "in"; "inherit"; "inline"; "interface"; "let"; "match"; "member"
          "module"; "mutable"; "namespace"; "new"; "not"; "null"; "of"; "open"; "or"; "override"; "private"; "public"
          "rec"; "return"; "static"; "struct"; "then"; "to"; "true"; "try"; "type"; "use"; "val"; "when"; "while"
          "with"; "yield" ],
        StringComparer.Ordinal
    )

let private builtins =
    HashSet<string>(
        [ "abs"; "cos"; "float"; "float32"; "ignore"; "int"; "max"; "min"; "printfn"; "sin"; "sprintf"; "sqrt"
          "string"; "uint32" ],
        StringComparer.Ordinal
    )

let private isIdentifierStart (c: char) = Char.IsAsciiLetter c || c = '_'
let private isIdentifierPart (c: char) = Char.IsAsciiLetterOrDigit c || c = '_' || c = '\''
let private isOperator (c: char) = "+-*/=<>|&!%^~?:@.".Contains c
let private isPunctuation (c: char) = "()[]{},;".Contains c

/// The runs of one line. The lexer keeps no state between lines, so equal text gives equal runs.
let tokenize (line: string) : Run[] =
    let tokens = ResizeArray<struct (int * int * int)>()
    let length = line.Length
    let mutable at = 0

    let nextVisible (from: int) =
        let mutable index = from

        while index < length && line[index] = ' ' do
            index <- index + 1

        if index < length then line[index] else '\000'

    while at < length do
        let start = at
        let c = line[at]

        let kind =
            if c = ' ' || c = '\t' then
                while at < length && (line[at] = ' ' || line[at] = '\t') do
                    at <- at + 1

                -1
            elif c = '/' && at + 1 < length && line[at + 1] = '/' then
                at <- length
                Kind.Comment
            elif c = '(' && at + 1 < length && line[at + 1] = '*' then
                let close = line.IndexOf("*)", at + 2, StringComparison.Ordinal)
                at <- if close < 0 then length else close + 2
                Kind.Comment
            elif c = '[' && at + 1 < length && line[at + 1] = '<' && line.IndexOf(">]", at + 2, StringComparison.Ordinal) > 0 then
                at <- line.IndexOf(">]", at + 2, StringComparison.Ordinal) + 2
                Kind.Attribute
            elif c = '"' || ((c = '$' || c = '@') && at + 1 < length && line[at + 1] = '"') then
                at <- if c = '"' then at + 1 else at + 2

                while at < length && line[at] <> '"' do
                    at <- if line[at] = '\\' then at + 2 else at + 1

                at <- min length (at + 1)
                Kind.String
            elif c = '\'' && at + 2 < length && (line[at + 2] = '\'' || (line[at + 1] = '\\' && at + 3 < length && line[at + 3] = '\'')) then
                at <- if line[at + 2] = '\'' then at + 3 else at + 4
                Kind.String
            elif Char.IsAsciiDigit c then
                let hex = c = '0' && at + 1 < length && line[at + 1] = 'x'
                at <- at + 1

                while at < length
                      && (Char.IsAsciiLetterOrDigit line[at]
                          || line[at] = '_'
                          || (line[at] = '.' && at + 1 < length && Char.IsAsciiDigit line[at + 1])
                          || ((line[at] = '-' || line[at] = '+') && not hex && (line[at - 1] = 'e' || line[at - 1] = 'E'))) do
                    at <- at + 1

                Kind.Number
            elif isIdentifierStart c || c = '\'' then
                at <- at + 1

                while at < length && isIdentifierPart line[at] do
                    at <- at + 1

                let word = line.Substring(start, at - start)

                if keywords.Contains word then Kind.Keyword
                elif c = '\'' || Char.IsAsciiLetterUpper c then Kind.Type
                elif builtins.Contains word || nextVisible at = '(' then Kind.Function
                else Kind.Plain
            elif isOperator c then
                while at < length && isOperator line[at] && not (line[at] = '/' && at + 1 < length && line[at + 1] = '/') do
                    at <- at + 1

                if at = start then
                    at <- at + 1

                Kind.Operator
            elif isPunctuation c then
                at <- at + 1
                Kind.Punctuation
            else
                at <- at + 1
                Kind.Plain

        tokens.Add(struct (start, at - start, kind))

    // White space joins the token after it, and neighbours of one kind become one run.
    let runs = ResizeArray<struct (int * int * int)>()
    let mutable pendingStart = -1

    for struct (start, count, kind) in tokens do
        if kind < 0 then
            if pendingStart < 0 then
                pendingStart <- start
        else
            let from = if pendingStart >= 0 then pendingStart else start
            pendingStart <- -1

            if runs.Count > 0 then
                let struct (lastStart, _, lastKind) = runs[runs.Count - 1]

                if lastKind = kind then
                    runs[runs.Count - 1] <- struct (lastStart, start + count - lastStart, kind)
                else
                    runs.Add(struct (from, start + count - from, kind))
            else
                runs.Add(struct (from, start + count - from, kind))

    if pendingStart >= 0 then
        if runs.Count > 0 then
            let struct (lastStart, _, lastKind) = runs[runs.Count - 1]
            runs[runs.Count - 1] <- struct (lastStart, length - lastStart, lastKind)
        else
            runs.Add(struct (pendingStart, length - pendingStart, Kind.Plain))

    Array.init runs.Count (fun index ->
        let struct (start, count, kind) = runs[index]
        { Text = line.Substring(start, count); Kind = kind })

/// The tag of each kind of run. `Kind.Plain` has none.
let private tags: Ranvier.Plume.Tag[] =
    [| Unchecked.defaultof<Ranvier.Plume.Tag>
       Ranvier.Plume.Tag.keyword
       Ranvier.Plume.Tag.typeName
       Ranvier.Plume.Tag.functionName
       Ranvier.Plume.Tag.string
       Ranvier.Plume.Tag.number
       Ranvier.Plume.Tag.comment
       Ranvier.Plume.Tag.operator
       Ranvier.Plume.Tag.punctuation
       Ranvier.Plume.Tag.variable |]

/// The lexer as a language of Ranvier.Plume: every run with a colour becomes a token.
let language: Ranvier.Plume.Language =
    Ranvier.Plume.Language.ofLineLexer "fsharp-sample" (fun line ->
        let tokens = ResizeArray<Ranvier.Plume.Token>()
        let mutable at = 0

        for run in tokenize line do
            if run.Kind <> Kind.Plain then
                tokens.Add { From = at; To = at + run.Text.Length; Tag = tags[run.Kind] }

            at <- at + run.Text.Length

        tokens.ToArray())
