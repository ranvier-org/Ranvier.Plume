From Stdlib Require Import List Arith Lia.
Import ListNotations.

(* A cut is the sequence immediately before the first retained transaction.
   Plume stores [cut + 1] in firstSeq. Readers record the last sequence seen. *)
Fixpoint oldest (head : nat) (readers : list nat) : nat :=
  match readers with
  | [] => head
  | r :: rs => oldest (Nat.min head r) rs
  end.

Lemma oldest_bound h rs : oldest h rs <= h.
Proof.
  revert h; induction rs as [|r rs IH]; intros h; simpl; [lia|].
  specialize (IH (Nat.min h r)); lia.
Qed.

Lemma oldest_reader h rs r : In r rs -> oldest h rs <= r.
Proof.
  revert h; induction rs as [|x rs IH]; intros h; simpl; [tauto|].
  intros [-> | Hin]; [pose proof (oldest_bound (Nat.min h r) rs); lia|].
  apply IH; assumption.
Qed.

Lemma oldest_greatest lo h rs :
  lo <= h -> Forall (fun r => lo <= r) rs -> lo <= oldest h rs.
Proof.
  revert h; induction rs as [|r rs IH]; intros h Hh Hall; simpl; auto.
  inversion Hall; apply IH; auto; lia.
Qed.

(* An index in the current registry is only a hint. It may have shifted or
   fallen out of range after disposal. Only the current value can certify a cut. *)
Definition hinted_cut cut head rs index :=
  match nth_error rs index with
  | Some r => if Nat.eqb r cut then cut else oldest head rs
  | None => oldest head rs
  end.

Theorem hinted_cut_exact cut head rs index :
  cut <= head -> Forall (fun r => cut <= r) rs ->
  hinted_cut cut head rs index = oldest head rs.
Proof.
  intros Hhead Hrs. unfold hinted_cut.
  destruct (nth_error rs index) as [r|] eqn:E; auto.
  destruct (Nat.eqb r cut) eqn:Eq; auto.
  apply Nat.eqb_eq in Eq; subst r. apply Nat.le_antisymm.
  - now apply oldest_greatest.
  - apply oldest_reader. eapply nth_error_In; eassumption.
Qed.

Section Log.
Context {A : Type}.

(* Implementation bodies operate on a suffix. The specification keeps the whole
   abstract history; values of A distinguish transactions even at equal lengths. *)
Definition trim (cut next : nat) (log : list A) := skipn (next - cut) log.
Definition since (cut cursor : nat) (log : list A) := skipn (cursor - cut) log.

Lemma skip_twice n m (xs : list A) :
  skipn n (skipn m xs) = skipn (m + n) xs.
Proof.
  revert xs; induction m; intros xs; simpl; auto.
  destruct xs; simpl; [now rewrite skipn_nil|apply IHm].
Qed.

Theorem append_trim_refines cut (history : list A) event readers :
  cut <= length history -> Forall (fun r => cut <= r) readers ->
  let next := oldest (S (length history)) readers in
  trim cut next (skipn cut history ++ [event]) = skipn next (history ++ [event]).
Proof.
  intros Hcut Hread next.
  assert (cut <= next) by (apply oldest_greatest; [lia|exact Hread]).
  assert (Happend : skipn cut (history ++ [event]) = skipn cut history ++ [event]).
  { rewrite skipn_app. replace (cut - length history) with 0 by lia. reflexivity. }
  unfold trim. rewrite <- Happend, skip_twice. f_equal; lia.
Qed.

Theorem unread_preserved cut (history : list A) cursor :
  cut <= cursor -> since cut cursor (skipn cut history) = skipn cursor history.
Proof. intros H. unfold since. rewrite skip_twice. f_equal; lia. Qed.

Theorem live_reader_after_append cut (history : list A) event readers cursor :
  cut <= length history -> Forall (fun r => cut <= r) readers -> In cursor readers ->
  let next := oldest (S (length history)) readers in
  since next cursor (trim cut next (skipn cut history ++ [event])) =
    skipn cursor (history ++ [event]).
Proof.
  intros Hcut Hrs Hin next. unfold next. rewrite append_trim_refines by assumption.
  apply unread_preserved, oldest_reader; assumption.
Qed.

Theorem last_reader_gone cut (history : list A) event :
  cut <= length history -> trim cut (S (length history)) (skipn cut history ++ [event]) = [].
Proof.
  intros H. pose proof (append_trim_refines cut history event [] H (Forall_nil _)) as E.
  cbv delta [oldest] in E. rewrite E.
  apply skipn_all2. rewrite length_app; simpl; lia.
Qed.
End Log.
