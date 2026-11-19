# EditorShowcase native window turn

## Attribution

`--profile-turn` enables the scheduling and rendering traces on the mounted host. Set `PLUME_PROFILE` to `typing|every feature` or `lines x3|every feature` to isolate a workload. Run the Release showcase with `--profile-turn --frames 160`. Use the ordinary `--profile` for timing comparisons. The traced run drains events before and after each `Window.Step(timeout = 0)`, reports only events completed in timed turns, and rejects trace loss. Setup, dispatch, the showcase frame callback and the trace drains are outside the turn timer. Trace collection can influence subsequent work.

Measured here on Windows x64 with the hidden, GPU-composited 1280 × 800 showcase. Both Plume packages were pinned to NuGet 0.1.1 and the SDL host to 0.2.0. Each workload ran five rounds, each with 30 warm-up turns and 160 timed turns. Inclusive times below are milliseconds per timed turn, including turns without a paint.

| Span | Typing | Three-line scroll |
| --- | ---: | ---: |
| Entire window turn | 1.304 | 1.219 |
| WebCore run-loop cycle | 0.918 | 0.210 |
| Present | 0.249 | 0.392 |
| Native paint | 0.185 | 0.328 |
| Rendering update | 0.025 | 0.021 |
| Host frame callback | 0.026 | 0.022 |
| SDL event drain | 0.001 | 0.002 |

Measured here, typing produced 146 paint and 146 present events in 800 turns. Scrolling produced 145 of each in 800 turns. The host spans overlap. In particular, native paint can run inside present. The run-loop measurement times `rv_run_loop_cycle` as a whole. The trace gives neither an internal WebKit CPU breakdown nor a display-latency measurement.

## Renderer experiments

Measured here with host tracing disabled and a fresh process per workload and configuration. Every workload kept syntax highlighting, gutter, cursor, active line, and selection enabled.

- **Layer placement and transport.** Tested the existing sheet promotion switch, requesting the host's bounded GPU pipeline, and promoting the viewport, cursor, or individual rows. None showed a consistent improvement across the pilot runs. Promoting every row increased the scrolling process means to 2.405 and 1.848 ms/frame, versus 1.245 and 1.494 in the bracketing baseline processes. The transport experiment tested the request on this host. It did not establish which transport path another backend would select.
- **Containment.** Compared strict viewport containment with layout-and-paint containment, and strict row containment with layout-only or no containment. Removing row containment looked fastest for typing in the pilot. In the confirmation run each process did five rounds of 320 timed frames. The candidate won one of six typing pairs and two of six scroll pairs. Median process means for typing were 1.148 ms/frame published and 1.288 candidate. For scrolling they were 1.455 and 1.566. Process order reversed on even pairs. Discarded.
- **Row positioning.** Tested anchoring each absolute row at zero and applying its existing pixel offset with `translateY`. In the initial four paired runs of five rounds of 320 timed frames, the scroll median moved from 1.341 to 1.306 ms/frame. The local build also contained the previous reader optimisation. A separate comparison isolated the row change using a single harness executable and three assembly sets.

Measured here on three-line scrolling, five rounds of 480 timed frames in each fresh process. Every ordering of the three builds ran once. Timing was untraced. Values are mean milliseconds per workload frame within each process.

| Order | Published (B) | Current with top (C) | Current with translateY (T) |
| --- | ---: | ---: | ---: |
| BTC | 1.423 | 1.703 | 1.502 |
| TCB | 1.667 | 1.781 | 1.571 |
| CBT | 1.835 | 2.044 | 1.688 |
| CTB | 1.453 | 1.510 | 1.379 |
| TBC | 1.266 | 1.265 | 1.360 |
| BCT | 1.309 | 1.251 | 1.258 |
| Median process mean | 1.438 | 1.607 | 1.441 |

Translation won four of six comparisons against each control. The median was effectively tied with the published package and the slower two configurations changed with run order. Discarded the row-positioning patch. This pass retains the opt-in diagnostic mode and the measurements.
