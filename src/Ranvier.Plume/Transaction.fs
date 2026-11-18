namespace Ranvier.Plume

open System
open System.Threading

/// A typed piece of metadata on a transaction.
[<Sealed>]
type Annotation internal (typeId: int, value: obj) =
    member internal _.TypeId = typeId
    member internal _.Value = value

/// A kind of annotation holding a <c>'T</c>.
[<Sealed>]
type AnnotationType<'T> internal (id: int) =
    member internal _.Id = id
    /// An annotation of this kind.
    member _.Of(value: 'T) : Annotation = Annotation (id, box value)

[<RequireQualifiedAccess>]
module Annotation =
    let mutable private lastId = 0

    /// A new kind of annotation.
    let define<'T> () : AnnotationType<'T> =
        AnnotationType<'T> (Interlocked.Increment &lastId)

/// A kind of state effect, without its value type.
[<AbstractClass>]
type StateEffectType internal () =
    abstract MapBoxed: value: obj * changes: ChangeDesc -> obj voption

/// A typed side effect carried by a transaction, for state fields and views to act on.
[<Sealed>]
type StateEffect internal (effectType: StateEffectType, value: obj) =
    member internal _.Type = effectType
    member internal _.Value = value

/// A kind of state effect holding a <c>'T</c>.
[<Sealed>]
type StateEffectType<'T> internal (map: 'T -> ChangeDesc -> 'T voption) =
    inherit StateEffectType ()

    override _.MapBoxed(value: obj, changes: ChangeDesc) : obj voption =
        match map (unbox<'T> value) changes with
        | ValueSome mapped -> ValueSome (box mapped)
        | ValueNone -> ValueNone

    /// An effect of this kind.
    member this.Of(value: 'T) : StateEffect = StateEffect (this, box value)

type StateEffect with
    /// True when the effect is of kind <c>effectType</c>.
    member this.Is(effectType: StateEffectType<'T>) : bool =
        obj.ReferenceEquals (this.Type, effectType)

[<RequireQualifiedAccess>]
module StateEffect =

    /// A new kind of effect. <c>map</c> moves a value over a change made before the effect applies; an effect
    /// mapped to <c>ValueNone</c> is dropped.
    let define<'T> (map: 'T -> ChangeDesc -> 'T voption) : StateEffectType<'T> = StateEffectType<'T> map

    /// A new kind of effect whose value holds no position.
    let defineSimple<'T> () : StateEffectType<'T> =
        StateEffectType<'T> (fun value _ -> ValueSome value)

    /// The value of <c>effect</c> when it is of kind <c>effectType</c>.
    let tryValue (effectType: StateEffectType<'T>) (effect: StateEffect) : 'T voption =
        if obj.ReferenceEquals (effect.Type, effectType) then
            ValueSome (unbox<'T> effect.Value)
        else
            ValueNone

    /// <c>effects</c> moved over <c>changes</c>.
    let mapEffects (changes: ChangeDesc) (effects: StateEffect[]) : StateEffect[] =
        if effects.Length = 0 || ChangeDesc.isEmpty changes then
            effects
        else
            effects
            |> Array.choose (fun effect ->
                match effect.Type.MapBoxed (effect.Value, changes) with
                | ValueSome value when obj.ReferenceEquals (value, effect.Value) -> Some effect
                | ValueSome value -> Some (StateEffect (effect.Type, value))
                | ValueNone -> None)

/// One atomic edit, as data.
[<ReferenceEquality>]
type Transaction =
    {
        /// The transaction's position in its document's log; 0 before it is appended.
        Seq: int64
        /// The position of the log the transaction was written against.
        Base: int64
        /// The editor that made the transaction; 0 for a source outside every editor.
        Source: int
        Changes: ChangeSet
        /// The selection of the source after the transaction, in the document it produces.
        Selection: EditorSelection voption
        /// The selection of the source before the transaction.
        StartSelection: EditorSelection voption
        Effects: StateEffect[]
        Annotations: Annotation[]
        /// True when the source's view scrolls its main selection into view.
        ScrollIntoView: bool
    }

    member this.DocChanged = not (ChangeSet.isEmpty this.Changes)

/// The annotations the library reads.
[<RequireQualifiedAccess>]
module Annotations =

    /// When the transaction was made, in milliseconds on its document's clock.
    let time: AnnotationType<int64> = Annotation.define ()

    /// What the user did, as dot-separated words from general to specific: "input.type", "delete.backward".
    let userEvent: AnnotationType<string> = Annotation.define ()

    /// False on a transaction that history leaves out.
    let addToHistory: AnnotationType<bool> = Annotation.define ()

    /// The history item the transaction belongs to. Transactions with one group id form one item in each document.
    let group: AnnotationType<int64> = Annotation.define ()

    /// True on a transaction that stands alone in history.
    let isolateHistory: AnnotationType<bool> = Annotation.define ()

/// A part of a transaction under construction.
type TxSpec =
    internal
    | ChangeSpecs of ChangeSpec list
    | ChangeSetSpec of ChangeSet
    | SelectionSpec of EditorSelection
    | EffectSpec of StateEffect
    | AnnotationSpec of Annotation
    | ScrollIntoViewSpec

/// The parts a transaction is built from.
[<RequireQualifiedAccess>]
module Tx =

    /// A replacement, addressing the document before the transaction.
    let change (spec: ChangeSpec) : TxSpec = ChangeSpecs [ spec ]

    /// Replacements, each addressing the document before the transaction.
    let changes (specs: ChangeSpec list) : TxSpec = ChangeSpecs specs

    /// A change set for the document before the transaction. A transaction takes one, and no other change.
    let changeSet (changes: ChangeSet) : TxSpec = ChangeSetSpec changes

    /// The selection after the transaction, addressing the document it produces.
    let selection (selection: EditorSelection) : TxSpec = SelectionSpec selection

    let effect (effect: StateEffect) : TxSpec = EffectSpec effect
    let annotate (annotation: Annotation) : TxSpec = AnnotationSpec annotation
    let userEvent (event: string) : TxSpec = AnnotationSpec (Annotations.userEvent.Of event)
    /// Scrolls the main selection into view in the view of the editor that dispatches the transaction.
    let scrollIntoView: TxSpec = ScrollIntoViewSpec
    let group (id: int64) : TxSpec = AnnotationSpec (Annotations.group.Of id)
    let addToHistory (record: bool) : TxSpec = AnnotationSpec (Annotations.addToHistory.Of record)
    let isolateHistory: TxSpec = AnnotationSpec (Annotations.isolateHistory.Of true)

[<RequireQualifiedAccess>]
module Transaction =

    /// The value of the transaction's annotation of kind <c>annotationType</c>.
    let annotation (annotationType: AnnotationType<'T>) (transaction: Transaction) : 'T voption =
        let annotations = transaction.Annotations
        let mutable result = ValueNone
        let mutable index = 0

        while index < annotations.Length do
            if annotations[index].TypeId = annotationType.Id then
                result <- ValueSome (unbox<'T> annotations[index].Value)
                index <- annotations.Length
            else
                index <- index + 1

        result

    /// True when the transaction's user event is <c>event</c> or more specific: "input" matches "input.type".
    let isUserEvent (event: string) (transaction: Transaction) : bool =
        match annotation Annotations.userEvent transaction with
        | ValueSome actual ->
            actual = event
            || (actual.Length > event.Length
                && actual.StartsWith (event, StringComparison.Ordinal)
                && actual[event.Length] = '.')
        | ValueNone -> false

    /// The transaction as an editor other than its source applies it: the changes and annotations, without the
    /// selection, the effects and the scroll request.
    let asForeign (transaction: Transaction) : Transaction =
        if
            transaction.Selection.IsNone
            && transaction.StartSelection.IsNone
            && transaction.Effects.Length = 0
            && not transaction.ScrollIntoView
        then
            transaction
        else
            { transaction with
                Selection = ValueNone
                StartSelection = ValueNone
                Effects = Array.empty
                ScrollIntoView = false
            }

    /// The transaction `specs` describe for a document of `length` characters, where `make` builds the change set of
    /// its replacements and `selection` is the selection of its source. Its positions in the log are 0.
    let internal buildWith (length: int) (make: ChangeSpec list -> ChangeSet) (selection: EditorSelection voption) (specs: TxSpec list) : Transaction =
        let mutable changeSpecs: ChangeSpec list = []
        let mutable changeSet: ChangeSet voption = ValueNone
        let mutable after: EditorSelection voption = ValueNone
        let mutable scroll = false
        let effects = ResizeArray<StateEffect> ()
        let annotations = ResizeArray<Annotation> ()

        for spec in specs do
            match spec with
            | ChangeSpecs added -> changeSpecs <- changeSpecs @ added
            | ChangeSetSpec set ->
                if changeSet.IsSome then
                    raise (ArgumentException "A transaction takes one change set.")

                changeSet <- ValueSome set
            | SelectionSpec selected -> after <- ValueSome selected
            | EffectSpec effect -> effects.Add effect
            | AnnotationSpec annotation -> annotations.Add annotation
            | ScrollIntoViewSpec -> scroll <- true

        let changes =
            match changeSet with
            | ValueSome set ->
                if not (List.isEmpty changeSpecs) then
                    raise (ArgumentException "A transaction with a change set takes no other change.")

                if ChangeSet.length set <> length then
                    raise (
                        ArgumentException (
                            String.Concat (
                                "A change set for a document of length ",
                                string (ChangeSet.length set),
                                " was given for one of length ",
                                string length,
                                "."
                            )
                        )
                    )

                set
            | ValueNone ->
                if List.isEmpty changeSpecs then
                    ChangeSet.empty length
                else
                    make changeSpecs

        {
            Seq = 0L
            Base = 0L
            Source = 0
            Changes = changes
            Selection = after
            StartSelection =
                if ChangeSet.isEmpty changes then
                    ValueNone
                else
                    selection
            Effects = effects.ToArray ()
            Annotations = annotations.ToArray ()
            ScrollIntoView = scroll
        }

    /// The transaction `specs` describe for `doc` and the selection of its source.
    let internal build (doc: Text) (selection: EditorSelection) (specs: TxSpec list) : Transaction =
        buildWith doc.Length (ChangeSet.ofSpecs doc) (ValueSome selection) specs
