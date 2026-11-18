namespace Ranvier.Plume

open System
open System.Threading

/// How a document's history groups and bounds its items.
[<NoEquality; NoComparison>]
type HistoryConfig =
    {
        /// The number of items that can be undone.
        Depth: int
        /// Milliseconds after which an edit starts a new item.
        NewGroupDelay: int64
        /// Decides whether an edit joins the item before it; receives the transaction and whether its ranges touch
        /// that item's.
        JoinToEvent: Transaction -> bool -> bool
    }

[<RequireQualifiedAccess>]
module History =

    /// 1,000 items, a new item after 500 ms, and edits joined when their ranges touch.
    let defaults: HistoryConfig =
        {
            Depth = 1000
            NewGroupDelay = 500L
            JoinToEvent = fun _ adjacent -> adjacent
        }

    /// Set on a transaction made by undo (true) or redo (false).
    let undoOp: AnnotationType<bool> = Annotation.define ()

    let mutable private lastGroup = 0L

    /// A group id no other call returned. Transactions annotated with one id form one history item in each
    /// document they change.
    let newGroupId () : int64 = Interlocked.Increment &lastGroup

/// An edit that can be undone: the change set that undoes it, and where it sits among the positions of the document.
[<NoEquality; NoComparison>]
type internal HistoryItem =
    {
        /// The change that undoes the item, applying to the document as it is when the item is on top.
        Changes: ChangeSet
        /// Changes history left out since the item came on top, as the items below still have to be moved over them.
        Mapped: ChangeDesc voption
        /// The selection of `Source` in the document the item's undo produces.
        StartSelection: EditorSelection voption
        Source: int
        /// The position of the document before the item's edit, and after it.
        Before: int64
        After: int64
        Group: int64
    }

/// A stack of items bounded by a depth. Dropping the oldest item costs no copy: the stack is a recent list and an
/// older list of which the first `OlderLimit` items count.
[<NoEquality; NoComparison>]
type internal HistoryStack =
    {
        Recent: HistoryItem list
        RecentCount: int
        Older: HistoryItem list
        OlderLimit: int
    }

module internal HistoryStack =

    let empty: HistoryStack =
        {
            Recent = []
            RecentCount = 0
            Older = []
            OlderLimit = 0
        }

    let count (stack: HistoryStack) : int = stack.RecentCount + stack.OlderLimit

    let tryPop (stack: HistoryStack) : struct (HistoryItem * HistoryStack) voption =
        match stack.Recent with
        | top :: rest ->
            ValueSome (
                struct (top,
                        { stack with
                            Recent = rest
                            RecentCount = stack.RecentCount - 1
                        })
            )
        | [] ->
            match stack.Older with
            | top :: rest when stack.OlderLimit > 0 ->
                ValueSome (
                    struct (top,
                            { stack with
                                Older = rest
                                OlderLimit = stack.OlderLimit - 1
                            })
                )
            | _ -> ValueNone

    /// Puts back an item just popped, or its replacement.
    let restore (item: HistoryItem) (stack: HistoryStack) : HistoryStack =
        { stack with
            Recent = item :: stack.Recent
            RecentCount = stack.RecentCount + 1
        }

    let push (depth: int) (item: HistoryItem) (stack: HistoryStack) : HistoryStack =
        // An item beyond the depth leaves the count; its list goes once every item of it has.
        let olderLimit =
            if stack.RecentCount + stack.OlderLimit >= depth then
                max 0 (stack.OlderLimit - 1)
            else
                stack.OlderLimit

        let older = if olderLimit = 0 then [] else stack.Older

        if stack.RecentCount >= depth then
            {
                Recent = [ item ]
                RecentCount = 1
                Older = stack.Recent
                OlderLimit = depth - 1
            }
        else
            {
                Recent = item :: stack.Recent
                RecentCount = stack.RecentCount + 1
                Older = older
                OlderLimit = olderLimit
            }

    /// Moves the top item over `mapping`, a change history leaves out. The item records the mapping for the items
    /// below; an item with nothing left to undo is dropped and the next one is moved.
    let rec addMapping (mapping: ChangeDesc) (stack: HistoryStack) : HistoryStack =
        match tryPop stack with
        | ValueNone -> stack
        | ValueSome (struct (top, rest)) ->
            let changes = ChangeSet.map false mapping top.Changes
            let below = ChangeDesc.map true (ChangeSet.desc top.Changes) mapping

            let pending =
                match top.Mapped with
                | ValueSome earlier -> ChangeDesc.compose earlier below
                | ValueNone -> below

            if ChangeSet.isEmpty changes then
                addMapping pending rest
            else
                restore
                    { top with
                        Changes = changes
                        Mapped = ValueSome pending
                        StartSelection = top.StartSelection |> ValueOption.map (EditorSelection.map below)
                    }
                    rest

    /// The top item and the stack below it, moved over the changes the top item recorded.
    let tryPopMapped (stack: HistoryStack) : struct (HistoryItem * HistoryStack) voption =
        match tryPop stack with
        | ValueSome (struct (top, rest)) ->
            match top.Mapped with
            | ValueSome mapping -> ValueSome (struct (top, addMapping mapping rest))
            | ValueNone -> ValueSome (struct (top, rest))
        | ValueNone -> ValueNone

/// The history of a document as a fold over its transactions: the edits that can be undone and redone, and the
/// document's position among them.
[<NoEquality; NoComparison>]
type HistoryState =
    internal
        {
            Config: HistoryConfig
            Done: HistoryStack
            Undone: HistoryStack
            /// Raised by every change history leaves out; positions of different epochs are different texts.
            Epoch: int64
            Position: int64
            LastPosition: int64
            /// The time of the last edit recorded, for grouping.
            PrevTime: int64
        }

[<RequireQualifiedAccess>]
module HistoryState =

    let private noTime = Int64.MinValue / 2L

    let empty (config: HistoryConfig) : HistoryState =
        {
            Config = config
            Done = HistoryStack.empty
            Undone = HistoryStack.empty
            Epoch = 0L
            Position = 0L
            LastPosition = 0L
            PrevTime = noTime
        }

    /// The number of items that can be undone.
    let undoDepth (state: HistoryState) : int = HistoryStack.count state.Done
    /// The number of items that can be redone.
    let redoDepth (state: HistoryState) : int = HistoryStack.count state.Undone

    /// The document's position. Two equal positions of one document are the same text.
    let position (state: HistoryState) : struct (int64 * int64) = struct (state.Epoch, state.Position)

    let private isAdjacent (top: ChangeSet) (next: ChangeSet) : bool =
        let ranges = ResizeArray<struct (int * int)> ()
        ChangeDesc.iterChangedRanges (fun from to' _ _ -> ranges.Add (struct (from, to'))) (ChangeSet.desc top)
        let mutable adjacent = false

        ChangeDesc.iterChangedRanges
            (fun _ _ from to' ->
                for struct (start, finish) in ranges do
                    if to' >= start && from <= finish then
                        adjacent <- true)
            (ChangeSet.desc next)

        adjacent

    let private joinable (transaction: Transaction) : bool =
        (Transaction.annotation Annotations.userEvent transaction).IsNone
        || Transaction.isUserEvent "input.type" transaction
        || Transaction.isUserEvent "delete" transaction

    let private record (transaction: Transaction) (state: HistoryState) : HistoryState =
        let inverse = ChangeSet.invert transaction.Changes
        let position = state.LastPosition + 1L
        let time = ValueOption.defaultValue 0L (Transaction.annotation Annotations.time transaction)
        let group = ValueOption.defaultValue 0L (Transaction.annotation Annotations.group transaction)

        let isolated =
            ValueOption.defaultValue false (Transaction.annotation Annotations.isolateHistory transaction)

        let joined =
            match HistoryStack.tryPop state.Done with
            | ValueSome (struct (top, rest)) ->
                let joins =
                    if group <> 0L then
                        top.Group = group
                    else
                        top.Group = 0L
                        && not isolated
                        && joinable transaction
                        && time - state.PrevTime < state.Config.NewGroupDelay
                        && state.Config.JoinToEvent transaction (isAdjacent top.Changes inverse)

                if joins then
                    ValueSome (
                        HistoryStack.restore
                            { top with
                                Changes = ChangeSet.compose inverse top.Changes
                                After = position
                            }
                            rest
                    )
                else
                    ValueNone
            | ValueNone -> ValueNone

        let stack =
            match joined with
            | ValueSome stack -> stack
            | ValueNone ->
                HistoryStack.push
                    state.Config.Depth
                    {
                        Changes = inverse
                        Mapped = ValueNone
                        StartSelection = transaction.StartSelection
                        Source = transaction.Source
                        Before = state.Position
                        After = position
                        Group = group
                    }
                    state.Done

        { state with
            Done = stack
            Undone = HistoryStack.empty
            Position = position
            LastPosition = position
            PrevTime = (if isolated then noTime else time)
        }

    /// The item an undo or redo transaction leaves on the other stack.
    let private reverse (transaction: Transaction) (popped: HistoryItem) : HistoryItem =
        {
            Changes = ChangeSet.invert transaction.Changes
            Mapped = ValueNone
            StartSelection = transaction.StartSelection
            Source = transaction.Source
            Before = popped.Before
            After = popped.After
            Group = popped.Group
        }

    let private leaveOut (transaction: Transaction) (state: HistoryState) : HistoryState =
        let mapping = ChangeSet.desc transaction.Changes
        let position = state.LastPosition + 1L

        { state with
            Done = HistoryStack.addMapping mapping state.Done
            Undone = HistoryStack.addMapping mapping state.Undone
            Epoch = state.Epoch + 1L
            Position = position
            LastPosition = position
        }

    /// <c>state</c> after <c>transaction</c>.
    let apply (transaction: Transaction) (state: HistoryState) : HistoryState =
        if not transaction.DocChanged then
            // A selection set between two edits ends the item the first belongs to.
            if transaction.Selection.IsSome && state.PrevTime <> noTime then
                { state with PrevTime = noTime }
            else
                state
        else
            match Transaction.annotation History.undoOp transaction with
            | ValueSome true ->
                match HistoryStack.tryPopMapped state.Done with
                | ValueSome (struct (popped, rest)) ->
                    { state with
                        Done = rest
                        Undone = HistoryStack.restore (reverse transaction popped) state.Undone
                        Position = popped.Before
                        PrevTime = noTime
                    }
                | ValueNone -> leaveOut transaction state
            | ValueSome false ->
                match HistoryStack.tryPopMapped state.Undone with
                | ValueSome (struct (popped, rest)) ->
                    { state with
                        Done = HistoryStack.push state.Config.Depth (reverse transaction popped) state.Done
                        Undone = rest
                        Position = popped.After
                        PrevTime = noTime
                    }
                | ValueNone -> leaveOut transaction state
            | ValueNone ->
                if Transaction.annotation Annotations.addToHistory transaction = ValueSome false then
                    leaveOut transaction state
                else
                    record transaction state

    let private spec (isUndo: bool) (source: int) (selection: EditorSelection) (stack: HistoryStack) : TxSpec list voption =
        match HistoryStack.tryPop stack with
        | ValueSome (struct (top, _)) ->
            let after =
                match top.StartSelection with
                | ValueSome start when top.Source = source -> start
                | _ -> EditorSelection.map (ChangeSet.desc top.Changes) selection

            ValueSome
                [
                    Tx.changeSet top.Changes
                    Tx.selection after
                    Tx.annotate (History.undoOp.Of isUndo)
                    Tx.userEvent (if isUndo then "undo" else "redo")
                    Tx.scrollIntoView
                ]
        | ValueNone -> ValueNone

    /// The transaction that undoes the last item, for the editor <c>source</c> whose selection is
    /// <c>selection</c>; <c>ValueNone</c> with no item to undo. The editor that made the item gets back the selection
    /// it had before it.
    let undoSpec (source: int) (selection: EditorSelection) (state: HistoryState) : TxSpec list voption =
        spec true source selection state.Done

    /// The transaction that redoes the last item undone; <c>ValueNone</c> with no item to redo.
    let redoSpec (source: int) (selection: EditorSelection) (state: HistoryState) : TxSpec list voption =
        spec false source selection state.Undone
