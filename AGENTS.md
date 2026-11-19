# AGENTS.md

## Repository

`Ranvier.Plume` is a text editor as a fold over a log of edits, built on Ranvier. The design, its decisions and
the phase plan are in `docs/DOSSIER.md`; the phase 1 build plan is in `docs/superpowers/plans/`.

- `src/Ranvier.Plume`: the state package. No DOM dependency. Rope (`Text`), change sets, selections,
  transactions, `EditorState` with facets, fields and compartments, history, the log (`Document`), `Editor`,
  commands, keymaps and the tokenizer model.
- `src/Ranvier.Plume.View`: the `EditorView` component on `Ranvier.View`, `Ranvier.Css` and
  `Ranvier.Keyboard.Dom`.
- `tests/Ranvier.Plume.Tests`: Expecto tests of the state package.
- `tests/Ranvier.Plume.View.Tests`: a hidden-window smoke test of the view.
- `samples/EditorShowcase`: the benchmark sample.

Always pass `--exec` when invoking `dotnet fsi` (`dotnet fsi --exec build.fsx -- ci`).

## Library constraints

- Library projects set `RanvierAotClean=true` (`--reflectionfree`, `IsAotCompatible` on `net10.0`): no
  `sprintf "%A"` and no reflection-based formatting.
- SIMD code is gated behind `#if !FABLE_COMPILER`, reads inside span bounds, and keeps a scalar fallback. A
  vectorised loop stays only while its benchmark shows a gain.
- Change reaches consumers through Ranvier memos and effects. The public API has no listeners, observers,
  event emitters or update callbacks.
- The view follows `Ranvier/.claude/rules/view-uses-core.md`: change detection, keyed identity, ownership and
  scheduling use core primitives. A view-side duplicate needs the user's agreement.
- Offsets are UTF-16 code units. Line numbers are 0-based.
- A traced build is opt-in: `-p:RanvierTrace=true`.

## Before reporting work as done

`dotnet fsi --exec build.fsx -- ci` builds the solution in Release and runs every test project unfiltered.

`dotnet fsi --exec build.fsx -- check` builds the solution in Debug and runs `rfs check --fail-on-error`. `rfs`
reads the Debug configuration, so it needs that build once; a Release build after it changes nothing `rfs` reads.

`dotnet fsi --exec build.fsx -- dev` runs the sample under the hot reload host with its window off screen, on a
copy of its sources, through the steps of `samples/EditorShowcase/hot-reload.json`.

`dotnet fsi --exec build.fsx -- traced` builds both libraries with `-p:RanvierTrace=true` and runs the state tests
on them, in a copy of the tree.

`dotnet fsi --exec build.fsx -- acceptance` recomputes the verdicts of the dossier's section 7 from the recorded
benchmark runs in `docs/bench` and from memory and loading runs it makes.

`dotnet fsi --exec build.fsx -- red` shows the tests of plan tasks 1 and 2 failing to compile without their
implementation, each in a copy of its task's commit.

## Order of work

Write a task's tests before its code, and run them to see them fail. Tick a plan step that says "run; it fails"
when that run was made, with its output. Phase 1 wrote the code first in every task (`docs/ACCEPTANCE.md`, "The
order the work was done in").
