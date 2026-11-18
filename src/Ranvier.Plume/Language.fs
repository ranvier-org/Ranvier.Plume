namespace Ranvier.Plume

open System
open System.Collections.Generic

/// A kind of token a theme gives a look to.
[<Struct; CustomEquality; NoComparison>]
type Tag =
    {
        Id: int
        Name: string
    }

    override this.Equals(other: obj) =
        match other with
        | :? Tag as tag -> tag.Id = this.Id
        | _ -> false

    override this.GetHashCode() = this.Id

    interface IEquatable<Tag> with
        member this.Equals(other: Tag) = other.Id = this.Id

[<RequireQualifiedAccess>]
module Tag =

    /// A new tag named <c>name</c>.
    let define (name: string) : Tag = { Id = Ids.next (); Name = name }

    let keyword = define "keyword"
    let typeName = define "typeName"
    let string = define "string"
    let number = define "number"
    let comment = define "comment"
    let operator = define "operator"
    let punctuation = define "punctuation"
    let variable = define "variable"
    let functionName = define "functionName"

/// A tagged span of a line, in UTF-16 units from the line's start.
[<Struct>]
type Token = { From: int; To: int; Tag: Tag }

/// How the lines of a language divide into tokens.
[<NoEquality; NoComparison>]
type Language =
    {
        Name: string
        /// The tokens of a line, in order and apart. Text between tokens has no tag.
        Tokenize: string -> Token[]
    }

[<RequireQualifiedAccess>]
module Language =

    /// A language whose tokenizer reads one line at a time and keeps no state between lines.
    let ofLineLexer (name: string) (tokenize: string -> Token[]) : Language = { Name = name; Tokenize = tokenize }

    /// The language of an editor: the first by precedence.
    let facet: Facet<Language, Language voption> =
        Facet.define (fun (languages: Language[]) ->
            if languages.Length > 0 then
                ValueSome languages[0]
            else
                ValueNone)

    /// An extension that sets the editor's language.
    let extension (language: Language) : Extension = facet.Of language

/// The CSS class of each tag.
[<Sealed>]
type HighlightStyle internal (classes: Dictionary<int, string>) =
    member internal _.Classes = classes

[<RequireQualifiedAccess>]
module HighlightStyle =

    /// A style giving each tag of <c>classes</c> its class name.
    let define (classes: (Tag * string) list) : HighlightStyle =
        let table = Dictionary<int, string> ()

        for tag, name in classes do
            table[tag.Id] <- name

        HighlightStyle table

    /// The class of <c>tag</c> in <c>style</c>.
    let classOf (tag: Tag) (style: HighlightStyle) : string voption =
        match style.Classes.TryGetValue tag.Id with
        | true, name -> ValueSome name
        | _ -> ValueNone

/// The tokens of the lines of one language, kept by line text. A line that occurs again costs a lookup; when the
/// cache holds <c>capacity</c> lines it starts again empty.
[<Sealed>]
type TokenCache(capacity: int) =
    let lines = Dictionary<string, Token[]> (StringComparer.Ordinal)
    let mutable language: Language voption = ValueNone
    let mutable hits = 0L
    let mutable misses = 0L

    /// False to tokenize every line asked for.
    member val Enabled = true with get, set

    member _.Hits = hits
    member _.Misses = misses

    /// Empties the cache and its counts.
    member _.Reset() =
        lines.Clear ()
        hits <- 0L
        misses <- 0L

    /// The tokens of <c>line</c> in <c>language</c>.
    member this.Tokens (language': Language) (line: string) : Token[] =
        match language with
        | ValueSome held when obj.ReferenceEquals (held, language') -> ()
        | _ ->
            lines.Clear ()
            language <- ValueSome language'

        if not this.Enabled then
            misses <- misses + 1L
            language'.Tokenize line
        else
            let mutable tokens = null

            if lines.TryGetValue (line, &tokens) then
                hits <- hits + 1L
                tokens
            else
                misses <- misses + 1L

                if lines.Count >= capacity then
                    lines.Clear ()

                let made = language'.Tokenize line
                lines[line] <- made
                made
