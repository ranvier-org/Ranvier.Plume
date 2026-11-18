module Ranvier.Plume.Tests.CommandTests

open System
open Expecto
open Ranvier
open Ranvier.Keyboard
open Ranvier.Plume

let private withEditor (text: string) (extensions: Extension list) (body: Editor -> unit) =
    let graph = new Graph ()
    let activation = graph.Activate ()

    try
        body (Editor.create (Document.create (Text.ofString text)) extensions)
    finally
        activation.Dispose ()
        graph.Dispose ()

let private at (position: int) (editor: Editor) =
    editor.Dispatch [ Tx.selection (EditorSelection.single position position) ]

let private head (editor: Editor) = editor.Selection.Value.Main.Head
let private span (editor: Editor) = editor.Selection.Value.Main.Anchor, editor.Selection.Value.Main.Head
let private text (editor: Editor) = Text.toString editor.Text.Value

[<Tests>]
let commandTests =
    testList
        "Commands"
        [
            testCase "character motion moves by grapheme and across lines"
            <| fun () ->
                withEditor "a😀b\ncd" [] (fun editor ->
                    Expect.isFalse (Commands.cursorCharLeft editor) "nothing before the start"
                    Expect.isTrue (Commands.cursorCharRight editor) "right"
                    Expect.equal (head editor) 1 "after a"
                    Commands.cursorCharRight editor |> ignore
                    Expect.equal (head editor) 3 "after the pair"
                    Commands.cursorCharRight editor |> ignore
                    Commands.cursorCharRight editor |> ignore
                    Expect.equal (head editor) 5 "the next line's start"
                    Commands.cursorCharLeft editor |> ignore
                    Expect.equal (head editor) 4 "the line's end"
                    Commands.cursorCharLeft editor |> ignore
                    Commands.cursorCharLeft editor |> ignore
                    Expect.equal (head editor) 1 "before the pair"
                    at 7 editor
                    Expect.isFalse (Commands.cursorCharRight editor) "nothing after the end")

            testCase "group motion moves by word"
            <| fun () ->
                withEditor "foo bar_baz, x" [] (fun editor ->
                    Commands.cursorGroupRight editor |> ignore
                    Expect.equal (head editor) 3 "after foo"
                    Commands.cursorGroupRight editor |> ignore
                    Expect.equal (head editor) 11 "after bar_baz"
                    Commands.cursorGroupRight editor |> ignore
                    Expect.equal (head editor) 12 "after the comma"
                    Commands.cursorGroupLeft editor |> ignore
                    Expect.equal (head editor) 11 "before the comma"
                    Commands.cursorGroupLeft editor |> ignore
                    Expect.equal (head editor) 4 "before bar_baz")

            testCase "vertical motion keeps the goal column"
            <| fun () ->
                withEditor "long line here\nab\nanother long line" [] (fun editor ->
                    at 10 editor
                    Commands.cursorLineDown editor |> ignore
                    Expect.equal (head editor) 17 "the short line's end"
                    Commands.cursorLineDown editor |> ignore
                    Expect.equal (head editor) 28 "column 10 again"
                    Commands.cursorLineDown editor |> ignore
                    Expect.equal (head editor) 35 "the document's end from the last line"
                    Commands.cursorLineUp editor |> ignore
                    Commands.cursorLineUp editor |> ignore
                    Expect.equal (head editor) 10 "back at column 10"
                    Commands.cursorLineUp editor |> ignore
                    Expect.equal (head editor) 0 "the document's start from the first line")

            testCase "line, page and document edges"
            <| fun () ->
                withEditor (String.Join ("\n", Seq.init 100 (fun index -> "line " + string index))) [] (fun editor ->
                    at 3 editor
                    Commands.cursorLineEnd editor |> ignore
                    Expect.equal (head editor) 6 "line end"
                    Commands.cursorLineStart editor |> ignore
                    Expect.equal (head editor) 0 "line start"
                    editor.PageLines.Value <- 10
                    Commands.cursorPageDown editor |> ignore
                    Expect.equal (Text.lineAt (head editor) editor.Text.Value).Number 10 "a page down"
                    Commands.cursorPageUp editor |> ignore
                    Expect.equal (head editor) 0 "a page up"
                    Commands.cursorDocEnd editor |> ignore
                    Expect.equal (head editor) (Text.length editor.Text.Value) "document end"
                    Commands.cursorDocStart editor |> ignore
                    Expect.equal (head editor) 0 "document start")

            testCase "selecting extends from the anchor and a plain motion collapses"
            <| fun () ->
                withEditor "hello world" [] (fun editor ->
                    at 2 editor
                    Commands.selectCharRight editor |> ignore
                    Commands.selectCharRight editor |> ignore
                    Expect.equal (span editor) (2, 4) "extended"
                    Commands.selectLineEnd editor |> ignore
                    Expect.equal (span editor) (2, 11) "to the line end"
                    Commands.cursorCharLeft editor |> ignore
                    Expect.equal (span editor) (2, 2) "collapsed to the start"
                    Commands.selectAll editor |> ignore
                    Expect.equal (span editor) (0, 11) "all"
                    Commands.cursorCharRight editor |> ignore
                    Expect.equal (span editor) (11, 11) "collapsed to the end")

            testCase "selectLine takes whole lines"
            <| fun () ->
                withEditor "one\ntwo\nthree" [] (fun editor ->
                    at 5 editor
                    Commands.selectLine editor |> ignore
                    Expect.equal (span editor) (4, 8) "the line and its break"
                    at 10 editor
                    Commands.selectLine editor |> ignore
                    Expect.equal (span editor) (8, 13) "the last line")

            testCase "typing, newline and deletion"
            <| fun () ->
                withEditor "  indented" [] (fun editor ->
                    at 10 editor
                    Expect.isTrue (Commands.insertText "!" editor) "insert"
                    Expect.equal (text editor) "  indented!" "inserted"
                    Expect.equal (head editor) 11 "cursor after the text"
                    Commands.insertNewlineKeepIndent editor |> ignore
                    Expect.equal (text editor) "  indented!\n  " "indentation copied"
                    Expect.equal (head editor) 14 "cursor after the indentation"
                    Commands.deleteCharBackward editor |> ignore
                    Commands.deleteCharBackward editor |> ignore
                    Commands.deleteCharBackward editor |> ignore
                    Expect.equal (text editor) "  indented!" "three characters deleted, the break among them"
                    at 0 editor
                    Expect.isFalse (Commands.deleteCharBackward editor) "nothing before the start"
                    Commands.deleteCharForward editor |> ignore
                    Expect.equal (text editor) " indented!" "forward"
                    Commands.insertNewline editor |> ignore
                    Expect.equal (text editor) "\n indented!" "a plain newline"
                    at 11 editor
                    Expect.isFalse (Commands.deleteCharForward editor) "nothing after the end")

            testCase "an edit replaces the selection"
            <| fun () ->
                withEditor "hello world" [] (fun editor ->
                    editor.Dispatch [ Tx.selection (EditorSelection.single 0 5) ]
                    Commands.insertText "bye" editor |> ignore
                    Expect.equal (text editor) "bye world" "replaced"
                    editor.Dispatch [ Tx.selection (EditorSelection.single 3 9) ]
                    Commands.deleteCharBackward editor |> ignore
                    Expect.equal (text editor) "bye" "the selection is deleted"
                    Expect.equal (head editor) 3 "cursor")

            testCase "deleting an astral character removes both units"
            <| fun () ->
                withEditor "a😀b" [] (fun editor ->
                    at 3 editor
                    Commands.deleteCharBackward editor |> ignore
                    Expect.equal (text editor) "ab" "the pair is gone")

            testCase "edits at several cursors"
            <| fun () ->
                withEditor "one\ntwo\nthree" [ EditorState.allowMultipleSelections.Of true ] (fun editor ->
                    editor.Dispatch [ Tx.selection (EditorSelection.create [ EditorSelection.cursor 0; EditorSelection.cursor 4; EditorSelection.cursor 8 ] 0) ]
                    Commands.insertText "> " editor |> ignore
                    Expect.equal (text editor) "> one\n> two\n> three" "three insertions in one transaction"
                    Expect.equal (editor.Selection.Value.Ranges |> Array.map (fun range -> range.Head)) [| 2; 8; 14 |] "cursors after each"
                    Expect.equal editor.Document.Head.Value 2L "one transaction"
                    Commands.deleteCharBackward editor |> ignore
                    Commands.deleteCharBackward editor |> ignore
                    Expect.equal (text editor) "one\ntwo\nthree" "deleted at each cursor")

            testCase "a read-only state refuses edits"
            <| fun () ->
                withEditor "abc" [ EditorState.readOnly.Of true ] (fun editor ->
                    Expect.isFalse (Commands.insertText "x" editor) "insert"
                    Expect.isFalse (Commands.deleteCharForward editor) "delete"
                    Expect.isTrue (Commands.cursorCharRight editor) "motion still applies"
                    Expect.equal (text editor) "abc" "text")

            testCase "a state command runs without an editor"
            <| fun () ->
                let state =
                    EditorState.create
                        {
                            Doc = Text.ofString "abc"
                            Selection = ValueNone
                            Extensions = []
                        }

                let mutable next = state

                let command: StateCommand =
                    fun state dispatch ->
                        dispatch [ Tx.change { From = 0; To = 0; Insert = Text.ofString "x" } ]
                        true

                Expect.isTrue (command state (fun specs -> next <- EditorState.apply (EditorState.transaction specs state) state)) "ran"
                Expect.equal (Text.toString next.Doc) "xabc" "applied"
        ]

[<Tests>]
let keymapTests =
    testList
        "Keymap"
        [
            testCase "chords"
            <| fun () ->
                Expect.equal (Keymap.chord "Ctrl-z") (KeyChord.ofW3c ("z", true, false, false)) "Ctrl-z"
                Expect.equal (Keymap.chord "Ctrl-Shift-z") (KeyChord.ofW3c ("Z", true, false, true)) "a shifted letter is upper case"
                Expect.equal (Keymap.chord "Shift-ArrowLeft") (KeyChord.ofW3c ("ArrowLeft", false, false, true)) "Shift-ArrowLeft"
                Expect.equal (Keymap.chord "Enter") (KeyChord.ofW3c ("Enter", false, false, false)) "Enter"
                Expect.equal (Keymap.chord "Ctrl--") (KeyChord.ofW3c ("-", true, false, false)) "the minus key"
                Expect.equal (Keymap.chord "-") (KeyChord.ofW3c ("-", false, false, false)) "minus alone"
                Expect.throwsT<ArgumentException> (fun () -> Keymap.chord "Hyper-x" |> ignore) "unknown modifier"

            testCase "the default bindings"
            <| fun () ->
                withEditor "hello" [ Keymap.extension Keymap.defaults ] (fun editor ->
                    let press (stroke: string) = Keymap.run editor [] (Keymap.chord stroke)
                    Expect.equal (press "ArrowRight") Handled "right"
                    Expect.equal (head editor) 1 "moved"
                    Expect.equal (press "Shift-ArrowRight") Handled "the Shift form"
                    Expect.equal (span editor) (1, 2) "extended"
                    Expect.equal (press "Backspace") Handled "backspace"
                    Expect.equal (text editor) "hllo" "deleted"
                    Expect.equal (press "Ctrl-z") Handled "undo"
                    Expect.equal (text editor) "hello" "undone"
                    Expect.equal (press "Ctrl-Shift-z") Handled "redo"
                    Expect.equal (text editor) "hllo" "redone"
                    Expect.equal (press "x") Unhandled "a key without a binding"
                    Expect.equal (press "Ctrl-End") Handled "document end"
                    Expect.equal (press "ArrowRight") Unhandled "a command that does not apply leaves the key unhandled")

            testCase "a sequence of strokes"
            <| fun () ->
                let mutable ran = 0

                let binding =
                    Keymap.bind "Ctrl-k Ctrl-c" (fun _ ->
                        ran <- ran + 1
                        true)

                withEditor "" [ Keymap.extension [ binding ] ] (fun editor ->
                    match Keymap.run editor [] (Keymap.chord "Ctrl-k") with
                    | Pending strokes ->
                        Expect.equal (Keymap.run editor strokes (Keymap.chord "Ctrl-x")) Unhandled "another second stroke"
                        Expect.equal (Keymap.run editor strokes (Keymap.chord "Ctrl-c")) Handled "the second stroke"
                        Expect.equal ran 1 "ran once"
                    | other -> failtestf "expected Pending, got %A" other)

            testCase "precedence orders bindings"
            <| fun () ->
                let log = ResizeArray<string> ()

                let named (name: string) (applies: bool) : KeyBinding =
                    Keymap.bind "Ctrl-q" (fun _ ->
                        log.Add name
                        applies)

                withEditor
                    ""
                    [
                        Keymap.extension [ named "default" true ]
                        Extension.prec Prec.High (Keymap.extension [ named "high, declines" false ])
                        Extension.prec Prec.Highest (Keymap.extension [ named "highest, declines" false ])
                    ]
                    (fun editor ->
                        Expect.equal (Keymap.run editor [] (Keymap.chord "Ctrl-q")) Handled "handled"
                        Expect.sequenceEqual log [ "highest, declines"; "high, declines"; "default" ] "in precedence order until one applies")
        ]

[<Tests>]
let languageTests =
    testList
        "Language"
        [
            testCase "the token cache counts hits and misses"
            <| fun () ->
                let mutable calls = 0

                let language =
                    Language.ofLineLexer "words" (fun line ->
                        calls <- calls + 1
                        [| { From = 0; To = line.Length; Tag = Tag.keyword } |])

                let cache = TokenCache 2
                Expect.equal (cache.Tokens language "let").Length 1 "tokens"
                cache.Tokens language "let" |> ignore
                Expect.equal (cache.Hits, cache.Misses, calls) (1L, 1L, 1) "one miss, one hit"
                cache.Tokens language "a" |> ignore
                cache.Tokens language "b" |> ignore
                cache.Tokens language "let" |> ignore
                Expect.equal calls 4 "a full cache starts again"
                cache.Enabled <- false
                cache.Tokens language "let" |> ignore
                Expect.equal calls 5 "disabled, every line is tokenized"
                cache.Reset ()
                Expect.equal (cache.Hits, cache.Misses) (0L, 0L) "reset"

            testCase "tags, styles and the language facet"
            <| fun () ->
                let custom = Tag.define "custom"
                let style = HighlightStyle.define [ Tag.keyword, "kw"; custom, "cu" ]
                Expect.equal (HighlightStyle.classOf Tag.keyword style) (ValueSome "kw") "keyword"
                Expect.equal (HighlightStyle.classOf custom style) (ValueSome "cu") "custom"
                Expect.equal (HighlightStyle.classOf Tag.comment style) ValueNone "a tag without a class"
                Expect.notEqual Tag.keyword Tag.comment "tags differ"

                let language = Language.ofLineLexer "none" (fun _ -> Array.empty)

                let state =
                    EditorState.create
                        {
                            Doc = Text.empty
                            Selection = ValueNone
                            Extensions = [ Language.extension language ]
                        }

                Expect.isTrue (EditorState.facet Language.facet state).IsSome "configured"

                Expect.isTrue
                    (EditorState.facet
                        Language.facet
                        (EditorState.create
                            {
                                Doc = Text.empty
                                Selection = ValueNone
                                Extensions = []
                            }))
                        .IsNone
                    "no language"
        ]
