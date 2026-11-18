namespace Ranvier.Plume

open System
open Ranvier

/// What a document starts from.
[<NoEquality; NoComparison>]
type DocumentConfig =
    {
        Text: Text
        History: HistoryConfig
        /// Milliseconds on a monotonic clock, for the time of each transaction.
        Clock: unit -> int64
    }

/// A reader's position in a log. The log keeps every transaction after the lowest position.
[<Sealed>]
type internal ReaderCursor(at: int64) =
    member val At = at with get, set

/// A reader of a document's log.
[<Sealed>]
type LogReader internal (memo: Memo<struct (Transaction[] * int64)>) =
    /// The transactions appended since the reader's last value, in order. A tracked read.
    member _.Read() : Transaction[] =
        let struct (transactions, _) = memo.Value
        transactions

/// One file: the log of its transactions, its saved text and its current text. The log is the source of truth;
/// the text, the history and every editor of the document follow it.
[<Sealed>]
type Document internal (config: DocumentConfig) =
    let graph = Graph.Current
    // The clock alone is kept of the configuration: kept whole, it would hold the text the document started with.
    let clock = config.Clock
    let head = Api.createSignal 0L
    let savedVersion = Api.createSignal 0
    let mutable current = config.Text
    let mutable saved = config.Text
    let mutable history = HistoryState.empty config.History
    let mutable savedPosition = HistoryState.position history
    // The transactions after the lowest reader position; `log[0]` has sequence number `firstSeq`.
    let log = ResizeArray<Transaction> ()
    let mutable firstSeq = 1L
    let readers = ResizeArray<ReaderCursor> ()

    let text =
        Api.createMemo (fun (_: Text voption) ->
            head.Value |> ignore
            current)

    let savedText =
        Api.createMemo (fun (_: Text voption) ->
            savedVersion.Value |> ignore
            saved)

    let historyState =
        Api.createMemo (fun (_: HistoryState voption) ->
            head.Value |> ignore
            history)

    let isDirty =
        Api.createMemo (fun (_: bool voption) ->
            head.Value |> ignore
            savedVersion.Value |> ignore
            HistoryState.position history <> savedPosition)

    let undoDepth =
        Api.createMemo (fun (_: int voption) -> HistoryState.undoDepth historyState.Value)

    let redoDepth =
        Api.createMemo (fun (_: int voption) -> HistoryState.redoDepth historyState.Value)

    let indexOf (seq: int64) : int =
        let index = seq - firstSeq

        if index < 0L || index > int64 log.Count then
            raise (
                InvalidOperationException (
                    String.Concat ("The log holds positions ", string (firstSeq - 1L), " to ", string head.Peek, "; position ", string (seq - 1L), " was asked for.")
                )
            )

        int index

    let trim () =
        let mutable keepFrom = head.Peek + 1L

        for reader in readers do
            keepFrom <- min keepFrom (reader.At + 1L)

        let dropped = int (keepFrom - firstSeq)

        if dropped > 0 then
            log.RemoveRange (0, min dropped log.Count)
            firstSeq <- keepFrom

    /// `pending`, written against an older position, moved over the transactions appended since.
    let rebase (pending: Transaction) (at: int64) : Transaction =
        let first = indexOf (pending.Base + 1L)
        let mutable over = ChangeSet.desc log[first].Changes

        for index in first + 1 .. log.Count - 1 do
            over <- ChangeDesc.compose over (ChangeSet.desc log[index].Changes)

        // Positions in the document `pending` produces move over `over` as it applies after `pending`.
        let after = ChangeDesc.map true (ChangeSet.desc pending.Changes) over

        { pending with
            Base = at
            Changes = ChangeSet.stamp current (ChangeSet.mapUnstamped false over pending.Changes)
            Selection = pending.Selection |> ValueOption.map (EditorSelection.map after)
            StartSelection = pending.StartSelection |> ValueOption.map (EditorSelection.map over)
            Effects = StateEffect.mapEffects after pending.Effects
        }

    /// The position of the last transaction; 0 for a document without one. Writing it is the log's own step.
    member _.Head: Signal<int64> = head
    /// The current text.
    member _.Text: Memo<Text> = text
    /// The text when the document was created or last marked saved.
    member _.Saved: Memo<Text> = savedText
    /// True when the current text differs from the saved text. Undoing back to the saved position gives false.
    member _.IsDirty: Memo<bool> = isDirty
    member _.History: Memo<HistoryState> = historyState
    /// The number of history items that can be undone.
    member _.UndoDepth: Memo<int> = undoDepth
    /// The number of history items that can be redone.
    member _.RedoDepth: Memo<int> = redoDepth
    member internal _.Clock = clock
    /// The current text, read without tracking.
    member internal _.Current = current

    /// Takes the current text as the saved text.
    member _.MarkSaved() =
        saved <- current
        savedPosition <- HistoryState.position history
        savedVersion.Value <- savedVersion.Peek + 1

    /// The transactions after position `seq`.
    member internal _.Since(seq: int64) : Transaction[] =
        let start = indexOf (seq + 1L)

        if start >= log.Count then
            Array.empty
        else
            log.GetRange(start, log.Count - start).ToArray ()

    /// A cursor at the head, removed when the current owner is disposed.
    member internal _.RegisterReader() : ReaderCursor =
        let cursor = ReaderCursor head.Peek
        readers.Add cursor
        Api.onCleanup (fun () -> readers.Remove cursor |> ignore)
        cursor

    /// The number of transactions the log holds.
    member internal _.LogLength = log.Count

    /// Appends `pending` to the log: moves it over the transactions appended since its base, gives it its position
    /// and time, and advances the text, the history and the head.
    member internal _.Append(pending: Transaction) : Transaction =
        let at = head.Peek
        let rebased = if pending.Base = at then pending else rebase pending at

        let annotations =
            if (Transaction.annotation Annotations.time rebased).IsSome then
                rebased.Annotations
            else
                Array.append rebased.Annotations [| Annotations.time.Of (clock ()) |]

        let transaction =
            { rebased with
                Seq = at + 1L
                Annotations = annotations
            }

        if transaction.DocChanged then
            current <- ChangeSet.apply current transaction.Changes

        history <- HistoryState.apply transaction history
        log.Add transaction
        trim ()
        head.Value <- transaction.Seq
        transaction

    /// Appends the transaction <c>specs</c> describe, from a source outside every editor, written against the
    /// document at position <c>baseSeq</c>. Its changes are moved over the transactions appended since. Runs on the
    /// graph's thread.
    /// <exception cref="T:System.InvalidOperationException">The log holds no transaction of position
    /// <c>baseSeq + 1</c>: every reader has passed it.</exception>
    member this.DispatchAt(baseSeq: int64, specs: TxSpec list) : unit =
        Api.batch (fun () ->
            let at = head.Peek

            let pending =
                if baseSeq = at then
                    Transaction.buildWith current.Length (ChangeSet.ofSpecs current) ValueNone specs
                else
                    let length = ChangeSet.length log[indexOf (baseSeq + 1L)].Changes
                    Transaction.buildWith length (ChangeSet.ofSpecsUnstamped length) ValueNone specs

            this.Append { pending with Base = baseSeq } |> ignore)

    /// Appends the transaction <c>specs</c> describe, from a source outside every editor. Every editor of the
    /// document applies its changes and its effects. Runs on the graph's thread.
    member this.Dispatch(specs: TxSpec list) : unit = this.DispatchAt (head.Peek, specs)

    /// <c>Dispatch</c> from any thread: the transaction is appended when the graph next runs posted work.
    member this.Post(specs: TxSpec list) : unit =
        graph.Dispatch (Action (fun () -> this.Dispatch specs))

    /// <c>DispatchAt</c> from any thread.
    member this.PostAt(baseSeq: int64, specs: TxSpec list) : unit =
        graph.Dispatch (Action (fun () -> this.DispatchAt (baseSeq, specs)))

    /// A reader of the log, starting at the head and owned by the current owner. The log keeps every transaction a
    /// reader has yet to read, so a reader is read regularly or disposed.
    member this.Reader() : LogReader =
        let cursor = this.RegisterReader ()

        let memo =
            Api.createMemo (fun (previous: struct (Transaction[] * int64) voption) ->
                let seq = head.Value

                let from =
                    match previous with
                    | ValueSome (struct (_, at)) -> at
                    | ValueNone -> cursor.At

                let transactions = this.Since from
                cursor.At <- seq
                struct (transactions, seq))

        LogReader memo

[<RequireQualifiedAccess>]
module Document =

    /// A document of <c>config.Text</c>, with its nodes owned by the current owner.
    let createWith (config: DocumentConfig) : Document = Document config

    /// A document of <c>text</c> with <c>History.defaults</c> and the system's tick clock.
    let create (text: Text) : Document =
        Document
            {
                Text = text
                History = History.defaults
                Clock = fun () -> Environment.TickCount64
            }
