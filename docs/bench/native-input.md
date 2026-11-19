# Focused native input against published Plume

Measured here in a hidden GPU-composited Windows x64 EditorShowcase using Release .NET 10. The baseline references both Plume NuGet packages at exactly 0.1.1. The candidate includes the editable textarea and the diagnostic example. Both executables use the same profiling harness and SDL host. File, viewport, enabled features, and keystrokes match.

Set `PLUME_PROFILE_INPUT=1` and run `--profile --frames 160`. Set `PLUME_PROFILE` to `typing|every feature` or `lines x3|every feature`. The harness focuses the native editor, types through `WebCoreHost.Type`, and checks that every warm-up and timed keystroke adds exactly one character. Scrolling uses the focused view and the existing three-line scroll workload. Each fresh process runs five rounds with 30 warm-up turns per round. The process order alternates within successive pairs. Times cover dispatch, the showcase frame callback and a nonblocking native window turn. The host can present fewer images than there are turns.

This workload times ordinary committed text. Composition, clipboard and assistive selection use separate correctness tests. The typing lines are away from the example diagnostic, so the measurements include the registered decoration but do not time an open tooltip. The native accessibility-tree integration test runs on a hidden window and exercises the text-selection API directly.

## Proof boundary for focused typing

| Invariant | Verification |
| --- | --- |
| Signal updates, owner disposal, retained DOM updates | Inherited from Ranvier and Ranvier.View |
| Native and document selection boundaries across line-ending normalization | Pure Rocq in InputGeometry.v, compared with F# observations |
| Mutable document reader retention | Existing Iris HeapLang reader proofs |
| Host composition, clipboard events, selection direction and caret placement | Native integration tests. These are host assumptions for the pure offset proof. |

## Focused-input campaign

Measured here with tiered compilation disabled. Six blocks per workload alternated the order of the published packages, an unchanged feature build, and an experiment. Each number below is the median of the six process summaries for focused typing, in milliseconds per turn. Compare builds within a row.

| Experiment | Published 0.1.1 | Unchanged feature build | Experiment |
| --- | ---: | ---: | ---: |
| Skip offset scans when normalized and raw lengths agree | 1.113 | 1.654 | 1.674 |
| Directly restore selection after replacing the native value | 1.009 | 1.528 | 1.530 |
| Temporarily bypass native mirror writes and selection adoption before insertion | 0.996 | 1.501 | 1.272 |

Both implementation experiments were discarded. The diagnostic bypass lowered the time in every paired block but omitted accessibility behavior. It was removed after measurement. This places part of the typing cost in native mirror synchronization and selection adoption; the bypass still ran more slowly than the published package. The offset identity is kept as a checked normalization invariant. [Per-process measurements including scrolling](focused-input-campaign.json)

## Separating native writes from selection adoption

Measured here on Windows x64 in fresh hidden EditorShowcase processes with tiered compilation disabled, focused native input, and every showcase feature enabled. Each process ran five rounds of 160 timed keys after 30 warm-up keys. Each round checked the document length. The package control pinned both Plume NuGet packages to 0.1.1. All probes were confined to benchmark copies.

In ten blocks, each build or probe occupied each position twice, once in each ordering direction. W suppressed just the value and range setters during synchronous typing while leaving the DOM reads, selection-window preparation, and event handlers in place. The counter verified 950 suppressed value writes per process including warm-up. A skipped only the adoption call at the start of `beforeinput`. The native `select` listener remained installed. In W, the stale control may require an extra value comparison when adoption is enabled. The two interventions can interact.

| Mode | Median ms/turn | Paired median change from full feature build |
| --- | ---: | ---: |
| Published package | 1.067 | |
| Full feature build | 1.619 | |
| A, bypass adoption | 1.601 | +0.001 |
| W, bypass setters | 1.342 | −0.233 |
| WA, bypass both | 1.380 | |

Paired changes use differences within each block. Suppressing setters improved all ten pairs with adoption enabled, and all ten with it bypassed (paired median −0.207 ms). Adoption alone improved four pairs, tied one and lost five. The two-bypass build remained slower than the package in every block (paired median +0.326 ms).

### Where the residual appears

A separate six-block comparison focused the control on both builds and compared `Host.Type` with directly issuing `Commands.insertText`. The direct probe still dispatched the edit, updated the view, and stepped the window. During direct edits the two-bypass control also suppressed mirror setters on document changes. Values below are medians of process means, in ms/turn. Columns have independent medians.

| Delivery | Build | Dispatch | App callback | Window step | Whole turn |
| --- | --- | ---: | ---: | ---: | ---: |
| Native key | Package | 0.283 | 0.002 | 0.758 | 1.046 |
| Native key | WA | 0.868 | 0.003 | 0.451 | 1.322 |
| Direct command | Package | 0.150 | 0.002 | 0.804 | 0.954 |
| Direct command | WA | 0.196 | 0.002 | 0.928 | 1.128 |

Timing the three original host calls inside `Type` localized the synchronous difference. On the package, key down / character / key up took 0.267 / 0.008 / 0.002 ms. With WA they took 0.151 / 0.701 / 0.007 ms. In the full feature build they took 0.115 / 0.879 / 0.007 ms. The package handles ordinary text at key down; the accessible editor commits at `beforeinput` during character delivery.

In a separate six-block instrumentation run, full-feature `beforeinput` took 0.210 ms inclusive (median of process means), including 0.0023 ms for adoption and 0.0330 ms for mirror synchronization. With WA the median was 0.181 ms; the paired character-call time outside that listener was 0.518 ms. These spans cover different scopes. A setter can invalidate layout that is serviced after it returns. Three independent native trace runs put median dispatch-plus-callback Input at 0.272 ms on the package and 0.881 ms on WA; Window.Step NativeCycle moved the opposite way, from 0.562 to 0.004 ms. Trace phases overlap. This locates work at the native character dispatch boundary; it does not isolate an internal WebCore function or measure screen presentation latency.

### Implementation experiments

A guarded `setRangeText(..., "end")` splice retained the live selection and checked that the native contents matched the previous mirror. It won three of six typing pairs. CSS containment on the textarea won three of six, and moving its static inline declarations to a stylesheet won three of eight. All were discarded. Holding the textarea position fixed on WA was faster in five of six pairs (paired median −0.102 ms), but it breaks caret-relative IME placement and was diagnostic only. The native suite exercises composition and accessible selection on the full feature build.

[Per-process timings, host phases and probe counts](input-attribution.json). The raw mode names `N`, `control`, `both-key`, and `frozen` denote full feature, full feature, WA with native keys, and WA with a stationary anchor, respectively.

The [native character trace](webcore-character-path.md) follows the residual through WebCore selection reveal and compares dispatch with the subsequent window step.

## Results

Measured here, in milliseconds per turn. Each entry is the median of the four process summaries for that build and workload.

| Workload | Published 0.1.1 | Candidate | Change |
| --- | ---: | ---: | ---: |
| Focused typing | 1.243 | 1.710 | +0.467 ms |
| Focused three-line scroll | 1.408 | 1.459 | +0.051 ms |

The candidate took longer in every typing pair. The direction of the scrolling difference varied across pairs. These are turn times; they do not establish a worst-case input latency. [Proof and conformance coverage](../../proofs/README.md) and the [editor view guide](../content/guide/view.md) describe the operations behind the new input surface.
