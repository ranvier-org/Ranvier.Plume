namespace Ranvier.Plume.View

open System
open Ranvier
open Ranvier.Dom
open Ranvier.Plume

/// A native textarea normalises CRLF and CR to LF. Map only its selection window.
module internal NativeInput =
    open InputGeometry

    let mount (view: EditorView) (focused: Signal<bool>) (active: Signal<bool>) (preedit: Signal<string>) (area: HTMLTextAreaElement) =
        let editor = view.Editor
        let graph = Graph.Current
        let mutable initialized = false
        let mutable start = 0
        let mutable raw = ""
        let mutable value = ""
        let mutable from = 0
        let mutable finish = 0
        let mutable savedDoc = editor.Text.Peek
        let mutable savedSelection = editor.Selection.Peek
        let readOnly = editor.Facet EditorState.readOnly
        let mutable composing = false
        let mutable stale = false
        let mutable writing = false

        let sync (doc: Text) (selection: EditorSelection) =
            let sel = selection.Main
            let firstLine, lastLine = (Text.lineAt sel.From doc).Number, (Text.lineAt sel.To doc).Number
            start <- (Text.line (max 0 (firstLine - 1)) doc).From
            let last = (Text.line (min (doc.Lines - 1) (lastLine + 1)) doc).To
            raw <- Text.sliceString start last doc
            value <- raw.Replace("\r\n", "\n").Replace('\r', '\n')
            from <- nativeOffset raw (sel.From - start)
            finish <- nativeOffset raw (sel.To - start)
            let direction = if sel.Head < sel.Anchor then "backward" else "forward"
            initialized <- true
            savedDoc <- doc
            savedSelection <- selection
            writing <- true
            try
                if area.value <> value then area.value <- value
                if int area.selectionStart <> from || int area.selectionEnd <> finish ||
                   (from <> finish && area.selectionDirection <> direction) then
                    area.setSelectionRange(uint32 from, uint32 finish, direction)
            finally writing <- false

        let refresh () = sync editor.Text.Peek editor.Selection.Peek
        let valid () =
            obj.ReferenceEquals(savedDoc, editor.Text.Peek) && savedSelection = editor.Selection.Peek

        let input () =
            let next = area.value
            if active.Peek && not composing && not writing && next <> value then
                if valid () && not readOnly.Peek then
                    let struct (a, b, c) = replacement value next from finish
                    let inserted = next.Substring(a, c-a)
                    if a = from && b = finish then Commands.insertText inserted editor |> ignore
                    else
                        let first = start + rawOffset raw a
                        editor.Dispatch [
                            Tx.change { From = first; To = start + rawOffset raw b; Insert = Text.ofString inserted }
                            Tx.selection (EditorSelection.single (first + inserted.Length) (first + inserted.Length))
                            Tx.userEvent "input"
                        ]
                else refresh ()

        let listen name handler =
            let callback (event: Event) = graph.Run(fun () -> handler event)
            area.addEventListener(name, callback)
            fun () -> area.removeEventListener(name, callback)

        let adoptSelection () =
            if active.Peek && not writing && not composing && valid () then
                let a, b = int area.selectionStart, int area.selectionEnd
                let backward = a <> b && area.selectionDirection = "backward"
                let sel = savedSelection.Main
                if (a <> from || b <> finish || (a <> b && backward <> (sel.Anchor > sel.Head))) && area.value = value then
                    let first, last = start + rawOffset raw a, start + rawOffset raw b
                    let range = if backward then EditorSelection.single last first else EditorSelection.single first last
                    editor.Dispatch [Tx.selection range]

        let copy cut (event: Event) =
            adoptSelection ()
            let selection = editor.Selection.Value
            if selection.Ranges |> Array.exists (fun r -> r.From <> r.To) then
                let transfer = (event :?> ClipboardEvent).clipboardData
                if not (isNull transfer) then
                    let doc = editor.Text.Value
                    let parts = selection.Ranges |> Array.map (fun r -> Text.sliceString r.From r.To doc)
                    transfer.setData("text/plain", String.Join(editor.Facet(EditorState.lineSeparator).Value, parts))
                    event.preventDefault()
                    if cut then Commands.cutSelection editor |> ignore

        let hooks =
            [ listen "copy" (copy false)
              listen "cut" (copy true)
              listen "paste" (fun event ->
                  adoptSelection ()
                  let transfer = (event :?> ClipboardEvent).clipboardData
                  if not (isNull transfer) && Commands.pasteText (transfer.getData "text/plain") editor then
                      event.preventDefault())
              listen "focus" (fun _ -> focused.Value <- true; active.Value <- true; refresh ())
              listen "blur" (fun _ -> active.Value <- false; focused.Value <- false; composing <- false; preedit.Value <- "")
              listen "beforeinput" (fun event ->
                  adoptSelection ()
                  let pending = event :?> InputEvent
                  if event.cancelable && not composing && not pending.isComposing && pending.inputType = "insertText" &&
                     not (String.IsNullOrEmpty pending.data) && valid () && not readOnly.Peek then
                      if Commands.insertText pending.data editor then event.preventDefault())
              listen "input" (fun _ -> input ())
              listen "compositionstart" (fun _ -> adoptSelection (); composing <- true; stale <- false)
              listen "compositionupdate" (fun e -> preedit.Value <- (e :?> CompositionEvent).data)
              listen "compositionend" (fun e ->
                  let commit = (e :?> CompositionEvent).data
                  let accept = composing && not stale && valid () && not area.readOnly
                  composing <- false
                  preedit.Value <- ""
                  if accept && not (String.IsNullOrEmpty commit) then Commands.insertText commit editor |> ignore
                  refresh ())
              listen "select" (fun _ -> adoptSelection ()) ]

        let owner = Api.createRoot(fun owner ->
            Api.createEffect(fun () -> area.readOnly <- readOnly.Value)
            Api.createEffect(fun () ->
                if active.Value then
                    let doc, selection = editor.Text.Value, editor.Selection.Value
                    if composing then stale <- stale || not (valid ())
                    elif not initialized || not (obj.ReferenceEquals(doc, savedDoc) && obj.ReferenceEquals(selection, savedSelection)) then
                        sync doc selection)
            owner)
        fun () ->
            for remove in hooks do remove ()
            owner.Dispose()
