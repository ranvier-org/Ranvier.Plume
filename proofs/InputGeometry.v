From Stdlib Require Import List Arith Lia Bool.
Import ListNotations.

(* Each native UTF-16 unit represents one source unit, or two for CRLF. *)
Fixpoint raw_boundary (widths : list nat) (n : nat) : nat :=
  match widths, n with
  | w :: ws, S k => w + raw_boundary ws k
  | _, _ => 0
  end.

Fixpoint native_boundary (widths : list nat) (offset : nat) : nat :=
  match widths, offset with
  | w :: ws, S k => S (native_boundary ws (S k - w))
  | _, _ => 0
  end.

Theorem selection_boundary_roundtrip widths :
  Forall (fun w => 0 < w) widths ->
  forall n, n <= length widths ->
  native_boundary widths (raw_boundary widths n) = n.
Proof.
  intros W. induction W as [|w ws Pos Rest IH]; intros [|k] H; simpl in *; auto; try lia.
  destruct w as [|w]; [lia|]. simpl.
  replace (w + raw_boundary ws k - w) with (raw_boundary ws k) by lia.
  f_equal. apply IH. lia.
Qed.

(**md **Equal-length normalization preserves each offset.** Each native
    unit consumes at least one raw unit, so no prefix can shrink if the
    entire window has equal raw and normalized lengths. *)
Lemma widths_lower_bound widths :
  Forall (fun w => 0 < w) widths -> length widths <= fold_right Nat.add 0 widths.
Proof. intros H. induction H; simpl in *; lia. Qed.

Theorem unchanged_length_offset widths :
  Forall (fun w => 0 < w) widths ->
  fold_right Nat.add 0 widths = length widths ->
  forall n, n <= length widths -> native_boundary widths n = n.
Proof.
  intros H. induction H as [|w ws Pos Rest IH]; intros E n Bound.
  - destruct n; simpl in *; lia.
  - pose proof (widths_lower_bound ws Rest).
    simpl in E, Bound. assert (w = 1) by lia. subst w.
    destruct n; simpl; auto. rewrite Nat.sub_0_r. f_equal. apply IH; lia.
Qed.

Definition replay {A} (old new : list A) l r s :=
  firstn l old ++ firstn (s - l) (skipn l new) ++ skipn r old.

Theorem replay_exact {A} (old new : list A) l r s :
  l <= s -> firstn l old = firstn l new -> skipn r old = skipn s new ->
  replay old new l r s = new.
Proof.
  intros H P S. unfold replay. rewrite P, S.
  replace (skipn s new) with (skipn (s-l) (skipn l new)).
  - now rewrite !firstn_skipn.
  - rewrite List.skipn_skipn. f_equal; lia.
Qed.

Definition clip (a b c d : nat) := (Nat.max a c, Nat.min b d).

Theorem underline_visible_exact a b v w c d p :
  let '(l, r) := clip a b v w in
  let '(first, last) := clip l r c d in
  first <= p < last <-> a <= p < b /\ v <= p < w /\ c <= p < d.
Proof. unfold clip; lia. Qed.

Definition untouched (l r a b : nat) := (b <=? l) || (r <=? a).
Theorem diagnostic_invalidation l r a b :
  untouched l r a b = false <-> a < r /\ l < b.
Proof.
  unfold untouched. rewrite Bool.orb_false_iff, !Nat.leb_gt. tauto.
Qed.

(* A splice fully before the mark changes its offset by the prefix-length
   difference. Reading at the new offset returns exactly the same units. *)
Theorem surviving_mark {A} (prefix inserted gap mark tail : list A) :
  let after := prefix ++ inserted ++ gap ++ mark ++ tail in
  firstn (length mark) (skipn (length prefix + length inserted + length gap) after) = mark.
Proof.
  intros after. unfold after.
  replace (length prefix + length inserted + length gap) with (length (prefix ++ inserted ++ gap))
    by (rewrite !length_app; lia).
  replace (prefix ++ inserted ++ gap ++ mark ++ tail) with ((prefix ++ inserted ++ gap) ++ mark ++ tail)
    by now rewrite !app_assoc.
  rewrite skipn_app, skipn_all, Nat.sub_diag. simpl.
  now rewrite firstn_app, firstn_all, Nat.sub_diag, firstn_O, app_nil_r.
Qed.
