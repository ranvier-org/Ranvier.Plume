namespace Ranvier.Plume

open System
open System.Collections.Generic
open System.Threading

/// The order facet inputs take: inputs of a higher precedence come first, and within one precedence the order of
/// the extension list holds.
type Prec =
    | Highest = 0
    | High = 1
    | Default = 2
    | Low = 3
    | Lowest = 4

module internal Ids =
    let mutable private last = 0
    let next () : int = Interlocked.Increment &last

/// A facet, without its value types.
[<AbstractClass>]
type FacetBase internal (id: int) =
    member internal _.Id = id
    abstract CombineBoxed: inputs: obj[] -> obj

/// A state field, without its value type.
and [<AbstractClass>] FieldBase internal (id: int) =
    member internal _.Id = id
    abstract CreateBoxed: state: EditorState -> obj
    abstract UpdateBoxed: value: obj * transaction: Transaction -> obj
    abstract Provided: Extension list

/// A part of a configuration that a transaction can replace.
and [<Sealed>] Compartment internal (id: int) =
    member internal _.Id = id

/// A value added to an editor's configuration: a facet input, a state field, or a group of these.
and Extension =
    internal
    | Many of Extension list
    | WithPrec of Prec * Extension
    | FacetInput of FacetBase * obj
    | FacetComputed of FacetBase * (EditorState -> obj)
    | Field of FieldBase
    | InCompartment of Compartment * Extension

and [<NoEquality; NoComparison>] internal FacetProvider =
    | StaticInput of obj
    | ComputedInput of (EditorState -> obj)

/// The inputs of a facet in a configuration. `Index` is the facet's place among the facets computed per state, or
/// -1 when every input is a constant and `Static` holds the combined value.
and [<Sealed; AllowNullLiteral>] internal FacetSlot(facet: FacetBase, providers: FacetProvider[], index: int) =
    member _.Facet = facet
    member _.Providers = providers
    member _.Index = index
    member val Static: obj = null with get, set

and [<Sealed; AllowNullLiteral>] internal FacetEntry(inputs: obj[], output: obj) =
    member _.Inputs = inputs
    member _.Output = output

and [<Sealed>] internal Configuration
    (
        root: Extension,
        compartments: Dictionary<int, Extension>,
        fields: FieldBase[],
        fieldIndex: Dictionary<int, int>,
        facets: Dictionary<int, FacetSlot>,
        dynamicCount: int
    ) =
    let defaults = Dictionary<int, obj> ()
    member _.Root = root
    /// The content of each compartment, by compartment id.
    member _.Compartments = compartments
    member _.Fields = fields
    member _.FieldIndex = fieldIndex
    member _.Facets = facets
    member _.DynamicCount = dynamicCount

    /// The value of a facet without inputs.
    member _.Default(facet: FacetBase) : obj =
        lock defaults (fun () ->
            let mutable value = null

            if not (defaults.TryGetValue (facet.Id, &value)) then
                value <- facet.CombineBoxed Array.empty
                defaults[facet.Id] <- value

            value)

/// The state of an editor at one position of its document's log: the text, the selection, and the values of the
/// configured fields and facets. Immutable.
and [<Sealed>] EditorState internal (config: Configuration, doc: Text, selection: EditorSelection, fields: obj[], previous: FacetEntry[]) =
    let dynamic: FacetEntry[] = Array.zeroCreate config.DynamicCount

    member _.Doc = doc
    member _.Selection = selection
    member internal _.Config = config
    member internal _.Fields = fields
    member internal _.Dynamic = dynamic

    member internal this.FacetBoxed(facet: FacetBase) : obj =
        let mutable slot: FacetSlot = null

        if not (config.Facets.TryGetValue (facet.Id, &slot)) then
            config.Default facet
        elif slot.Index < 0 then
            slot.Static
        else
            let held = dynamic[slot.Index]

            if not (isNull held) then
                held.Output
            else
                let providers = slot.Providers
                let inputs = Array.zeroCreate<obj> providers.Length

                for index in 0 .. providers.Length - 1 do
                    inputs[index] <-
                        match providers[index] with
                        | StaticInput value -> value
                        | ComputedInput compute -> compute this

                let before =
                    if isNull previous then
                        null
                    else
                        previous[slot.Index]

                // Equal inputs keep the output of the state before, so a reader comparing outputs stays asleep.
                let entry =
                    if
                        not (isNull before)
                        && Array.forall2 (fun (a: obj) (b: obj) -> Object.Equals (a, b)) before.Inputs inputs
                    then
                        before
                    else
                        FacetEntry (inputs, facet.CombineBoxed inputs)

                dynamic[slot.Index] <- entry
                entry.Output

    member internal _.FieldBoxed(field: FieldBase) : obj voption =
        let mutable index = 0

        if config.FieldIndex.TryGetValue (field.Id, &index) then
            ValueSome fields[index]
        else
            ValueNone

/// A point where extensions add inputs, combined into one value an editor reads.
[<Sealed>]
type Facet<'In, 'Out> internal (id: int, combine: 'In[] -> 'Out) =
    inherit FacetBase (id)
    override _.CombineBoxed(inputs: obj[]) : obj = box (combine (Array.map unbox<'In> inputs))
    /// An extension that adds <c>value</c> to the facet.
    member this.Of(value: 'In) : Extension = FacetInput (this, box value)

/// How a state field starts and how a transaction changes it.
type StateFieldSpec<'T> =
    {
        Create: EditorState -> 'T
        Update: 'T -> Transaction -> 'T
    }

/// A value kept in the editor state and updated by every transaction.
[<Sealed>]
type StateField<'T> internal (id: int, spec: StateFieldSpec<'T>, same: 'T -> 'T -> bool, provide: (StateField<'T> -> Extension) list) as this =
    inherit FieldBase (id)
    let provided = lazy (provide |> List.map (fun make -> make this))
    override _.CreateBoxed(state: EditorState) : obj = box (spec.Create state)

    override _.UpdateBoxed(value: obj, transaction: Transaction) : obj =
        let before = unbox<'T> value
        let after = spec.Update before transaction
        if same before after then value else box after

    override _.Provided = provided.Value

type Compartment with
    /// An extension holding <c>content</c> as the compartment's first content.
    member this.Of(content: Extension) : Extension = InCompartment (this, content)

module internal Configuration =

    /// The configuration of `root`, where `overrides` gives the content of compartments already reconfigured.
    let resolve (root: Extension) (overrides: Dictionary<int, Extension>) : Configuration =
        let buckets = Array.init 5 (fun _ -> ResizeArray<Extension> ())
        let compartments = Dictionary<int, Extension> ()
        let seenFields = HashSet<int> ()

        let rec visit (prec: int) (extension: Extension) =
            match extension with
            | Many items ->
                for item in items do
                    visit prec item
            | WithPrec (inner, content) -> visit (int inner) content
            | InCompartment (compartment, initial) ->
                if compartments.ContainsKey compartment.Id then
                    raise (InvalidOperationException "A compartment appears twice in one configuration.")

                let content =
                    match overrides.TryGetValue compartment.Id with
                    | true, held -> held
                    | _ -> initial

                compartments[compartment.Id] <- content
                visit prec content
            | Field field ->
                if seenFields.Add field.Id then
                    buckets[prec].Add extension

                    for provided in field.Provided do
                        visit prec provided
            | FacetInput _
            | FacetComputed _ -> buckets[prec].Add extension

        visit (int Prec.Default) root

        let fields = ResizeArray<FieldBase> ()
        let fieldIndex = Dictionary<int, int> ()
        let providers = Dictionary<int, ResizeArray<FacetProvider>> ()
        let facetOrder = ResizeArray<FacetBase> ()

        let provide (facet: FacetBase) (provider: FacetProvider) =
            let mutable held: ResizeArray<FacetProvider> = null

            if not (providers.TryGetValue (facet.Id, &held)) then
                held <- ResizeArray<FacetProvider> ()
                providers[facet.Id] <- held
                facetOrder.Add facet

            held.Add provider

        for bucket in buckets do
            for extension in bucket do
                match extension with
                | Field field ->
                    fieldIndex[field.Id] <- fields.Count
                    fields.Add field
                | FacetInput (facet, value) -> provide facet (StaticInput value)
                | FacetComputed (facet, compute) -> provide facet (ComputedInput compute)
                | _ -> ()

        let facets = Dictionary<int, FacetSlot> ()
        let mutable dynamicCount = 0

        for facet in facetOrder do
            let inputs = providers[facet.Id].ToArray ()

            let constant =
                inputs
                |> Array.forall (fun provider ->
                    match provider with
                    | StaticInput _ -> true
                    | ComputedInput _ -> false)

            if constant then
                let slot = FacetSlot (facet, inputs, -1)

                slot.Static <-
                    facet.CombineBoxed (
                        inputs
                        |> Array.map (fun provider ->
                            match provider with
                            | StaticInput value -> value
                            | ComputedInput _ -> null)
                    )

                facets[facet.Id] <- slot
            else
                facets[facet.Id] <- FacetSlot (facet, inputs, dynamicCount)
                dynamicCount <- dynamicCount + 1

        Configuration (root, compartments, fields.ToArray (), fieldIndex, facets, dynamicCount)

[<RequireQualifiedAccess>]
module Extension =

    /// The extensions of <c>extensions</c>, in order.
    let many (extensions: Extension list) : Extension = Many extensions

    /// <c>extension</c> with its facet inputs at precedence <c>prec</c>.
    let prec (prec: Prec) (extension: Extension) : Extension = WithPrec (prec, extension)

[<RequireQualifiedAccess>]
module Facet =

    /// A new facet. <c>combine</c> receives the inputs in precedence order, and an empty array when no extension
    /// adds one.
    let define (combine: 'In[] -> 'Out) : Facet<'In, 'Out> =
        Facet<'In, 'Out> (Ids.next (), combine)

    /// An extension that adds <c>compute state</c> to <c>facet</c>, computed again for each state.
    let compute (facet: Facet<'In, 'Out>) (compute: EditorState -> 'In) : Extension =
        FacetComputed (facet, (fun state -> box (compute state)))

    /// An extension that adds <c>get value</c> to <c>facet</c>, where <c>value</c> is the value of <c>field</c>.
    /// The extension holds the field.
    let from (facet: Facet<'In, 'Out>) (field: StateField<'T>) (get: 'T -> 'In) : Extension =
        Many
            [
                Field field
                FacetComputed (facet, (fun state -> box (get (unbox<'T> (state.FieldBoxed field).Value))))
            ]

[<RequireQualifiedAccess>]
module StateField =

    let private sameByDefault<'T> : 'T -> 'T -> bool =
        if typeof<'T>.IsValueType then
            let comparer = EqualityComparer<'T>.Default
            fun a b -> comparer.Equals (a, b)
        else
            fun a b -> obj.ReferenceEquals (box a, box b)

    /// A new field. A field's update receives the transaction; a value that depends on the text is a computed
    /// facet.
    let define (spec: StateFieldSpec<'T>) : StateField<'T> =
        StateField<'T> (Ids.next (), spec, sameByDefault, [])

    /// A new field. <c>compare</c> returns true for two values a reader takes as one; <c>provide</c> lists the
    /// extensions added with the field.
    let defineWith (compare: 'T -> 'T -> bool) (provide: (StateField<'T> -> Extension) list) (spec: StateFieldSpec<'T>) : StateField<'T> =
        StateField<'T> (Ids.next (), spec, compare, provide)

    /// The extension that adds <c>field</c> to a configuration.
    let extension (field: StateField<'T>) : Extension = Field field

module internal StateEffects =
    let reconfigure: StateEffectType<Extension> = StateEffect.defineSimple ()
    let appendConfig: StateEffectType<Extension> = StateEffect.defineSimple ()
    let compartment: StateEffectType<struct (Compartment * Extension)> = StateEffect.defineSimple ()

type Compartment with
    /// An effect that replaces the compartment's content.
    member this.Reconfigure(content: Extension) : StateEffect =
        StateEffects.compartment.Of (struct (this, content))

[<RequireQualifiedAccess>]
module Compartment =
    let create () : Compartment = Compartment (Ids.next ())

/// What an editor state starts from.
type EditorStateConfig =
    {
        Doc: Text
        /// The selection, or a cursor at 0.
        Selection: EditorSelection voption
        Extensions: Extension list
    }

[<RequireQualifiedAccess>]
module EditorState =

    let private first (fallback: 'T) : 'T[] -> 'T =
        fun values -> if values.Length > 0 then values[0] else fallback

    /// The width of a tab in columns. Default 4.
    let tabSize: Facet<int, int> = Facet.define (first 4)
    /// The text one level of indentation inserts. Default four spaces.
    let indentUnit: Facet<string, string> = Facet.define (first "    ")
    /// The line break inserted text uses. Default "\n".
    let lineSeparator: Facet<string, string> = Facet.define (first "\n")
    /// True when user events leave the document alone. Default false.
    let readOnly: Facet<bool, bool> = Facet.define (first false)
    /// True when a selection keeps more than one range. Default false.
    let allowMultipleSelections: Facet<bool, bool> = Facet.define (Array.exists id)

    /// An effect that replaces the whole configuration. Fields of the new configuration keep their values.
    let reconfigure: StateEffectType<Extension> = StateEffects.reconfigure
    /// An effect that adds an extension to the configuration.
    let appendConfig: StateEffectType<Extension> = StateEffects.appendConfig

    /// The value of <c>facet</c> in <c>state</c>.
    let facet (facet: Facet<'In, 'Out>) (state: EditorState) : 'Out = unbox<'Out> (state.FacetBoxed facet)

    /// The value of <c>field</c> in <c>state</c>, or <c>ValueNone</c> when the configuration lacks the field.
    let tryField (field: StateField<'T>) (state: EditorState) : 'T voption =
        match state.FieldBoxed field with
        | ValueSome value -> ValueSome (unbox<'T> value)
        | ValueNone -> ValueNone

    /// The value of <c>field</c> in <c>state</c>.
    /// <exception cref="T:System.InvalidOperationException">The configuration lacks the field.</exception>
    let field (field: StateField<'T>) (state: EditorState) : 'T =
        match state.FieldBoxed field with
        | ValueSome value -> unbox<'T> value
        | ValueNone -> raise (InvalidOperationException "The state's configuration lacks the field.")

    let private fits (doc: Text) (selection: EditorSelection) : bool =
        selection.Ranges.Length = 1
        && selection.Ranges[0].From >= 0
        && selection.Ranges[0].To <= doc.Length

    let private normalise (multiple: bool) (doc: Text) (selection: EditorSelection) : EditorSelection =
        let clamped = EditorSelection.clamp doc.Length selection

        if multiple then
            clamped
        else
            EditorSelection.asSingle clamped

    let create (config: EditorStateConfig) : EditorState =
        let configuration = Configuration.resolve (Many config.Extensions) (Dictionary<int, Extension> ())
        let fields = Array.zeroCreate<obj> configuration.Fields.Length
        let selection = ValueOption.defaultValue (EditorSelection.single 0 0) config.Selection

        let state =
            let first = EditorState (configuration, config.Doc, selection, fields, null)

            if fits config.Doc selection then
                first
            else
                let multiple = unbox<bool> (first.FacetBoxed allowMultipleSelections)
                EditorState (configuration, config.Doc, normalise multiple config.Doc selection, fields, null)

        for index in 0 .. fields.Length - 1 do
            fields[index] <- configuration.Fields[index].CreateBoxed state

        state

    /// `state` after `transaction`, where `doc` is the text the transaction produces.
    let internal applyWithDoc (doc: Text) (transaction: Transaction) (state: EditorState) : EditorState =
        let config = state.Config
        let mutable root = config.Root
        let mutable overrides: Dictionary<int, Extension> = null
        let mutable reconfigured = false

        for effect in transaction.Effects do
            match StateEffect.tryValue StateEffects.compartment effect with
            | ValueSome (struct (compartment, content)) ->
                if isNull overrides then
                    overrides <- Dictionary<int, Extension> config.Compartments

                overrides[compartment.Id] <- content
                reconfigured <- true
            | ValueNone ->
                match StateEffect.tryValue StateEffects.reconfigure effect with
                | ValueSome extension ->
                    root <- extension
                    reconfigured <- true
                | ValueNone ->
                    match StateEffect.tryValue StateEffects.appendConfig effect with
                    | ValueSome extension ->
                        root <- Many [ root; extension ]
                        reconfigured <- true
                    | ValueNone -> ()

        let next =
            if reconfigured then
                Configuration.resolve root (if isNull overrides then config.Compartments else overrides)
            else
                config

        let selection =
            match transaction.Selection with
            | ValueSome selected -> selected
            | ValueNone -> EditorSelection.map (ChangeSet.desc transaction.Changes) state.Selection

        if not reconfigured then
            let before = state.Fields
            let mutable after = before

            for index in 0 .. before.Length - 1 do
                let value = config.Fields[index].UpdateBoxed (before[index], transaction)

                if not (obj.ReferenceEquals (value, before[index])) then
                    if obj.ReferenceEquals (after, before) then
                        after <- Array.copy before

                    after[index] <- value

            let first = EditorState (config, doc, selection, after, state.Dynamic)

            if fits doc selection then
                first
            else
                let multiple = unbox<bool> (first.FacetBoxed allowMultipleSelections)
                EditorState (config, doc, normalise multiple doc selection, after, state.Dynamic)
        else
            let fields = Array.zeroCreate<obj> next.Fields.Length

            let result =
                let first = EditorState (next, doc, selection, fields, null)

                if fits doc selection then
                    first
                else
                    let multiple = unbox<bool> (first.FacetBoxed allowMultipleSelections)
                    EditorState (next, doc, normalise multiple doc selection, fields, null)

            for index in 0 .. fields.Length - 1 do
                let field = next.Fields[index]

                fields[index] <-
                    match state.FieldBoxed field with
                    | ValueSome value -> field.UpdateBoxed (value, transaction)
                    | ValueNone -> field.CreateBoxed result

            result

    /// <c>state</c> after <c>transaction</c>.
    let apply (transaction: Transaction) (state: EditorState) : EditorState =
        let doc =
            if transaction.DocChanged then
                ChangeSet.apply state.Doc transaction.Changes
            else
                state.Doc

        applyWithDoc doc transaction state

    /// The transaction <c>specs</c> describe for <c>state</c>, with positions in the log of 0.
    /// <exception cref="T:System.ArgumentException">A change lies outside the document or overlaps another.</exception>
    let transaction (specs: TxSpec list) (state: EditorState) : Transaction =
        Transaction.build state.Doc state.Selection specs
