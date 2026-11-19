From Stdlib Require Import List Arith.
Import ListNotations.

Section edits.
Context {A : Type}.

(* A stamped witness partitions the original text. Keep carries its source
   span here; the executable section keeps only the span length. *)
Inductive span :=
| keep (contents : list A)
| replace (deleted inserted : list A).

Fixpoint before (ss : list span) : list A :=
  match ss with
  | [] => []
  | keep xs :: tail | replace xs _ :: tail => xs ++ before tail
  end.

Definition flip (s : span) : span :=
  match s with keep xs => keep xs | replace xs ys => replace ys xs end.

Fixpoint run (ss : list span) (doc : list A) : list A :=
  match ss with
  | [] => []
  | keep xs :: tail =>
      firstn (length xs) doc ++ run tail (skipn (length xs) doc)
  | replace xs ys :: tail => ys ++ run tail (skipn (length xs) doc)
  end.

Lemma drop_span (xs tail : list A) : skipn (length xs) (xs ++ tail) = tail.
Proof. induction xs; simpl; auto. Qed.

Lemma take_span (xs tail : list A) : firstn (length xs) (xs ++ tail) = xs.
Proof. induction xs; simpl; congruence. Qed.

Theorem apply_stamped ss : run ss (before ss) = before (map flip ss).
Proof.
  induction ss as [|[xs|xs ys] ss IH]; simpl; auto;
    rewrite drop_span, IH; try rewrite take_span; reflexivity.
Qed.

Lemma flip_twice ss : map flip (map flip ss) = ss.
Proof. induction ss as [|[] ss IH]; simpl; congruence. Qed.

Theorem undo_stamped ss :
  run (map flip ss) (run ss (before ss)) = before ss.
Proof. rewrite !apply_stamped, flip_twice. reflexivity. Qed.
(* Mirrors ChangeSet.apply's back-to-front replacements using offsets in the
   original document. Right-hand edits run before the current splice. *)
Fixpoint back (pos : nat) (ss : list span) (doc : list A) : list A :=
  match ss with
  | [] => doc
  | keep xs :: tail => back (pos + length xs) tail doc
  | replace xs ys :: tail =>
      let rest := back (pos + length xs) tail doc in
      firstn pos rest ++ ys ++ skipn (pos + length xs) rest
  end.

Lemma back_with_prefix prefix ss :
  back (length prefix) ss (prefix ++ before ss) = prefix ++ before (map flip ss).
Proof.
  revert prefix. induction ss as [|[xs|xs ys] ss IH]; intros prefix; simpl.
  - reflexivity.
  - rewrite app_assoc, <- length_app, IH, <- app_assoc. reflexivity.
  - rewrite (app_assoc prefix xs (before ss)), <- length_app, IH, drop_span, <- app_assoc, take_span.
    reflexivity.
Qed.

Theorem back_undo ss :
  back 0 (map flip ss) (back 0 ss (before ss)) = before ss.
Proof.
  pose proof (back_with_prefix [] ss) as H; cbn in H; rewrite H.
  pose proof (back_with_prefix [] (map flip ss)) as E; cbn in E; rewrite E, flip_twice.
  reflexivity.
Qed.
End edits.
