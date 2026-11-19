# Proofs of Plume editor invariants

The suite follows [Ranvier.View's pure models and conformance checks](https://github.com/ranvier-org/Ranvier.View/tree/main/proofs) and [Ranvier's Iris heap specifications and assumption audit](https://github.com/ranvier-org/Ranvier/tree/main/proofs).

| Production code | Checked property |
| --- | --- |
| `Document.Append`, `trim`, `Since` | [Retention.v](Retention.v) proves the retained log equals the suffix of an unbounded transaction history starting after the oldest live cursor. Every live reader's unread transactions survive each append, in order. Once all readers detach, the next append drops the entire log. |
| `trim` reading `ReaderCursor.At` | [Cursors.v](Cursors.v) uses Iris HeapLang to prove that scanning a snapshot of reader references returns the first sequence to retain. The scan owns its accumulator exclusively and returns every fractional reader permission with the cursor value intact. |
| `ChangeSet.ofSpecs`, `apply`, `invert` | [Edits.v](Edits.v) proves that applying stamped spans back to front agrees with the forward edit specification and preserves an arbitrary leading prefix. Swapping the inserted and deleted spans restores the input, including for empty, adjacent and unequal-length replacements. |

The retention cut is `firstSeq - 1`. A reader cursor holds the last sequence it saw. Advancing or disposing a reader changes the minimum at the next append. The heap model assumes a stable registry on the graph thread. It abstracts the ResizeArray as a snapshot and the sequence numbers as unbounded integers. The proofs cover the modeled operations; correspondence with F#, its rope, the CLR and the reactive scheduler is a separate question.

[Conformance.fsx](Conformance.fsx) executes the Release F# assemblies and writes the observed values into executable Rocq equalities. It exercises simultaneous edits, UTF-16 text, readers advancing at different rates, cached rereads, disposal, and acceptance or rejection of older transaction bases. Each base is probed on a fresh document because an accepted probe appends a transaction.

## Running the suite

With .NET and the [pinned opam toolchain](../.github/workflows/proofs.yml), run `opam exec -- bash proofs/check.sh` from the repository root. In WSL using the Windows SDK, set `DOTNET=dotnet.exe`. The script builds the test project, compiles the model and observations, verifies that each audited theorem is closed under the global context, and runs `rocq check` on the compiled results.

## Split-surrogate counterexample

The mathematical edit theorem holds for arbitrary sequences. The default differential fixture checks all ordered disjoint range pairs at scalar boundaries in `aλ🙂b`. Run `opam exec -- bash proofs/check.sh --all-offsets` to include the UTF-16 offset inside 🙂.

On the release implementation, inserting `x` at the start while deleting the first three UTF-16 units leaves a lone low surrogate. The model preserves that unit; the UTF-8 rope can return U+FFFD. The broader check fails at the forward result before it checks undo. The default conformance run excludes that offset explicitly. A green run gives no guarantee of lossless edits through surrogate pairs.
