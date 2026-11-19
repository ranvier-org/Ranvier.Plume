# Ranvier.Plume Phase 1 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build `Ranvier.Plume` (state) and `Ranvier.Plume.View` (view) as a standalone repository, with tests and a
benchmark sample, to the scope of dossier section 8.

**Architecture:** An editor is a fold over a per-file log of transactions. `Document` owns the log, the saved text
and the current text; `Editor` is one view of a document with its own selection, extensions and fields. Every
consumer reads through Ranvier memos; the view's row pool is a core `createLookup` keyed by slot.

**Tech Stack:** F# on `net10.0;net8.0`, reflection-free. Packages: `Ranvier` 0.2.0, `Ranvier.Keyboard` 0.1.0,
`Ranvier.Keyboard.Dom` 0.1.0, `Ranvier.View` 0.1.50, `Ranvier.View.Dom` 0.1.50, `Ranvier.Css` 0.1.1,
`Ranvier.Css.View` 0.1.1; the sample adds `Ranvier.View.WebCore.Sdl` 0.2.0. Tests: Expecto 11.0.0-alpha8,
Expecto.FsCheck 11.0.0-alpha8-fscheck2.

**Spec:** `docs/DOSSIER.md` (approved 2026-11-18). Section numbers below refer to it.

**As carried out (added 2026-11-19):** in tasks 1 to 8 each implementation file was written before its tests, the
reverse of the order of the steps below. The "run; it fails" steps of tasks 1 and 2 and the spike of task 8 were
therefore not made when the plan says. Each is marked "made late" where it stands, and `docs/ACCEPTANCE.md`, "The
order the work was done in", has the record.

## Core and view state

Required by `Ranvier/.claude/rules/view-uses-core.md`.

**Core primitives used**

| Primitive | Job |
|---|---|
| `Api.createSignal` | Log head (`Document.Head`); saved position; view inputs: `ScrollTarget`, `Scroll`, `Height`, `CharWidth`, `HasFocus`, caret blink phase; `Editor.PageLines`. |
| `Api.createMemo` (previous value) | Editor state fold; history fold; update readers (previous value is the cursor); slices (`Selection`, `Field`, `Facet`); the row window; geometry (visible range, thumb, layer rectangles). |
| `Api.createLookup` | Row pool: slot to `RowContent`. `affected` returns the slots of lines that entered the window or changed. |
| `Api.createSelector` | Active line by line number. |
| `Api.createEffect` / `createEffectOn` | View plugins; the `scrollTop` write; the frame request that commits `ScrollTarget` to `Scroll`. |
| `Api.createRoot`, `onCleanup`, `runWithOwner` | One owner per view plugin; disposed when the plugin facet's value changes or the view unmounts. Reader registration cleanup. |
| `Api.batch` | One flush per dispatch. |
| `Graph.Dispatch` | `Document.Post` from another thread. |
| `Ranvier.View.Dom.Frames.frame` / `timeout` over a `Ranvier.Dom.FrameScheduler` | Frame pacing and the caret blink timer. This is the view layer's published scheduling helper; the callback runs in a batch under an owned scope. |

**View-owned state**

| State | Why the core cannot hold it |
|---|---|
| Row elements (through `forEachFixed`), and references to the root, viewport, probe and track nodes | DOM nodes. |
| The last `scrollTop` written | Last value written to a DOM target. |
| Scrollbar drag flag and pointer capture; text-selection drag anchor | Event state of a native node. |
| The log's reader cursor cells (`Document`, state package) | Not view state: a reader's position used only to bound the log. It carries no notification. |

**Settled before the view is built (dossier section 8), both without a change to Ranvier.Css or the core:**

- **Styles hand-off.** `Ranvier.Css.View` 0.1.1's `styles` keeps the class name only (`EditorShowcase/LiveSheet.fs:29-45`).
  The view package uses its own recording builder (the `RecordedStylesBuilder` technique) and exposes the recorded
  `StyleDefinition list`. A host adds those definitions to whichever sheet it serves.
- **Frame pacing.** Pointer and wheel handlers write `ScrollTarget`. An effect requests one
  `Frames.frame scheduler` while `ScrollTarget <> Scroll`; the frame callback copies the target into `Scroll`, which
  is what the row window reads. Several wheel events in one frame recompute rows once. `Timed` (debounce/throttle on
  a millisecond clock) is not used.

## Global Constraints

- New repository folder `C:\wk\ranvier-release\Ranvier.Plume`, local `git init` only. No remote, push or publish.
- The only edit outside that folder: a Dispatch stopwatch in `C:\wk\ranvier-release\EditorShowcase\Harness.fs`.
- `C:\wk\atelier` is read-only. It is not built or run.
- Libraries: `TargetFrameworks` `net10.0;net8.0`, `RanvierAotClean=true` (`--reflectionfree`): no `sprintf "%A"`, no
  reflection-based formatting, no structural `%A` in exceptions.
- SIMD code sits behind `#if !FABLE_COMPILER`, never reads past span bounds, keeps a scalar fallback, and is kept only
  if its benchmark shows a gain.
- Public API: records, unions, module functions. No interface hierarchies. No listeners, observers, event emitters
  or update callbacks. No Atelier concept (`ProofStatus`, `DocId`, `WindowFramePacket`, `Session`, tour, kernel).
- Offsets are UTF-16 code units. Line numbers are 0-based (`Line.Number`); the line-number gutter prints `Number + 1`.
- Doc comments follow `Ranvier/.claude/rules/comments.md`; reviewer notes use `//FOR-REVIEW`.
- Every figure reported carries its label (measured here / from documentation / general knowledge).
- Bench, capture and smoke windows are created hidden.
- Commits: local only, message ends with `Co-Authored-By: Claude Fable 5.2 <noreply@anthropic.com>`.
- Gate: `dotnet fsi --exec build.fsx -- ci` (build Release, then every test project unfiltered).

## Review Focus

Inputs the spec implies and a user will meet. Each has a test in the task named.

1. **`\r\n`, lone `\r`, astral and non-ASCII text.** Offsets count UTF-16 units; a `\r\n` break is two units; a
   change may not split a surrogate pair's line bookkeeping. (Task 1: `Text offsets over CRLF and astral lines`.)
2. **Empty document, no trailing newline, positions at 0 and at `length`.** `lineAt length`, `mapPos` at both
   ends, an insertion at `length`, a selection at `length`. (Tasks 1, 2, 3.)
3. **A dispatch made while a fold or an effect runs** (an effect that reads an update and dispatches). The log
   must stay consistent and the reader must see every transaction once. (Task 6: `dispatch from an effect`.)
4. **Invalid specs:** overlapping or out-of-range changes, a selection beyond the document after a foreign edit.
   Overlap and range errors raise `ArgumentException` at build; selections are clamped. (Tasks 2, 4, 6.)
5. **Undo at the edges:** nothing to undo, redo cleared by a new edit, undoing an item whose region an unrecorded
   edit deleted, undo past the saved position and back. (Task 5.)
6. **Degenerate viewports:** height 0, a document shorter than the pool, scrolling beyond the end, character
   width not yet measured. (Task 8: smoke test cases.)

---

## File structure

```
Ranvier.Plume/
  .config/dotnet-tools.json   .editorconfig   .gitattributes   .gitignore   global.json   nuget.config
  AGENTS.md   CLAUDE.md   README.md   LICENSE   Directory.Build.props   Directory.Build.targets
  Ranvier.Plume.slnx   build.fsx
  docs/DOSSIER.md   docs/superpowers/plans/2026-11-18-plume-phase-1.md   docs/ACCEPTANCE.md
  src/Directory.Build.props   src/Directory.Build.targets
  src/Ranvier.Plume/            namespace Ranvier.Plume
    Text.fs          rope: Text, Line, module Text
    LineScan.fs      scalar and vectorised line-break scans (compiled before Text.fs)
    ChangeSet.fs     ChangeSpec, Assoc, MapMode, ChangeDesc, ChangeSet, LineEdit
    Selection.fs     SelectionRange, EditorSelection
    RangeSet.fs      RangeSet<'T>
    Transaction.fs   AnnotationType, Annotation, StateEffectType, StateEffect, Transaction, TxSpec, Tx
    State.fs         Prec, Extension, Facet, StateField, Compartment, EditorState
    History.fs       HistoryConfig, HistoryItem, HistoryState (pure fold)
    Document.fs      the log, Document
    Editor.fs        Editor, Update, UpdateReader
    Commands.fs      Command, StateCommand, the P1 commands
    Keymap.fs        KeyBinding, Keymap
    Language.fs      Tag, Token, Language, HighlightStyle, TokenCache
  src/Ranvier.Plume.View/       namespace Ranvier.Plume.View
    Styling.fs       recording styles builder, Theme
    ViewTypes.fs     ViewConfig, RowContent, Window, EditorView (the mounted view), LayerMarker, GutterMarker
    Extensions.fs    ViewPlugin, Gutter, Layer facets and define functions
    Rows.fs          window memo, affected, row computation, markup cache
    Geometry.fs      visible range, thumb, posAtCoords, coordsAtPos, scrollIntoView
    Features.fs      LineNumbers, ActiveLine, DrawSelection, Cursor
    Component.fs     EditorView.create: template, handlers, effects
  tests/Ranvier.Plume.Tests/        Expecto; one file per source file, plus Main.fs
  tests/Ranvier.Plume.View.Tests/   Expecto; hidden-window smoke test
  samples/EditorShowcase/           the baseline app rebuilt on the library
```

---

### Task 1: Repository scaffold and `Text`

**Files:**
- Create: the root files above, copied from `Ranvier.Keyboard` and renamed (`RepositoryUrl` and
  `PackageProjectUrl` become `https://github.com/ranvier-org/Ranvier.Plume`; `RanvierTrace` is **not** defaulted on
  in Debug; no `fable/`, `.github/`, `brand/` or `docs.fsproj`). `build.fsx` has targets `build`, `test`, `ci`.
- Create: `src/Ranvier.Plume/Ranvier.Plume.fsproj`, `LineScan.fs`, `Text.fs`
- Create: `tests/Ranvier.Plume.Tests/Ranvier.Plume.Tests.fsproj`, `TextTests.fs`, `Main.fs`
- Source: `C:\wk\atelier\src\Atelier.Text\TextSnapshot.fs`, `C:\wk\atelier\tests\Atelier.Tests\TextSnapshotTests.fs`

**Interfaces — produces:**

```fsharp
[<Struct>] type Line = { Number: int; From: int; To: int; Text: string }   // To excludes the line break
type Text  // sealed, immutable; structural content equality is not defined; use Text.contentEquals

module Text =
    val empty: Text
    val ofString: string -> Text
    val ofLines: seq<string> -> Text               // joined with "\n"
    val ofUtf8: ReadOnlySpan<byte> -> Text         // vectorised scan when it is kept (Task 10)
    val ofUtf8Scalar: ReadOnlySpan<byte> -> Text   // the scalar scan, kept for the comparison of 7.3
    val length: Text -> int                        // UTF-16 units, line breaks included
    val lines: Text -> int                         // >= 1; "" has one empty line
    val line: int -> Text -> Line                  // by 0-based number
    val lineAt: int -> Text -> Line                // by offset; an offset inside a break belongs to that line
    val lineText: int -> Text -> string            // reference-stable across edits to other lines
    val slice: int -> int -> Text -> Text
    val sliceString: int -> int -> Text -> string
    val replace: int -> int -> Text -> Text -> Text   // from, to, insert, text
    val append: Text -> Text -> Text
    val toString: Text -> string
    val iterLines: int -> int -> Text -> seq<string>  // [fromLine, toLine)
    val iterRange: int -> int -> Text -> seq<string>  // chunks of [from, to)
    val offsetOf: int -> int -> Text -> int           // line, character -> offset (character clamped)
    val positionOf: int -> Text -> struct (int * int) // offset -> line, character
    val contentEquals: Text -> Text -> bool
```

`DocId` and `Version` are removed from the type. Every tree node carries its UTF-16 length beside its line count.
A leaf whose bytes are all ASCII uses its byte offsets as character offsets; any other leaf holds a character
offset array.

- [x] **Step 1:** Scaffold the repository; `git init`; first commit of the dossier, this plan and the scaffold.
- [x] **Step 2:** Port `TextSnapshotTests.fs` to Expecto as `TextTests.fs` against the signatures above, and add:
  - `Text offsets over CRLF and astral lines`: `ofString "a\r\nb😀c\rd"` has `length = 9`, `lines = 3`;
    `line 1` is `{ Number = 1; From = 3; To = 7; Text = "b😀c" }`; `lineAt 8` is line 2; `positionOf 5` is `struct (1, 2)`.
  - `empty text`: `length = 0`, `lines = 1`, `lineAt 0 = { Number = 0; From = 0; To = 0; Text = "" }`.
  - `no trailing newline`: `ofString "a\nb"` has `lines = 2`; `ofString "a\n"` has `lines = 2` with an empty last line.
  - `replace keeps untouched line strings`: after `replace` inside line 500 of a 1,000-line text,
    `obj.ReferenceEquals (lineText 10 before, lineText 10 after)` once both were read before the edit.
  - Property `replace agrees with String`: for random text and random `(from, to, insert)`,
    `toString (replace from to (ofString insert) t) = s.Remove(from, to - from).Insert(from, insert)`.
  - Property `vectorised scan equals scalar scan`: `contentEquals (ofUtf8 b) (ofUtf8Scalar b)` and equal `lines`.
- [x] **Step 3 (made late):** Run `dotnet test tests/Ranvier.Plume.Tests`; it fails to compile (no `Text`).
  Not run at the time: `Text.fs` was written before `TextTests.fs`, and the first build of the tests succeeded.
  `dotnet fsi --exec build.fsx -- red` makes the run in a copy of commit `20c43cd` without `Text.fs`.
- [x] **Step 4:** Implement `LineScan.fs` (scalar `\n`/`\r` scan; `Vector256`/`Vector128` scan behind
  `#if !FABLE_COMPILER` with the scalar tail) and `Text.fs` from the extracted rope.
- [x] **Step 5:** Tests pass. Commit `feat: Text rope with offset addressing`.

### Task 2: `ChangeSet`

**Files:** Create `src/Ranvier.Plume/ChangeSet.fs`, `tests/Ranvier.Plume.Tests/ChangeSetTests.fs`.

**Interfaces — consumes:** `Text` (Task 1). **Produces:**

```fsharp
[<Struct>] type ChangeSpec = { From: int; To: int; Insert: Text }
type Assoc = Before = -1 | After = 1
type MapMode = Simple = 0 | TrackDel = 1 | TrackBefore = 2 | TrackAfter = 3
/// LSP-shaped edit: 0-based line and UTF-16 character.
type LineEdit = { StartLine: int; StartCharacter: int; EndLine: int; EndCharacter: int; NewText: string }

type ChangeDesc   // Sections: int[] of (length, inserted) pairs; inserted = -1 for an unchanged section
type ChangeSet    // Desc + Inserted: Text[] + Removed: Text[], one entry per section

module ChangeDesc =
    val length: ChangeDesc -> int
    val newLength: ChangeDesc -> int
    val isEmpty: ChangeDesc -> bool
    val invert: ChangeDesc -> ChangeDesc
    val compose: ChangeDesc -> ChangeDesc -> ChangeDesc
    val map: before: bool -> other: ChangeDesc -> ChangeDesc -> ChangeDesc
    val mapPos: Assoc -> int -> ChangeDesc -> int
    val mapPosMode: Assoc -> MapMode -> int -> ChangeDesc -> int voption   // ValueNone when the mode reports a deletion
    val touchesRange: int -> int -> ChangeDesc -> bool
    val iterChangedRanges: (int -> int -> int -> int -> unit) -> ChangeDesc -> unit   // fromA toA fromB toB

module ChangeSet =
    val empty: length: int -> ChangeSet
    val ofSpecs: Text -> ChangeSpec list -> ChangeSet        // any order; ArgumentException on overlap or range
    val ofLineEdits: Text -> LineEdit list -> ChangeSet
    val toLineEdits: Text -> ChangeSet -> LineEdit list      // Text is the document the set applies to
    val desc: ChangeSet -> ChangeDesc
    val length: ChangeSet -> int
    val newLength: ChangeSet -> int
    val isEmpty: ChangeSet -> bool
    val apply: Text -> ChangeSet -> Text                     // one left-to-right pass
    val invert: ChangeSet -> ChangeSet                       // needs no document
    val compose: ChangeSet -> ChangeSet -> ChangeSet         // b applies to the result of a
    val map: before: bool -> other: ChangeDesc -> ChangeSet -> ChangeSet
    val mapPos: Assoc -> int -> ChangeSet -> int
    val iterChanges: (int -> int -> int -> int -> Text -> unit) -> ChangeSet -> unit   // fromA toA fromB toB inserted
```

`compose` and `map` carry `Removed` through: a composed set's removed text is the first document's text, and a
mapped set's removed text is the part the other set left in place.

- [x] **Step 1:** Write `ChangeSetTests.fs`. Generators: a document of 0-40 characters over `"ab\n"`, and a random
  valid spec list for a given length. Tests:
  - `apply agrees with String` (property).
  - `removed text is the replaced slice` (property): for `ofSpecs`, `compose` and `map` results, each changed range's
    `Removed` entry equals `Text.sliceString fromA toA` of the document the set applies to.
  - `invert round-trips` (property): `apply (apply d c) (invert c)` has the content of `d`.
  - `compose equals sequential apply` (property): `apply d (compose a b) = apply (apply d a) b`.
  - `map converges` (property): for `a`, `b` on one document,
    `apply (apply d a) (map false (desc a) b)` equals `apply (apply d b) (map true (desc b) a)`.
  - `mapPos`: insertion at the position with `Assoc.Before` stays, with `Assoc.After` moves; a position inside a
    deleted range maps to the range start; `mapPosMode Assoc.After MapMode.TrackDel` returns `ValueNone` there;
    positions 0 and `length` map for an insertion at 0 and at `length`.
  - `ofSpecs rejects overlap`: `[ {From=0;To=3}; {From=2;To=4} ]` raises `ArgumentException`; `To > length` raises.
  - `line edits round-trip`: `toLineEdits d (ofLineEdits d edits)` applied through `ofLineEdits` gives the same text.
- [x] **Step 2 (made late):** Run; compile failure.
  Not run at the time: `ChangeSet.fs` was written before `ChangeSetTests.fs`, and the first build of the tests
  succeeded. `dotnet fsi --exec build.fsx -- red` makes the run in a copy of commit `673af03` without
  `ChangeSet.fs`.
- [x] **Step 3:** Implement. Sections merge as CodeMirror's `addSection` does (adjacent unchanged sections join; a
  replacement following a replacement joins). `apply` walks sections once and calls `Text.replace` back to front or
  builds by `slice`/`append`, whichever the many-range benchmark of Task 10 favours.
- [x] **Step 4:** Tests pass. Commit `feat: ChangeSet with removed text, compose, map and mapPos`.

### Task 3: `EditorSelection` and `RangeSet`

**Files:** Create `Selection.fs`, `RangeSet.fs`, `SelectionTests.fs`, `RangeSetTests.fs`.

**Interfaces — consumes:** `ChangeDesc.mapPos`. **Produces:**

```fsharp
[<Struct>] type SelectionRange = { Anchor: int; Head: int; GoalColumn: int; Assoc: Assoc }   // GoalColumn = -1 when unset
    member From: int;  member To: int;  member IsEmpty: bool
type EditorSelection = { Ranges: SelectionRange[]; MainIndex: int }
    member Main: SelectionRange

module EditorSelection =
    val cursor: int -> SelectionRange
    val range: anchor: int -> head: int -> SelectionRange
    val single: anchor: int -> head: int -> EditorSelection
    val create: SelectionRange list -> mainIndex: int -> EditorSelection   // sorted, overlaps merged
    val map: ChangeDesc -> EditorSelection -> EditorSelection
    val clamp: length: int -> EditorSelection -> EditorSelection
    val asSingle: EditorSelection -> EditorSelection

type RangeSet<'T>   // From: int[]; To: int[]; Values: 'T[]; sorted by From, then To
module RangeSet =
    val empty<'T> : RangeSet<'T>
    val ofRanges: (int * int * 'T) list -> RangeSet<'T>          // sorted here
    val count: RangeSet<'T> -> int
    val update: add: (int * int * 'T) list -> filter: (int -> int -> 'T -> bool) -> RangeSet<'T> -> RangeSet<'T>
    val map: ChangeDesc -> RangeSet<'T> -> RangeSet<'T>          // a range deleted to empty is dropped
    val between: int -> int -> (int -> int -> 'T -> unit) -> RangeSet<'T> -> unit
    val iter: (int -> int -> 'T -> unit) -> RangeSet<'T> -> unit
    val join: RangeSet<'T> list -> RangeSet<'T>
    val compare: RangeSet<'T> -> RangeSet<'T> -> (int * int) list   // ranges where coverage or value differs
```

- [x] **Step 1:** Tests. Selection: `create` merges `[0-5; 3-8]` into `0-8` and keeps the main range's index;
  `map` over an insertion before the head shifts it; over a deletion covering it collapses it; `clamp 3` on a
  cursor at 9 gives 3. RangeSet: `between 5 10` visits exactly the overlapping ranges; `map` shifts ranges after an
  insertion and drops a range inside a deletion; `update` adds in order and filters; `join` of two sets is sorted;
  property `map agrees with mapPos` on both endpoints (`Assoc.After` for From, `Assoc.Before` for To).
- [x] **Step 2:** Implement. `RangeSet.map` adds the length delta to every position after the last changed
  section in one loop over the flat arrays; positions before it go through `mapPos`.
- [x] **Step 3:** Tests pass. Commit `feat: EditorSelection and RangeSet`.

### Task 4: Transactions and `EditorState`

**Files:** Create `Transaction.fs`, `State.fs`, `TransactionTests.fs`, `StateTests.fs`.

**Interfaces — produces:**

```fsharp
type AnnotationType<'T>      // .Of: 'T -> Annotation
type Annotation
module Annotation =
    val define<'T> : unit -> AnnotationType<'T>
type StateEffectType<'T>     // .Of: 'T -> StateEffect
type StateEffect             // .Is: StateEffectType<'T> -> bool
module StateEffect =
    val define<'T> : map: ('T -> ChangeDesc -> 'T voption) -> StateEffectType<'T>
    val defineSimple<'T> : unit -> StateEffectType<'T>
    val tryValue: StateEffectType<'T> -> StateEffect -> 'T voption
    val mapEffects: ChangeDesc -> StateEffect[] -> StateEffect[]

type Transaction =
    { Seq: int64; Base: int64; Source: int                  // Source 0 = outside source, delivered to every editor
      Changes: ChangeSet
      Selection: EditorSelection voption                    // the source's selection afterwards
      StartSelection: EditorSelection voption               // the source's selection before
      Effects: StateEffect[]; Annotations: Annotation[]; ScrollIntoView: bool }
    member DocChanged: bool
module Transaction =
    val annotation: AnnotationType<'T> -> Transaction -> 'T voption
    val isUserEvent: string -> Transaction -> bool          // "input" matches "input.type"
    val asForeign: Transaction -> Transaction               // no selection, effects or scroll request

module Annotations =   // built in
    val time: AnnotationType<int64>          // milliseconds
    val userEvent: AnnotationType<string>
    val addToHistory: AnnotationType<bool>
    val group: AnnotationType<int64>
    val isolateHistory: AnnotationType<bool>

type TxSpec   // opaque union
module Tx =
    val change: ChangeSpec -> TxSpec
    val changes: ChangeSpec list -> TxSpec
    val changeSet: ChangeSet -> TxSpec
    val selection: EditorSelection -> TxSpec
    val effect: StateEffect -> TxSpec
    val annotate: Annotation -> TxSpec
    val userEvent: string -> TxSpec
    val scrollIntoView: TxSpec
    val group: int64 -> TxSpec
    val addToHistory: bool -> TxSpec
    val isolateHistory: TxSpec

type Prec = Highest = 0 | High = 1 | Default = 2 | Low = 3 | Lowest = 4
type Extension              // opaque
module Extension =
    val many: Extension list -> Extension
    val prec: Prec -> Extension -> Extension
type Facet<'In, 'Out>       // .Of: 'In -> Extension
module Facet =
    val define: combine: ('In[] -> 'Out) -> Facet<'In, 'Out>
    val compute: Facet<'In, 'Out> -> (EditorState -> 'In) -> Extension      // recomputed per state
    val from: Facet<'In, 'Out> -> StateField<'T> -> ('T -> 'In) -> Extension
type StateFieldSpec<'T> = { Create: EditorState -> 'T; Update: 'T -> Transaction -> 'T }
type StateField<'T>
module StateField =
    val define: StateFieldSpec<'T> -> StateField<'T>
    val defineWith: compare: ('T -> 'T -> bool) -> provide: (StateField<'T> -> Extension) list -> StateFieldSpec<'T> -> StateField<'T>
    val extension: StateField<'T> -> Extension
type Compartment            // .Of: Extension -> Extension;  .Reconfigure: Extension -> StateEffect
module Compartment =
    val create: unit -> Compartment

type EditorStateConfig = { Doc: Text; Selection: EditorSelection voption; Extensions: Extension list }
type EditorState            // Doc: Text; Selection: EditorSelection
module EditorState =
    val create: EditorStateConfig -> EditorState
    val apply: Transaction -> EditorState -> EditorState
    val transaction: TxSpec list -> EditorState -> Transaction   // Seq = Base = 0, Source = 0; time is not set
    val field: StateField<'T> -> EditorState -> 'T               // raises when the field is not configured
    val tryField: StateField<'T> -> EditorState -> 'T voption
    val facet: Facet<'In, 'Out> -> EditorState -> 'Out
    val tabSize: Facet<int, int>                      // default 4
    val indentUnit: Facet<string, string>             // default four spaces
    val lineSeparator: Facet<string, string>          // default "\n"
    val readOnly: Facet<bool, bool>                   // default false; the first input by precedence
    val allowMultipleSelections: Facet<bool, bool>    // default false
    val reconfigure: StateEffectType<Extension>
    val appendConfig: StateEffectType<Extension>
```

Rules: a field's `Update` sees the transaction only (a value that needs the text is a computed facet or a memo
over the state). Facet inputs are ordered by `Prec`, then by position in the extension list. A facet whose inputs
are all static is combined once per configuration. A selection is clamped to the new document and reduced to its
main range unless `allowMultipleSelections` is true. Reconfiguring keeps the value of every field still present.

- [x] **Step 1:** Tests.
  - `field counts doc changes`: the dossier's `editCount` field is 2 after two changing transactions and one
    selection-only transaction.
  - `facet precedence`: `tabSize.Of 2` at `Prec.Low` and `tabSize.Of 8` at default give 8; reversed list order with
    equal precedence gives the first.
  - `computed facet follows the document`: a facet computed as `Text.lines state.Doc` changes after a newline insert.
  - `facet from field`.
  - `compartment reconfigure`: reconfiguring `tabSize.Of 2` to `tabSize.Of 8` changes the facet and keeps the
    `editCount` field's value; a field added by `appendConfig` is created; one removed by `reconfigure` is gone
    (`tryField = ValueNone`).
  - `selection follows changes`: with no `Tx.selection`, an insertion before the cursor shifts it.
  - `selection is clamped`: `Tx.selection (single 99 99)` on a 5-character document gives 5.
  - `multiple ranges need the facet`.
  - `effects map through changes`: an effect defined with a position map moves with an insertion before it.
  - `asForeign strips selection, effects and scroll`.
  - `transaction rejects a change beyond the document` (`ArgumentException`).
- [x] **Step 2:** Implement. Typed slots hold `obj` behind integer ids; no reflection.
- [x] **Step 3:** Tests pass. Commit `feat: transactions, facets, fields, compartments and EditorState`.

### Task 5: History and `Document`

**Files:** Create `History.fs`, `Document.fs`, `HistoryTests.fs`, `DocumentTests.fs`.

**Interfaces — consumes:** Tasks 2-4. **Produces:**

```fsharp
type HistoryConfig = { Depth: int; NewGroupDelay: int64; JoinToEvent: Transaction -> bool -> bool }   // tr, adjacent
module History =
    val defaults: HistoryConfig                      // Depth 1000 until Task 11 sets it; NewGroupDelay 500L
    val undoOp: AnnotationType<bool>                 // set on a transaction produced by undo (true) or redo (false)
    val newGroupId: unit -> int64
type HistoryState   // pure
module HistoryState =
    val empty: HistoryConfig -> HistoryState
    val apply: Transaction -> HistoryState -> HistoryState
    val undoDepth: HistoryState -> int
    val redoDepth: HistoryState -> int
    val position: HistoryState -> struct (int64 * int64)                      // (epoch, id); equal positions mean equal text
    val undoSpec: source: int -> EditorSelection -> HistoryState -> TxSpec list voption   // ValueNone with nothing to undo
    val redoSpec: source: int -> EditorSelection -> HistoryState -> TxSpec list voption

type DocumentConfig = { Text: Text; History: HistoryConfig; Clock: unit -> int64 }
type Document
    member Head: Signal<int64>
    member Text: Memo<Text>
    member Saved: Memo<Text>
    member IsDirty: Memo<bool>
    member History: Memo<HistoryState>
    member MarkSaved: unit -> unit
    member Dispatch: TxSpec list -> unit                 // an outside source on the graph thread; Source = 0
    member Post: TxSpec list -> unit                     // any thread; runs through Graph.Dispatch
    member PostAt: baseSeq: int64 -> TxSpec list -> unit // written against an older head; mapped on append
    member UndoDepth: Memo<int>
    member RedoDepth: Memo<int>
    // internal: Append, Since, RegisterReader
module Document =
    val create: Text -> Document
    val createWith: DocumentConfig -> Document
```

Rules: `Append` maps a transaction whose `Base` is behind the head over the composed changes since `Base`
(`ChangeSet.map false`, selection and effects mapped), stamps the sequence number and time, applies it to the
current text and writes `Head`. History items hold inverse change sets; the top item carries the pending mapping
for the items below it. A recorded transaction joins the top item when it has the same `group` id, or when it is
adjacent, within `NewGroupDelay`, not isolated, and `JoinToEvent` allows it. An unrecorded document change maps the
stacks and starts a new epoch. Undo and redo append a transaction with `undoOp`. The selection restored by undo is
the item's `StartSelection` when the undoing source made the item, and otherwise the caller's selection mapped. The
log drops transactions at or below the minimum registered reader cursor.

- [x] **Step 1:** `HistoryTests.fs` (pure, no graph):
  - `undo with nothing to undo` gives `ValueNone`.
  - `undo then redo restores text and selection`.
  - `typing groups by time`: three `input.type` insertions 100 ms apart are one item; one 600 ms later is a second.
  - `group id joins non-adjacent edits`; `isolateHistory splits`.
  - `a new edit clears redo`.
  - `unrecorded edit maps the undo`: insert "abc" at 5 (recorded), insert "XX" at 0 (`addToHistory false`), undo
    removes "abc" at 7 and leaves "XX".
  - `undo of a region an unrecorded edit deleted` applies as an empty change and does not raise.
  - `depth bounds the stack`: with `Depth = 3`, five isolated edits leave `undoDepth = 3`.
  - `position returns after undo`: position after edit then undo equals the starting position; after an
    unrecorded edit it differs.
- [x] **Step 2:** `DocumentTests.fs` (each test activates a `new Graph ()`):
  - `text follows dispatch`; `two dispatches in one batch wake an effect once and lose nothing`.
  - `transaction against an older head is mapped`: head has an insert of "ab" at 0; `PostAt 0L` deleting `[1, 2)` of
    the original deletes the same character in the current text.
  - `dirty flag across the saved position`: edit, `MarkSaved`, edit, undo gives clean; undo again gives dirty; redo
    gives clean.
  - `cross-file group`: two documents receive transactions with one `group` id; each has `undoDepth = 1`, and undoing
    one leaves the other's text unchanged.
  - `Post from another thread` lands after `graph.Pump ()`.
- [x] **Step 3:** Implement `History.fs`, then `Document.fs`.
- [x] **Step 4:** Tests pass. Commit `feat: the log, Document and history as a reader`.

### Task 6: `Editor`

**Files:** Create `Editor.fs`, `EditorTests.fs`.

**Interfaces — produces:**

```fsharp
type Update =
    { StartState: EditorState; State: EditorState; Transactions: Transaction[]
      Changes: ChangeSet; Effects: StateEffect[] }
    member DocChanged: bool;  member SelectionSet: bool
type UpdateReader          // .Read: unit -> Update   (tracked; everything since this reader's last value)
type Editor
    member Id: int
    member Document: Document
    member State: Memo<EditorState>
    member Text: Memo<Text>
    member Selection: Memo<EditorSelection>
    member Field: StateField<'T> -> Memo<'T>           // cached per field; first call outside a pure body
    member Facet: Facet<'In, 'Out> -> Memo<'Out>
    member Dispatch: TxSpec list -> unit
    member Updates: unit -> UpdateReader
    member PageLines: Signal<int>                      // default 20; a view writes its visible line count
module Editor =
    val create: Document -> Extension list -> Editor   // under the current Owner
module History =       // continued
    val undo: Editor -> bool
    val redo: Editor -> bool
```

Rules: the state memo folds `Document.Since cursor`. A transaction from this editor applies whole; one from another
editor applies through `Transaction.asForeign`; one from source 0 applies whole except its selection. `Dispatch`
runs in one `Api.batch`: read `State` (folds to the head), build the transaction with `StartSelection` and the
document clock's time, `Append`. A reader created by `Updates ()` registers a cursor and removes it on cleanup; its
first `Read` returns an empty update at the current state.

- [x] **Step 1:** Tests:
  - the dossier's section 4.7 sketch compiles and runs: `edits.Value = 1`, text is `"let result = 42\n"`.
  - `selection memo does not wake on a foreign edit after the cursor`: an effect on `editor.Selection` runs once.
  - `two editors converge with separate selections`: A at 3 and B at 10 on one document; A inserts 2 characters at
    3; both texts are equal, A's head is 5, B's head is 12.
  - `fold under coalesced dispatch`: three dispatches in one `Api.batch`; an effect on `Updates().Read()` runs once
    more and `update.Transactions.Length = 3`, `update.Changes` equals the composition.
  - `dispatch from an effect`: an effect that reads an update and dispatches a selection-only transaction when the
    document changed terminates, and a second reader sees both transactions in order.
  - `compartment reconfigure through dispatch` changes `editor.Facet EditorState.tabSize`.
  - `undo in either editor undoes the document's last edit`; the undoing editor's selection is restored, the
    other's is mapped.
  - `read-only`: `EditorState.readOnly.Of true` leaves `Dispatch [Tx.change ...; Tx.userEvent "input.type"]`
    without effect, and applies a change with no user event.
  - `disposing the owner unregisters the reader`: the log length returns to 0 after further dispatches.
- [x] **Step 2:** Implement.
- [x] **Step 3:** Tests pass. Commit `feat: Editor with state fold, slices and update readers`.

### Task 7: Commands, keymaps, language

**Files:** Create `Commands.fs`, `Keymap.fs`, `Language.fs`, and a test file for each.

**Interfaces — produces:**

```fsharp
type Command = Editor -> bool
type StateCommand = EditorState -> (TxSpec list -> unit) -> bool
module Commands =
    val ofState: StateCommand -> Command
    val cursorCharLeft, cursorCharRight, cursorGroupLeft, cursorGroupRight: Command
    val cursorLineStart, cursorLineEnd, cursorLineUp, cursorLineDown: Command
    val cursorPageUp, cursorPageDown, cursorDocStart, cursorDocEnd: Command
    val selectCharLeft, selectCharRight, selectGroupLeft, selectGroupRight: Command
    val selectLineStart, selectLineEnd, selectLineUp, selectLineDown: Command
    val selectPageUp, selectPageDown, selectDocStart, selectDocEnd: Command
    val selectAll, selectLine: Command
    val insertText: string -> Command
    val insertNewline, insertNewlineKeepIndent: Command
    val deleteCharBackward, deleteCharForward: Command

type KeyBinding = { Keys: KeyChord list; Run: Command; Shift: Command voption; Scope: string }   // Scope "" = editor
type KeymapResult = Handled | Pending of KeyChord list | Unhandled
module Keymap =
    val facet: Facet<KeyBinding list, KeyBinding[]>
    val extension: KeyBinding list -> Extension
    val bind: keys: string -> Command -> KeyBinding          // Ranvier.Keyboard KeyChord.parse notation
    val standard, defaults, history: KeyBinding list
    val run: Editor -> pending: KeyChord list -> KeyChord -> KeymapResult

[<Struct>] type Tag = { Id: int; Name: string }
module Tag =
    val define: string -> Tag
    val keyword, typeName, string, number, comment, operator, punctuation, variable, functionName: Tag
[<Struct>] type Token = { From: int; To: int; Tag: Tag }
type Language = { Name: string; Tokenize: string -> Token[] }
module Language =
    val ofLineLexer: name: string -> (string -> Token[]) -> Language
    val facet: Facet<Language, Language voption>
    val extension: Language -> Extension
type HighlightStyle       // tag -> class name
module HighlightStyle =
    val define: (Tag * string) list -> HighlightStyle
    val classOf: Tag -> HighlightStyle -> string voption
type TokenCache =         // per language, keyed by line text
    new: capacity: int -> TokenCache
    member Tokens: Language -> string -> Token[]
    member Enabled: bool with get, set
    member Hits: int64;  member Misses: int64;  member Reset: unit -> unit
```

Motion uses `Ranvier.Keyboard`'s grapheme rules. Vertical motion keeps `GoalColumn`. Editing commands return
`false` when `EditorState.readOnly` is true, and tag their transactions `input.type`, `input`, `delete.backward` or
`delete.forward`. `Keymap.standard` holds arrows, Home/End, PageUp/PageDown, Ctrl+Home/End, their Shift forms,
Ctrl+A, Enter, Backspace, Delete, Tab (inserts `indentUnit`); `Keymap.history` holds Ctrl+Z and Ctrl+Y /
Ctrl+Shift+Z; `Keymap.defaults` is both.

- [x] **Step 1:** Tests: char motion over `"a😀b"` moves by grapheme; group motion over `"foo bar"`;
  line up and down keep the goal column across a short line; `selectAll`; `deleteCharBackward` at 0 returns false;
  delete over a selection removes it; `insertNewlineKeepIndent` copies leading spaces; a two-stroke binding returns
  `Pending` then `Handled`; a higher-precedence binding wins; `Shift` form runs with Shift held; token cache hit
  and miss counts; `HighlightStyle.classOf`.
- [x] **Step 2:** Implement.
- [x] **Step 3:** Tests pass. Run `dotnet fsi --exec build.fsx -- ci`. Commit `feat: commands, keymaps and the line tokenizer model`.

### Task 8: `Ranvier.Plume.View`

**Files:** Create `src/Ranvier.Plume.View/*` and `tests/Ranvier.Plume.View.Tests/*` as in the file structure.
Source read first: `EditorShowcase/App.fs:58-143`, `Editor.fs`, `Model.fs`, `Styles.fs:255-409`, `LiveSheet.fs`,
`Harness.fs` (`hosted`), `Ranvier.View/src/Ranvier.View/Dsl.fs`, `Ranvier.Css/src/Ranvier.Css.View`.

**Interfaces — produces:**

```fsharp
type Theme =
    { FontFamily: string; FontSize: float; LineHeight: float
      Ink: string; Dim: string; Faint: string; Accent: string; Background: string; Edge: string
      Selection: string; ActiveLine: string; Thumb: string
      Tokens: (Tag * string * string) list }          // tag, class name, CSS declarations text
module Theme =
    val define: Theme -> Theme      // validates
    val dark: Theme                 // the baseline's colours and classes t1..t9

type ViewConfig = { Theme: Theme; Scheduler: FrameScheduler; Overscan: int; RebaseLines: int }
[<Struct>] type VisibleRange = { FirstLine: int; LastLine: int; From: int; To: int }
[<Struct>] type Rect = { Left: float; Top: float; Width: float; Height: float }
type LayerMarker = { Rect: Rect; Class: string }
type GutterMarker = { Text: string; Class: string }

type EditorView     // one per mount
    member Editor: Editor
    member Viewport: Memo<VisibleRange>
    member HasFocus: Memo<bool>
    member Focus: unit -> unit
    member ScrollTop: Memo<float>
    member ScrollTo: float -> unit
    member ScrollBy: float -> unit
    member PosAtCoords: x: float -> y: float -> int            // viewport coordinates
    member CoordsAtPos: int -> Rect                            // sheet coordinates
    member LineHeight: float;  member CharWidth: Memo<float>;  member GutterWidth: Memo<float>
    member Scheduler: FrameScheduler
    member RowsWritten: int64                                  // rows recomputed, for the benchmark

module EditorView =
    val create: ViewConfig -> Component<Editor> * (unit -> StyleDefinition list)
    val recycleRows: Facet<bool, bool>        // default true
    val compositorLayer: Facet<bool, bool>    // default false
    val markupCache: Facet<bool, bool>        // default true
    val scrollIntoView: StateEffectType<int>  // position
    val current: Editor -> EditorView voption // the view mounted on an editor, for commands and harnesses

module ViewPlugin =
    val define: (EditorView -> unit) -> Extension      // runs under its own Owner; Api.onCleanup for teardown
module Gutter =
    val define: {| Class: string; Width: float; LineMarker: EditorState -> Line -> GutterMarker voption |} -> Extension
module Layer =
    val define: {| Above: bool; Class: string; Markers: EditorView -> LayerMarker[] |} -> Extension   // Markers is tracked

module LineNumbers =
    val defaults: {| Format: int -> string |}
    val extension: {| Format: int -> string |} -> Extension
module ActiveLine =
    val extension: Extension                   // a band layer, and the active class on the row's gutter cell
module DrawSelection =
    val extension: Extension                   // rectangles for every range
type CursorShape = Bar | Block | Underline
module Cursor =
    val shape: Facet<CursorShape, CursorShape>
    val defaults: {| Shape: CursorShape; BlinkRate: float |}   // BlinkRate 530.0 ms; 0 disables
    val extension: {| Shape: CursorShape; BlinkRate: float |} -> Extension
```

Design fixed here:

- **Row window.** `Window = { Text; Lines; Start; Pool; Anchor; Recycle; Config: obj; DirtyFrom; DirtyTo; Shifted; Update: obj }`
  is a memo over `Scroll`, `Height`, the view's own update reader and the gutter, language and highlight facets.
  `Pool = ceil (Height / LineHeight) + 1 + 2 * Overscan`. `Start = clamp (first - Overscan)`. The anchor moves when
  the first line is more than `RebaseLines` from it. `DirtyFrom..DirtyTo` is the line range of the update's changed
  ranges in the new text, set only when the update differs by reference from the previous window's.
- **`affected prev next`** returns every slot when `Pool`, `Anchor`, `Recycle` or `Config` differ; otherwise the
  slots of lines in the new window and outside the old one, of dirty lines inside the window, and of every line
  from `DirtyFrom` to the window's end when `Shifted`.
- **`RowContent = { Line: int; Top: float; Gutter: UnsafeHtml; Markup: UnsafeHtml }`.** A slot past the end of the
  document has `Line = -1` and sits above the sheet. The gutter cell is one markup slot holding each gutter's
  marker as a `span`; the text is one markup slot. Markup is cached by line string reference when `markupCache` is on.
- **Rows** mount through `forEachFixed` over a `Signal<RowSlot[]>` (`RowSlot = { Slot: int; View: EditorView }`); a
  row's bindings read `view.Rows.Get slot`.
- **Layers** render a pool of absolutely positioned elements per layer through `forEachFixed`; each reads its
  marker from the layer's memo, or hides.
- **Keyboard.** The root element is the focus target (`tabindex="0"`). `keydown` converts through
  `Ranvier.Keyboard.Dom.KeyboardEvent.toKeyChord`, runs `Keymap.run`, and on `Unhandled` inserts
  `KeyChord.tryPlainText` unless the state is read-only. A handled key calls `preventDefault`.
- **Pointer.** `pointerdown` focuses, places the cursor at `PosAtCoords` (Shift extends), captures the pointer;
  `pointermove` while captured extends the main range; the scrollbar track behaves as the baseline's.
- **Scroll into view.** The view's reader handles `Transaction.ScrollIntoView` and the `scrollIntoView` effect by
  writing `ScrollTarget` with the least movement that shows the main head's line.
- **Constants** kept from the baseline: line height 20 (theme), overscan 6, rebase 700 lines, lead 16,000 px,
  sheet 36,000 px, thumb minimum 28 px.

- [x] **Step 1 (made late):** Spike in a scratch test: a component from this package applying a recorded utility,
  mounted in a hidden window whose sheet received the exposed definitions, has the utility's computed style. A
  `Frames.frame` callback scheduled from an effect runs once for three writes to a signal. If either fails, stop
  and report.
  No spike was made before the view was written. Its two questions are tests of the view, written afterwards:
  "the classes the view hands over style its elements through the sheet a host adopts" and "a scroll request
  reaches the rows through the frame callback". Both pass.
- [x] **Step 2:** Smoke test (`tests/Ranvier.Plume.View.Tests`, hidden window, 1280 x 800):
  - mounts a 1,000,000-line document; after two steps the row pool has `ceil(h / 20) + 13` elements and the first
    row's text is line 1's.
  - `ScrollBy 60.0` then one frame: exactly 3 rows recomputed (`RowsWritten` delta), `scrollTop` moved by 60.
  - three `ScrollBy 20.0` in one turn recompute rows once (delta 3).
  - a one-character insert in a visible line recomputes 1 row; a newline insert recomputes the rows from that line.
  - cursor rectangle after `Dispatch [Tx.selection (single p p)]` equals `CoordsAtPos p`; `PosAtCoords` round-trips.
  - two-range selection draws at least two rectangles.
  - line-number gutter shows `Number + 1`; the active row carries the active class and moves with the cursor.
  - a synthesized `keydown` of `a` on the focused root inserts "a"; with `readOnly` it does not; Ctrl+Z undoes.
  - degenerate: height 0 mounts with pool 0 and no exception; a 3-line document shows 3 lines and blank slots;
    `ScrollTo 1e12` clamps to the maximum.
  - a compartment that removes `LineNumbers` empties the gutter cell and disposes the plugin's owner.
- [x] **Step 3:** Implement the package.
- [x] **Step 4:** Tests pass; `ci` passes. Commit `feat: Ranvier.Plume.View`.

### Task 9: The sample and the baseline stopwatch

**Files:**
- Create: `samples/EditorShowcase/` (`EditorShowcase.fsproj`, `Png.fs`, `Sample.fs`, `Lexer.fs`, `Styles.fs`,
  `LiveSheet.fs`, `App.fs`, `Harness.fs`, `Program.fs`), from `C:\wk\ranvier-release\EditorShowcase`.
- Modify: `C:\wk\ranvier-release\EditorShowcase\Harness.fs:265-308` (third stopwatch around `work ()`, columns
  `dispatch ms mean  median  p95`).

The sample keeps the shell (sidebar, tabs, status bar, statistics panel, tour) and replaces `Editor.fs`,
`Model.fs`'s viewport types, the text view and its styles with `EditorView.create` and an extension list. The three
toggles reconfigure compartments over `EditorView.recycleRows`, `TokenCache.Enabled` / `EditorView.markupCache`
and `EditorView.compositorLayer`. Documents are `Document` values built with `Text.ofLines`. `Lexer.fs` becomes a
`Language.ofLineLexer`. `LiveSheet.fs` gains `add: StyleDefinition list -> unit`.

Modes: interactive, `--shot`, `--bench` (the nine rows of `Harness.fs:254-263`, stopwatches App, Turn, Dispatch),
`--memory` (7.2: 10,000 single-character insertions at lines `(i * 7919) % 1_000_000`, each isolated;
`GC.GetTotalMemory true` before and after; prints bytes per insertion), `--load` (7.3: UTF-8 bytes of the
1,000,000-line file; `Text.ofUtf8Scalar` and `Text.ofUtf8`, 10 runs each after 3 warm-ups, median).

- [x] **Step 1:** Build the sample; `--shot` produces a PNG that shows text, gutter, caret and scrollbar (read it).
- [x] **Step 2:** Add the Dispatch stopwatch to the baseline harness; build it; `--bench` prints three column groups.
- [x] **Step 3:** Sample `--bench`, `--memory`, `--load` run to completion hidden.
- [x] **Step 4:** Commit `feat: EditorShowcase sample on Ranvier.Plume`. (The baseline edit is outside any repository.)

### Task 10: SIMD checks

- [x] **Step 1:** `--load`: record scalar and vectorised medians. If the vectorised scan is not faster, `Text.ofUtf8`
  calls the scalar scan and `LineScan`'s vector path is deleted.
- [x] **Step 2:** Many-range apply: a test-project benchmark (`--filter`-free console mode of the sample, `--apply`)
  applies 10,000 single-character replacements over the 1,000,000-line document as one `ChangeSet`; record the time
  and confirm one pass (no per-change full-tree rebuild) by comparing with 10,000 sequential `Text.replace` calls.
- [x] **Step 3:** Commit `perf: keep or drop the vectorised line scan by measurement`.

### Task 11: Acceptance

- [x] **Step 1:** 7.1: baseline `--bench` and sample `--bench`, alternating, three runs each, one session. Record
  every row. Apply the pass rules of 7.1.
- [x] **Step 2:** 7.2: `--memory`; pass under 512 bytes per insertion. Set `History.defaults.Depth` from the
  figure and state the arithmetic.
- [x] **Step 3:** If a 7.1 row fails: profile, and fix inside the core-lookup design. If the cause is a core
  primitive, stop and report the primitive and the missing behaviour.
  *Done 2026-11-19: the two typing rows failed in the first record. The profile and the changes are in
  `ACCEPTANCE.md`, 7.1; all nine rows pass in the record made after them. No core primitive was the cause.*
- [x] **Step 4:** Write `docs/ACCEPTANCE.md` with the tables and labels. Update the dossier's status line and
  decision 17.
- [x] **Step 5:** `dotnet fsi --exec build.fsx -- ci`; `rfs check --fail-on-error`; fresh-context review of the
  whole repository; commit `docs: acceptance measurements`.
