From Stdlib Require Import List Arith Lia.
Import ListNotations.

(** A cutoff partitions the ordered, disjoint hits into those before the
    selection boundary and those eligible in the direction of travel. *)
Fixpoint search_cut (threshold : nat) (positions : list nat) : nat :=
  match positions with
  | [] => 0
  | pos :: tail => if pos <? threshold then S (search_cut threshold tail) else 0
  end.

Theorem search_cut_partition : forall threshold before after,
  Forall (fun x => x < threshold) before ->
  (forall x xs, after = x :: xs -> threshold <= x) ->
  search_cut threshold (before ++ after) = length before.
Proof.
  intros threshold before after H. induction H; intros A; simpl.
  - destruct after as [|x xs]; simpl; auto.
    specialize (A x xs eq_refl). destruct (x <? threshold) eqn:E; auto.
    apply Nat.ltb_lt in E. lia.
  - apply Nat.ltb_lt in H. rewrite H. f_equal. apply IHForall. exact A.
Qed.

(** The two updates of the half-open binary search keep the unique cutoff
    in the candidate interval and strictly shrink it. [before] models the
    chosen comparison on an indexed endpoint (strict on starts, inclusive
    on ends). This covers both branches without machine-integer assumptions
    about the midpoint's sum. *)
Theorem search_bisect_step : forall (before : nat -> bool) cut lo hi,
  (forall i, before i = true <-> i < cut) ->
  lo <= cut <= hi -> lo < hi ->
  let mid := lo + (hi - lo) / 2 in
  if before mid then
    S mid <= cut <= hi /\ hi - S mid < hi - lo
  else
    lo <= cut <= mid /\ mid - lo < hi - lo.
Proof.
  intros before cut lo hi Htest Hcut Hopen mid.
  assert (lo <= mid < hi) as Hmid.
  { unfold mid. pose proof (Nat.div_lt (hi - lo) 2 ltac:(lia) ltac:(lia)). lia. }
  destruct (before mid) eqn:E.
  - apply Htest in E. lia.
  - assert (~ mid < cut) by (intro B; apply Htest in B; congruence). lia.
Qed.

Definition search_forward (count cut : nat) := if cut <? count then cut else 0.
Definition search_backward (count cut : nat) := if cut =? 0 then count - 1 else cut - 1.
Definition search_next (lowers : list nat) (right : nat) :=
  search_forward (length lowers) (search_cut right lowers).
Definition search_prev (uppers : list nat) (left : nat) :=
  search_backward (length uppers) (search_cut (S left) uppers).

(** A selected nonempty match is outside both eligible half-intervals.
    Moving forwards then backwards (or backwards then forwards) recovers
    the original index, including at the wrap and on singleton results. *)
Theorem search_cycle : forall n i, i < n ->
  search_backward n (search_forward n (S i)) = i /\
  search_forward n (S (search_backward n i)) = i.
Proof.
  intros n i Hi. unfold search_backward, search_forward.
  split.
  - destruct (S i <? n) eqn:E.
    + simpl. lia.
    + apply Nat.ltb_ge in E. simpl. lia.
  - destruct (i =? 0) eqn:E.
    + apply Nat.eqb_eq in E. destruct (S (n - 1) <? n) eqn:F; auto.
      apply Nat.ltb_lt in F. lia.
    + apply Nat.eqb_neq in E. destruct (S (i - 1) <? n) eqn:F; try lia.
      apply Nat.ltb_ge in F. lia.
Qed.
