namespace Ranvier.Plume

open System
open System.Collections.Generic
open System.Threading
open Ranvier

/// What changed in an editor between two reads of an update reader.
[<ReferenceEquality>]
type Update =
    {
        /// The state at the reader's read before this one.
        StartState: EditorState
        State: EditorState
        /// The transactions between the two states, as the editor applied them: a transaction of another editor
        /// carries its changes and annotations only.
        Transactions: Transaction[]
        /// The changes of every transaction, composed.
        Changes: ChangeSet
        /// The effects of every transaction, in order. An effect's positions address the document its own
        /// transaction produces.
        Effects: StateEffect[]
    }

    member this.DocChanged = not (ChangeSet.isEmpty this.Changes)

    member this.SelectionSet =
        this.Transactions |> Array.exists (fun transaction -> transaction.Selection.IsSome)

[<Sealed>]
type internal Folded(state: EditorState, at: int64) =
    member _.State = state
    member _.At = at

[<Sealed>]
type internal UpdateCell(update: Update, at: int64) =
    member _.Update = update
    member _.At = at

/// A reader of an editor's transactions.
[<Sealed>]
type UpdateReader internal (memo: Memo<UpdateCell>) =
    /// Everything since the reader's last value. A tracked read: an effect that calls it runs again when the
    /// editor's state changes, and each transaction reaches it once.
    member _.Read() : Update = memo.Value.Update

/// One view of a document: its own extensions, selection, fields and facets over the document's text.
[<Sealed>]
type Editor internal (document: Document, extensions: Extension list, id: int) =
    let owner = Api.getOwner ()
    let cursor = document.RegisterReader ()

    let initial =
        Folded (
            EditorState.create
                {
                    Doc = document.Current
                    Selection = ValueNone
                    Extensions = extensions
                },
            cursor.At
        )

    /// A transaction as this editor applies it.
    let seen (transaction: Transaction) : Transaction =
        if transaction.Source = id then
            transaction
        elif transaction.Source = 0 then
            if transaction.Selection.IsNone && not transaction.ScrollIntoView then
                transaction
            else
                { transaction with
                    Selection = ValueNone
                    StartSelection = ValueNone
                    ScrollIntoView = false
                }
        else
            Transaction.asForeign transaction

    let fold =
        Api.createMemo (fun (previous: Folded voption) ->
            let seq = document.Head.Value
            let before = ValueOption.defaultValue initial previous

            if seq = before.At then
                before
            else
                let transactions = document.Since before.At
                let last = transactions.Length - 1
                let mutable state = before.State

                for index in 0..last do
                    let transaction = seen transactions[index]

                    let doc =
                        if not transaction.DocChanged then state.Doc
                        elif index = last then document.Current
                        else ChangeSet.apply state.Doc transaction.Changes

                    state <- EditorState.applyWithDoc doc transaction state

                cursor.At <- seq
                Folded (state, seq))

    let state = Api.createMemo (fun (_: EditorState voption) -> fold.Value.State)

    let selection =
        Api.createMemo (fun (_: EditorSelection voption) -> state.Value.Selection)

    let pageLines = Api.createSignal 20
    let slices = Dictionary<int, obj> ()

    let slice (key: int) (make: unit -> Memo<'T>) : Memo<'T> =
        let mutable held = null

        if slices.TryGetValue (key, &held) then
            held :?> Memo<'T>
        else
            let memo = Api.runWithOwner owner make
            slices[key] <- box memo
            memo

    /// A number that identifies the editor as the source of its transactions.
    member _.Id = id
    member _.Document = document
    /// The editor's state at the head of the document's log.
    member _.State: Memo<EditorState> = state
    member _.Text: Memo<Text> = document.Text
    /// The selection. Its readers stay asleep while a transaction leaves the selection where it is.
    member _.Selection: Memo<EditorSelection> = selection
    /// The number of lines a page motion moves by. A view writes the number of lines it shows.
    member _.PageLines: Signal<int> = pageLines

    /// The value of <c>field</c>, as a memo kept for the editor's life. The first call for a field runs outside a
    /// memo's body.
    member _.Field(field: StateField<'T>) : Memo<'T> =
        slice field.Id (fun () -> Api.createMemo (fun (_: 'T voption) -> EditorState.field field state.Value))

    /// The value of <c>facet</c>, as a memo kept for the editor's life. The first call for a facet runs outside a
    /// memo's body.
    member _.Facet(facet: Facet<'In, 'Out>) : Memo<'Out> =
        slice facet.Id (fun () -> Api.createMemo (fun (_: 'Out voption) -> EditorState.facet facet state.Value))

    /// Appends the transaction <c>specs</c> describe to the document's log, in one batch. A transaction that
    /// changes the document and carries a user event is dropped while <c>EditorState.readOnly</c> is true.
    /// <exception cref="T:System.ArgumentException">A change lies outside the document or overlaps another.</exception>
    member _.Dispatch(specs: TxSpec list) : unit =
        Api.batch (fun () ->
            let current = Api.untrack (fun () -> state.Value)
            let pending = Transaction.build current.Doc current.Selection specs

            let blocked =
                pending.DocChanged
                && (Transaction.annotation Annotations.userEvent pending).IsSome
                && EditorState.facet EditorState.readOnly current

            if not blocked then
                document.Append
                    { pending with
                        Base = document.Head.Peek
                        Source = id
                    }
                |> ignore)

    /// A reader of the editor's transactions, starting at the current state and owned by the current owner. The
    /// document's log keeps every transaction a reader has yet to read.
    member _.Updates() : UpdateReader =
        let readerCursor = document.RegisterReader ()
        let start = Api.untrack (fun () -> fold.Value)

        let memo =
            Api.createMemo (fun (previous: UpdateCell voption) ->
                let folded = fold.Value

                let struct (startState, at) =
                    match previous with
                    | ValueSome cell -> struct (cell.Update.State, cell.At)
                    | ValueNone -> struct (start.State, start.At)

                if folded.At = at then
                    match previous with
                    | ValueSome cell -> cell
                    | ValueNone ->
                        UpdateCell (
                            {
                                StartState = startState
                                State = startState
                                Transactions = Array.empty
                                Changes = ChangeSet.empty startState.Doc.Length
                                Effects = Array.empty
                            },
                            at
                        )
                else
                    let transactions = document.Since at |> Array.map seen
                    let mutable changes = transactions[0].Changes

                    for index in 1 .. transactions.Length - 1 do
                        changes <- ChangeSet.compose changes transactions[index].Changes

                    readerCursor.At <- folded.At

                    UpdateCell (
                        {
                            StartState = startState
                            State = folded.State
                            Transactions = transactions
                            Changes = changes
                            Effects = transactions |> Array.collect (fun transaction -> transaction.Effects)
                        },
                        folded.At
                    ))

        UpdateReader memo

[<RequireQualifiedAccess>]
module Editor =

    let mutable private lastId = 0

    /// An editor of <c>document</c> with a cursor at 0, owned by the current owner.
    let create (document: Document) (extensions: Extension list) : Editor =
        Editor (document, extensions, Interlocked.Increment &lastId)

    let private run (make: int -> EditorSelection -> HistoryState -> TxSpec list voption) (editor: Editor) : bool =
        let state = Api.untrack (fun () -> editor.State.Value)
        let history = Api.untrack (fun () -> editor.Document.History.Value)

        if EditorState.facet EditorState.readOnly state then
            false
        else
            match make editor.Id state.Selection history with
            | ValueSome specs ->
                editor.Dispatch specs
                true
            | ValueNone -> false

    /// Undoes the document's last history item. False with nothing to undo or a read-only state.
    let undo (editor: Editor) : bool = run HistoryState.undoSpec editor

    /// Redoes the item last undone. False with nothing to redo or a read-only state.
    let redo (editor: Editor) : bool = run HistoryState.redoSpec editor
