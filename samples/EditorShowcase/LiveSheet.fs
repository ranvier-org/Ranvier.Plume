/// The stylesheet, built in memory and held by the engine. The rules of a class are recorded where
/// `styles { ... }` applies it, and served from a constructed sheet that the document adopts: `replaceSync` for
/// the rules known at start-up, `insertRule` for a class first applied later. No file and no style element.
module EditorShowcase.LiveSheet

open System
open System.Collections.Generic
open System.Text
open System.Text.RegularExpressions
open Ranvier.Dom
open Ranvier.View
open Ranvier.Css
open Ranvier.Css.View
open Ranvier.View.WebCore

let private known = HashSet<string>()

/// The classes applied so far, in the order of first application. A view applies one class per element, so
/// the order between classes decides nothing in the cascade.
let private recorded = ResizeArray<StyleDefinition>()

/// Classes the engine's sheet holds, counted from the start of `recorded`. The process has one window.
let mutable private served = 0

let private record (definition: StyleDefinition) =
    if known.Add definition.ClassName then
        recorded.Add definition

/// `styles { ... }` of Ranvier.Css.View, which also records the rules of the class it puts on the element.
/// Ranvier.Css.View 0.1.1 computes those rules and keeps the class name only.
type RecordedStylesBuilder() =
    inherit UtilityBuilderBase()

    member _.Run<'native, 'instance>(values: StaticUtilityFragment) : Part<'native, 'instance> array =
        let style: Styled<'instance> = UtilityBuilder().Run values |> Utility.toStyled
        record style.Definition
        CssView.parts style

    member _.Run<'native, 'instance>(values: ReactiveUtilityFragment<'instance>) : Part<'native, 'instance> array =
        let style = UtilityBuilder().Run values |> Utility.toStyledReactive
        record style.Definition
        CssView.parts style

/// Opened after Ranvier.Css.View, this takes the place of its `styles`.
let styles = RecordedStylesBuilder()

/// Adds the classes a library applies in its own views, each with its rules.
let add (definitions: StyleDefinition list) =
    for definition in definitions do
        record definition

/// False under EDITOR_INHERITED_VARIABLES=1, which leaves the registrations out to measure the difference.
let registers = Environment.GetEnvironmentVariable "EDITOR_INHERITED_VARIABLES" <> "1"

/// An `@property` rule for every variable the rules read. A reader value of Ranvier.Css is such a variable,
/// written on its element. Unregistered, a variable inherits, so a change to it restyles every element under
/// that one; registered as non-inherited, the change stays on the element.
/// Every reader of this app sits on the element that owns it. A reader inside a `descendant` or `pseudoElement`
/// block reaches its target through inheritance, and registering its variable this way would break it.
let private registrations (css: string) =
    if not registers then
        []
    else
        Regex.Matches(css, @"var\((--r-[0-9a-f]+-[0-9]+)")
        |> Seq.map (fun found -> found.Groups[1].Value)
        |> Seq.distinct
        |> Seq.map (fun name -> $"@property {name} {{ syntax: \"*\"; inherits: false; }}")
        |> Seq.toList

/// The rules of one class, each as the text of one rule, after the registrations of the variables they read.
let private rulesOf (definition: StyleDefinition) =
    let rules = definition.Rules |> List.map (fun rule -> (Stylesheet.render [ rule ]).TrimEnd())
    registrations (String.concat "\n" rules) @ rules

/// Fills the window's sheet with `page` and the rules of every class applied so far. With no sheet yet
/// (`existing` is null) it constructs one and adds it to the sheets the document adopts.
/// A reload passes the sheet of the window back, and the sheet is filled again.
let attach (host: WebCoreHost) (page: Rule list) (existing: obj) : CSSStyleSheet =
    let text = StringBuilder(Stylesheet.render page)

    for definition in recorded do
        for rule in rulesOf definition do
            text.Append(rule).Append('\n') |> ignore

    served <- recorded.Count

    match existing with
    | :? CSSStyleSheet as sheet ->
        sheet.replaceSync (text.ToString())
        sheet
    | _ ->
        use service = StyleSheets.forHost host
        let sheet = service.Create StyleSheetOptions.Default
        sheet.replaceSync (text.ToString())
        let doc = host.Document
        service.SetAdopted(doc, Array.append (service.GetAdopted doc) [| sheet |])
        sheet

/// Appends the rules of the classes applied since the sheet was last filled. Called once a frame, after the
/// frame's writes: a change to an adopted sheet restyles the document, so the rules of a frame go in together.
let flush (sheet: CSSStyleSheet) =
    if served < recorded.Count then
        for index in served .. recorded.Count - 1 do
            for rule in rulesOf recorded[index] do
                try
                    sheet.insertRule (rule, sheet.cssRules.length) |> ignore
                with failure ->
                    Console.Error.WriteLine $"[styles] the engine refused a rule of {recorded[index].ClassName}: {failure.Message}\n{rule}"

        served <- recorded.Count

/// The rules the engine holds in the sheet, as the engine serialises them.
let held (sheet: CSSStyleSheet) =
    let rules = sheet.cssRules
    let text = StringBuilder()

    for index in 0u .. rules.length - 1u do
        text.Append((rules.item index).cssText).Append('\n') |> ignore

    text.ToString()

/// Classes applied so far.
let count () = recorded.Count

/// Classes applied since the sheet was last filled, which `flush` will append.
let pending () = recorded.Count - served
