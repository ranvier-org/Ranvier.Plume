# Reading transaction batches

`Document.Since` supplies the editor state fold, the update reader and public log readers. In published Plume 0.1.1 each nonempty read copied the unread references into a temporary list and then an array. The .NET implementation now copies directly from the retained list into the result array. Fable uses its existing implementation.

## Public reader

Measured here on Windows x64, .NET 10 Release, with tiered compilation disabled. [BenchReader.fsx](BenchReader.fsx) times public `LogReader.Read()` including memo evaluation. Dispatch and setup take place outside the timed interval. Each of 256 readers consumes a fresh batch in each of 16 cycles. The table gives the median of four process medians per build, with seven samples per process after warm-up. Baseline/candidate process order was ABBA ABBA. Bytes come from the thread allocation counter.

| Transactions per read | Published ns/read | Direct copy ns/read | Published B/read | Direct copy B/read |
| ---: | ---: | ---: | ---: | ---: |
| 1 | 90.8 | 43.7 | 98 | 34 |
| 16 | 140.5 | 82.2 | 338 | 154 |
| 256 | 390.5 | 332.7 | 4,178 | 2,074 |

Allocations fell at every size in every run. Timing varied most at the largest batch, where each build won two of the four pairs. The allocation saving is the basis for keeping this change.

To reproduce, use an EditorShowcase Release output directory containing either the published Ranvier.Plume and Ranvier.Plume.View packages pinned to 0.1.1 or the local project references. In PowerShell run `$env:DOTNET_TieredCompilation='0'` and `dotnet fsi --lib:<absolute-output-directory> --exec proofs/BenchReader.fsx` with each output.

## Native showcase

Measured here using the same hidden, GPU-composited EditorShowcase, with all features enabled. Each cell gives the process mean across five rounds of 240 timed frames after 30 warm-up frames per round. Total time includes dispatch, the showcase callback and the nonblocking window turn. Baseline ran first on odd pairs and second on even pairs. See the showcase's `--profile` mode with `PLUME_PROFILE` set to `typing|every feature` or `lines x3|every feature`.

| Pair | Typing published ms/frame | Typing direct ms/frame | Three-line scroll published ms/frame | Three-line scroll direct ms/frame |
| ---: | ---: | ---: | ---: | ---: |
| 1 | 1.272 | 1.374 | 2.578 | 2.379 |
| 2 | 1.333 | 1.318 | 1.505 | 1.654 |
| 3 | 1.284 | 1.497 | 1.622 | 1.789 |
| 4 | 1.211 | 1.210 | 1.822 | 1.551 |

Both workloads split the pairs evenly. This experiment establishes a reader allocation saving. An end-to-end frame-time improvement remains unconfirmed.
