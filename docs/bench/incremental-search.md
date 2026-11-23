# Incremental search in Plume 0.3.0

## Million-line state benchmark

Measured here on Windows with `dotnet fsi --exec scripts/measure-search.fsx` against Release builds, remeasuring the feature branch before and after the change on this machine. The literal was `Mu: float`. Each entry is the median of five rounds after warmup. The edit inserts a character at the start, then navigates to the next match. This workload excludes input dispatch and painting.

| Operation | Original search (ms) | Incremental search (ms) |
|---|---:|---:|
| Install fresh query and navigate | 21.218 | 29.540 |
| Navigate with cached index | 0.0008 | 0.0016 |
| Edit and navigate | 22.511 | 0.099 |

Indexing every leaf separately measured 69.361 ms for a fresh query. Grouping up to 512 lines per index section reduced initial cache construction while retaining unaffected subtrees after edits. Cold queries still cost more than the original flat scan. A changed section is rescanned in full, including a very long single line. Multiline literals and .NET regexes retain whole-snapshot scanning.

## Native window comparison

Measured here with the published 0.2.0 packages and their tagged showcase versus the 0.3.0 candidate. Each comparison used six paired blocks of the existing focused hidden-window benchmark, alternating launch order, with 160 measured frames per round. Each build column gives the median whole turn in milliseconds. The final column is the median of within-block differences. The 0.2.0 release has no search feature. In the active run only the candidate had search highlighting enabled for `Mu: float`. The control was rerun for each comparison.

| Candidate query | Workload | 0.2.0 | 0.3.0 | Paired difference |
|---|---|---:|---:|---:|
| idle | typing | 1.671 | 1.783 | -0.118 |
| idle | lines x3 | 1.089 | 1.111 | 0.058 |
| active | typing | 1.742 | 2.056 | 0.243 |
| active | lines x3 | 1.364 | 1.475 | 0.075 |

Run `scripts/compare-editor.ps1 -Baseline <baseline DLL> -Candidate <candidate DLL>` once with `PLUME_PROFILE_SEARCH` unset and again with it set to `Mu: float`. The process-level samples preserve the individual runs and timing components. [Idle query](incremental-search-idle.json) · [Active query](incremental-search-active.json)
