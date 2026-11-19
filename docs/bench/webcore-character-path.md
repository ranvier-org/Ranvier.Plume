# WebCore character delivery after a cancelled beforeinput

Measured here in the hidden GPU-composited Windows x64 EditorShowcase. The published control pins both Plume packages to 0.1.1. Both editors load the same packaged WebCore.dll (SHA-256 `CC7A3E2CA5D0212A24AFABDEE8B0F9A1F944B73937C955E27DE922502E75B398`). The package manifest identifies Ranvier.WebCore.Native.win-x64 0.1.3 and WebKit revision `b95c91b1c0ee43cc86d25ff7af73038f67a88e04`.

## Native attribution

Native timing probes attached to exported functions in the packaged binaries. The outer scope was the owner thread's `rv_host_key(RV_KEY_CHAR)`. The listener boundary was the actual registered `beforeinput` callback and its context. Entry and return timestamps partitioned the character into intervals before, inside and after the callback. A native stack of instrumented calls charged each interval to its innermost observed function. Separate counters collected the inclusive selection-reveal subtree. These are wall times and include probe overhead. Use the lightly instrumented and uninstrumented process comparisons below for end-to-end decisions.

Measured here with tiered compilation disabled, all showcase features enabled, the input focused, five rounds of 160 timed keys and 30 preceding warm-up keys per round. Each process verified its inserted character count. The table gives medians of four process means in ms per timed character. The mirror bypass was diagnostic and left the editor transaction and view updates in place.

| Character phase | Full feature build | Mirror writes and pre-insertion adoption bypassed |
| --- | ---: | ---: |
| Before the listener | 0.034 | 0.033 |
| Inside the listener | 0.290 | 0.257 |
| After the listener | 0.810 | 0.624 |
| ↳ selection reveal, layout | 0.495 | 0.449 |
| ↳ selection reveal, style | 0.154 | 0.146 |
| ↳ selection reveal, remaining work | 0.152 | 0.019 |

On the full feature build, every timed character reached `FrameSelection::revealSelection` once after the listener returned. All 800 post-listener layout passes in each run fell inside that reveal. With the mirror bypassed the layout pass still occurred. On the package, ordinary typing was handled on key down. Its character call took 0.010 ms in this trace and did not enter either the editing listener or selection reveal.

The call sequence explains why cancellation is visible in the trace. WebCore's editor calls `TypingCommand::insertText`, then asks the page to reveal its current selection for trusted input when the editor client permits it. The reveal updates selection appearance, obtains caret geometry, scrolls it into view as needed, and updates appearance. A deeper trace measured 0.124 ms in `Document::resolveStyle`, 0.062 ms in `LocalFrameView::scrollRectToVisible`, and 0.054 ms in `FrameSelection::updateAppearance` (medians of three processes, exclusive of other instrumented functions). The host also checks style after event dispatch, but the costly layout was nested inside the editor's reveal. [Pinned editor source](https://github.com/WebKit/WebKit/blob/b95c91b1c0ee43cc86d25ff7af73038f67a88e04/Source/WebCore/editing/Editor.cpp#L1489) · [Pinned selection source](https://github.com/WebKit/WebKit/blob/b95c91b1c0ee43cc86d25ff7af73038f67a88e04/Source/WebCore/editing/FrameSelection.cpp#L2669) · [Packaged host source](https://github.com/ranvier-org/Ranvier.WebCore/blob/8dc790c317087d8772839eeccf6e3a8eb3a1d3a9/host/RanvierWebCoreAbi.cpp#L5109)

## What happens when the reveal is skipped

In an isolated timing process, the diagnostic requested WebCore's existing `DoNotReveal` mode only for a character's reveal after the registered listener returned. The matching control observed the same boundary and kept the real reveal. Each full-feature process recorded 950 such calls including warm-up. The package recorded zero. IME and accessibility require a correctly positioned selection, so the diagnostic was excluded from production.

Measured here in six alternating blocks. Each cell is the median process mean in ms/turn. The whole turn includes input dispatch, the app callback, and a nonblocking native window step.

| Build | Input dispatch | Window step | Whole turn |
| --- | ---: | ---: | ---: |
| Published 0.1.1 | 0.282 | 0.779 | 1.067 |
| Full feature build | 1.009 | 0.578 | 1.594 |
| Diagnostic without reveal | 0.540 | 1.046 | 1.588 |

The diagnostic had a faster whole turn in only one of six paired blocks. Its paired median change was +0.006 ms. By contrast, the full feature build cost a paired median +0.500 ms against 0.1.1 in the same blocks. Skipping reveal changed when the dirty view paid for layout. These measurements cover ordinary committed characters and do not time IME sessions or measure display latency.

## Caret positioning experiments

Two isolated showcase builds kept the native control focusable and continued to place it at the caret. One put the existing textarea in an absolute, strictly contained wrapper. The other translated a full-width wrapper to the clamped caret position. The timing control used the existing absolute positioning. Each block also ran the pinned package. These comparisons ran without native tracing.

Measured here, paired change in whole-turn ms against the full feature control. Each series used eight blocks with process order rotated and reversed. The confirmation used longer typing runs and reversed the initial mode order.

| Experiment | 160 timed keys per round | 320 timed keys per round |
| --- | ---: | ---: |
| Contained wrapper | −0.030 (faster in 5/8 pairs) | +0.188 (faster in 2/8) |
| Translated wrapper | −0.028 (faster in 5/8 pairs) | −0.011 (faster in 4/8) |

Neither experiment met the repeatability gate. The campaign retained its measurements and kept the production editing, reveal, and accessibility paths intact.

[Raw native, paired and anchor measurements](webcore-character-trace.json). Native phase values are exclusive except for the explicitly nested `REVEAL/` and deeper subtree counters. A call can start before the callback and accrue time after it returns. A zero entry count alongside positive time reflects that boundary crossing.

[Follow-up tracing identifies the affected DOM elements and records the whole-turn candidate comparisons](element-layout.md).
