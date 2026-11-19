From iris.heap_lang Require Import proofmode notation lib.array.
From Plume Require Import Retention.
From Stdlib Require Import List Lia.

(* A List<T> has spare capacity past Count. Copy exactly the live suffix. *)
Definition snapshot {A} (storage : list A) (count start : nat) :=
  firstn (count - start) (skipn start storage).

Theorem snapshot_exact {A} (history spare : list A) cut cursor :
  cut <= cursor -> cursor <= length history ->
  let live := skipn cut history in
  snapshot (live ++ spare) (length live) (cursor - cut) = skipn cursor history.
Proof.
  intros Hcut Hcursor live.
  assert (Hstart : cursor - cut <= length live)
    by (unfold live; rewrite length_skipn; lia).
  unfold snapshot. rewrite skipn_app.
  replace (cursor - cut - length live) with 0 by lia. simpl.
  rewrite <- (length_skipn (cursor - cut) live).
  rewrite firstn_app firstn_all Nat.sub_diag firstn_O app_nil_r.
  unfold live. apply unread_preserved; assumption.
Qed.

(* NONE models the immutable empty array. In the nonempty case the caller
   owns an independent array of references. Transaction objects are shared. *)
Definition read_slice : val :=
  λ: "src" "start" "n",
    if: "n" ≤ #0 then NONE else SOME (array_clone ("src" +ₗ "start") "n").

Section heap.
Context `{!heapGS Σ}.

Lemma wp_read_slice (src : loc) dq (prefix batch spare : list val) :
  {{{ src ↦∗{dq} (prefix ++ batch ++ spare) }}}
    read_slice #src #(Z.of_nat (length prefix)) #(Z.of_nat (length batch))
  {{{ result, RET result; src ↦∗{dq} (prefix ++ batch ++ spare) ∗
      match batch with
      | nil => ⌜result = NONEV⌝
      | _ :: _ => ∃ dst : loc, ⌜result = SOMEV #dst⌝ ∗ dst ↦∗ batch
      end }}}.
Proof.
  iIntros (Φ) "Hsrc Post". rewrite /read_slice. wp_pures.
  destruct batch as [|x xs].
  - wp_pures. iApply "Post". iFrame. done.
  - case_bool_decide as H; first (simpl in H; lia).
    wp_pures. iDestruct (array_app with "Hsrc") as "[Hp Hr]".
    iDestruct (array_app with "Hr") as "[Hb Hs]".
    wp_apply (wp_array_clone with "Hb"); [done | simpl; lia |].
    iIntros (dst) "[Hd Hb]". wp_pures. iApply "Post".
    iSplitL "Hp Hb Hs".
    + iApply array_app. iSplitL "Hp"; first done. iApply array_app. iSplitL "Hb"; done.
    + iExists dst. iFrame. done.
Qed.
(* A valid public cursor is relative to the retained cut. Instantiate the
   heap triple with that offset and the unread part of the original history. *)
Theorem wp_read_since (src : loc) dq (history spare : list val) cut cursor :
  cut <= cursor -> cursor <= length history ->
  {{{ src ↦∗{dq} (skipn cut history ++ spare) }}}
    read_slice #src #(Z.of_nat (cursor - cut)) #(Z.of_nat (length history - cursor))
  {{{ result, RET result; src ↦∗{dq} (skipn cut history ++ spare) ∗
      match skipn cursor history with
      | nil => ⌜result = NONEV⌝
      | _ :: _ => ∃ dst : loc, ⌜result = SOMEV #dst⌝ ∗ dst ↦∗ skipn cursor history
      end }}}.
Proof.
  intros Hcut Hcursor.
  set (prefix := firstn (cursor - cut) (skipn cut history)).
  assert (P : length prefix = cursor - cut).
  { unfold prefix. rewrite length_firstn length_skipn. lia. }
  assert (E : skipn cut history = prefix ++ skipn cursor history).
  { unfold prefix. rewrite <- (unread_preserved cut history cursor Hcut).
    unfold since. now rewrite firstn_skipn. }
  rewrite E -app_assoc -P -(length_skipn cursor history).
  iIntros (Φ) "Hs Post".
  wp_apply (wp_read_slice with "Hs").
  iIntros (v) "Hv". iApply "Post". done.
Qed.
End heap.
