---
title: Verifying editor invariants
description: Reproduce Plume's Rocq proofs, Iris heap checks and differential tests.
---

# Verifying editor invariants

A slow reader can hold a document’s transaction history open while faster readers advance. Plume remembers the index of a reader that previously held the oldest revision. On the next append it checks that the index still names a live reader at the current cut. A match proves that trimming would remove nothing. A miss runs the minimum scan again. Disposing a reader can shift every subsequent index, so remembering an index alone would be insufficient.

The Rocq theorem `hinted_cut_exact` covers every possible index, including an out-of-range index. The remaining retention proofs establish that every live reader sees its complete unread suffix. An Iris proof checks that the fallback scan leaves each reader's cursor and fractional permission intact.

For changes to text, the model compares the back-to-front splice implementation with a forward edit specification. Inverting stamped changes restores the original sequence. For materialization, `fill_frame` proves that writing a subtree into its assigned region produces the concatenated text while preserving both surrounding regions.

## Comparing editor performance

Use the full-feature editor from v0.2.0 as the control for focused typing and scrolling. Keep IME, clipboard, accessible selection and decorations enabled in both builds. Compare paired whole-turn timings on the same machine. The v0.1.1 timings remain available for historical comparisons. [Benchmark protocol and measurements](https://github.com/ranvier-org/Ranvier.Plume/blob/v0.2.0/docs/bench/full-feature-baseline.md)

## Running the checks

Run `opam exec -- bash proofs/check.sh` with the toolchain from the proof workflow. The script compiles the Release F# editor, records its results as executable Rocq examples, compiles the proofs, checks their assumptions and verifies the compiled dependencies. The examples include owner disposal, older transaction bases, mixed line endings, multiple leaves, and edits inside a UTF-16 surrogate pair.

The proofs describe algorithms over mathematical sequences. The differential checks are the link to the implementation. For example, the first all-offsets comparison found that the rope could turn a lone low surrogate into U+FFFD. The fix preserves the exact contents of affected lines and the reproducer now runs on every proof build. [Proof sources and scope](https://github.com/ranvier-org/Ranvier.Plume/tree/main/proofs) · [Surrogate regression](https://github.com/ranvier-org/Ranvier.Plume/issues/3)
