namespace Ranvier.Plume.View

open System
open System.Collections.Generic
open System.Globalization
open Ranvier.View
open Ranvier.Css
open Ranvier.Css.View
open Ranvier.Plume

/// The look of an editor view. Colours are CSS colour values.
type Theme =
    {
        /// A CSS font-family list. A column is one character advance wide, so the font is monospaced.
        FontFamily: string
        /// The font size in CSS pixels.
        FontSize: float
        /// The height of a row in CSS pixels.
        LineHeight: float
        /// Text.
        Ink: string
        /// Gutter text.
        Faint: string
        /// The cursor.
        Accent: string
        Background: string
        /// The gutter's edge and the scrollbar track's edge.
        Edge: string
        Selection: string
        ActiveLine: string
        /// The scrollbar thumb.
        Thumb: string
        /// The look of each token tag: a class name for the token's span, and CSS declarations as property and value.
        Tokens: (Tag * string * (string * string) list) list
    }

[<RequireQualifiedAccess>]
module Theme =

    /// <c>theme</c>, checked.
    /// <exception cref="T:System.ArgumentException">The font size or the line height is not positive, or a token
    /// class name is empty.</exception>
    let define (theme: Theme) : Theme =
        if not (theme.FontSize > 0.0) || not (theme.LineHeight > 0.0) then
            raise (ArgumentException "A theme's font size and line height are positive.")

        for _, name, _ in theme.Tokens do
            if String.IsNullOrWhiteSpace name then
                raise (ArgumentException "A token's class name is not empty.")

        theme

    /// A dark theme.
    let dark: Theme =
        {
            FontFamily = "Consolas,'DejaVu Sans Mono',Menlo,monospace"
            FontSize = 14.0
            LineHeight = 20.0
            Ink = "#bfbdb6"
            Faint = "#3d4753"
            Accent = "#e6b450"
            Background = "#0d1017"
            Edge = "#1b222c"
            Selection = "rgba(64, 159, 255, 0.28)"
            ActiveLine = "rgba(255, 255, 255, 0.045)"
            Thumb = "#2a3340"
            Tokens =
                [
                    Tag.keyword, "t1", [ "color", "#ff8f40"; "font-weight", "600" ]
                    Tag.typeName, "t2", [ "color", "#59c2ff" ]
                    Tag.functionName, "t3", [ "color", "#ffb454" ]
                    Tag.string, "t4", [ "color", "#aad94c" ]
                    Tag.number, "t5", [ "color", "#d2a6ff" ]
                    Tag.comment, "t6", [ "color", "#626a73"; "font-style", "italic" ]
                    Tag.operator, "t7", [ "color", "#f29668" ]
                    Tag.punctuation, "t8", [ "color", "#8a9199" ]
                    Tag.variable, "t9", [ "color", "#e6b673"; "font-style", "italic" ]
                ]
        }

/// The classes a view applies, each with its rules, in the order of first application.
[<Sealed>]
type internal StyleRecorder() =
    let known = HashSet<string> ()
    let recorded = ResizeArray<StyleDefinition> ()

    member _.Record(definition: StyleDefinition) =
        if known.Add definition.ClassName then
            recorded.Add definition

    member _.Definitions: StyleDefinition list = List.ofSeq recorded

/// `classOf { ... }`: the class of the utilities, as one part, with its rules recorded. The element's style
/// attribute stays free for the values the view writes itself. `styles { ... }` of Ranvier.Css.View 0.1.1 keeps the
/// class name only, and adds a tracked style attribute to every element.
[<Sealed>]
type internal RecordingClassBuilder(recorder: StyleRecorder) =
    inherit UtilityBuilderBase ()

    member _.Run<'native, 'instance>(values: StaticUtilityFragment) : Part<'native, 'instance> =
        let style: Styled<'instance> = UtilityBuilder().Run values |> Utility.toStyled
        recorder.Record style.Definition
        TemplateParts.composableClass (TemplateParts.attribute "class" style.Definition.ClassName)

/// CSS lengths as text.
module internal Px =

    /// `value` in CSS pixels, to a thousandth of a pixel.
    let text (value: float) : string =
        if value = Math.Floor value && abs value < 1e9 then
            String.Concat ((int value).ToString CultureInfo.InvariantCulture, "px")
        else
            String.Concat (value.ToString ("0.###", CultureInfo.InvariantCulture), "px")
