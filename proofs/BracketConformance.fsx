module BracketConformance

open Ranvier
open Ranvier.Plume

let observations () =
    let tokens forward (c: char) =
        let enter, leave = if forward then "enter ", "leave_ " else "leave_ ", "enter "
        match c with
        | '(' -> enter + "round" | ')' -> leave + "round"
        | '[' -> enter + "square" | ']' -> leave + "square"
        | '{' -> enter + "curly" | '}' -> leave + "curly"
        | _ -> "plain"
    let example name expr = $"Example {name} : {expr}. Proof. reflexivity. Qed."
    let cases = [ "()"; "[{}]"; "(😀\r\n{[]})"; "{(]}"; "([)]"; "[abc"; "xyz)" ]
    [ yield "From Plume Require Import Brackets. Import TypedBrackets."
      for index, value in List.indexed cases do
          for forward in [true; false] do
              let source = if forward then Seq.toList value else value |> Seq.rev |> Seq.toList
              let first, tail = List.head source, List.tail source
              match (forward,first) with
              | (true,('('|'['|'{')) | (false,(')'|']'|'}')) ->
                  let k = match first with '('|')' -> "round" | '['|']' -> "square" | _ -> "curly"
                  let input = tail |> List.map (tokens forward) |> String.concat ";"
                  let head = if forward then 0 else value.Length
                  let actual =
                      match Brackets.matchAt (Text.ofString value) head with
                      | ValueSome(struct (a,b)) when (forward && a = 0) || (not forward && b = value.Length - 1) ->
                          "Some " + string (b - a)
                      | ValueSome _ -> failwith "Wrong bracket origin"
                      | ValueNone -> "None"
                  yield example $"bracket_scan_{index}_{forward}" $"scan [{input}] {k} [] = {actual}"
              | _ -> ()
      use graph = new Graph()
      use active = graph.Activate()
      let editor = Editor.create (Document.create (Text.ofString "abcd")) [Brackets.autoClose]
      editor.Dispatch [Tx.selection (EditorSelection.single 3 1)]
      Brackets.typeText "[" editor |> ignore
      let units (s: string) = "[" + (s |> Seq.map (int >> string) |> String.concat ";") + "]"
      let doc, sel = editor.Text.Value, editor.Selection.Value.Main
      yield example "bracket_wrap_text" ("[97] ++ 91 :: [98;99] ++ 93 :: [100] = " + units (Text.toString doc))
      yield example "bracket_wrap_selection" $"(3+1,1+1) = ({sel.Anchor},{sel.Head})"
      for existing in [true;false] do
          let initial = if existing then ")" else ""
          let e = Editor.create (Document.create (Text.ofString initial)) [Brackets.autoClose]
          if not existing then Brackets.typeText "(" e |> ignore
          Brackets.typeText "xy" e |> ignore
          let pos = e.Selection.Value.Main.Head
          let before = e.Text.Value.Length
          Brackets.typeText ")" e |> ignore
          let skipped = if e.Text.Value.Length = before then "true" else "false"
          let origins = if existing then "[]" else "map (Nat.add 2) [1]"
          yield example $"bracket_origin_{existing}" $"can_skip true round round {pos} ({origins}) = {skipped}" ]
