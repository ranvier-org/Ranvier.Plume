# Retention and text materialization

Measured here on Windows x64 with the .NET 10 Release assemblies. The baseline includes the UTF-16 correctness fix and precedes the optimisation commits. Run the same [retention](BenchRetention.fsx) and [text](BenchText.fsx) scripts against each build with `DOTNET_TieredCompilation=0`. Each script warms the workload and reports the median of nine timed loops per process. The tables give the median of three process medians per revision, run in alternating order. Timing varies with machine load.

| Empty transaction dispatch | Baseline ns/append | Optimised ns/append |
| --- | ---: | ---: |
| No readers | 204.4 | 219.4 |
| One pinned reader | 103.5 | 108.3 |
| 64 pinned readers | 187.8 | 100.3 |
| 1,024 pinned readers | 677.2 | 169.5 |
| 1,024 readers, last pinned and other readers previously observed | 2,305.2 | 1,814.8 |

A loop appends 10,000 transactions through the public document API. The last workload includes reactive invalidation of previously observed readers. The low-fanout cases showed no speedup. An experimental scan that stopped at the first reader on the cut still traversed the registry when that reader came last. The retained implementation checks a remembered index against the live registry, then falls back to a scan on a miss. The `hinted_cut_exact` theorem covers any stale index.

| Repeated Text.toString | Baseline ns/read | Optimised ns/read | Baseline B/read | Optimised B/read |
| --- | ---: | ---: | ---: | ---: |
| 15 UTF-16 units, single line | 67.4 | 4.2 | 160 | 0 |
| 20 units, two lines | 57.7 | 35.2 | 176 | 64 |
| 3,300 units, multiple leaves | 5,330.9 | 3,626.9 | 13,296 | 6,624 |

Allocation is the current-thread allocation delta divided by completed reads. A repeated single-line read can reuse the line string. On .NET, other reads fill the final string directly. The `fill_frame` theorem establishes that each subtree writes exactly its assigned slice and preserves the rest. The byte measurements cover repeated reads of an existing text; constructing or editing the rope is outside this benchmark.
