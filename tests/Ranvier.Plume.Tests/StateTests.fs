module Ranvier.Plume.Tests.StateTests

open System
open Expecto
open Ranvier.Plume

let private editCount =
    StateField.define
        {
            Create = fun _ -> 0
            Update = fun count transaction -> if transaction.DocChanged then count + 1 else count
        }

let private stateOf (text: string) (extensions: Extension list) =
    EditorState.create
        {
            Doc = Text.ofString text
            Selection = ValueNone
            Extensions = extensions
        }

let private run (specs: TxSpec list) (state: EditorState) =
    EditorState.apply (EditorState.transaction specs state) state

let private insert (at: int) (text: string) =
    Tx.change { From = at; To = at; Insert = Text.ofString text }

let private head (state: EditorState) = state.Selection.Main.Head

[<Tests>]
let tests =
    testList
        "EditorState"
        [
            testCase "a field counts document changes"
            <| fun () ->
                let state =
                    stateOf "hello" [ StateField.extension editCount ]
                    |> run [ insert 0 "a" ]
                    |> run [ Tx.selection (EditorSelection.single 2 2) ]
                    |> run [ insert 3 "b" ]

                Expect.equal (EditorState.field editCount state) 2 "count"
                Expect.equal (Text.toString state.Doc) "ahebllo" "text"

            testCase "a field outside the configuration"
            <| fun () ->
                let state = stateOf "x" []
                Expect.equal (EditorState.tryField editCount state) ValueNone "tryField"
                Expect.throwsT<InvalidOperationException> (fun () -> EditorState.field editCount state |> ignore) "field"

            testCase "facet precedence and order"
            <| fun () ->
                Expect.equal (EditorState.facet EditorState.tabSize (stateOf "" [])) 4 "default"

                let byPrec = stateOf "" [ Extension.prec Prec.Low (EditorState.tabSize.Of 2); EditorState.tabSize.Of 8 ]
                Expect.equal (EditorState.facet EditorState.tabSize byPrec) 8 "the higher precedence comes first"

                let byOrder = stateOf "" [ EditorState.tabSize.Of 8; EditorState.tabSize.Of 2 ]
                Expect.equal (EditorState.facet EditorState.tabSize byOrder) 8 "the first of one precedence comes first"

                let all = Facet.define (fun (values: int[]) -> List.ofArray values)

                let state =
                    stateOf
                        ""
                        [
                            all.Of 1
                            Extension.prec Prec.Highest (all.Of 2)
                            Extension.many [ all.Of 3; Extension.prec Prec.Lowest (all.Of 4) ]
                            all.Of 5
                        ]

                Expect.equal (EditorState.facet all state) [ 2; 1; 3; 5; 4 ] "inputs in order"

            testCase "a computed facet follows the document"
            <| fun () ->
                let lineCount = Facet.define (fun (values: int[]) -> Array.sum values)
                let state = stateOf "a" [ Facet.compute lineCount (fun state -> Text.lines state.Doc) ]
                Expect.equal (EditorState.facet lineCount state) 1 "one line"
                Expect.equal (EditorState.facet lineCount (run [ insert 1 "\n" ] state)) 2 "two lines"

            testCase "a computed facet keeps its output while its inputs are equal"
            <| fun () ->
                let boxed = Facet.define (fun (values: int[]) -> ref (Array.sum values))
                let state = stateOf "a" [ Facet.compute boxed (fun state -> Text.lines state.Doc) ]
                let before = EditorState.facet boxed state
                let same = EditorState.facet boxed (run [ insert 1 "b" ] state)
                let different = EditorState.facet boxed (run [ insert 1 "\n" ] state)
                Expect.isTrue (obj.ReferenceEquals (before, same)) "equal inputs"
                Expect.isFalse (obj.ReferenceEquals (before, different)) "changed inputs"

            testCase "a facet from a field"
            <| fun () ->
                let doubled = Facet.define (fun (values: int[]) -> Array.sum values)
                let state = stateOf "abc" [ Facet.from doubled editCount (fun count -> count * 2) ]
                Expect.equal (EditorState.facet doubled state) 0 "start"
                Expect.equal (EditorState.facet doubled (run [ insert 0 "x" ] state)) 2 "after a change"

            testCase "a field provides extensions"
            <| fun () ->
                let sum = Facet.define (fun (values: int[]) -> Array.sum values)

                let providing =
                    StateField.defineWith (fun a b -> a = b) [ (fun field -> Facet.from sum field id) ] { Create = (fun _ -> 7); Update = fun value _ -> value }

                let state = stateOf "" [ StateField.extension providing ]
                Expect.equal (EditorState.facet sum state) 7 "provided"

            testCase "compartment reconfigure keeps field values"
            <| fun () ->
                let tabs = Compartment.create ()
                let state = stateOf "abc" [ StateField.extension editCount; tabs.Of (EditorState.tabSize.Of 2) ] |> run [ insert 0 "x" ]
                Expect.equal (EditorState.facet EditorState.tabSize state) 2 "before"
                let reconfigured = state |> run [ Tx.effect (tabs.Reconfigure (EditorState.tabSize.Of 8)) ]
                Expect.equal (EditorState.facet EditorState.tabSize reconfigured) 8 "after"
                Expect.equal (EditorState.field editCount reconfigured) 1 "the field keeps its value"
                let again = reconfigured |> run [ insert 0 "y" ]
                Expect.equal (EditorState.facet EditorState.tabSize again) 8 "a later transaction keeps the content"
                Expect.equal (EditorState.field editCount again) 2 "count"

            testCase "appendConfig and reconfigure"
            <| fun () ->
                let state = stateOf "abc" [ EditorState.tabSize.Of 2 ] |> run [ insert 0 "x" ]
                let appended = state |> run [ Tx.effect (EditorState.appendConfig.Of (StateField.extension editCount)) ]
                Expect.equal (EditorState.tryField editCount appended) (ValueSome 0) "a field added later is created"
                Expect.equal (EditorState.facet EditorState.tabSize appended) 2 "the rest stays"
                let replaced = appended |> run [ Tx.effect (EditorState.reconfigure.Of (EditorState.tabSize.Of 3)) ]
                Expect.equal (EditorState.tryField editCount replaced) ValueNone "a field removed is gone"
                Expect.equal (EditorState.facet EditorState.tabSize replaced) 3 "new configuration"

            testCase "a field is created before a field whose creation reads it"
            <| fun () ->
                let total = Facet.define (fun (values: int[]) -> Array.sum values)
                let counter = StateField.define { Create = (fun _ -> 5); Update = fun value _ -> value }

                let reader =
                    StateField.define
                        {
                            Create = fun state -> EditorState.facet total state * 2
                            Update = fun value _ -> value
                        }

                let state = stateOf "" [ StateField.extension reader; Facet.from total counter id ]
                Expect.equal (EditorState.field reader state) 10 "whatever their order in the configuration"

                let appended = stateOf "" [] |> run [ Tx.effect (EditorState.appendConfig.Of (Extension.many [ StateField.extension reader; Facet.from total counter id ])) ]
                Expect.equal (EditorState.field reader appended) 10 "and when a transaction adds them"

                let other: StateField<int> ref = ref Unchecked.defaultof<StateField<int>>

                let first =
                    StateField.define
                        {
                            Create = fun state -> EditorState.field other.Value state
                            Update = fun value _ -> value
                        }

                other.Value <-
                    StateField.define
                        {
                            Create = fun state -> EditorState.field first state
                            Update = fun value _ -> value
                        }

                Expect.throwsT<InvalidOperationException>
                    (fun () -> stateOf "" [ StateField.extension first; StateField.extension other.Value ] |> ignore)
                    "two fields that read each other"

            testCase "reconfiguring one compartment keeps the values of the facets it does not touch"
            <| fun () ->
                let all = Facet.define (fun (values: int[]) -> values)
                let tabs = Compartment.create ()
                let state = stateOf "" [ all.Of 1; all.Of 2; tabs.Of (EditorState.tabSize.Of 2) ]
                let before = EditorState.facet all state
                let after = EditorState.facet all (run [ Tx.effect (tabs.Reconfigure (EditorState.tabSize.Of 8)) ] state)
                Expect.isTrue (obj.ReferenceEquals (before, after)) "the same value"
                let grown = EditorState.facet all (run [ Tx.effect (EditorState.appendConfig.Of (all.Of 3)) ] state)
                Expect.equal grown [| 1; 2; 3 |] "another input gives another value"

            testCase "a facet without inputs has one value in every state"
            <| fun () ->
                let all = Facet.define (fun (values: int[]) -> values)
                let bare = stateOf "" []
                let other = stateOf "x" [ EditorState.tabSize.Of 2 ]
                Expect.equal (EditorState.facet all bare) Array.empty "the value of no inputs"
                Expect.isTrue (obj.ReferenceEquals (EditorState.facet all bare, EditorState.facet all bare)) "the same value on each read"
                Expect.isTrue (obj.ReferenceEquals (EditorState.facet all bare, EditorState.facet all other)) "and in a state of another configuration"

                let nothing = Facet.define (fun (values: string[]) -> if values.Length = 0 then null else values[0])
                Expect.isNull (EditorState.facet nothing bare) "a value of null is a value"
                Expect.equal (EditorState.facet nothing (stateOf "" [ nothing.Of "set" ])) "set" "an input replaces it"

            testCase "selection follows changes"
            <| fun () ->
                let state =
                    EditorState.create
                        {
                            Doc = Text.ofString "hello world"
                            Selection = ValueSome (EditorSelection.single 6 6)
                            Extensions = []
                        }

                Expect.equal (head (run [ insert 0 "ab" ] state)) 8 "insertion before"
                Expect.equal (head (run [ insert 9 "ab" ] state)) 6 "insertion after"
                Expect.equal (head (run [ insert 0 "ab"; Tx.selection (EditorSelection.single 1 1) ] state)) 1 "explicit selection"

            testCase "selection is clamped"
            <| fun () ->
                let state = stateOf "hello" [] |> run [ Tx.selection (EditorSelection.single 99 99) ]
                Expect.equal (head state) 5 "clamped"

                let created =
                    EditorState.create
                        {
                            Doc = Text.ofString "ab"
                            Selection = ValueSome (EditorSelection.single 7 9)
                            Extensions = []
                        }

                Expect.equal (created.Selection.Main.Anchor, created.Selection.Main.Head) (2, 2) "created clamped"

            testCase "multiple ranges need the facet"
            <| fun () ->
                let two = EditorSelection.create [ EditorSelection.cursor 1; EditorSelection.cursor 3 ] 1
                let single = stateOf "hello" [] |> run [ Tx.selection two ]
                Expect.equal single.Selection.Ranges.Length 1 "one range"
                Expect.equal (head single) 3 "the main range stays"
                let multiple = stateOf "hello" [ EditorState.allowMultipleSelections.Of true ] |> run [ Tx.selection two ]
                Expect.equal multiple.Selection.Ranges.Length 2 "two ranges"

            testCase "effects map through changes"
            <| fun () ->
                let mark =
                    StateEffect.define (fun (position: int) changes -> ValueSome (ChangeDesc.mapPos Assoc.After position changes))

                let dropped = StateEffect.define (fun (_: int) _ -> ValueNone)
                let doc = Text.ofString "0123456789"
                let changes = ChangeSet.desc (ChangeSet.ofSpecs doc [ { From = 0; To = 0; Insert = Text.ofString "ab" } ])
                let mapped = StateEffect.mapEffects changes [| mark.Of 5; dropped.Of 1 |]
                Expect.equal mapped.Length 1 "an effect mapped to nothing is dropped"
                Expect.equal (StateEffect.tryValue mark mapped[0]) (ValueSome 7) "moved"
                Expect.isTrue (mapped[0].Is mark) "kind"
                Expect.isFalse (mapped[0].Is dropped) "another kind"
                Expect.equal (StateEffect.tryValue dropped mapped[0]) ValueNone "value of another kind"

            testCase "transaction metadata"
            <| fun () ->
                let state = stateOf "hello" []
                let custom = Annotation.define<int> ()

                let transaction =
                    EditorState.transaction [ insert 0 "x"; Tx.userEvent "input.type"; Tx.annotate (custom.Of 42); Tx.scrollIntoView; Tx.group 9L ] state

                Expect.isTrue transaction.DocChanged "doc changed"
                Expect.isTrue transaction.ScrollIntoView "scroll"
                Expect.equal (Transaction.annotation custom transaction) (ValueSome 42) "custom annotation"
                Expect.equal (Transaction.annotation Annotations.group transaction) (ValueSome 9L) "group"
                Expect.equal (Transaction.annotation Annotations.time transaction) ValueNone "no time"
                Expect.isTrue (Transaction.isUserEvent "input" transaction) "input"
                Expect.isTrue (Transaction.isUserEvent "input.type" transaction) "input.type"
                Expect.isFalse (Transaction.isUserEvent "inp" transaction) "a prefix of a word"
                Expect.isFalse (Transaction.isUserEvent "delete" transaction) "delete"
                Expect.equal transaction.StartSelection (ValueSome state.Selection) "start selection"

                let foreign = Transaction.asForeign transaction
                Expect.equal foreign.Selection ValueNone "no selection"
                Expect.equal foreign.Effects.Length 0 "no effects"
                Expect.isFalse foreign.ScrollIntoView "no scroll"
                Expect.isTrue foreign.DocChanged "changes stay"
                Expect.equal (Transaction.annotation custom foreign) (ValueSome 42) "annotations stay"

            testCase "invalid changes raise"
            <| fun () ->
                let state = stateOf "hello" []

                Expect.throwsT<ArgumentException>
                    (fun () -> EditorState.transaction [ Tx.change { From = 2; To = 9; Insert = Text.empty } ] state |> ignore)
                    "beyond the document"

                Expect.throwsT<ArgumentException>
                    (fun () ->
                        EditorState.transaction
                            [ Tx.change { From = 0; To = 3; Insert = Text.empty }; Tx.change { From = 2; To = 4; Insert = Text.empty } ]
                            state
                        |> ignore)
                    "overlap"

                Expect.throwsT<ArgumentException>
                    (fun () -> EditorState.transaction [ Tx.changeSet (ChangeSet.empty 3) ] state |> ignore)
                    "a change set of another length"

            testCase "changes of one transaction address the start document"
            <| fun () ->
                let state = stateOf "0123456789" [] |> run [ Tx.changes [ { From = 8; To = 9; Insert = Text.ofString "B" }; { From = 1; To = 2; Insert = Text.ofString "AA" } ] ]
                Expect.equal (Text.toString state.Doc) "0AA234567B9" "both applied"
        ]
