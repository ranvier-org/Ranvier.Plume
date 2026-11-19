From Stdlib Require Import List Arith Lia.
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
(* Materialization concatenates each line's contents and recorded terminator. *)
Definition flatten (lines : list (list A * list A)) :=
  flat_map (fun line => fst line ++ snd line) lines.

Theorem singleton_materialize contents :
  flatten [(contents, [])] = contents.
Proof. simpl. now rewrite !app_nil_r. Qed.
Inductive tree :=
| leaf (lines : list (list A * list A))
| branch (left right : tree).

Fixpoint contents (t : tree) : list A :=
  match t with leaf lines => flatten lines | branch l r => contents l ++ contents r end.

Fixpoint size (t : tree) : nat :=
  match t with leaf lines => length (flatten lines) | branch l r => size l + size r end.

Lemma size_contents t : size t = length (contents t).
Proof. induction t; simpl; auto. now rewrite length_app, IHt1, IHt2. Qed.

(* Overwrite only the slice of a preallocated buffer assigned to each leaf. *)
Fixpoint fill (t : tree) (pos : nat) (buffer : list A) : list A :=
  match t with
  | leaf lines =>
      firstn pos buffer ++ flatten lines ++ skipn (pos + size t) buffer
  | branch l r => fill r (pos + size l) (fill l pos buffer)
  end.

Theorem fill_frame t prefix space suffix :
  length space = size t ->
  fill t (length prefix) (prefix ++ space ++ suffix) =
    prefix ++ contents t ++ suffix.
Proof.
  revert prefix space suffix. induction t as [lines|l IHl r IHr]; intros pre space tail Hlen.
  - cbn [fill contents]. rewrite take_span, <- Hlen, <- length_app, (app_assoc pre space tail), drop_span.
    reflexivity.
  - cbn [size] in Hlen. cbn [fill contents].
    pose proof (firstn_skipn (size l) space) as Hsplit.
    set (a := firstn (size l) space) in *.
    set (b := skipn (size l) space) in *.
    assert (Ha : length a = size l) by (unfold a; rewrite length_firstn; lia).
    assert (Hb : length b = size r) by (unfold b; rewrite length_skipn; lia).
    rewrite <- Hsplit. rewrite <- (app_assoc a b tail), IHl by exact Ha.
    rewrite (app_assoc pre (contents l) (b ++ tail)).
    rewrite (size_contents l), <- length_app, IHr by exact Hb.
    now rewrite !app_assoc.
Qed.
End edits.
