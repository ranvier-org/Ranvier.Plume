From Stdlib Require Import List Arith Lia.
Import ListNotations.

(** A nonempty single-line literal cannot consume characters on both sides
    of a rope split: the left non-final leaf ends in a line separator. *)
Theorem literal_cannot_cross {A} (separator : A) pattern before after :
  ~ In separator pattern -> pattern <> before ++ separator :: after.
Proof. intros H ->. apply H. apply in_or_app. right. simpl. auto. Qed.

(** Greedy matching consumes a whole nonempty hit, or advances one unit
    on a miss. A safe split preserves every failed prefix test on its left. *)
Section Greedy.
Context {A : Type} (first : A) (tail : list A).
Local Definition pat := first :: tail.
Inductive searches : list A -> nat -> list nat -> Prop :=
| search_empty n : searches [] n []
| search_hit rest n hits : searches rest (n + length pat) hits ->
    searches (pat ++ rest) n (n :: hits)
| search_miss c rest n hits : (~ exists t, c :: rest = pat ++ t) ->
    searches rest (S n) hits -> searches (c :: rest) n hits.

Theorem greedy_join left n lh :
  searches left n lh -> forall right rh,
  (forall suffix pre, left = pre ++ suffix ->
     (exists t, suffix ++ right = pat ++ t) -> suffix <> [] ->
     exists t, suffix = pat ++ t) ->
  searches right (n + length left) rh ->
  searches (left ++ right) n (lh ++ rh).
Proof.
  intros H. induction H as [n|rest n hits H IH|c rest n hits miss H IH];
    intros right rh safe R.
  - simpl in *. now rewrite Nat.add_0_r in R.
  - rewrite <- app_assoc. simpl. apply search_hit. apply IH.
    + intros suf pre E. apply (safe suf (pat ++ pre)). rewrite E, app_assoc. reflexivity.
    + rewrite length_app in R. replace (n + length pat + length rest)
        with (n + (length pat + length rest)) by lia. exact R.
  - simpl. apply search_miss.
    + intro M. apply miss. apply (safe (c :: rest) [] eq_refl M). discriminate.
    + apply IH.
      * intros suf pre E. apply (safe suf (c :: pre)). cbn. now rewrite E.
      * simpl in R. replace (S n + length rest) with (n + S (length rest)) by lia. exact R.
Qed.
End Greedy.

Theorem literal_leaf_join {A} (head sep : A) tail left right n lh rh :
  ~ In sep (head :: tail) ->
  searches head tail (left ++ [sep]) n lh ->
  searches head tail right (n + length (left ++ [sep])) rh ->
  searches head tail ((left ++ [sep]) ++ right) n (lh ++ rh).
Proof.
  intros absent L R. eapply greedy_join; [exact L| |exact R].
  intros suffix pre E [rest M] nonempty.
  destruct (app_eq_app _ _ _ _ M) as [extra [[inside _]|[cross _]]].
  - now exists extra.
  - exfalso. apply absent. change (head :: tail = suffix ++ extra) in cross.
    rewrite cross. apply in_or_app. left.
    pose proof (app_removelast_last sep nonempty) as S.
    rewrite S, app_assoc in E.
    apply (f_equal (fun s => last s sep)) in E. simpl in E.
    repeat rewrite last_last in E.
    rewrite S, <- E. apply in_elt.
Qed.

(** Cached hits use subtree-local UTF-16 offsets. Reading by rank from a
    joined index is equivalent to reading the flattened, rebased list. *)
Theorem search_join_rank (left right : list nat) width i :
  nth_error (left ++ map (Nat.add width) right) i =
  if i <? length left then nth_error left i
  else option_map (Nat.add width) (nth_error right (i - length left)).
Proof.
  destruct (i <? length left) eqn:E.
  - apply Nat.ltb_lt in E. now apply nth_error_app1.
  - apply Nat.ltb_ge in E. rewrite nth_error_app2 by exact E.
    apply nth_error_map.
Qed.

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
