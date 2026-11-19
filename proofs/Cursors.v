From iris.heap_lang Require Import proofmode notation.
From Plume Require Import Retention.

(* Snapshot of the reader registry on the graph thread. Each entry contains a
   reference to the live cursor, just as the ResizeArray holds ReaderCursors. *)
Fixpoint registry (ls : list loc) : val :=
  match ls with
  | nil => NONEV
  | l :: ls' => SOMEV (#l, registry ls')
  end.

Definition scan : val :=
  rec: "loop" "keep" "rs" :=
    match: "rs" with
      NONE => !"keep"
    | SOME "p" =>
        let: "candidate" := !(Fst "p") + #1 in
        (if: "candidate" < !"keep" then "keep" <- "candidate" else #());;
        "loop" "keep" (Snd "p")
    end.

Section proof.
Context `{!heapGS Σ}.

(* Fractional ownership permits other clients to retain read access. The scan
   must return every fraction, including when the same cursor appears twice. *)
Fixpoint cursors (q : Qp) (ls : list loc) (rs : list nat) : iProp Σ :=
  match ls, rs with
  | nil, nil => True
  | l :: rest, r :: tail => l ↦{#q} #(Z.of_nat r) ∗ cursors q rest tail
  | _, _ => False
  end%I.

Lemma wp_scan q keep ls rs h :
  {{{ keep ↦ #(Z.of_nat h + 1) ∗ cursors q ls rs }}}
    scan #keep (registry ls)
  {{{ RET #(Z.of_nat (oldest h rs) + 1);
      keep ↦ #(Z.of_nat (oldest h rs) + 1) ∗ cursors q ls rs }}}.
Proof.
  iIntros (Φ) "[Hk Hrs] Hpost".
  iInduction ls as [|l ls] "IH" forall (rs h Φ);
    destruct rs as [|r rs]; simpl; try by iDestruct "Hrs" as %[].
  - wp_rec. wp_pures. wp_load. iApply "Hpost". by iFrame.
  - iDestruct "Hrs" as "[Hl Hrs]".
    wp_rec. wp_pures. wp_load. wp_pures. wp_load. wp_pures.
    case_bool_decide.
    + wp_pures. wp_store. wp_pures.
      rewrite Nat.min_r; last lia.
      wp_apply ("IH" with "Hk Hrs"). iIntros "[Hk Hr]".
      iApply "Hpost". iFrame.
    + wp_pures. rewrite Nat.min_l; last lia.
      wp_apply ("IH" with "Hk Hrs"). iIntros "[Hk Hr]".
      iApply "Hpost". iFrame.
Qed.
End proof.
