module Ranvier.Plume.Tests.SearchTests

open Expecto
open Ranvier
open Ranvier.Plume

let private withEditor text extensions action =
    use graph = new Graph()
    use active = graph.Activate()
    let editor = Editor.create (Document.create (Text.ofString text)) (Search.extension :: extensions)
    action editor

let private spans (editor: Editor) =
    let ranges = Search.matches editor.State.Value
    Array.zip ranges.From ranges.To

[<Tests>]
let tests =
    testList "Search" [
        testCase "a local edit reuses dense matches outside the edited rope leaf" <| fun () ->
            let raw = String.replicate 20000 "aaaaa\r\n"
            withEditor raw [] (fun editor ->
                Search.setQuery (ValueSome (Search.literal "aa" true)) editor |> ignore
                Search.next editor |> ignore
                editor.Dispatch [Tx.change {From=0; To=1; Insert=Text.ofString "ba"}]
                let before = System.GC.GetAllocatedBytesForCurrentThread()
                Search.next editor |> ignore
                let allocated = System.GC.GetAllocatedBytesForCurrentThread() - before
                Expect.isLessThan allocated 150000L "lookup after an edit must not flatten the document or its dense index"
                let oracle () =
                    let text = Text.toString editor.Text.Value
                    let mutable cursor = 0
                    [| while cursor <= text.Length do
                           let i = text.IndexOf("aa", cursor, System.StringComparison.Ordinal)
                           if i < 0 then cursor <- text.Length + 1
                           else
                               yield i, i+2
                               cursor <- i+2 |]
                for offset in [895; 0; raw.Length / 2; raw.Length - 2] do
                    editor.Dispatch [Tx.change {From=offset; To=offset+2; Insert=Text.ofString "a\naaa"}]
                    Expect.equal (spans editor) (oracle ()) "all offsets after splitting or joining chunks"
                    Commands.undo editor |> ignore
                    Expect.equal (spans editor) (oracle ()) "undo reuses the correct index")

        testCase "navigation and viewport cuts agree with an ordinal scan at rope boundaries" <| fun () ->
            let line = "Aaa𐐀😀"
            let raw = String.concat "\r\n" (List.replicate 1600 line)
            withEditor raw [] (fun editor ->
                for pattern in ["aa"; "𐐨"; "\r\nA"; "😀\r\nAa"] do
                    Search.setQuery (ValueSome (Search.literal pattern false)) editor |> ignore
                    let positions = ResizeArray<int>()
                    let mutable found = raw.IndexOf(pattern, System.StringComparison.OrdinalIgnoreCase)
                    while found >= 0 do
                        positions.Add found
                        found <- raw.IndexOf(pattern, found + pattern.Length, System.StringComparison.OrdinalIgnoreCase)
                    for row in [0; 127; 255; 511; 512; 767; 1023; 1024; 1599] do
                        for delta in 0 .. line.Length do
                            let point = row * (line.Length + 2) + delta
                            let seen = ResizeArray<int>()
                            Search.between point (point+2) (fun a _ -> seen.Add a) editor.State.Value
                            let window = positions |> Seq.filter (fun a -> a < point+2 && a+pattern.Length > point)
                            Expect.sequenceEqual seen window "visible intersection"
                            let move command =
                                editor.Dispatch [Tx.selection (EditorSelection.single point point)]
                                command editor |> ignore
                                editor.Selection.Value.Main.From
                            let next = positions |> Seq.tryFind ((<=) point) |> Option.defaultValue positions[0]
                            let prev = positions |> Seq.tryFindBack (fun a -> a+pattern.Length <= point) |> Option.defaultValue positions[positions.Count-1]
                            Expect.equal (move Search.next) next "forward rank"
                            Expect.equal (move Search.previous) prev "backward rank")

        testCase "ordinal non-overlapping UTF-16 matches and wrapping navigation" <| fun () ->
            withEditor "😀AAa\r\naa" [] (fun editor ->
                Search.setQuery (ValueSome (Search.literal "aa" false)) editor |> ignore
                Expect.equal (spans editor) [| 2,4; 7,9 |] "non-overlapping, including the final match"
                let at n = Expect.equal editor.Selection.Value.Main.From n "match start"
                Search.previous editor |> ignore
                at 7
                Search.next editor |> ignore
                at 2
                Search.next editor |> ignore
                at 7
                Search.previous editor |> ignore
                at 2
                editor.Dispatch [Tx.selection (EditorSelection.single 3 3)]
                Search.next editor |> ignore
                at 7
                Search.setQuery ValueNone editor |> ignore
                Expect.isFalse (Search.next editor) "cleared")

        testCase "regex replacement is literal, isolated and undoable" <| fun () ->
            withEditor "a12 a3" [] (fun editor ->
                let pattern = Search.regex "a[0-9]+" true |> Result.defaultWith failtest
                Search.setQuery (ValueSome pattern) editor |> ignore
                Expect.isFalse (Search.replaceCurrent "z" editor) "caret alone is not a match"
                Search.next editor |> ignore
                Expect.isTrue (Search.replaceCurrent "b" editor) "replace selected match"
                Expect.equal (spans editor) [| 2,4 |] "updated after edit"
                Commands.undo editor |> ignore
                Expect.isTrue (Search.replaceAll "$1" editor) "replace both"
                Expect.equal (Text.toString editor.Text.Value) "$1 $1" "literal replacement"
                Commands.undo editor |> ignore
                Expect.equal (Text.toString editor.Text.Value) "a12 a3" "one undo restores both")

        testCase "read-only, empty, multiline and unsupported patterns" <| fun () ->
            withEditor "a\r\nb" [EditorState.readOnly.Of true] (fun editor ->
                for query in [Search.literal "" true; Search.regex "^|$" true |> Result.defaultWith failtest] do
                    Search.setQuery (ValueSome query) editor |> ignore
                    Expect.isEmpty (spans editor) "empty ranges excluded"
                Expect.isTrue (Search.regex "[" true |> Result.isError) "invalid pattern"
                Search.setQuery (ValueSome (Search.literal "\r\nb" true)) editor |> ignore
                Expect.isTrue (Search.next editor) "read-only search"
                Expect.isFalse (Search.replaceCurrent "" editor) "read-only replacement"
                Expect.isFalse (Search.replaceAll "" editor) "read-only replace all"
                Expect.equal (spans editor) [| 1,4 |] "cross-line offsets")

        testCase "viewport intersection excludes adjacent hits and public results are snapshots" <| fun () ->
            withEditor "aaaaaaaa" [] (fun editor ->
                Search.setQuery (ValueSome (Search.literal "aa" true)) editor |> ignore
                let copy = Search.matches editor.State.Value
                copy.From[1] <- 1000
                let visible left right =
                    let hits = ResizeArray<int * int>()
                    Search.between left right (fun a b -> hits.Add(a,b)) editor.State.Value
                    List.ofSeq hits
                Expect.equal (visible 2 4) [(2,4)] "half-open clip"
                Expect.equal (visible 3 5) [(2,4); (4,6)] "crossing both edges"
                Expect.equal (visible 4 4) [] "empty viewport")

        testCase "editors keep independent queries and observe shared document edits" <| fun () ->
            withEditor "aa AA" [] (fun first ->
                let second = Editor.create first.Document [Search.extension]
                Search.setQuery (ValueSome (Search.literal "aa" true)) first |> ignore
                Search.setQuery (ValueSome (Search.literal "AA" true)) second |> ignore
                Expect.equal (spans first) [| 0,2 |] "local query"
                Expect.equal (spans second) [| 3,5 |] "sibling query"
                Search.replaceAll "AA" first |> ignore
                Expect.isEmpty (spans first) "edited snapshot"
                Expect.equal (spans second) [| 0,2; 3,5 |] "sibling sees the same edit")
    ]
