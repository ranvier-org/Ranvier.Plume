# Editor tools against Plume v0.2.0

## Focused native window

Measured here on Windows using `scripts/compare-editor.ps1` with the tagged EditorShowcase built against the published v0.2.0 state and view packages. The candidate ran the updated showcase with its toolbar, search highlighting, folding, bracket matching and paired entry installed. The query was empty and the document unfolded. Each build ran in a fresh hidden window. Six paired blocks alternated build order, with 160 measured frames per round using the existing harness. The table gives median whole-turn times and the median difference calculated *within* each pair. [Raw process results](editing-tools.json)

| Workload | Published v0.2.0 | Candidate | Paired candidate − baseline |
|---|---:|---:|---:|
| Focused typing | 1.754 ms | 1.838 ms | +0.032 ms |
| Three-line scroll | 1.262 ms | 1.245 ms | +0.029 ms |

Both workloads had faster and slower candidate blocks. Machine load varied across the run. These samples do not establish a speedup or a fixed overhead.

## Active literal search

Measured here with `dotnet fsi --exec scripts/measure-search.fsx` against the Release candidate. The state-only probe searches for `Mu: float` in the real showcase files. Results are medians of five post-warmup rounds. Cached next is the elapsed time for a batch of 100 navigation commands divided by 100. Edit + next inserts a character at the start and navigates on the new snapshot. These figures exclude native input, highlighting and painting.

| Showcase file | Lines | Set query + first match | Cached next | Edit + next |
|---|---:|---:|---:|---:|
| Program.fs | 300 | 0.029 ms | 0.0009 ms | 0.031 ms |
| Integrators.fs | 20,000 | 1.082 ms | 0.0011 ms | 1.039 ms |
| Ephemeris.fs | 1,000,000 | 23.885 ms | 0.0006 ms | 19.686 ms |

This first implementation rebuilt the index when the text snapshot changed. The [incremental search measurements](incremental-search.md) cover the released implementation and include focused typing with a live query.
