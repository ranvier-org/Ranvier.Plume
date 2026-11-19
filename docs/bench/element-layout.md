# Elements touched by focused typing

Measured in the hidden Windows x64 EditorShowcase with all showcase features enabled. The packaged control pins both Plume packages to 0.1.1. Both builds load the same WebCore.dll. The Windows PDB from its native release matched the DLL's debug GUID and age. This allowed probes on WebCore's internal style and renderer methods in the actual shipping engine. The PDB signature was `724BF591-278D-E54D-4C4C-44205044422E`, age 1. Each timing run used Release .NET, `DOTNET_TieredCompilation=0`, `PLUME_PROFILE_INPUT=1`, and `PLUME_PROFILE=typing|every feature`.

The sampling window started before key down and ended at the next key down, including character delivery, key up, the app frame, and the nonblocking window step. Each build ran three fresh processes. Each process sampled three nonboundary keys in each of five rounds, giving 45 sampled keystrokes per build. Native probes resolved each live render object's DOM node, followed its parents through the shadow host, and read tag names, sibling positions and Plume's existing attributes. They did not request computed styles or bounding rectangles. No experimental attributes were added to the two builds being compared.

## Which elements changed

Measured here. Fractions count sampled keystrokes with at least one observed call for the named element or group. Invalidation covers explicit style invalidation and layout-dirty marking. Style resolution and layout are counted separately; an ancestor can have its layout method called to reach a dirty descendant.

| Element or subtree | Published 0.1.1 | Full feature editor |
| --- | --- | --- |
| Edited virtual row | Exactly one row marked for layout and entered layout on 45/45 keys | Same |
| Syntax-token spans inside that row | Style resolution and layout-dirty marking on 36/45 | Same |
| Drawn cursor | Style invalidation, style resolution and layout-dirty marking on 45/45 | Same |
| Line/column status text and status bar | Status subtree marked dirty and entered layout on 45/45 | Same |
| Viewport, virtual sheet and scrollbar | Entered layout on 45/45 | Same |
| Sidebar, including its statistics panel | Entered layout on 45/45; direct layout-dirty marking observed on 1/45 | Same |
| Editor root | Explicit style invalidation and resolution on 45/45 | Neither hook observed on this root |
| Native textarea | Absent | Style invalidation, resolution, layout-dirty marking and layout on 45/45 |
| Textarea's internal editable div and text | Absent | Inner div resolved style and entered layout on 45/45; inner text was marked dirty on 45/45 |

HTML, body and the showcase flex ancestors also entered layout on every sampled key. The edited row entered layout after native key delivery on all package samples and during character delivery on all full-feature samples. The textarea and its editable subtree are the additional named layout work in the current build. The overlapping ancestor paths explain how a document-level layout can coexist with a single updated pooled row. These counts do not measure CPU cost per node.

**Probe coverage.** The style probes were `Node::invalidateStyle`, the element invalidation entry points, and `Style::TreeResolver::resolveElement`. Layout probes covered dirty-marking calls, concrete renderer layout and block-layout methods, inline-content layout on the containing block, and `RenderObject::clearNeedsLayout`. The latter is recorded separately from entry into layout. Optimized inline operations are visible only through the instrumented callers or callees. Two method stubs (`RenderInline::layout` and `RenderBlock::layoutBlock`) could not be hooked. Renderers without a DOM node are grouped as anonymous. Nested method calls on the same element do not increase the keystroke counts.

## Testing retained syntax spans

The row normally receives freshly generated HTML on an edit. The experiment kept the existing DOM text node and following token spans when the edited line had a plain ASCII leading text run and an identical serialized suffix. Other edits used the existing HTML replacement. It changed neither the textarea nor the editor's input, composition or accessibility code.

In a diagnostic run on the candidate, no sampled key entered token style resolution under the edited row. The unchanged feature build entered it on 12 of the 15 samples in its corresponding process. The untraced comparison then timed native input dispatch, the showcase callback and the next window step. All builds verified the inserted character count. Tiered compilation was disabled and order was rotated and reversed within each six-block series.

Measured here in milliseconds per whole turn. The paired difference is computed within each block before taking its median.

| Timed keys per round | Package | Full feature editor | Retained spans | Paired change vs full editor | Faster pairs |
| ---: | ---: | ---: | ---: | ---: | ---: |
| 160 | 1.093 | 1.618 | 1.712 | +0.082 | 2/6 |
| 320 | 1.001 | 1.584 | 1.626 | +0.056 | 2/6 |

The experiment removed an observable source of style work but increased median whole-turn time in both series. It was discarded. The production row, IME and accessible selection implementations stayed intact.

## Testing containment of the whole editor

The next experiment added CSS `contain: size layout` to the editor root, which encloses both the viewport and the native input. The full-feature control kept its original CSS. With containment, HTML, the sidebar container and the status bar still entered layout on all 15 diagnostic samples. The changing status counter is outside the editor root.

Measured here, the contained editor was faster in four of six pairs at 160 keys per round (paired median −0.048 ms). At 320 keys per round it was faster in one of six pairs (paired median +0.043 ms). It was discarded.

[Element-level observations and process timings](element-layout.json) · [Earlier native timing by function](webcore-character-path.md)
