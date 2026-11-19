# Live typing and scrolling in EditorShowcase

The native window turn dominated the published baseline for both workloads. Two experiments reduced the amount of DOM reconstruction. Neither earned a change to Plume.

## Workload and measurement

Measured here on Windows x64 with the .NET 10 Release EditorShowcase at `4d0fab6`. The baseline used exact NuGet references to Ranvier.Plume and Ranvier.Plume.View 0.1.1. The candidate used the same showcase sources with local project references. The hidden, GPU-composited window measured 1280 × 800 device pixels and had 49 pooled rows. Typing inserted one character per frame in the million-line fixture. Scrolling advanced by three lines per frame with the same features enabled.

To repeat the baseline, copy the EditorShowcase project and its sources, replace its Plume project references with the two exact package references above, and build in Release. Run the resulting DLL with `--profile --frames 240` and set `PLUME_PROFILE` to `typing|every feature` or `lines x3|every feature`. Use `--writes` for the mutation observer. Each timed frame covers dispatch, the showcase frame callback and a nonblocking native window turn. This measures work in the hidden window, not key-to-display latency.

Measured here in the initial feature profile (five rounds of 80 frames), typing averaged 0.330 ms dispatch, 0.002 ms app, and 1.229 ms window turn. Scrolling averaged 0.008, 0.472, and 1.364 ms respectively. The mutation observer recorded a highlighted-line subtree replacement on each keystroke and about three gutter subtree replacements per scroll frame. The window-turn timing includes native work beyond DOM writes.

## Experiments

**Retain each gutter cell.** Measured here on three-line scrolling with 160 frames per round and alternating baseline/candidate/candidate/baseline process order, the two baseline totals were 1.370 and 1.175 ms/frame. The keyed gutter candidate took 2.373 and 1.970 ms/frame. Discarded.

**Patch the first literal text node.** When the token wrapper and all trailing markup matched, the candidate wrote the existing text node. It fell back to the original full markup write for recycled lines, empty runs, escaped text and structural changes. A native regression exercised node identity, escaping, token removal and the empty row. A Rocq theorem checked that replacing a certified literal run preserves the wrapper and suffix in the model. Executable conformance checked the offsets returned by the F# matcher. These checks applied to the experimental implementation and were removed with it.

Measured here over 20 keystrokes, the candidate reduced line subtree replacements from 1.00 to 0.05 per frame. The following independent processes measured full frame time. Each cell is the mean of five rounds of 240 timed frames after 30 warm-up frames per round. Process order reversed on even pairs. All values are milliseconds per frame.

| Pair | Typing baseline | Typing patch | Scroll baseline | Scroll patch |
| ---: | ---: | ---: | ---: | ---: |
| 1 | 1.354 | 1.259 | 1.578 | 1.781 |
| 2 | 1.288 | 1.384 | 1.424 | 1.444 |
| 3 | 1.228 | 1.303 | 1.391 | 1.648 |
| 4 | 2.257 | 1.669 | 1.681 | 1.870 |
| 5 | 1.724 | 1.639 | 1.806 | 2.512 |
| 6 | 2.073 | 2.499 | 1.594 | 1.786 |
| Median process mean | 1.539 | 1.512 | 1.586 | 1.784 |

The patch won three of six typing pairs. It lost all six scrolling pairs, including when the candidate ran first. Fewer subtree mutations did not yield a repeatable typing win, and the scrolling regression outweighed the small difference in median typing time. Discarded.
