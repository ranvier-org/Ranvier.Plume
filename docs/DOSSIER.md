# Ranvier.Plume: feature and API dossier

**Date:** 2026-11-18 (second version)
**Status:** approved 2026-11-18. Phase 1 is built: see `ACCEPTANCE.md` for the measurements and section 11 for
what changed while building.
**Source of the extraction:** `C:\wk\atelier`, branch `feat/atelier-foundation`.
**Relation to CodeMirror:** the state model takes its ideas from CodeMirror 6. This is not a port and not a
binding.

Every figure carries its source: **measured here** (what was run), **from source** (file and line),
**from documentation** (file named), **estimate** (how it was derived), or **general knowledge** (not checked).

---

## 1. Intent

Atelier's text view becomes a standalone library, `Ranvier.Plume`. An editor is a fold over a log of edits: the
log is the source of truth, and the document, the undo history and every other consumer are readers of it.
Ranvier is the only change-propagation mechanism.

The reason is reuse outside Atelier. The scenarios this design is sized for are my proposal:

| Scenario | What it needs from the library |
|---|---|
| Atelier's editor | 1,000,000-line files, modal input, gutters fed by a kernel, several windows on one file |
| Read-only code viewer (docs, showcase pages) | Document and view with no input and no history |
| Small inputs (command bar, search field, rename box) | A single-line configuration of the same view |
| Diff, log and console panes | Decorations, append-only edits, no caret |
| Hosts without a DOM (terminal, tests, a kernel thread, `.fsx` scripts) | The state package alone |
| The browser, through fickl | The same sources compiled by Fable |

**Goals**

1. A state package with no DOM dependency that a test, a script or a background thread can use.
2. An extension model where a feature is a value added to a list, not a change to the editor's source.
3. Fine-grained reads: a consumer subscribes to the text, the selection or one field, and wakes only when that
   slice changes.
4. History costs bytes per edit. It holds no document value and no part of one beyond the text an edit removed.
5. The frame cost of the current view is kept (section 7).

**Non-goals**

- A port of CodeMirror's source, or compatibility with its JavaScript packages.
- `contenteditable`. The view keeps pooled rows.
- A port of the Lezer parser.

**Anti-goals** (the library has failed if any of these holds, even if it works)

- A second notification system in the public API: listeners, observers, event emitters, update callbacks.
- A view-side state manager that duplicates a Ranvier core primitive (section 4.1).
- An Atelier concept inside the library: `ProofStatus`, `DocId`, `WindowFramePacket`, `Session`, the scroll tour,
  the kernel.
- A regression of the frame cost on the 1,000,000-line document.

---

## 2. What exists today

The "text viewport" is not one component. It is spread over three projects.

| Piece | File | Lines (**measured here**, `wc -l`) | What it does | What ties it to Atelier |
|---|---|---|---|---|
| Text store | `Atelier.Text/TextSnapshot.fs` | 813 | Immutable AVL rope. A leaf holds up to 128 lines as UTF-8 bytes (`DefaultChunkLines`, line 55) and materialises line strings lazily. Untouched lines keep their `string` identity across an edit. | `DocId` and `Version` are fields of the snapshot. |
| History | `Atelier.Text/TextHistory.fs` | 183 | A mutable `DocumentBuffer`: current snapshot, caret, undo and redo stacks of before/after snapshot pairs, explicit undo groups. | `EditOrigin` from `Atelier.Contracts`; a BAREWire journal codec. |
| View model | `Atelier.Shell/Editor.fs` | 511 | A class: scroll, caret, edits, the projection of visible lines onto a row pool, caret and selection geometry, blink. | `Session` (tour, recycle, layer, token cache), `VimState`, `ScriptConfig`, `WindowFramePacket`. |
| View state | `Atelier.Shell/Model.fs` lines 16-108 | 93 | `Viewport`: 30 `Signal` fields. `Row`: one `Signal<RowContent>` per pooled element. Layout constants. | `GutterMark` (which carries `ProofStatus`), `CursorShape` from the Vim engine. |
| Template | `Atelier.Shell/App.fs` lines 172-299 | 128 | `rowView` and `textView`, pointer and wheel handlers, the custom scrollbar. | Handlers call `OnVimChanged`; selection is set through Vim's Visual mode. |
| Frame loop | `Atelier.Shell/App.fs` lines 1789-1878 | 90 | Measures the viewport, calls `Sync` in one `Api.batch`, writes `scrollTop`, reports the visible range to the kernel. | Interleaved with tab-drag polling, the stats HUD and kernel dispatch. |
| Styles | `Atelier.Shell/Styles.fs` lines 862-1086 | 225 | One `Ranvier.Css` utility per element; positions are reader values on the 30 signals. | Token colours are descendant rules on `.t1` to `.t10`. |
| Lexer | `Atelier.Shell/Lexer.fs` | 270 | A stateless per-line lexer for F# and Clef, and a cache of markup keyed by line text. | The language is fixed. |
| Modal input | `Atelier.Input/*.fs` | 2,943 | The Vim engine. | It mutates `DocumentBuffer` directly: 45 lines call `DocumentBuffer.apply` (**measured here**, `grep -c` on `VimEngine.fs`). |

Layout constants (**from source**, `Model.fs:16-44`): line height 20 px, gutter 72 px, overscan 6 rows, row pool
capped at 128, anchor rebase every 700 lines, sheet height 36,000 px.

### How Ranvier is used today

Only at the leaf. Scroll, caret and edit operations change plain mutable fields and set a `stale` flag
(`Editor.fs:20-34`). Once a frame, `Sync` compares each pooled row with its line by hand and writes the row
signals and the 30 viewport signals inside one `Api.batch` (`Editor.fs:296-482`, `App.fs:1828-1830`). There is no
memo and no effect in the text view.

Nothing can subscribe to "the document changed" or "the caret moved". The shell polls: `App.fs:1866` compares
`document.CaretLine` with the last frame's value on every frame.

### What history costs today

An undo step holds two rope roots (`HistoryEntry`, `TextHistory.fs:8-14`). They share every leaf except the
edited one, so a step does not copy the document. It does retain one leaf: an edit to a line copies the leaf's
byte array and both offset arrays (`TextSnapshot.fs:494-540`). At 128 lines a leaf and 40-character lines that
is about 6 KB a step (**estimate**: 128 x 40 bytes of text plus two arrays of 129 four-byte integers; not
measured). An edit recorded as data is tens of bytes.

### Limits of the current view (**from source**)

- One caret. A selection exists only as Vim's Visual mode and is drawn as three fixed bands (`Editor.fs:383-471`).
- A column is `column * charWidth` (`Editor.fs:366`). Tabs, wide characters and proportional fonts are wrong.
- No horizontal scroll and no wrapping. Selection bands are a fixed 2,000 px wide (`Editor.fs:405`).
- Fixed line height.
- No text input, IME or clipboard in the view. Keys arrive at the window and go through the Vim engine.
- No decoration API. The gutter shows one hardwired badge; an inline hint is one string per row.
- The lexer keeps no state between lines, so a multi-line comment or string is coloured per line.
- Two windows on one file exchange whole snapshots (`DocSyncListeners`, `App.fs:1672-1704`).

---

## 3. Approaches

**A. Lift and shift.** Move `Editor.fs`, the `Viewport` record, the template and the styles into a package and
replace `Session` with an options record. Smallest change. It keeps the imperative class and the snapshot
history, has no extension model, and leaves the Vim coupling in place.

**B. State as the source of truth.** An immutable editor state replaced on every transaction, as CodeMirror 6
does, with history as a field of that state. This was the first version of this dossier. It needs a separate
mechanism for anything that wants to see transactions, and two windows on one file must forward edits to each
other.

**C. The log as the source of truth (chosen).** Edits are appended to a per-file log. The text, the editor
state, history, the dirty flag and a second window are all folds over that log. Atelier's rope and its
pooled-row design are extracted and kept. Extensions are facets and state fields, as in B.

**D. Port CodeMirror 6.** Rejected. CodeMirror's view scrolls a native scrolling element (**from
documentation**, `atelier/research/text-editor-optimisation.md`, section 2), and edits through `contenteditable`
while reading the DOM selection back (**general knowledge**). Atelier's renderer avoids all three on purpose.

**C is the choice.** You proposed the event-sourced shape; it removes a mechanism that B needed and makes
shared documents the ordinary case.

---

## 4. Architecture

### 4.1 Core and view state

This section follows the rule in `Ranvier/.claude/rules/view-uses-core.md`: ownership, disposal, keyed identity,
change detection and scheduling belong to the Ranvier core, and a view-side duplicate needs your agreement.

**Core primitives used**

| Primitive | Its job here |
|---|---|
| `Signal` | The head of a document's log. The viewport's inputs: scroll offset, size, character width, focus. |
| `createMemo`, with its previous value | The fold: document text and editor state from the log. The cursor of each update reader. Slices: selection, a field, a facet. Geometry: visible range, caret and selection rectangles, scrollbar thumb. |
| `createLookup` | The row pool: slot to row content. Its `affected` function names the slots whose line changed, so a 3-line scroll recomputes 3 rows. |
| `createSelector` | The active line: two rows wake, not the pool. |
| `createProjection` | Keyed sets with a lifetime per key: layer rectangles and gutter markers; later, panels and tooltips. |
| `createEffect`, `createEffectOn` | View plugins; the `scrollTop` write; the frame request. |
| `Owner`, `createRoot`, `onCleanup`, `runWithOwner` | Extension lifetimes. Reconfiguring a compartment disposes one owner. |
| `batch`, `flush`, the graph's dispatcher | One flush per dispatch. `Graph.Dispatch` for an edit from another thread. |
| `createAsync` and boundaries | Later: completion, hover, lint. |
| `Journal` | Later: the log on disk. |

**View-owned state**

| State | Why the core cannot hold it |
|---|---|
| The pooled row elements; the references to the viewport, probe and scrollbar-track nodes | DOM nodes. |
| The last `scrollTop` written, and the sheet's anchor line | The last value written to a DOM target; layout. |
| A measured character width or viewport size, until it is written to its signal | A layout reading. |
| Pointer capture and the drag in progress | Event state of a native node. |

Nothing else. Atelier's `Editor.Sync` is a hand-written change detector and commit loop, and the log's readers
could have been a hand-written subscriber list. Neither is carried over: rows are a core lookup, and a reader's
cursor is the previous value of a memo.

If the benchmark of section 7 shows that a core primitive cannot meet the frame cost, that is reported to you
with the primitive and the behaviour it lacks. The default proposal is then a change to the core, not a
view-side loop.

### 4.2 The log is the source

| Thing | What it is | Reactive surface |
|---|---|---|
| `Text` | An immutable rope. | None; a value. |
| `Transaction` | The event: one atomic edit, as data. | None; a value. |
| `Document` | One file: its log, its saved text, its current text. | `Head: Signal<int64>`, `Text: Memo<Text>`, `IsDirty: Memo<bool>` |
| `Editor` | One view of a document: its extensions, its selection, its fields. | `State: Memo<EditorState>` and a memo per slice |
| `EditorState` | Text, selection, field and facet values at one position of the log. | None; a value. |

Two text values are kept per file: the saved text and the current text. Both are rope roots, so the saved text
costs only the leaves that differ. Everything else is derived from the log.

**A transaction carries data only:**

- its sequence number, and the sequence number it was written against;
- its source (which editor, or an outside source such as a language server);
- its changes, as a `ChangeSet` over any number of ranges, including the text each range removed;
- the selection its source wants afterwards;
- its effects and annotations (user event, time, whether history records it, a group id).

It holds no reference to a state. CodeMirror's transaction points at the state before and after it; in a log
that would pin every intermediate rope and bring back the 6 KB a step.

**Rules of the log**

1. **The signal is the log's head, not the latest transaction.** Ranvier coalesces: a batch flushes when the
   outermost one exits (**from source**, `Ranvier/src/Ranvier/Core.fs:3632-3644`), so two dispatches in one batch
   reach a reader as one wake. A reader folds every transaction between its own position and the head, so none
   is lost.
2. **A transaction is invertible on its own.** Removed text is a slice of the old rope, which shares its leaves.
   Undo never replays from the saved text.
3. **Undo appends.** Undoing a transaction appends its inverse as a new transaction, because other readers have
   already consumed the original.
4. **The log is bounded.** Past the undo depth, transactions that every reader has consumed are dropped.

**The fold.** `createMemo` hands its compute function the previous value, so a memo can fold. A stale memo
recomputes when it is read, including inside a batch (**from source**, `Core.fs:5263-5273`), so a dispatch can
read the current state, build the next transaction against it and append, all in one batch.

```fsharp
// A sketch, not compiled.
let text =
    Api.createMemo (fun prev ->
        let struct (text, at) = ValueOption.defaultValue (struct (saved, savedAt)) prev
        struct (log.Apply (at, head.Value, text), head.Value))
```

**Readers of the log**

| Reader | What it reads | Phase |
|---|---|---|
| Document text | changes | P1 |
| Editor state: selection, fields | the whole transaction | P1 |
| History | changes and annotations | P1 |
| Dirty flag | history's position against the saved position | P1 |
| The view: scroll-into-view requests, positions it holds | effects and changes | P1 |
| Token cache | changed ranges | P1 |
| A second editor on the same document | the whole transaction | P1 |
| Changed-lines gutter | saved against current text, skipping shared subtrees | P3 |
| Incremental sync to a language server | changes since the last one sent | P3, in the host |
| Crash journal | transactions, through a `Ranvier.Journal` codec | P3 |
| Another process | transactions over a wire | P4 |

**Reading transactions.** A consumer that needs transactions, not just a slice of state, takes its own reader:

```fsharp
let updates = editor.Updates ()         // owned by the current Owner; a memo whose previous value is its cursor

Api.createEffect (fun () ->
    let update = updates.Read ()        // tracked; everything since this reader's last read
    // update.StartState, update.State, update.Transactions, update.Changes (composed)
    for effect in update.Effects do ...)
```

**Several sources on one document.** Each transaction names the sequence number it was written against. One
that arrives behind the head is mapped over the transactions that landed since (`ChangeSet.map`) and then
appended. Atelier's kernel protocol already carries that base version (`WindowIntent.ApplyEdits`,
`Atelier.Contracts/Primitives.fs:84`). Each editor keeps its own selection: it takes the selection of its own
transactions and maps it through everyone else's.

**One edit across several files.** Each file registers its own transaction in its own log; the transactions
share a group id. In each file the group is one history item. Undoing the group in every file together needs
something that knows all the files, which is a host. Phase 1 carries the group id and the per-file history item;
the coordinator is phase 3, with Atelier's kernel as its first user.

**History.** A reader of the document's log, one per document and shared by every editor on it. It tracks which
transactions can be undone and in what groups. An inverse is mapped over later transactions that are not being
undone. "Saved" is a position in history, so undoing back to it clears the dirty flag.

**The state layer stays pure.** `EditorState.create` and `EditorState.apply` are functions over values with no
graph. A test, a script or a kernel thread can fold a log without a view.

### 4.3 Packages

The repository mirrors `Ranvier.Keyboard` (`src/`, `tests/`, `fable/`, `build.fsx`, traced package variants).

| Package | Depends on | Contents |
|---|---|---|
| `Ranvier.Plume` | `Ranvier`, `Ranvier.Keyboard` | Text, changes, selection, transactions, the log, `Document`, `EditorState`, facets, fields, effects, compartments, range sets, history, commands, keymaps, tokenizer and highlight model, search cursors, `Editor` |
| `Ranvier.Plume.View` | the above, `Ranvier.View`, `Ranvier.View.Dom`, `Ranvier.Css`, `Ranvier.Keyboard.Dom` | The `EditorView` component, row pool, gutters, layers, pointer and keyboard input, theme |
| `Fable.Ranvier.Plume.View` (later) | `Fable.Ranvier.Dom` | The same view sources compiled for the browser, as `Fable.Ranvier.Keyboard.Dom` does |
| `Ranvier.Plume.Lang.FSharp` (later) | core | The existing F# lexer as a language pack |
| `Ranvier.Plume.Vim` (later) | core | The Vim engine from `Atelier.Input`, as an extension |

### 4.4 Where CodeMirror has a callback

| CodeMirror 6 | Ranvier.Plume | Ranvier primitive |
|---|---|---|
| `view.state` | `editor.State` | a memo folding the log |
| `ViewUpdate.docChanged`, `selectionSet` | `editor.Text`, `editor.Selection` | `Memo` with an equality cutoff |
| `state.field(f)` read inside `update` | `editor.Field f` | `Memo<'T>` |
| `state.facet(f)` | `editor.Facet f` | `Memo<'Out>` |
| `EditorView.updateListener` | none; read a memo in an effect | `Api.createEffect` |
| `ViewUpdate.transactions`, `changes` | `editor.Updates ()` | a memo whose previous value is the cursor |
| `ViewPlugin` with `update` and `destroy` | `ViewPlugin.define (fun view -> ...)` | a function run under an `Owner`; `Api.onCleanup` |
| `Compartment.reconfigure` | the same effect | disposes the old extension's `Owner` |
| `view.dispatch(tr)` | `editor.Dispatch spec` | one `Api.batch`: build, append, advance the head |
| `requestMeasure` read and write phases | the view's frame | the host-neutral `FrameScheduler` (`Ranvier/src/Ranvier.Dom/Frames.fs:15`) |
| "Is this the active line?" | per-row flag | `Api.createSelector` |
| Async sources (completion, hover, lint) | the same | `Api.createAsync`, boundaries |
| Dispatch from another thread | `document.Post transaction` | `Graph.Dispatch` |
| `@codemirror/collab` | several sources on one log | section 4.2 |

### 4.5 The view

- **Rows.** A fixed pool of row elements, as today. Which line a slot shows and what it draws is a core lookup
  keyed by slot. Scrolling by three lines names three slots as affected; an edit names the slots of the changed
  lines. The hand-written comparison in `Editor.Sync` goes.
- **Geometry.** Scroll offset, viewport height and character width are input signals. The visible range, caret
  rectangle, selection rectangles and scrollbar thumb are memos. The 30 hand-written signals go.
- **Frames.** An effect requests a frame when something the view draws has changed. An idle editor requests
  none. Atelier's research lists this as item A1 (**from documentation**, `text-editor-optimisation.md`,
  section 5).
- **Extensions.** Line numbers, the active line, the drawn selection and the caret are written on
  `ViewPlugin.define`, `Gutter.define` and `Layer.define`, the same public points a host's extension uses. The
  frame cost of section 7 is therefore measured on the extensible view.

### 4.6 SIMD

Resolving one small edit is a walk down the tree and one leaf copy. The walk is pointer chasing, and the runtime
already vectorises the copy (**general knowledge**, not measured). SIMD is planned where the work is a scan over
bytes or a pass over flat arrays:

| Where | Today | Planned | Phase |
|---|---|---|---|
| Finding line breaks while loading | scalar byte loops (`TextSnapshot.fs:349`, `:450`) | a vectorised scan for `\n` and `\r` | P1 |
| Applying a change set with many ranges (replace-all, a large paste, thousands of carets) | each edit splits and rejoins the tree (`applyEdits`, `TextSnapshot.fs:772-813`) | one left-to-right pass that rebuilds touched leaves and reuses untouched subtrees | P1 |
| Shifting positions after an edit (selections, markers, diagnostics) | not applicable | positions stored as flat integer arrays, moved by a vector add | P1 for the layout, P2 for use |
| Searching | Vim's search over line strings | a search over the UTF-8 leaves | P2 |
| Comparing saved and current text | not applicable | pointer equality on shared subtrees, then a vector compare of leaves | P3 |

The layouts are chosen to allow it: a change set is a flat array of section lengths, and a range set stores
positions in arrays, not in objects. Each vectorised loop keeps a scalar fallback and is gated out of the Fable
build, as `Ranvier.Keyboard`'s rules require (`Ranvier.Keyboard/AGENTS.md`). Each is kept only if a benchmark
shows a gain.

### 4.7 What the API looks like

A sketch, not compiled:

```fsharp
open Ranvier
open Ranvier.Plume
open Ranvier.Plume.View

// A state field: the number of edits this view has seen.
let editCount =
    StateField.define
        { Create = fun _ -> 0
          Update = fun count tr -> if tr.DocChanged then count + 1 else count }

// One per file: the log, the saved text and the current text.
let document = Document.create (Text.ofString "let answer = 42\n")

// One per view of the file.
let editor =
    Editor.create document
        [ Keymap.extension Keymap.standard
          Keymap.extension Keymap.history
          LineNumbers.extension LineNumbers.defaults
          EditorState.tabSize.Of 4
          StateField.extension editCount ]

let edits = editor.Field editCount            // Memo<int>

Api.createEffect (fun () ->                   // wakes on a selection change, not on a scroll
    printfn "caret at %d" editor.Selection.Value.Main.Head)

editor.Dispatch
    [ Tx.change { From = 4; To = 10; Insert = Text.ofString "result" }
      Tx.userEvent "input.type"
      Tx.scrollIntoView ]

// A second view of the same file: the same text, its own selection.
let other = Editor.create document [ LineNumbers.extension LineNumbers.defaults ]
```

Records, unions and module functions throughout; no interface hierarchies. This matches Atelier's design rule
(`atelier/docs/superpowers/specs/2026-11-16-atelier-base-architecture-design.md`, section 1).

---

## 5. Feature and API inventory

**Today:** *have* (in Atelier, extract), *partial* (generalise), *new*.
**Phase:** P1 foundation, P2 editor essentials, P3 IDE features, P4 advanced. Section 8 defines P1 exactly.

The CodeMirror column is **general knowledge** of the CodeMirror 6 reference manual, not re-read for this
document. It is there to show what a mature editor offers, not as a contract.

### 5.1 Text

| Feature | CodeMirror 6 | Today | API | Phase |
|---|---|---|---|---|
| Immutable rope with structure sharing | `Text` | have | `Text.ofString`, `ofLines`, `ofUtf8`, `empty` | P1 |
| Length, line count | `length`, `lines` | have | `Text.length`, `Text.lines` | P1 |
| Line by number or by offset | `line(n)`, `lineAt(pos)` | have (number only) | `Text.line`, `Text.lineAt` returning `Line { Number; From; To; Text }` | P1 |
| Slice, replace, append | `slice`, `replace`, `append` | have (through edits) | `Text.slice`, `sliceString`, `replace`, `append` | P1 |
| Iteration | `iter`, `iterRange`, `iterLines` | new | `Text.iterLines`, `Text.iterRange` | P1 |
| Offset and line/character addressing | offsets only | have (line/character only) | offsets are UTF-16 code units; `Text.offsetOf`, `Text.positionOf` convert | P1 |
| Reference-stable line strings | no | have | kept; row change detection depends on it | P1 |
| Line endings kept per line | normalised | have | kept; `lineSeparator` facet for inserted text | P1 |
| UTF-8 leaves, lazy UTF-16 strings | no | have | kept on .NET | P1 |
| Load from bytes | no | have (scalar scan) | `Text.ofUtf8` with a vectorised line scan; file reading stays with the host | P1 |
| Fable build of the rope | n/a | new | a line-array leaf under `FABLE_COMPILER` (the .NET leaf uses `Encoding`, `WeakReference`, `ArrayPool`, `lock`) | P3 |

`DocId` and `Version` leave the text type. Identity is the host's concern; the sequence number belongs to the log.

### 5.2 Changes

| Feature | CodeMirror 6 | Today | API | Phase |
|---|---|---|---|---|
| A change | `ChangeSpec` | have (`TextEdit`, line/character ranges) | `ChangeSpec { From; To; Insert }` | P1 |
| A set of changes over one document | `ChangeSet` | partial (a sorted `TextEdit list`) | `ChangeSet.ofSpecs`, `apply`, `length`, `newLength`, `isEmpty`; flat section lengths | P1 |
| Removed text kept in the set | no (`invert` needs the old document) | partial (`applyEdits` returns inverse edits) | each range carries the rope slice it removed | P1 |
| Inversion | `invert(doc)` | have | `ChangeSet.invert`, needing no document | P1 |
| Composition | `compose` | new | `ChangeSet.compose` | P1 |
| Position mapping | `mapPos(pos, assoc, mode)` | new | `ChangeSet.mapPos` with `Assoc` and `MapMode` | P1 |
| Rebase one set over another | `map(other)` | new | `ChangeSet.map` | P1 |
| Description without text | `ChangeDesc` | new | `ChangeDesc` | P1 |
| Iterate changes and changed ranges | `iterChanges`, `iterChangedRanges` | new | same names | P1 |
| One-pass application of many ranges | internal | no (one split and rejoin per edit) | `ChangeSet.apply` | P1 |
| LSP interop | no | have | `ChangeSet.ofTextEdits`, `toTextEdits` | P1 |

### 5.3 Selection

| Feature | CodeMirror 6 | Today | API | Phase |
|---|---|---|---|---|
| A range with anchor, head, goal column | `SelectionRange` | partial (caret, plus a Visual-mode anchor) | `SelectionRange { Anchor; Head; GoalColumn; Assoc }` | P1 |
| Several ranges with a main one | `EditorSelection` | new | `EditorSelection.single`, `cursor`, `range`, `create` | P1 |
| Normalisation and mapping through changes | `map`, merge of overlaps | new | `EditorSelection.map`, `normalize` | P1 |
| Opt-in to several ranges | `allowMultipleSelections` | new | facet of the same name | P1 |
| Rectangular selection | `rectangularSelection` | partial (Vim BlockWise) | a set of ranges; pointer gesture | P3 |

### 5.4 Log, document, state

| Feature | CodeMirror 6 | Today | API | Phase |
|---|---|---|---|---|
| The log | no | no | internal to `Document`; `document.Head` | P1 |
| Document | no | partial (`DocumentBuffer`) | `Document.create`, `document.Text`, `document.Saved`, `document.IsDirty`, `document.MarkSaved` | P1 |
| Transaction as data | `Transaction` (holds states) | partial (`TextEdit list` plus `EditOrigin`) | `Transaction`; built by `Tx.change`, `Tx.selection`, `Tx.effect`, `Tx.annotate`, `Tx.userEvent`, `Tx.scrollIntoView`, `Tx.group` | P1 |
| A transaction written against an older head | `collab` | partial (`baseVersion` on the wire) | mapped on append | P1 |
| Editor | `EditorView` minus the DOM | no | `Editor.create document extensions`, `editor.Dispatch` | P1 |
| Several editors on one document | by forwarding transactions | partial (snapshot exchange) | the ordinary case | P1 |
| Update reader | `ViewUpdate` | no | `editor.Updates ()` | P1 |
| Immutable state | `EditorState` | new | `EditorState.create`, `EditorState.apply` | P1 |
| Typed annotations | `Annotation` | partial (`EditOrigin`) | `Annotation.define`; built in: `time`, `userEvent`, `addToHistory`, `group` | P1 |
| Typed effects, mappable | `StateEffect` | new | `StateEffect.define { Map }` | P1 |
| State fields | `StateField` | new | `StateField.define { Create; Update }`, optional `Compare` and `Provide` | P1 |
| Facets | `Facet` | new | `Facet.define { Combine }`, `.Of`, `Facet.compute`, `Facet.from` | P1 |
| Precedence | `Prec` | new | `Prec.highest` to `Prec.lowest` | P1 |
| Compartments | `Compartment` | new | `Compartment.create`, `.Of`, `.Reconfigure` | P1 |
| Reconfigure, append configuration | `StateEffect.reconfigure`, `appendConfig` | new | same names | P1 |
| Change and transaction filters, extenders | `changeFilter`, `transactionFilter`, `transactionExtender` | new | facets of the same names | P2 |
| Read-only | `EditorState.readOnly` | new | facet | P1 |
| Tab size, indent unit, line separator | facets | partial (`ScriptConfig.TabSize`) | facets | P1 |
| Language data by position | `languageDataAt` | new | facet | P2 |
| Phrases for translation | `EditorState.phrases` | new | facet | P3 |
| The log on disk | `toJSON`, `fromJSON` | partial (BAREWire journal codec) | a `Ranvier.Journal` codec in a separate package | P3 |

### 5.5 Range sets and decorations

| Feature | CodeMirror 6 | Today | API | Phase |
|---|---|---|---|---|
| Sorted, immutable, mappable ranges | `RangeSet` | new | `RangeSet.ofRanges`, `update`, `map`, `between`, `iter`, `join`, `compare`; positions in flat arrays | P1 |
| Mark decoration (class, attributes) | `Decoration.mark` | new | `Decoration.mark` | P2 |
| Line decoration | `Decoration.line` | partial (the `Current` row flag) | `Decoration.line` | P2 |
| Widget decoration | `Decoration.widget` | partial (the inline hint string) | `Decoration.widget` with a `Ranvier.View` template | P2 |
| Replace decoration | `Decoration.replace` | new | `Decoration.replace` | P3 |
| Block widgets | `block: true` | new | needs variable line heights | P4 |
| Decoration sources | `EditorView.decorations` | new | `decorations` facet, from state or from the view | P2 |
| Underlines without touching row markup | no | planned in Atelier's design | marks rendered through the CSS highlight registry (`Ranvier.View.WebCore/Highlights.fs`) | P3 |

### 5.6 History

| Feature | CodeMirror 6 | Today | API | Phase |
|---|---|---|---|---|
| Undo and redo | `undo`, `redo` | have | `History.undo`, `History.redo`; each appends a transaction | P1 |
| What history holds | inverted change sets | before and after snapshots | positions in the log, and grouping | P1 |
| Depth | `undoDepth`, `redoDepth` | new | same names; the depth bounds the log | P1 |
| Grouping by time and event | `newGroupDelay`, `joinToEvent` | new | `History.defaults` fields | P1 |
| Explicit groups | `isolateHistory` | have (`beginUndoGroup`, `endUndoGroup`) | `History.isolate`, plus begin and end group for modal editors | P1 |
| Edits history does not record | `addToHistory: false` | partial (`recordSnapshot`) | undoable items are mapped over them | P1 |
| A group across files | no | no | `Tx.group`: one item in each file | P1 |
| Undoing a group in every file | no | no | a coordinator in the host | P3 |
| Saved position | no | have (`SavedVersion`) | `document.MarkSaved`; `IsDirty` compares positions | P1 |
| Selection undo | `undoSelection`, `redoSelection` | new | same names | P2 |
| Effects that undo | `invertedEffects` | new | facet | P2 |

### 5.7 Commands and keymaps

A command is `Editor -> bool`. A state command takes the state and a dispatch function, so it runs without a view.

| Group | Commands | Today | Phase |
|---|---|---|---|
| Caret | by grapheme, word, line boundary, line, page, document start and end | partial (`MoveCaret`, `MoveBy`, page size) | P1 |
| Selection | the extending form of each caret command; select all, select line | new | P1 |
| Editing | insert text, newline, delete backward and forward | have | P1 |
| Editing | delete word, delete to line start and end, delete line, move and copy line, transpose | new | P2 |
| Indentation | newline and indent, indent more and less, insert tab | new | P2 |
| Comments | toggle line and block comment | new | P2 |
| Multiple carets | add caret above and below, split selection by line, simplify selection | new | P3 |
| Brackets | move to and select the matching bracket | new | P3 |

Keymaps: a `keymap` facet of `KeyBinding { Keys: KeyChord list; Run; Shift; Scope }`, with multi-stroke
sequences. `Keymap.standard`, `Keymap.defaults`, `Keymap.history`. Key types come from `Ranvier.Keyboard`; the
grapheme rules come from its UAX #29 implementation.

### 5.8 View

| Feature | CodeMirror 6 | Today | API | Phase |
|---|---|---|---|---|
| The editor element | `new EditorView` | have | `EditorView.component`, a `Ranvier.View` component | P1 |
| Virtualised rows, recycling, anchor rebase | viewport and gaps | have (hand-written sync) | a core lookup keyed by slot; pool size is a facet, not a cap of 128 | P1 |
| Visible range | `view.viewport`, `visibleRanges` | have (computed in the frame loop) | `view.Viewport: Memo` | P1 |
| Focus | `hasFocus`, `focus()` | have | `view.HasFocus: Memo<bool>`, `view.Focus` | P1 |
| View plugins | `ViewPlugin` | new | `ViewPlugin.define (fun view -> ...)`, run under an `Owner` | P1 |
| Gutters with text and class markers | `gutter()` | partial (one hardwired badge) | `Gutter.define { Class; LineMarker }` | P1 |
| Gutter markers that are templates | `GutterMarker.toDOM` | new | a `Ranvier.View` template per marker | P2 |
| Layers of positioned rectangles | `layer()` | partial (hardwired overlays) | `Layer.define { Above; Markers }` | P1 |
| Line numbers | `lineNumbers()` | have, including relative numbers | `LineNumbers.extension { Format }`, a gutter | P1 |
| Active line and active gutter line | `highlightActiveLine` | have | a layer, and a gutter class | P1 |
| Drawn selection | `drawSelection()` | partial (three bands, one range) | a layer: rectangles for any number of ranges | P1 |
| Caret with blink and shapes | cursor layer | have (block, bar, underline) | a layer: `Cursor.extension { Shape; BlinkRate }`; shape is a facet | P1 |
| Position from coordinates | `posAtCoords` | have (`Locate`) | `view.PosAtCoords` | P1 |
| Coordinates from position | `coordsAtPos` | partial (caret only) | `view.CoordsAtPos` | P1 |
| Scroll a position into view | `scrollIntoView` | have (`Reveal`) | effect and `Tx.scrollIntoView` | P1 |
| Scrollbar | native | have (custom track and thumb) | kept | P1 |
| Wheel, click, drag selection | yes | have | kept; handlers dispatch transactions | P1 |
| Double and triple click | yes | new | word and line selection | P2 |
| Keyboard through keymaps | `keymap` | partial (window-level, through Vim) | the view's focus target runs the `keymap` facet; a printable key with no binding inserts its text | P1 |
| Text input and IME | `contenteditable` | new | `ImeContext` on the native host; composition events in the browser | P2 |
| Clipboard | yes | new | copy, cut, paste, with input and output filters | P2 |
| Horizontal scroll | native | new | a second scroll axis on the sheet | P2 |
| Tabs and wide characters | browser layout | new | column measurement with a cache (prior art: `Ranvier.View/tests/Responsiveness/ViewportModel.fs`) | P2 |
| Long-line windowing | gaps | new | prior art: `Ranvier.View/tests/Responsiveness/HorizontalLine.fs` | P3 |
| DOM event handlers | `domEventHandlers` | new | facet | P2 |
| Read-only and non-editable | `editable`, `readOnly` | new | facets | P1 |
| Theme | `EditorView.theme`, `baseTheme` | have (fixed utilities) | `Theme.define` over `Ranvier.Css`; token colours by named tag | P1 |
| Placeholder, special characters, whitespace | extensions | new | extensions | P3 |
| Tooltips and hover | `showTooltip`, `hoverTooltip` | partial (app-level hover card) | facets | P3 |
| Panels | `showPanel` | new | facet | P3 |
| Drag and drop, drop cursor | yes | new | extension | P3 |
| Accessibility attributes and announcements | yes | new | `contentAttributes`, `view.Announce` | P3 |
| Line wrapping | `lineWrapping` | new | needs a height map | P4 |
| Variable line heights | height map | new | fixed height stays the fast path | P4 |
| Bidirectional text | yes | new | not planned in detail | P4 |

### 5.9 Language

| Feature | CodeMirror 6 | Today | API | Phase |
|---|---|---|---|---|
| Stateless line tokenizer | no | have | `Language.ofLineLexer` | P1 |
| Named highlight tags | `tags`, `HighlightStyle` | partial (`t1` to `t10`) | `Tag.keyword`, `Tag.typeName`, ..., `HighlightStyle.define` | P1 |
| Token cache keyed by line text | no | have | kept, per language | P1 |
| Tokenizer with state carried between lines | `StreamLanguage` | new | `Language.ofStream`; restarts from the edited line until the state converges | P2 |
| Indentation | `indentService`, `indentUnit` | new | facets | P2 |
| Comment tokens, bracket pairs, word characters | `languageData` | new | facet | P2 |
| Bracket matching | `bracketMatching` | new | extension | P3 |
| Folding | `foldService`, `foldGutter` | new | extension | P3 |
| Semantic tokens from a server | LSP client | new | async decorations, checked against the log position | P3 |
| Syntax tree | Lezer | new | a facet slot only; no parser ships | P4 |

### 5.10 Search

| Feature | CodeMirror 6 | Today | API | Phase |
|---|---|---|---|---|
| Text and regular-expression cursors | `SearchCursor`, `RegExpCursor` | partial (Vim `/`) | `SearchCursor`, `RegexCursor`; text search runs over the UTF-8 leaves | P2 |
| Find, replace, select matches | commands | new | commands | P2 |
| Highlight matches of the selection | `highlightSelectionMatches` | new | extension | P3 |
| Go to line | `gotoLine` | partial (Vim) | command | P2 |
| Search panel | `search()` | new | a panel | P3 |

### 5.11 IDE features

| Feature | CodeMirror 6 | Today | Phase |
|---|---|---|---|
| Completion with async sources | `autocompletion` | new | P3 |
| Snippets | `snippet` | new | P3 |
| Close brackets | `closeBrackets` | new | P3 |
| Diagnostics, lint gutter, next and previous | `linter`, `lintGutter` | partial (kernel packets, Vim `[d` `]d`) | P3 |
| Hover tooltips | `hoverTooltip` | partial (app-level) | P3 |

### 5.12 Later packages

Vim (`Ranvier.Plume.Vim`, P3), a merge and diff view (P4), an LSP client package (P4), a minimap (P4), a
terminal cell renderer for `Ranvier.Terminal` (P4, not committed).

### 5.13 Targets

- .NET `net10.0` and `net8.0`, reflection-free and NativeAOT-compatible, as `Ranvier.Keyboard` is.
- Fable: the core package ships its sources; the rope needs the alternative leaf (5.1). P3.

---

## 6. What changes in Atelier

| Atelier today | After |
|---|---|
| `Atelier.Text` (`TextSnapshot`, `DocumentBuffer`) | Replaced by the library's `Text` and `Document`. |
| `Atelier.Contracts` `Position`, `Range`, `TextEdit` | Stay as wire types; converted at the boundary. |
| `Editor.fs`, the `Viewport` record, `textView`, the viewport styles, `Lexer.fs` | Deleted; replaced by `EditorView.component` and a list of extensions. |
| Snapshot exchange between windows (`DocSyncListeners`) | Two editors on one document. |
| Gutter badges for diagnostics and proof status | An Atelier-side gutter extension. |
| Inline hints | Widget decorations. |
| The scroll tour, the recycle, layer and cache toggles | Atelier-side effects and compartments. |
| `Atelier.Input` (Vim) | Unchanged in P1; later re-based on transactions and published as `Ranvier.Plume.Vim`. |
| The per-frame poll of the caret line | An effect on `editor.Selection`. |

Atelier is outside this workspace (`C:\wk\atelier`), and its working tree has uncommitted changes in the files this
extraction would replace: 20 files, 587 insertions and 1,553 deletions, including `Editor.fs`, `App.fs` and
`VimEngine.fs` (**measured here**, `git diff --stat` on 2026-11-18). The library is therefore built and tested on
its own first, and Atelier is rewired after that work is committed.

---

## 7. Acceptance

### 7.1 Frame cost

Atelier's design records a scroll and edit frame of about 1.4 ms on a 1,000,000-line file (**from
documentation**, the design document named in 4.7, section 1; not re-measured).

**The baseline is `EditorShowcase`, not Atelier.** Atelier's README is byte-identical to
`EditorShowcase/README.md` (**measured here**, `diff -q`), so the published benchmark figures are
`EditorShowcase`'s. It is the app Atelier was seeded from, it is inside this workspace, and it is a lean app
with the same `--bench` harness. Atelier's own `--bench` times the whole shell frame (UI queue, tab-drag
polling, kernel dispatch, HUD), which a library sample does not have. Atelier is not built or run in phase 1.

Two differences are known in advance. `EditorShowcase` keeps its lines in a `ResizeArray<string>`
(`EditorShowcase/Sample.fs:7`) and the library uses the rope. `EditorShowcase` writes rows from a hand-written
loop and the library computes them in a core lookup.

**What is timed.** `--bench` runs nine rows: scroll 3 lines a frame, scroll 7 px a frame and type one character
a frame, each on the 1,000,000-line file under settings of recycling, markup cache and layer promotion
(`EditorShowcase/Harness.fs:254-263`). After 30 untimed frames it times 240 (**from documentation**,
`EditorShowcase/README.md`, "Benchmark") and reports mean, median and p95 of two stopwatches:

- **App**: around `State.Frame`, which is the sync in one batch, the flush and the `scrollTop` write.
- **Turn**: around `Window.Step` after it.

The edit is outside both: the workload's `editor.Insert` runs before the first stopwatch starts
(`Harness.fs:287-288`). The sample adds a third stopwatch:

- **Dispatch**: around the edit call. The same stopwatch is added to `EditorShowcase`'s harness around its
  existing `editor.Insert` call, which changes no behaviour.

**Passing.** The sample carries the same nine rows and the three stopwatches. Both are run back to back in one
session, three runs each, because the README notes that sessions differ.

- Scroll rows pass when the App and Turn means fall inside the range of `EditorShowcase`'s three runs, or below.
- Typing rows pass when App plus Turn plus Dispatch falls inside that range, or below. If it does not, the row
  still passes under 1.5 ms, the frame budget Atelier's design sets for typing (**from documentation**, the same
  design document, section 7.3), and the excess is reported with what it buys.

**Ruling, 2026-11-19.** The two apps put the page's layout in different stopwatches (`ACCEPTANCE.md`), so every
row is judged by its total: App plus Turn plus Dispatch.

### 7.2 History memory

On the 1,000,000-line document the sample dispatches 10,000 single-character insertions at scattered lines, with
history grouping off so each is its own item. Heap size is read after a forced full collection before and after.

It passes when the growth per insertion is under 512 bytes. That bound is the size of one leaf's line-offset
array (129 four-byte integers, **from source**, `TextSnapshot.fs:143`), so staying under it shows that no leaf
is retained. The measured figure is reported beside the 6 KB estimate of section 2.

### 7.3 Loading

Time to build the rope from the bytes of the 1,000,000-line file, with the scalar scan and with the vectorised
one, in one benchmark. The vectorised scan is kept only if it is faster.

None of these numbers exists yet; they are measured during the build.

---

## 8. Phase 1, exactly

A new repository folder, `C:\wk\ranvier-release\Ranvier.Plume`, with a local `git init`. No remote, no push, no
package publish.

1. **`Ranvier.Plume`**: `Text` (the extracted rope with the offset API and the vectorised load scan);
   `ChangeSet` with removed text, `compose`, `invert`, `mapPos`, `map` and one-pass application;
   `EditorSelection`; `Transaction` and the `Tx` builders; the log; `Document`; `EditorState` with `Facet`,
   `StateField`, `StateEffect`, `Annotation`, `Prec` and `Compartment`; `RangeSet`; history as a reader of the
   log, with grouping, the saved position and the group id; the P1 commands; keymaps; the line tokenizer and
   highlight tags; `Editor` with its memos and its update reader.
2. **`Ranvier.Plume.View`**: `EditorView.component` with the row pool as a core lookup, scrollbar, wheel and
   pointer selection, keymap dispatch, read-only mode and theme; the extension points `ViewPlugin.define`,
   `Gutter.define` and `Layer.define`; and, written on those points, line numbers, the active line, the drawn
   selection for several ranges, and the caret with its shapes and blink.
3. **Tests** (Expecto, as in `Ranvier.Keyboard`): the existing `TextSnapshotTests` ported; property tests for
   change composition, inversion, position mapping and rebasing; the fold under coalesced dispatch; two editors
   on one document converging with separate selections; a transaction written against an older head; history
   over unrecorded edits, with groups, and across the saved position; facet, field and compartment tests; a
   hidden-window smoke test of the view.
4. **A sample**: `EditorShowcase` rebuilt on the library, with the `--bench` mode and the three acceptance
   measurements of section 7.

The one change outside the new folder is the Dispatch stopwatch in `EditorShowcase/Harness.fs`.

**Settled first, before anything is built on them:**

- **Styles.** The stock `styles` builder of `Ranvier.Css.View` puts a class name on the element and keeps no
  rules (**from source**, the comment at `atelier/src/Atelier.Shell/LiveSheet.fs:33-34`); Atelier gets its rules
  by shadowing `styles` with a recording builder. I found no existing way for a library to hand its rules to a
  host. The proposal is that the view package exposes its `StyleDefinition` list and the host adds it to
  whichever sheet it uses.
- **Frame pacing.** Several wheel events can arrive in one frame, and the rows should be recomputed once. The
  candidate is a `Timed` value on a frame clock (`Ranvier/src/Ranvier/Timed.fs`); I have read its summary only.

If either needs a change to `Ranvier.Css` or the Ranvier core, it is reported to you before work continues.

Not in phase 1: the Atelier rewire, the Vim package, the Fable build, text input and IME, clipboard, decorations
inside the text, the cross-file undo coordinator, the journal on disk, CI and packaging.

---

## 9. Decisions

**Yours**

1. The name is `Ranvier.Plume`.
2. The log of edits is the source of truth. Two text values are kept per file, saved and current.
3. An edit across several files is one history item, and each file registers its own part.
4. Change reaches consumers through Ranvier, not through callbacks.
18. Frame cost is judged by the total time of a frame, decided on 2026-11-19 with the measurements in hand.

**Mine, for you to overrule**

5. **Rows are a core lookup, not a hand-written sync loop.** This follows `view-uses-core.md`. It replaces the
   design the published frame figures were measured on, so section 7.1 is the check.
6. **A document and an editor are separate things in phase 1**, so two views on one file is the ordinary case
   and not a later retrofit.
7. **History is per document**, shared by every editor on it. Undo in either window undoes the file's last
   edit.
8. **The cross-file group id is in phase 1; the coordinator that undoes a group in every file is phase 3.**
9. **Transactions are read through `editor.Updates ()`**, a memo whose previous value is the reader's cursor.
10. **Line numbers, active line, selection and caret are extensions in phase 1**, on public points.
11. **The benchmark baseline is `EditorShowcase`**, with a third stopwatch around the edit.
12. **Vim stays in Atelier for now.** Its 45 direct buffer mutations need the transaction API to exist first.
13. **Atelier is rewired after its current uncommitted work lands.**
14. **Offsets are UTF-16 code units**, matching .NET strings and LSP's default.
15. **No Lezer port.** Tokenizers in tiers; a syntax tree is a facet slot only.
16. **Expecto for tests**, matching `Ranvier.Keyboard`. Atelier uses xUnit.
17. **The undo depth default is 10,000 items**, set from the memory measurement of section 7.2: 279 bytes an item
    for a single-character edit (`ACCEPTANCE.md`).

## 10. Left to the builder

File layout inside each project; the internal structure of the log, of `RangeSet` and of the facet resolver;
how dropped transactions are composed; the exact record fields of option types; test names; and the order of
work inside phase 1.

---

## 11. As built in phase 1

Where the build differs from the sections above. The measurements are in `ACCEPTANCE.md`.

| Section | As built | Why |
|---|---|---|
| 4.2, 5.6 History | A history item holds the change set that undoes its edit (the text the edit removed), and the top item carries the changes the items below still have to be moved over. It is a fold over the log's transactions, eagerly advanced when a transaction is appended. | Positions alone would make the log keep every transaction an item refers to. |
| 4.2 Readers | An editor and an update reader register a cursor with the log, removed when their owner is disposed. The log keeps the transactions after the lowest cursor. `Document.Reader ()` gives a host the same reader. | Rule 4 of the log needs to know what every reader has consumed. A cursor carries no notification. |
| 4.2 Effects | A transaction's selection and effects apply in the editor that made it. A transaction from a source outside every editor (`Document.Dispatch`) delivers its effects to all editors. | Two editors on one document each have their own configuration and view. |
| 4.5 Rows | A row has two markup slots: one for the gutter markers and one for the line. | An edit to a line writes the line's slot and leaves the markers' elements in place. A text binding for the marker cost the engine about 1 ms a frame while scrolling (`ACCEPTANCE.md`). |
| 4.5 Styles | A class holds what is fixed on an element. What changes (a row's place, a rectangle of a layer, the scrollbar's thumb) is one text in the element's style attribute. The view uses no `Ranvier.Css` reader values. | An instance renders every binding when one value changes, and a reader value is lowered again on each render. The 7 px scroll frame is 0.39 ms against the baseline's 0.75 ms (`ACCEPTANCE.md`). |
| 4.5 Rows | The markup of a line without token spans ends with an empty comment. | Without it, rows of such lines cost the engine more to scroll (`ACCEPTANCE.md`); the cause is not established. |
| 5.1 Line strings | A line an edit left alone returns the same string instance while its leaf holds the strings it decoded: the 64 leaves decoded most recently in the process hold theirs, an older one until a garbage collection. Row change detection compares markup by content, not by instance. | A leaf holds its decoded strings weakly, so a text read from end to end does not keep a second copy of itself. The stronger wording of 5.1 failed about once in 40 test runs when other tests read texts beside it. |
| 4.5 Frames | The viewport is measured when it is mounted and by a timer twice a second, not in a frame. | A layout read after a transaction's DOM writes lays the page out twice. A resize observer is the phase 2 replacement. |
| 4.6, 5.2 | `ChangeSet.apply` replaces its ranges back to front through `Text.replace`. The single pass over touched leaves is not written. | 10,000 ranges took 49.4 ms as one set and 47.1 ms as single replacements (`ACCEPTANCE.md`); the pass gains only where many ranges fall in one leaf. |
| 4.7, 5.4 | A state field's update receives the transaction only. | A transaction holds no state. A value that needs the text is a computed facet or a memo over `editor.State`. |
| 5.4 Read-only | `Editor.Dispatch` drops a transaction that changes the document and carries a user event while `EditorState.readOnly` is true. | One place for the rule, in front of every command. |
| 5.6 | Undo and redo are `Editor.undo`, `Editor.redo` (also `Commands.undo`, `Commands.redo`). A selection set between two edits ends the item the first belongs to. | A module cannot be continued in a later file; the second matches what a user expects of undo after moving the cursor. |
| 5.8 Extension points | `Gutter.define` takes a `GutterSpec` (with a `Key` naming what its markers depend on beside the line), `Layer.define` a `LayerSpec`, and `ViewPlugin.define` returns a typed plugin whose value `ViewPlugin.tryGet` reads. | A lookup cell tracks nothing its function reads, so a gutter states its dependency; a layer and a plugin of one feature share state through the plugin's value. |
| 5.8 Mounting | `EditorView.create config` gives the component, the classes it applies and its theme's rules. A mount takes `{ Editor; Scheduler }`. | The component is defined before any window exists; the frame scheduler belongs to the host of each mount. |
| 4.2 Log | With no reader behind it, a transaction leaves the log as soon as it is appended. | The log holds what a reader has yet to read, and nothing else. |
| 4.5 Row window | A row window carries the edits of the windows before it, newest first, in place of one dirty range and a shifted flag. | The lookup compares the window it last saw with the newest, and may have skipped windows between them. |
| 5.1 Rope | Every leaf holds an offset for each of its lines. The shortcut for leaves of ASCII alone is not built. | Loading and editing met their criteria without it (`ACCEPTANCE.md`, 7.3). |
| 5.4 Untyped bases | `FacetBase`, `FieldBase`, `StateEffectType` and `ViewPluginBase` are public types with internal constructors. | A configuration holds facets and fields of every value type in one list; the typed `Facet<_, _>`, `StateField<_>`, `StateEffectType<_>` and `ViewPlugin<_>` are the ones a consumer makes. |
| 5.6 Undo of deleted text | An item whose text an unrecorded edit deleted is dropped, and undo then has nothing to do. | An undo that changes nothing would still take a key press and a history position. |
| 5.7 Key notation | `Keymap.bind` reads strokes as "Ctrl-z" and "Shift-ArrowLeft": modifiers and a W3C key name joined by "-". | `KeyChord.parse` of Ranvier.Keyboard reads Vim sequences such as "&lt;C-w&gt;v". A binding names its key as a keyboard event does. |
| 5.8 Theme | A theme has no `Dim` colour, and `Tokens` gives each tag a class name and CSS declarations as property and value pairs. | Nothing in the view draws in a dimmed text colour; `Faint` is the gutter's. A token's look is more than a colour. |
| 5.8 View configuration | `ViewConfig` is `{ Theme; Overscan; RebaseLines }`. The frame scheduler is a property of each mount. `EditorView` is a record of memos and functions; the line height is `Theme.LineHeight`, and `RowsWritten` is a function. | See "5.8 Mounting". A record of functions is what a layer and a plugin are handed. |
| 5.8 Features | `LineNumbers.extension` and `Cursor.extension` take `LineNumbersConfig` and `CursorConfig`. The cursor's shape is the facet `ViewFacets.cursorShape`. The gutter marker of the cursor's line takes the text colour through a variable of its row, not through a class. | Named records read at the call site. The row's element is written by its instance alone, so the active mark is part of the row's style text. |
| 5.8 Files | The view is `Styling.fs`, `Types.fs`, `Rows.fs`, `Features.fs` and `Component.fs`. | The geometry and the extension points are a few functions each, and sit with what uses them. |
| 5.9 Token cache | `TokenCache` is in the state package and neither the view nor the sample uses it: the view keeps a line's markup by its text. | Markup is what a row needs, and one cache by line text serves both lexing and escaping. |
| Tests | The properties ran as loops over seeded generators; five properties run by Expecto.FsCheck were added on 2026-11-19, over every kind of line break and characters outside ASCII. | The seeded loops give the same cases on every run. The plan's stack named Expecto.FsCheck, and an audit found it unused. |
| 5.8 Theme rules | Layers and gutter markers are selected by a `data-pm` attribute, tokens by class, all under an attribute of the view's own viewport. | The template is fixed; a class chosen at run time has no place in it. |
