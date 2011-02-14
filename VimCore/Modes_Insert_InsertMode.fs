﻿#light

namespace Vim.Modes.Insert
open Vim
open Microsoft.VisualStudio.Text
open Microsoft.VisualStudio.Text.Operations
open Microsoft.VisualStudio.Text.Editor

type CommandFunction = unit -> ProcessResult

type internal InsertMode
    ( 
        _data : IVimBuffer, 
        _operations : Modes.ICommonOperations,
        _broker : IDisplayWindowBroker, 
        _editorOptions : IEditorOptions,
        _undoRedoOperations : IUndoRedoOperations,
        _textChangeTracker : ITextChangeTracker,
        _isReplace : bool ) as this =

    let _textView = _data.TextView
    let mutable _transaction : (IUndoTransaction * int) option = None
    let mutable _commandMap : Map<KeyInput,CommandFunction> = Map.empty

    do
        let commands : (string * CommandFunction) list = 
            [
                ("<Esc>", this.ProcessEscape);
                ("<C-[>", this.ProcessEscape);
                ("<C-d>", this.ProcessShiftLeft)
                ("<C-t>", this.ProcessShiftRight)
                ("<C-o>", this.ProcessNormalModeOneCommand)
            ]

        _commandMap <-
            commands 
            |> Seq.ofList
            |> Seq.map (fun (str,func) -> (KeyNotationUtil.StringToKeyInput str),func)
            |> Map.ofSeq

    /// Enter normal mode for a single command
    member this.ProcessNormalModeOneCommand() =
        ProcessResult.SwitchModeWithArgument (ModeKind.Normal, ModeArgument.OneTimeCommand ModeKind.Insert)

    /// Process the CTRL-D combination and do a shift left
    member this.ProcessShiftLeft() = 
        let range = TextViewUtil.GetCaretLineRange _textView 1
        _operations.ShiftLineRangeLeft range 1
        ProcessResult.Processed

    /// Process the CTRL-T combination and do a shift right
    member this.ProcessShiftRight() = 
        let range = TextViewUtil.GetCaretLineRange _textView 1
        _operations.ShiftLineRangeRight range 1
        ProcessResult.Processed

    member this.ProcessEscape () =

        // Need to close out the edit transaction if there is any
        match _transaction with
        | None ->
            // nothing to do
            ()
        | Some (transaction, count) ->

            // Start by None'ing out the _transaction variable.  Don't need it anymore
            _transaction <- None
            try
                match _textChangeTracker.CurrentChange with
                | Some change ->
                    match change with
                    | TextChange.Insert text -> 
                        // Insert the same text 'count - 1' times at the cursor
                        let text = StringUtil.repeat (count - 1) text
                        let caretPoint = TextViewUtil.GetCaretPoint _textView
                        let span = SnapshotSpan(caretPoint, 0)
                        let snapshot = _textView.TextBuffer.Replace(span.Span, text) |> ignore

                        // Now make sure to position the caret at the end of the inserted
                        // text
                        TextViewUtil.MoveCaretToPosition _textView (caretPoint.Position + text.Length)
                    | TextChange.Delete deleteCount -> 
                        // Delete '(count - 1) * deleteCount' more characters
                        let caretPoint = TextViewUtil.GetCaretPoint _textView
                        let count = deleteCount * (count - 1)
                        let count = min (_textView.TextSnapshot.Length - caretPoint.Position) count
                        _textView.TextBuffer.Delete((Span(caretPoint.Position, count))) |> ignore

                        // Now make sure the caret is still at the same position
                        TextViewUtil.MoveCaretToPosition _textView caretPoint.Position
                | None -> 
                    // Nothing to do if there is no change
                    ()

            finally
                transaction.Complete()

        if _broker.IsCompletionActive || _broker.IsSignatureHelpActive || _broker.IsQuickInfoActive then
            _broker.DismissDisplayWindows()
            _operations.MoveCaretLeft 1 
            ProcessResult.SwitchMode ModeKind.Normal

        else
            _operations.MoveCaretLeft 1 
            ProcessResult.SwitchMode ModeKind.Normal

    interface IMode with 
        member x.VimBuffer = _data
        member x.CommandNames =  _commandMap |> Seq.map (fun p -> p.Key) |> Seq.map OneKeyInput
        member x.ModeKind = if _isReplace then ModeKind.Replace else ModeKind.Insert
        member x.CanProcess ki = Map.containsKey ki _commandMap 
        member x.Process (ki : KeyInput) = 
            match Map.tryFind ki _commandMap with
            | Some(func) -> func()
            | None -> Processed
        member x.OnEnter arg = 

            // On enter we need to check the 'count' and possibly set up a transaction to 
            // lump edits and their repeats together
            _transaction <-
                match arg with
                | ModeArgument.InsertWithCount count ->
                    if count > 1 then
                        let transaction = _undoRedoOperations.CreateUndoTransaction "Insert"
                        Some (transaction, count)
                    else
                        None
                | _ -> 
                    None

            // If this is replace mode then go ahead and setup overwrite
            if _isReplace then
                _editorOptions.SetOptionValue(DefaultTextViewOptions.OverwriteModeId, true)
        member x.OnLeave () = 

            // Ensure the transaction is complete and None'd out.  We can get here with an active
            // transaction if we leave Insert mode via an API call or possibly if an exception
            // happens during processing of the transaction
            try
                match _transaction with
                | None -> ()
                | Some (transaction, _) -> transaction.Complete()
            finally
                _transaction <- None

            // If this is replace mode then go ahead and undo overwrite
            if _isReplace then
                _editorOptions.SetOptionValue(DefaultTextViewOptions.OverwriteModeId, false)
        member x.OnClose() = ()
