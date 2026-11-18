/// The shell's styles as typed Ranvier.Css utilities, applied to elements with `styles { ... }`. The text view
/// brings its own classes and rules.
/// `LiveSheet` gives their rules to the engine.
module EditorShowcase.Styles

open Ranvier.Css

let private ink = Css.hex "#bfbdb6"
let private dim = Css.hex "#5c6773"
let private faint = Css.hex "#3d4753"
let private accent = Css.hex "#e6b450"
let private editorBack = Css.hex "#0d1017"
let private panelBack = Css.hex "#0a0d13"
let private edge = Css.hex "#1b222c"
let private raised = Css.hex "#151b24"

let private unselectable =
    utility {
        Css.userSelect CssValue.UserSelect.none
        Css.webkitUserSelect CssValue.WebkitUserSelect.none
    }

/// A button drawn as a plain row of text in the page font.
let private plainButton =
    utility {
        Css.display CssValue.Display.block
        Css.backgroundColor CssValue.Color.transparent
        Css.borderWidth (Css.px 0.)
        Css.fontFamily CssValue.All.``inherit``
        Css.textAlign CssValue.TextAlign.left
        Css.whiteSpace CssValue.WhiteSpace.nowrap
        Css.cursor CssValue.Cursor.pointer
        unselectable
    }

// ---- The shell: explorer, tabs, breadcrumb and status bar ----

let app =
    utility {
        Css.display CssValue.Display.flex
        Css.flexDirection CssValue.Flex.Direction.column
        Css.height (Css.vh 100.)
        Css.overflow CssValue.Overflow.hidden
        Css.fontSize (Css.px 12.)
        Css.color ink
    }

let body =
    utility {
        Css.display CssValue.Display.flex
        Css.flexGrow (Css.number 1.)
        Css.minHeight (Css.px 0.)
    }

let sidebar =
    utility {
        Css.width (Css.px 248.)
        Css.flexShrink (Css.number 0.)
        Css.boxSizing CssValue.BoxSizing.borderBox
        Css.backgroundColor panelBack
        Css.borderRightWidth (Css.px 1.)
        Css.borderRightStyle CssValue.Border.Style.solid
        Css.borderRightColor edge
        Css.paddingTop (Css.px 10.)
        Css.overflow CssValue.Overflow.hidden
    }

let sideTitle =
    utility {
        Css.fontSize (Css.px 10.5)
        Css.fontWeight (Css.number 600.)
        Css.letterSpacing (Css.em 0.1)
        Css.textTransform CssValue.TextTransform.uppercase
        Css.color dim
        Css.paddingLeft (Css.px 16.)
        Css.paddingTop (Css.px 8.)
        Css.paddingBottom (Css.px 6.)
    }

/// A file of the explorer; `active` marks the open one.
let fileItem (active: 'instance -> bool) =
    utility {
        plainButton
        Css.width (Css.percent 100.)
        Css.boxSizing CssValue.BoxSizing.borderBox
        Css.color (Css.hex "#8a9199")
        Css.fontSize (Css.px 12.5)
        Css.lineHeight (Css.px 24.)
        Css.paddingLeft (Css.px 28.)
        Css.paddingRight (Css.px 12.)
        hover { Css.backgroundColor raised }

        whenActive active {
            Css.backgroundColor raised
            Css.color (Css.hex "#e6e1cf")
            hover { Css.backgroundColor raised }
        }
    }

/// The line count beside a file name.
let fileSize =
    utility {
        Css.color faint
        Css.marginLeft (Css.px 8.)
        Css.fontSize (Css.px 11.)
    }

/// A renderer switch; `on` lights its marker.
let toggle (on: 'instance -> bool) =
    utility {
        plainButton
        Css.width (Css.percent 100.)
        Css.boxSizing CssValue.BoxSizing.borderBox
        Css.color dim
        Css.fontSize (Css.px 12.)
        Css.lineHeight (Css.px 24.)
        Css.paddingLeft (Css.px 16.)
        Css.paddingRight (Css.px 12.)
        Css.borderLeftWidth (Css.px 2.)
        Css.borderLeftStyle CssValue.Border.Style.solid
        Css.borderLeftColor CssValue.Color.transparent
        hover { Css.backgroundColor raised }

        whenActive on {
            Css.color ink
            Css.borderLeftColor accent
        }
    }

/// The statistics panel: one label and value per row, in fixed-pitch digits.
let hud =
    utility {
        Css.marginTop (Css.px 6.)
        Css.paddingLeft (Css.px 16.)
        Css.paddingRight (Css.px 12.)
        Declaration.create "font-family" "Consolas,'DejaVu Sans Mono',Menlo,monospace"
        Css.fontSize (Css.px 11.5)
        Css.lineHeight (Css.px 19.)
    }

let hudRow =
    utility {
        Css.display CssValue.Display.flex
        Css.justifyContent CssValue.JustifyContent.spaceBetween
        Css.whiteSpace CssValue.WhiteSpace.nowrap
    }

let hudLabel = utility { Css.color dim }
let hudValue = utility { Css.color (Css.hex "#95e6cb") }

let hint =
    utility {
        Css.color faint
        Css.fontSize (Css.px 11.)
        Css.lineHeight (Css.number 1.5)
        Css.paddingLeft (Css.px 16.)
        Css.paddingRight (Css.px 12.)
        Css.marginTop (Css.px 12.)
    }

let main =
    utility {
        Css.display CssValue.Display.flex
        Css.flexDirection CssValue.Flex.Direction.column
        Css.flexGrow (Css.number 1.)
        Css.minWidth (Css.px 0.)
        Css.backgroundColor editorBack
    }

let tabs =
    utility {
        Css.display CssValue.Display.flex
        Css.flexShrink (Css.number 0.)
        Css.height (Css.px 35.)
        Css.backgroundColor panelBack
        Css.borderBottomWidth (Css.px 1.)
        Css.borderBottomStyle CssValue.Border.Style.solid
        Css.borderBottomColor edge
    }

let tab (active: 'instance -> bool) =
    utility {
        plainButton
        Css.color dim
        Css.fontSize (Css.px 12.5)
        Css.lineHeight (Css.px 33.)
        Css.paddingLeft (Css.px 16.)
        Css.paddingRight (Css.px 18.)
        Css.borderRightWidth (Css.px 1.)
        Css.borderRightStyle CssValue.Border.Style.solid
        Css.borderRightColor edge
        Css.borderTopWidth (Css.px 2.)
        Css.borderTopStyle CssValue.Border.Style.solid
        Css.borderTopColor CssValue.Color.transparent
        hover { Css.color ink }

        whenActive active {
            Css.backgroundColor editorBack
            Css.color (Css.hex "#e6e1cf")
            Css.borderTopColor accent
        }
    }

let crumbs =
    utility {
        Css.flexShrink (Css.number 0.)
        Css.lineHeight (Css.px 26.)
        Css.paddingLeft (Css.px 18.)
        Css.color dim
        Css.fontSize (Css.px 12.)
        Css.whiteSpace CssValue.WhiteSpace.nowrap
    }

/// The room the text view fills.
let editorArea =
    utility {
        Css.position CssValue.Position.relative
        Css.display CssValue.Display.flex
        Declaration.create "flex-direction" "column"
        Css.flexGrow (Css.number 1.)
        Css.minHeight (Css.px 0.)
        Css.overflow CssValue.Overflow.hidden
    }

let statusBar =
    utility {
        Css.display CssValue.Display.flex
        Css.flexShrink (Css.number 0.)
        Css.height (Css.px 24.)
        Css.lineHeight (Css.px 24.)
        Css.backgroundColor panelBack
        Css.borderTopWidth (Css.px 1.)
        Css.borderTopStyle CssValue.Border.Style.solid
        Css.borderTopColor edge
        Css.color dim
        Css.fontSize (Css.px 11.5)
        Css.whiteSpace CssValue.WhiteSpace.nowrap
        unselectable
    }

let statusItem =
    utility {
        Css.paddingLeft (Css.px 14.)
        Css.paddingRight (Css.px 4.)
    }

let statusMode =
    utility {
        Css.paddingLeft (Css.px 14.)
        Css.paddingRight (Css.px 14.)
        Css.color accent
        Css.flexGrow (Css.number 1.)
        Css.textAlign CssValue.TextAlign.right
    }

/// Page-level rules that no element in the view owns.
let page =
    [ Rule.style
          (Selector.unsafeFromText "html,body")
          [ Css.margin (Css.px 0.)
            Css.padding (Css.px 0.)
            Css.backgroundColor editorBack
            Declaration.create "font-family" "'Segoe UI','Noto Sans','DejaVu Sans',Arial,sans-serif"
            Declaration.create "overflow" "hidden" ] ]
