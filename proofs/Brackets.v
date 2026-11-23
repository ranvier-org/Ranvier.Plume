From Stdlib Require Import List Bool PeanoNat Lia.
Import ListNotations.

Module TypedBrackets.
Inductive kind := round | square | curly.
Definition same (x y : kind) : bool :=
  match x, y with round, round | square, square | curly, curly => true | _, _ => false end.
Lemma same_refl x : same x x = true.
Proof. destruct x; reflexivity. Qed.

(* In a reverse scan enter is a closer and leave its corresponding opener.
   Distance counts UTF-16 units, including both halves of a surrogate pair. *)
Inductive token := enter (k : kind) | leave_ (k : kind) | plain.
Fixpoint scan (input : list token) (top : kind) (outer : list kind) : option nat :=
  match input with
  | [] => None
  | t :: ts =>
    match t with
    | plain => option_map S (scan ts top outer)
    | enter k => option_map S (scan ts k (top :: outer))
    | leave_ k => if same k top then
        match outer with [] => Some 1 | p :: ps => option_map S (scan ts p ps) end
      else None
    end
  end.

Inductive balanced : list token -> Prop :=
| bal_nil : balanced []
| bal_plain xs : balanced xs -> balanced (plain :: xs)
| bal_pair k xs ys : balanced xs -> balanced ys ->
    balanced (enter k :: xs ++ leave_ k :: ys).

Lemma lift_succ n (v : option nat) :
  option_map S (option_map (Nat.add n) v) = option_map (Nat.add (S n)) v.
Proof. destruct v; reflexivity. Qed.

(** Scanning a well-nested interior preserves the expected closing type and
    adds exactly the length of that interior to the eventual match distance. *)
Lemma balanced_prefix xs (H : balanced xs) :
  forall rest top outer, scan (xs ++ rest) top outer =
    option_map (Nat.add (length xs)) (scan rest top outer).
Proof.
  induction H as [|xs H IH|k xs ys Hx IHx Hy IHy]; intros rest top outer.
  - simpl. destruct (scan rest top outer); reflexivity.
  - simpl. rewrite IH. apply lift_succ.
  - simpl. rewrite <- app_assoc, IHx. simpl. rewrite same_refl, IHy, length_app.
    simpl. destruct (scan rest top outer); simpl; f_equal; lia.
Qed.

Theorem matched_pair k xs tail (H : balanced xs) :
  scan (xs ++ leave_ k :: tail) k [] = Some (S (length xs)).
Proof. rewrite balanced_prefix by assumption. simpl. rewrite same_refl. simpl. f_equal. lia. Qed.

Theorem mismatched_pair x y xs tail (H : balanced xs) (D : same x y = false) :
  scan (xs ++ leave_ x :: tail) y [] = None.
Proof. rewrite balanced_prefix by assumption. simpl. now rewrite D. Qed.

(** Only an empty cursor at a recorded closer of the typed kind can skip.
    Inserting ordinary text before a pending closer translates its offset. *)
Definition can_skip (empty : bool) (typed actual : kind) (head : nat) (closers : list nat) :=
  empty && same typed actual && existsb (Nat.eqb head) closers.

Theorem skip_origin empty typed actual head closers :
  can_skip empty typed actual head closers = true <->
  empty = true /\ same typed actual = true /\ In head closers.
Proof.
  unfold can_skip. rewrite !Bool.andb_true_iff, existsb_exists. split.
  - intros [[He Ht] [p [Hin Hp]]]. apply Nat.eqb_eq in Hp. subst. auto.
  - intros [He [Ht Hin]]. split; auto. exists head. split; auto. apply Nat.eqb_refl.
Qed.

Theorem shifted_closer k head closers n :
  can_skip true k k (n + head) (map (Nat.add n) closers) =
  can_skip true k k head closers.
Proof.
  unfold can_skip. rewrite same_refl. simpl. induction closers as [|p ps IH]; simpl; auto.
  rewrite IH. destruct (Nat.eqb_spec head p) as [->|Hneq].
  - now rewrite !Nat.eqb_refl.
  - assert (H : (n + head =? n + p) = false) by (apply Nat.eqb_neq; lia).
    now rewrite H.
Qed.

(** Wrapping a selection retains exactly its UTF-16 units, in order. *)
Theorem wrap_contents {A : Type} (prefix selected suffix : list A) (l r : A) :
  firstn (length selected)
    (skipn (S (length prefix)) (prefix ++ l :: selected ++ r :: suffix)) = selected.
Proof.
  induction prefix as [|a xs IH]; simpl; auto.
  rewrite firstn_app, firstn_all, Nat.sub_diag. simpl. apply app_nil_r.
Qed.
End TypedBrackets.
