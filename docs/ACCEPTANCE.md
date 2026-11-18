# Ranvier.Plume phase 1: acceptance measurements

**Date:** 2026-11-19
**Spec:** `DOSSIER.md`, section 7. **Plan:** `superpowers/plans/2026-11-18-plume-phase-1.md`.

Every figure here is **measured here** unless it says otherwise: one machine, Windows 11, .NET 10, Release
builds, hidden windows of 1280 x 800 CSS px with GPU compositing on (`Window.IsGpuComposited`). The text view was
713 CSS px high with 49 row elements in both apps.

## Summary

| Criterion | Result |
|---|---|
| 7.1 Frame cost, by the total time of a frame (your ruling of 2026-11-19) | Lower than the baseline in 6 of 9 rows, by 4% to 23%. Higher in 3: line scroll with the markup cache off (5%), typing (15%), typing with the cache off (20%) |
| 7.2 History memory | **Passes**: 279 bytes an insertion after `MarkSaved`, bound 512 |
| 7.3 Loading | **Passes**: the vectorised scan is 1.85 times faster and is kept |

## 7.1 Frame cost

`EditorShowcase --bench` (the baseline, with the Dispatch stopwatch added) and
`Ranvier.Plume/samples/EditorShowcase --bench`, alternating, five runs each, in one session, on the code as it is
after the review fixes below. Each row is 240 timed frames after 30 untimed ones on the 1,000,000-line file.
Values are means of a run in milliseconds; a cell gives the lowest and highest of the five runs. The raw logs
are in `bench/`.

| Workload | Recycling | Cache | Layer | App, baseline | App, Plume | Turn, baseline | Turn, Plume | Dispatch, Plume | Total, baseline | Total, Plume | Total, means |
|---|---|---|---|---|---|---|---|---|---|---|---|
| lines x3 | on | on | on | 0.75 to 0.81 | 0.50 to 0.53 | 0.82 to 0.91 | 0.91 to 0.99 | 0.01 to 0.01 | 1.58 to 1.72 | 1.42 to 1.53 | 1.47 against 1.62 |
| lines x3 | off | on | on | 3.69 to 4.28 | 1.72 to 2.12 | 3.50 to 4.37 | 3.80 to 4.37 | 0.01 to 0.02 | 7.19 to 8.65 | 5.53 to 6.51 | 5.90 against 7.64 |
| lines x3 | on | off | on | 0.59 to 0.66 | 0.41 to 0.51 | 0.80 to 0.93 | 0.96 to 1.22 | 0.00 to 0.01 | 1.39 to 1.59 | 1.38 to 1.73 | 1.56 against 1.48 |
| lines x3 | off | off | on | 4.40 to 4.89 | 1.88 to 2.45 | 4.18 to 5.01 | 4.67 to 6.69 | 0.01 to 0.01 | 8.58 to 9.90 | 6.63 to 9.15 | 7.49 against 9.32 |
| lines x3 | on | on | off | 0.57 to 0.85 | 0.35 to 0.44 | 0.79 to 1.46 | 1.00 to 1.31 | 0.00 to 0.01 | 1.36 to 2.31 | 1.35 to 1.76 | 1.50 against 1.63 |
| 7 px | on | on | on | 0.33 to 0.38 | 0.26 to 0.35 | 0.50 to 0.60 | 0.43 to 0.61 | 0.00 to 0.00 | 0.83 to 0.98 | 0.69 to 0.96 | 0.84 against 0.88 |
| 7 px | on | on | off | 0.26 to 0.31 | 0.22 to 0.28 | 0.47 to 0.55 | 0.42 to 0.58 | 0.00 to 0.00 | 0.72 to 0.86 | 0.64 to 0.86 | 0.74 against 0.77 |
| typing | on | on | on | 0.13 to 0.15 | 0.00 to 0.00 | 1.02 to 1.17 | 1.11 to 1.32 | 0.20 to 0.27 | 1.16 to 1.32 | 1.31 to 1.59 | 1.44 against 1.25 |
| typing | on | off | on | 0.14 to 0.15 | 0.00 to 0.00 | 1.11 to 1.18 | 1.15 to 1.36 | 0.26 to 0.27 | 1.25 to 1.33 | 1.41 to 1.62 | 1.55 against 1.29 |

The baseline's Dispatch is 0.000 to 0.002 ms in every row: its workload call changes plain fields.

**By the total time of a frame**, the measure you ruled on:

- **Scrolling is at or below the baseline** in six of seven rows: 4% to 9% lower with rows recycled, 20% to 23%
  lower when every row is written. The row with the markup cache off is 5% higher (1.56 against 1.48 ms), inside
  the spread of the runs.
- **Typing is above the baseline**: 1.44 against 1.25 ms (15%), and 1.55 against 1.29 ms with the cache off
  (20%). The first is under the 1.5 ms budget of section 7.1 at its mean, with two runs of five over it; the
  second is 0.05 ms over.

What the excess of a keystroke pays for: the baseline's edit replaces a string in a list; the library's builds a
transaction, appends it to the log, folds it into the text, the editor state and the history, and moves the
cursor, active-line and selection layers. Its dispatch takes 0.20 to 0.27 ms, of which about 0.01 ms is the state
layer (see below); the turn after it is 0.1 to 0.2 ms longer than the baseline's. A row is one markup slot, so a
keystroke also writes the line's gutter marker again; that is the price of the row shape that made scrolling
fast.

**An earlier session disagreed on one row.** Three runs each before the review fixes had typing with the cache
on at 1.18 against 1.32 ms, lower than the baseline. Rows computed a frame were the same in both sessions (1.0),
so the DOM work was the same; the library's typing time varies more between sessions than the baseline's. The
table above is the record.

**The stopwatches hold different work in the two apps**, which is why total time is the measure. The baseline
writes its rows and then writes `scrollTop`, which makes the engine lay the page out inside App (its harness says
so: "the layout that write forces"). In Plume the `scrollTop` effect runs before the row effects, so the layout
is done in the window's turn. Checked before the review fixes: with a layout read added at the end of the
sample's App stopwatch, two runs moved the first row to App 0.63 and 0.66 with Turn 0.91 and 0.91, and the second
to App 3.01 and 3.38 with Turn 3.01 and 3.25. That harness change was reverted.

### What the row lookup costs

Dossier decision 5 replaced the hand-written sync loop by a core `createLookup`. App time, which holds the
lookup, its `affected` function and the row effects, is below the baseline in every scroll row: 0.50 to 0.53 ms
against 0.75 to 0.81 ms with all settings on, and 1.72 to 2.12 ms against 3.69 to 4.28 ms when every row is
written. No core primitive had to change.

Rows computed a frame match the baseline: 3.2 for a 3-line scroll, 49.0 with recycling off, 0.3 for a 7 px
scroll, 1.0 for typing. The view tests assert the exact counts: 3 rows for a 3-line scroll, 1 row for a typed
character, the rows from the edited line to the pool's end for a new line.

### Two findings on the way

1. **A separate element for the gutter marker cost the engine about 1 ms a frame while scrolling.** With the line
   number in its own cell beside the text, the 3-line scroll took 2.2 to 2.6 ms in total; with a constant number
   in that cell, 1.5 ms; with the number and the text in one markup slot, 1.7 ms (single runs, before the
   review fixes). A row is therefore one markup slot. I did not find why the baseline, which has such a cell,
   does not pay this.
2. **A typed character costs about 9 microseconds in the state layer** (2,000 calls of `Commands.insertText` on
   the 1,000,000-line document with no view). Each of the cursor, active-line and selection layers adds 0.03 to
   0.07 ms to a keystroke (single runs).

## 7.2 History memory

`samples/EditorShowcase --memory`: 10,000 single-character insertions at lines `(i * 7919) mod 1,000,000`, each
its own history item, with an undo depth of 20,000. `GC.GetTotalMemory(true)` after a full collection. Measured
again after the review fixes.

| Measured | Growth | An insertion |
|---|---|---|
| After the insertions, the text at the start still the saved text | 48,493,592 bytes | 4,849 bytes |
| After `Document.MarkSaved` | 2,788,848 bytes | **279 bytes** |

The bound is 512 bytes, the size of one leaf's line-offset array. The second row passes it: history holds no
leaf. The dossier's estimate for the snapshot design was about 6 KB a step (not measured).

The first row is not history. The insertions touch nearly every leaf of the file, so the saved text and the
current text share almost nothing, and the saved text is the size of the file. That cost is bounded by the file,
not by the number of edits, and ends when the document is saved. Section 7.2 did not say which of the two it
meant; I measured both.

Two defects were found by this measurement and fixed, with a regression test (`Retention`): the `Document` kept
its start-up configuration, and the `Editor` kept its initial state. Each held the text the document started with
for the object's whole life. Before the fixes both rows read 4,849 bytes.

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

## Settled before the view was built

- **Styles hand-off: no change to Ranvier.Css.** `EditorView.create` returns the component, the
  `StyleDefinition` list of the classes it applies, and the rules of its theme. The sample adds them to its
  `LiveSheet`. Its harness checks that every Ranvier.Css class on the page has a rule in the adopted sheet: 34
  classes, 9 of them handed over by the view.
- **Frame pacing: no change to the Ranvier core.** Scroll requests write a target; an effect asks for one frame
  through `Ranvier.View.Dom.Frames.frame`; the frame copies the target to the value the rows read. A test with a
  manual scheduler shows one frame callback for two requests, and none at rest.

## Found in the packages this builds on

- **`Ranvier.View.Dom.Frames` over a WebCore host raises outside a graph.** `Frames.frame` and `Frames.timeout`
  call `Api.runWithOwner` in the host's callback, and `WebCoreHost.runDueTimers` runs that callback with no
  ambient graph: "No ambient graph on this thread" (Ranvier.View.Dom 0.1.50 with Ranvier.View.WebCore 0.1.2). The
  view wraps the scheduler it is given so each callback enters the view's graph (`Component.fs`, `onGraph`, marked
  `//FOR-REVIEW`). A fix in Ranvier.View would remove the wrapper.
- **A hidden window delivers no animation frames.** The frame-pacing path is therefore tested with a manual
  scheduler, and has not run under a display.

## Not verified

- **No window was shown.** The interactive sample, pointer selection, the scroll tour, the caret blink and frame
  delivery by a display were not run. Keyboard input, scrolling, selection and layers were exercised through
  synthetic events and scripted frames in hidden windows.
- The hot reload path of the sample (`dotnet run -- dev`) is carried over from the baseline and was not run.
- Both libraries build for `net10.0` and `net8.0`. The tests ran on `net10.0` only.
- The traced build (`-p:RanvierTrace=true`) is wired and was not built.
- No Fable build, packaging or CI: not in phase 1.
- Atelier was not read beyond `Atelier.Text` and its tests, and was not built or run.

## Gate

`dotnet fsi --exec build.fsx -- ci`: builds the solution in Release, then runs every test project unfiltered.
The state package has 111 tests and the view has 19.
