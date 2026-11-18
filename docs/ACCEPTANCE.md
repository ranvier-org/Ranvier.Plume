# Ranvier.Plume phase 1: acceptance measurements

**Date:** 2026-11-19
**Spec:** `DOSSIER.md`, section 7. **Plan:** `superpowers/plans/2026-11-18-plume-phase-1.md`.

Every figure here is **measured here** unless it says otherwise: one machine, Windows 11, .NET 10, Release
builds, hidden windows of 1280 x 800 CSS px with GPU compositing on (`Window.IsGpuComposited`). The text view was
713 CSS px high with 49 row elements in both apps.

## Summary

| Criterion | Result |
|---|---|
| 7.1 Frame cost, by total time a frame | Lower than the baseline in 7 of 9 rows; higher in the two rows with the markup cache off, by 6% and 17% |
| 7.1 Frame cost, by the letter (App and Turn each inside the baseline's range) | App below the baseline in all 7 scroll rows; Turn above its range in the 5 line-scroll rows. **Needs your ruling**, see below |
| 7.2 History memory | **Passes**: 279 bytes an insertion after `MarkSaved`, bound 512 |
| 7.3 Loading | **Passes**: the vectorised scan is 1.85 times faster and is kept |

## 7.1 Frame cost

`EditorShowcase --bench` (the baseline, with the Dispatch stopwatch added) and
`Ranvier.Plume/samples/EditorShowcase --bench`, alternating, three runs each, in one session. Each row is 240 timed
frames after 30 untimed ones on the 1,000,000-line file. Values are means of a run in milliseconds; a cell gives
the lowest and highest of the three runs. The raw logs are in `bench/`.

| Workload | Recycling | Cache | Layer | App, baseline | App, Plume | Turn, baseline | Turn, Plume | Dispatch, Plume | Total, baseline | Total, Plume | Total, means |
|---|---|---|---|---|---|---|---|---|---|---|---|
| lines x3 | on | on | on | 0.72 to 0.77 | 0.49 to 0.56 | 0.77 to 0.84 | 0.88 to 1.08 | 0.01 to 0.01 | 1.50 to 1.61 | 1.38 to 1.65 | 1.48 against 1.54 |
| lines x3 | off | on | on | 3.61 to 3.68 | 1.63 to 1.97 | 3.21 to 3.40 | 3.60 to 4.10 | 0.01 to 0.01 | 6.83 to 7.08 | 5.24 to 6.08 | 5.76 against 6.94 |
| lines x3 | on | off | on | 0.55 to 0.64 | 0.42 to 0.47 | 0.73 to 0.89 | 0.96 to 1.16 | 0.00 to 0.00 | 1.29 to 1.54 | 1.39 to 1.63 | 1.53 against 1.44 |
| lines x3 | off | off | on | 4.03 to 4.19 | 1.64 to 1.95 | 3.60 to 3.94 | 4.17 to 4.97 | 0.01 to 0.01 | 7.64 to 8.13 | 5.82 to 6.92 | 6.21 against 7.82 |
| lines x3 | on | on | off | 0.54 to 0.63 | 0.33 to 0.36 | 0.76 to 0.94 | 0.97 to 1.03 | 0.00 to 0.00 | 1.30 to 1.57 | 1.31 to 1.39 | 1.36 against 1.40 |
| 7 px | on | on | on | 0.29 to 0.38 | 0.26 to 0.28 | 0.45 to 0.60 | 0.41 to 0.46 | 0.00 to 0.00 | 0.74 to 0.98 | 0.67 to 0.74 | 0.70 against 0.85 |
| 7 px | on | on | off | 0.25 to 0.29 | 0.21 to 0.23 | 0.44 to 0.55 | 0.39 to 0.46 | 0.00 to 0.00 | 0.69 to 0.84 | 0.61 to 0.69 | 0.65 against 0.75 |
| typing | on | on | on | 0.14 to 0.19 | 0.00 to 0.00 | 1.10 to 1.27 | 0.94 to 1.08 | 0.18 to 0.22 | 1.24 to 1.47 | 1.12 to 1.30 | 1.18 against 1.32 |
| typing | on | off | on | 0.13 to 0.16 | 0.00 to 0.00 | 1.01 to 1.21 | 1.09 to 1.27 | 0.24 to 0.28 | 1.14 to 1.37 | 1.33 to 1.55 | 1.44 against 1.24 |

The baseline's Dispatch is 0.000 to 0.002 ms in every row: its workload call changes plain fields.

**What each stopwatch holds differs between the two apps, so the dossier's rule does not apply as written.**

- The baseline writes its rows and then writes `scrollTop`, which makes the engine lay the page out inside App (its
  own harness says so: "the scrollTop write: the row effects, their DOM writes and the layout that write forces").
- In Plume the `scrollTop` effect runs before the row effects, so the layout is done in the window's turn.
- Checked: with a layout read added at the end of the sample's App stopwatch, two runs gave, for the first row,
  App 0.63 and 0.66 with Turn 0.91 and 0.91, and for the second row App 3.01 and 3.38 with Turn 3.01 and 3.25. The
  second row is then inside the baseline's range for both; the first has App below it and Turn 0.07 ms above it.
  That harness change was reverted; the table above is the unmodified sample.

**By the letter of 7.1** (App and Turn means each inside the baseline's range, or below): both "7 px" rows pass.
The five "lines x3" rows have App below the baseline and Turn above its range, by up to 0.24 ms with recycling
on and up to 1.03 ms with it off. "typing" with the cache on passes. "typing" with the cache off has a mean of
1.44 ms, under the 1.5 ms budget, with one run of three at 1.55 ms.

**By the time a frame takes in total:** Plume is lower in rows 1, 2, 4, 5, 6, 7 and 8, by 3% to 21%. It is
higher in row 3 (1.53 against 1.44 ms, inside the spread of the baseline's own three runs) and in row 9 (1.44
against 1.24 ms). I have not found why the two cache-off rows cost more.

**Your ruling is needed** on whether total time is the measure, or whether Turn has to fall inside the baseline's
range with the stopwatches as they are.

### What the row lookup costs

Dossier decision 5 replaced the hand-written sync loop by a core `createLookup`. App time, which holds the
lookup, its `affected` function and the row effects, is below the baseline in every scroll row: 0.49 to 0.56 ms
against 0.72 to 0.77 ms with all settings on, and 1.63 to 1.97 ms against 3.61 to 3.68 ms when every row is
written. No core primitive had to change.

Rows computed a frame match the baseline: 3.2 for a 3-line scroll, 49.0 with recycling off, 0.3 for a 7 px
scroll, 1.0 for typing. The view tests assert the exact counts: 3 rows for a 3-line scroll, 1 row for a typed
character, the rows from the edited line to the pool's end for a new line.

### Two findings on the way

1. **A separate element for the gutter marker cost the engine about 1 ms a frame.** With the line number in its
   own cell beside the text, the 3-line scroll took 2.2 to 2.6 ms in total; with a constant number in that cell,
   1.5 ms; with the number and the text in one markup slot, 1.7 ms (single runs). A row is therefore one markup
   slot. I did not find why the baseline, which has such a cell, does not pay this.
2. **The cost of a typed character in the state layer is about 9 microseconds** (2,000 calls of
   `Commands.insertText` on the 1,000,000-line document with no view). The rest of the 0.18 to 0.22 ms Dispatch
   is the view's memos, the row's markup and its DOM write. Each of the cursor, active-line and selection layers
   adds 0.03 to 0.07 ms to a keystroke (single runs).

## 7.2 History memory

`samples/EditorShowcase --memory`: 10,000 single-character insertions at lines `(i * 7919) mod 1,000,000`, each
its own history item, with an undo depth of 20,000. `GC.GetTotalMemory(true)` after a full collection.

| Measured | Growth | An insertion |
|---|---|---|
| After the insertions, the text at the start still the saved text | 48,493,632 bytes | 4,849 bytes |
| After `Document.MarkSaved` | 2,788,888 bytes | **279 bytes** |

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
after 3 untimed ones.

| Scan | Median | Best |
|---|---|---|
| Scalar (`Text.ofUtf8Scalar`) | 50.0 ms | 27.7 ms |
| Vectorised (`Text.ofUtf8`) | 27.0 ms | 14.8 ms |

The vectorised scan is 1.85 times faster at the median and is kept. It compares 32 bytes at a time with
`Vector256` (16 with `Vector128`), behind `#if !FABLE_COMPILER`, with a scalar tail, and a test checks that both
scans give the same text.

### Applying many ranges

`samples/EditorShowcase --apply`: 10,000 one-character replacements, one every 100 lines. Median of 7 runs.

| How | Time |
|---|---|
| `ChangeSet.apply` of one set of 10,000 ranges | 71.0 ms |
| 10,000 calls of `Text.replace` | 51.3 ms |

Both are 5 to 7 microseconds a range, so neither rebuilds the tree per range. `ChangeSet.apply` applies its
ranges back to front through `Text.replace`; the single pass that rebuilds each touched leaf once (dossier 4.6)
is not written, because these ranges each fall in a leaf of their own and it would gain nothing here. It would
gain where many ranges fall in one leaf, which this benchmark does not measure.

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
The state package has 104 tests and the view has 15.
