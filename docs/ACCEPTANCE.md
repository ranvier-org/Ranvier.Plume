# Ranvier.Plume phase 1: acceptance measurements

**Date:** 2026-11-19
**Spec:** `DOSSIER.md`, section 7. **Plan:** `superpowers/plans/2026-11-18-plume-phase-1.md`.

Every figure here is **measured here** unless it says otherwise: one machine, Windows 11, .NET 10, Release
builds, hidden windows of 1280 x 800 CSS px with GPU compositing on (`Window.IsGpuComposited`). The text view was
713 CSS px high with 49 row elements in both apps.

## Summary

| Criterion | Result |
|---|---|
| 7.1 Frame cost, by the total time of a frame (your ruling of 2026-11-19) | **Passes**: at or below the baseline in all nine rows. Scrolling is 11% to 53% lower. Typing is level: 1.05 against 1.09 ms, and 1.12 against 1.13 ms with the markup cache off |
| 7.2 History memory | **Passes**: 279 bytes an insertion after `MarkSaved`, bound 512 |
| 7.3 Loading | **Passes**: the vectorised scan is 1.85 times faster and is kept |

## 7.1 Frame cost

`EditorShowcase --bench` (the baseline, with the Dispatch stopwatch added) and
`Ranvier.Plume/samples/EditorShowcase --bench`, alternating, five runs each, in one session, on the code of
commit `7b2e860`. The commits after it change the pointer-down handler, tests, the sample's `--profile` mode,
where the sample's restore is written, the sample's `--tour` mode, the build script and comments, none of which
`--bench` runs. Each row is 240 timed frames after 30 untimed ones on the 1,000,000-line
file. Values are means of a
run in milliseconds; a cell gives the lowest and highest of the five runs. The last column compares the means of
the five runs, and counts the pairs in which Plume's run was below the baseline's run made just before it. The
raw logs are in `bench/`.

| Workload | Recycling | Cache | Layer | App, baseline | App, Plume | Turn, baseline | Turn, Plume | Dispatch, Plume | Total, baseline | Total, Plume | Total, means | Plume against baseline |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| lines x3 | on | on | on | 0.69 to 0.81 | 0.35 to 0.39 | 0.77 to 0.90 | 0.85 to 0.93 | 0.01 to 0.01 | 1.51 to 1.71 | 1.21 to 1.33 | 1.25 against 1.59 | -21%, lower in 5 of 5 pairs |
| lines x3 | off | on | on | 3.50 to 3.98 | 1.36 to 1.44 | 3.16 to 3.73 | 3.46 to 3.60 | 0.01 to 0.01 | 6.67 to 7.71 | 4.84 to 5.04 | 4.93 against 7.06 | -30%, lower in 5 of 5 pairs |
| lines x3 | on | off | on | 0.54 to 0.60 | 0.32 to 0.34 | 0.72 to 0.80 | 0.76 to 0.90 | 0.00 to 0.01 | 1.26 to 1.40 | 1.08 to 1.25 | 1.17 against 1.32 | -11%, lower in 5 of 5 pairs |
| lines x3 | off | off | on | 3.84 to 4.16 | 1.21 to 1.28 | 3.37 to 4.01 | 3.35 to 3.45 | 0.01 to 0.01 | 7.21 to 8.17 | 4.56 to 4.74 | 4.63 against 7.55 | -39%, lower in 5 of 5 pairs |
| lines x3 | on | on | off | 0.52 to 0.60 | 0.24 to 0.27 | 0.71 to 0.86 | 0.77 to 0.86 | 0.00 to 0.00 | 1.23 to 1.46 | 1.02 to 1.13 | 1.07 against 1.32 | -19%, lower in 5 of 5 pairs |
| 7 px | on | on | on | 0.29 to 0.32 | 0.14 to 0.14 | 0.42 to 0.47 | 0.25 to 0.26 | 0.00 to 0.00 | 0.70 to 0.78 | 0.38 to 0.40 | 0.39 against 0.75 | -48%, lower in 5 of 5 pairs |
| 7 px | on | on | off | 0.22 to 0.26 | 0.09 to 0.09 | 0.40 to 0.45 | 0.22 to 0.23 | 0.00 to 0.00 | 0.62 to 0.71 | 0.30 to 0.33 | 0.31 against 0.67 | -53%, lower in 5 of 5 pairs |
| typing | on | on | on | 0.12 to 0.15 | 0.00 to 0.00 | 0.93 to 1.00 | 0.85 to 0.94 | 0.16 to 0.17 | 1.06 to 1.14 | 1.02 to 1.10 | 1.05 against 1.09 | -4%, lower in 5 of 5 pairs |
| typing | on | off | on | 0.12 to 0.15 | 0.00 to 0.00 | 0.96 to 1.06 | 0.91 to 0.99 | 0.15 to 0.17 | 1.11 to 1.20 | 1.07 to 1.15 | 1.12 against 1.13 | -1%, lower in 3 of 5 pairs |

The baseline's Dispatch is 0.000 to 0.002 ms in every row: its workload call changes plain fields.

**By the total time of a frame**, the measure you ruled on, every row is at or below the baseline:

- **Scrolling** is 11% to 21% lower with rows recycled, 30% to 39% lower when every row is written, and about
  half for the 7 px scroll (0.39 against 0.75 ms).
- **Typing** is level with the baseline: 1.05 against 1.09 ms, lower in each of the five pairs, and 1.12 against
  1.13 ms with the markup cache off, lower in three pairs of five. Every run is under the 1.5 ms budget of
  section 7.1.

By the rule of section 7.1 (a row passes when its mean falls inside the range of the baseline's runs, or below),
all nine rows pass.

### The record before this one

The record this one replaces (commit `dd00fc9`, five pairs) had typing above the baseline: 1.44 against 1.25 ms,
and 1.55 against 1.29 ms with the cache off. Plan task 11, step 3 covers a row that fails: profile, and fix inside
the core-lookup design. The next section is that profile and the changes it led to. The lookup design is as it
was, and no core primitive had to change.

Sessions differ: the baseline's own typing row read 1.25 ms in that record and reads 1.09 ms in this one. A figure
of one record compares with the baseline of the same record only.

### What a keystroke costs, and what was changed

1. **A keystroke makes three writes to the page** (`--writes`: a mutation observer on the document's body, 20
   frames): the children of the line's slot, the style of the cursor's element, and the text of the sample's
   status line. The sample's statistics panel adds text writes when it publishes, four times a second (0.3 a
   frame in two runs of four). A 3-line scroll makes about ten: the style, the gutter slot and the line slot of each row that
   takes another line, and the style of the scrollbar's thumb.
2. **An edit wrote the line's gutter marker again.** A row was one markup slot holding the markers and the line.
   A row now has a slot for each, so an edit writes the line and leaves the markers' elements in place. Rows of
   each shape written straight to the page, with the turn after the write (a temporary mode of the sample, since
   removed; 5 rounds of 240 frames, three runs): one slot 0.43, 0.43 and 0.49 ms a frame, a slot for each 0.38,
   0.40 and 0.42 ms. With three rows written a frame the shapes were within 0.03 ms: 0.55, 0.55 and 0.56 ms
   against 0.57, 0.58 and 0.56 ms.
3. **Reader values were lowered again on every render.** The view had the place of a row, the rectangle of a
   layer and the scrollbar's thumb as `Ranvier.Css` reader values. A template instance renders every binding when
   one value changes, and each reader value is turned from a typed value into text again: a CPU trace of typing
   (`dotnet-trace`, 2,150 keystrokes) had 17% of its managed samples in Ranvier.Css, in `NumberText.format`,
   `GrammarRuntime.lookup` and `Declaration.isCssWideWith` under `Style.variableDeclarations`. The view now
   writes what changes on an element as one text in its style attribute, and a class holds what is fixed. The
   engine has less to do as well: the Turn of the 7 px scroll is 0.25 to 0.26 ms against the baseline's 0.42 to
   0.47 ms, where the earlier record had 0.43 to 0.61 ms against 0.50 to 0.60 ms.
4. **A facet without inputs took a lock on every read, and the row configuration was built on every
   transaction** before it was compared with the one before. A facet now keeps its own default, and the
   configuration is compared first.
5. **Lines of text alone were slow to scroll.** With rows of two slots, `--profile` had scrolling without a
   language at 1.15 times the total of scrolling with one (1.68 against 1.46 ms). The markup of a line without
   spans now ends with an empty comment, and the same comparison reads 0.81 and 0.79 times in two runs (0.90
   against 1.12 ms, and 1.17 against 1.48 ms). Each ratio is within one run. Why the comment helps is not
   established. My guess is that the engine writes markup of text alone into the text node already in the
   element (**general knowledge** of WebKit, not checked on this build); the second reviewer's probe could not
   settle it.

`--profile` types and scrolls as `--bench` does, in rounds in one window, with the view's features switched off
one at a time. It found these costs; its absolute figures move between runs, so the comparison that counts is
the table above. With every feature on, typing's Dispatch was 0.17 and 0.22 ms in two runs before the changes and
0.12 to 0.16 ms in five runs after them; the App time of the 3-line scroll was 0.42 and 0.44 ms, and 0.32 to
0.41 ms after. The three layers together add 0.03 to 0.04 ms to a keystroke's Dispatch (0.130 against 0.097 ms,
and 0.159 against 0.118 ms). All but the last of those runs were made while the mode typed, in its later rounds,
on lines that earlier rounds had lengthened (the second review's finding, corrected since).

**Where the rest of a typing frame goes.** The turn after a keystroke is about 0.9 ms in both apps. About half
of it is the repaint of what lies between the edited row and the status line: in the temporary mode, a new status
text alone cost 0.002 ms a frame, the row with the cursor 0.53 ms, and the three together 0.95 ms (one run). Both
samples write the status line on every keystroke, so both pay it.

**The state layer** takes about 9 microseconds of a keystroke (2,000 calls of `Commands.insertText` on the
1,000,000-line document with no view).

**The stopwatches hold different work in the two apps**, which is why total time is the measure. The baseline
writes its rows and then writes `scrollTop`, which makes the engine lay the page out inside App (its harness says
so: "the layout that write forces"). In Plume the `scrollTop` effect runs before the row effects, so the layout
is done in the window's turn. Checked before the review fixes: with a layout read added at the end of the
sample's App stopwatch, two runs moved the first row to App 0.63 and 0.66 with Turn 0.91 and 0.91, and the second
to App 3.01 and 3.38 with Turn 3.01 and 3.25. That harness change was reverted.

### What the row lookup costs

Dossier decision 5 replaced the hand-written sync loop by a core `createLookup`. App time, which holds the
lookup, its `affected` function and the row effects, is below the baseline in every scroll row: 0.35 to 0.39 ms
against 0.69 to 0.81 ms with all settings on, and 1.36 to 1.44 ms against 3.50 to 3.98 ms when every row is
written. No core primitive had to change.

Rows computed a frame match the baseline: 3.2 for a 3-line scroll, 49.0 with recycling off, 0.3 for a 7 px
scroll, 1.0 for typing. The view tests assert the exact counts: 3 rows for a 3-line scroll, 1 row for a typed
character, the rows from the edited line to the pool's end for a new line.

### A text binding in a row

With the line number in a cell of its own beside the text, written as text, the 3-line scroll took 2.2 to 2.6 ms
in total; with a constant number in that cell, 1.5 ms (single runs, before the review fixes). The gutter markers
are therefore markup. I did not find why the baseline, which writes its line number as text, does not pay this.

## 7.2 History memory

`samples/EditorShowcase --memory`: 10,000 single-character insertions at lines `(i * 7919) mod 1,000,000`, each
its own history item, with an undo depth of 20,000. `GC.GetTotalMemory(true)` after a full collection. Measured
at commit `7b2e860`.

| Measured | Growth | An insertion |
|---|---|---|
| After the insertions, the text at the start still the saved text | 48,496,664 bytes | 4,850 bytes |
| After `Document.MarkSaved` | 2,791,920 bytes | **279 bytes** |

The bound is 512 bytes, the size of one leaf's line-offset array. The second row passes it: history holds no
leaf. The dossier's estimate for the snapshot design was about 6 KB a step (not measured).

The first row is not history. The insertions touch nearly every leaf of the file, so the saved text and the
current text share almost nothing, and the saved text is the size of the file. That cost is bounded by the file,
not by the number of edits, and ends when the document is saved. Section 7.2 did not say which of the two it
meant; I measured both.

Two defects were found by this measurement and fixed, with a regression test (`Retention`): the `Document` kept
its start-up configuration, and the `Editor` kept its initial state. Each held the text the document started with
for the object's whole life. Before the fixes both rows read about 4,850 bytes.

**Undo depth (decision 17).** `History.defaults.Depth` is 10,000 items: at 279 bytes an item for a
single-character edit, about 2.8 MB. An item that deletes text holds the removed text as a slice sharing the
leaves of the document it was removed from, so a large deletion costs what it deleted.

## 7.3 Loading

`samples/EditorShowcase --load`: `Text.ofUtf8` over the 36,549,886 UTF-8 bytes of the 1,000,000-line file, 10 runs
after 3 untimed ones. Measured before the review fixes, which did not touch the loading path.

| Scan | Median | Best |
|---|---|---|
| Scalar (`Text.ofUtf8Scalar`) | 50.0 ms | 27.7 ms |
| Vectorised (`Text.ofUtf8`) | 27.0 ms | 14.8 ms |

The vectorised scan is 1.85 times faster at the median and is kept. It compares 32 bytes at a time with
`Vector256` (16 with `Vector128`), behind `#if !FABLE_COMPILER`, with a scalar tail, and a test checks that both
scans give the same text.

### Applying many ranges

`samples/EditorShowcase --apply`: 10,000 one-character replacements, one every 100 lines. Median of 7 runs, after
the review fixes.

| How | Time |
|---|---|
| `ChangeSet.apply` of one set of 10,000 ranges | 49.4 ms |
| 10,000 calls of `Text.replace` | 47.1 ms |

Both are about 5 microseconds a range, so neither rebuilds the tree per range. `ChangeSet.apply` applies its
ranges back to front through `Text.replace`; the single pass that rebuilds each touched leaf once (dossier 4.6)
is not written, because these ranges each fall in a leaf of their own and it would gain nothing here. It would
gain where many ranges fall in one leaf, which this benchmark does not measure.

## Independent review

A reviewer with no part in the build read the code and confirmed ten defects, each with a reproduction. All ten
are fixed, and each has a regression test that fails on the code as it was and passes now.

| Defect | Fix |
|---|---|
| A history item whose edits cancel (type a character, delete it) was never taken off the stack: undo returned true and did nothing | The item is dropped when its edits cancel, and an undo or redo takes its item whether or not it changes the document |
| A row stayed stale when the view was read between an edit and a selection in one batch | A row window carries the edits of the windows before it, so the lookup finds them whichever window it last saw |
| A line height above 22.8 px put rows above the sheet's top | The anchor moves before the sheet's lead or the room after it is used up, for any line height |
| Reconfiguring any compartment recreated every view plugin and layer and recomputed every row | A facet whose constant inputs are unchanged keeps its value across a reconfiguration |
| A field whose creation read a field later in the configuration raised | A field is created on demand; two fields that read each other raise a named error |
| Lines added one edit at a time left one-line leaves: 200,000 lines held 64.0 MB (the reviewer's figure) | Two small leaves that meet are merged: the same lines hold 4.67 MB, against 4.63 MB parsed |
| An editor folding an edit and a selection in one batch built a third copy of the text | The last transaction that changes the document yields the document's own text |
| After the document shrank below the scroll position, the first scroll request did nothing | A request starts from where the view is |
| An undo depth of 0 gave a depth of -1 after an undo | A depth below 1 is 1 |
| A field update that threw during an editor's first fold left the editor unusable | The fold's starting state is released once a fold has succeeded |

Of four further items the reviewer could not reproduce, two are fixed: a transaction now computes the next text
and the next history before replacing either, and a view's unmount leaves a second view's registration alone.
Two are accepted: an update reader and a state each keep the state before the last transaction, so the text
before the last edit lives until the next one.

The reviewer found sound: the rope against a fresh parse across "\r", "\n" and "\r\n" (60,000 small and 300
large edit sequences), change-set composition, mapping and inversion with removed text (150,000 seeds), the
history stack past its depth and across unrecorded edits (220,000 runs), a transaction rebased over a later
deletion, and the row window for consecutive scrolls and edits (18 seeds of 250 steps). Those figures are the
reviewer's.

### Second review, of commit `7b2e860`

A second reviewer with no part in the work read the commit that made typing level with the baseline. It found
no defect in the library's behaviour, and two of low severity elsewhere, both fixed:

| Finding | Fix |
|---|---|
| The scrollbar test put the thumb only where its centre and its top give the same place, so it passed with the centring removed | The test also puts the thumb a quarter of the way down; with the centring removed it now fails (4810 against 4610) |
| `--profile` typed, in its later rounds, on lines that earlier rounds had lengthened, against its own comment | Each typing run has 40 lines of the file to itself |

One thing it could not run is changed as well: a press that failed to capture the pointer left the view
extending the selection on later moves. The press now selects and no drag follows. It also could not confirm why
the empty comment of 7.1, point 5 helps; that sentence now says so.

Its checks, by its own figures: a probe comparing every row, marker and rectangle with the document after each
turn found no failure over 6 seeds of 5 document sizes and 220 turns, six line heights that are not whole
numbers, and two editors or two views on one document; the text view of commit `7b2e860` is pixel-identical to
its parent's in `--shot` for the three sample files; and 4,000 rounds of 8 racing threads read one instance of a
facet's default.

## Settled before the view was built

- **Styles hand-off: no change to Ranvier.Css.** `EditorView.create` returns the component, the
  `StyleDefinition` list of the classes it applies, and the rules of its theme. The sample adds them to its
  `LiveSheet`. Its harness checks that every Ranvier.Css class on the page has a rule in the adopted sheet: 32
  classes, 9 of them handed over by the view. The view's classes hold what is fixed; what changes on an element
  is in its style attribute (7.1, point 3).
- **Frame pacing: no change to the Ranvier core.** Scroll requests write a target; an effect asks for one frame
  through `Ranvier.View.Dom.Frames.frame`; the frame copies the target to the value the rows read. A test with a
  manual scheduler shows one frame callback for two requests, and none at rest.

## Found in the packages this builds on

- **`Ranvier.View.Dom.Frames` over a WebCore host raises outside a graph.** `Frames.frame` and `Frames.timeout`
  call `Api.runWithOwner` in the host's callback, and `WebCoreHost.runDueTimers` runs that callback with no
  ambient graph: "No ambient graph on this thread" (Ranvier.View.Dom 0.1.50 with Ranvier.View.WebCore 0.1.2). The
  view wraps the scheduler it is given so each callback enters the view's graph (`Component.fs`, `onGraph`, marked
  `//FOR-REVIEW`). A fix in Ranvier.View would remove the wrapper.
- **A hidden window's own loop delivers no animation frames.** The tests and `--tour` give the host a frame
  whenever the page asks for one, as a shown window does at a refresh. A manual scheduler is used as well, where
  a test counts frame callbacks.
- **A `Ranvier.Css` reader value is lowered again on every render of its element's instance** (7.1, point 3;
  Ranvier.Css 0.1.1). `CssView.partsWithInline` (`Ranvier.Css/src/Ranvier.Css.View/Bindings.fs`) calls
  `Style.variableDeclarations` on each read of the style attribute, and that function
  (`Ranvier.Css/src/Ranvier.Css/Styles.fs`, line 229) runs every reader: each run builds the typed declaration,
  checks it for CSS-wide keywords and makes the variable's declaration, and nothing is kept between reads
  (**from source**, the workspace's checkout at version 0.1.1). The baseline uses reader values throughout. A
  cache of the lowered text in Ranvier.Css would let the view use them again; the place is marked
  `//FOR-REVIEW` in `Component.fs`.
- **Pointer and wheel events come from the host's input calls, not from event construction.**
  `Ranvier.Dom.EventConstruction` has `Event`, `Custom`, `UI`, `Mouse`, `Keyboard` and `Focus` (**from source**,
  `Ranvier/src/Ranvier.Dom/EventConstruction.fs`), so a test cannot construct a pointer or a wheel event. The
  host takes the input itself: `WebCoreHost.Mouse`, `Wheel`, `Click`, `Type` and `SetFocused` are what a window
  calls for the mouse, the wheel, a key and its own focus. The view tests drive its listeners with them.
- **A package referenced in one configuration only makes `rfs check` report a false error.** The sample
  references the hot reload host in Debug only. A Release build then wrote a restore without it into the folder
  `rfs` reads, and `rfs` reported `'Hmr' is not defined` until a Debug restore and `rfs status --reload`. The
  sample now has a restore folder for each configuration (`samples/Directory.Build.props`), and `rfs check`
  reports 0 errors straight after the Release gate, with a reload and without. The workspace's `EditorShowcase`
  and `AstroShowcase` reference the host the same way (**from source**, their project files); they are outside
  this repository and unchanged.
- **Rows whose line is text alone cost the engine more to scroll** (7.1, point 5). The view ends such markup
  with an empty comment. The cause is not established.

## What the plan's checks ran in, and what is outside them

**Every check of the plan ran, in the windows the plan names.** Its global constraints say "Bench, capture and
smoke windows are created hidden"; task 8 names a hidden window for its spike and its smoke test, and task 9 has
`--shot` and has `--bench`, `--memory` and `--load` "run to completion hidden". No step of the plan shows a
window, and none was shown.

**From the host inward, the paths a shown window uses ran too**, hidden:

- **Input.** The view tests send the mouse, the wheel and keys through `WebCoreHost.Mouse`, `Wheel`, `Click` and
  `Type`, which are what a window calls. A press, a drag that leaves the viewport, a release and a press with
  Shift select through the pointer listeners; a press on the text gives the view the keyboard once the page has
  its window's focus (`SetFocused`); the wheel scrolls; a press and a drag on the scrollbar's track move the
  document and the thumb; a key typed after a click inserts at the click. With the three listeners removed, the
  four input tests fail.
- **Frames.** A test gives the host a frame whenever the page asks for one (`WebCoreHost.FrameRequested`, then
  `Frame`), which is what a shown window does at each refresh of its display. A scroll request is laid out by
  that frame, two wheel events before one frame lay the rows out once, and the view at rest asks for no frame.
- **Timers.** The cursor blinks on the host's timers, and a move of the cursor lights it at once.
- **The app.** `samples/EditorShowcase --tour` runs the app as its window runs it, tour on, on the same frames
  and input: each of 120 refreshes ran a frame the app asked for and the text moved 152 CSS px; a click on the
  tour's button stopped it, after which 1 of 60 refreshes ran a frame and the text stayed; the wheel, a click in
  the text and a typed key did what they do; F5 started the tour again; a click on a tab opened its file
  (**measured here**: four runs, 14 checks each, exit code 0; the figures are the first run's).

**Outside the plan, and not run:**

- What only a display or a desktop gives: the timing of a real refresh, and desktop events on their way into the
  host, which is the window package's code and not this repository's. Showing a window is left to you: the
  workspace's rule is that the app is launched when you ask for it.
- The hot reload path of the sample (`dotnet run -- dev`), which shows a window. It is carried over from the
  baseline.
- Tests on `net8.0`: both libraries build for `net10.0` and `net8.0`, and the test projects target `net10.0`.
- A traced build (`-p:RanvierTrace=true`): the plan asks only that tracing is off by default, which it is.
- A Fable build, packaging and CI: not in phase 1.
- Atelier was not read beyond `Atelier.Text` and its tests, and was not built or run.

## Gate

`dotnet fsi --exec build.fsx -- ci`: builds the solution in Release, then runs every test project unfiltered.
The state package has 112 tests and the view has 30.

`dotnet fsi --exec build.fsx -- check`: builds the solution in Debug, which `rfs` reads, then runs
`rfs check --fail-on-error`: 0 errors in 5 projects. Run straight after the Release gate, `rfs check` also
reports 0 errors.
