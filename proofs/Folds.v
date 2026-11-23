From Stdlib Require Import List Arith Lia.
Import ListNotations.

(* A run keeps its header h and hides the next n lines. *)
Definition fold_row h n line :=
  if line <=? h then line else h + (line - (h + n)).

Definition unfold_row h n row :=
  if row <=? h then row else row + n.

Theorem fold_header h n line :
  h <= line <= h + n -> fold_row h n line = h.
Proof.
  intros B. unfold fold_row. destruct (line <=? h) eqn:E;
    [apply Nat.leb_le in E | apply Nat.leb_gt in E]; lia.
Qed.

Theorem fold_visible_inverse h n row :
  fold_row h n (unfold_row h n row) = row.
Proof.
  unfold unfold_row, fold_row. destruct (row <=? h) eqn:E.
  - now rewrite E.
  - apply Nat.leb_gt in E. assert (row + n <=? h = false) as F by (apply Nat.leb_gt; lia).
    rewrite F. lia.
Qed.

Theorem fold_retains_visible_lines h n line :
  line <= h \/ h + n < line -> unfold_row h n (fold_row h n line) = line.
Proof.
  unfold unfold_row, fold_row. destruct (line <=? h) eqn:E.
  - now rewrite E.
  - apply Nat.leb_gt in E. intros B.
    assert (h + (line - (h + n)) <=? h = false) as F by (apply Nat.leb_gt; lia).
    rewrite F. lia.
Qed.

(* The outermost run acts first on a visual row. Runs are expressed in the
   coordinates of the layout in which they were closed. *)
Fixpoint rows_to_document (runs : list (nat * nat)) row :=
  match runs with
  | [] => row
  | (h,n) :: rest => rows_to_document rest (unfold_row h n row)
  end.

Fixpoint document_to_rows (runs : list (nat * nat)) line :=
  match runs with
  | [] => line
  | (h,n) :: rest => fold_row h n (document_to_rows rest line)
  end.

Theorem folded_rows_roundtrip runs row :
  document_to_rows runs (rows_to_document runs row) = row.
Proof.
  revert row. induction runs as [|[h n] rest IH]; intro row; simpl; auto.
  rewrite IH. apply fold_visible_inverse.
Qed.

