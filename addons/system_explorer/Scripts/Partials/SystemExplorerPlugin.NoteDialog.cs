#if TOOLS
using Godot;
using System;

public partial class SystemExplorerPlugin
{
	private static readonly Vector2I NoteDialogMinimumSize = new(740, 540);
	private static readonly Vector2 NoteTextEditMinimumSize = new(700, 420);

	private AcceptDialog _noteDialog;
	private TextEdit _noteTextEdit;
	private Button _noteSaveButton;
	private Button _noteCancelButton;
	private string _activeNoteMetadata = "";

	private void CreateNoteDialogUi()
	{
		_noteDialog = new AcceptDialog
		{
			Title = "Note",
			MinSize = NoteDialogMinimumSize,
			DialogHideOnOk = false,
		};

		Button defaultOkButton = _noteDialog.GetOkButton();
		if (defaultOkButton != null)
			defaultOkButton.Visible = false;

		var content = new VBoxContainer
		{
			Name = "Note Content",
			CustomMinimumSize = new Vector2(700, 470),
			SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
			SizeFlagsVertical = Control.SizeFlags.ExpandFill,
		};

		_noteTextEdit = new TextEdit
		{
			Name = "Note Text",
			PlaceholderText = "Write a note...",
			CaretBlink = true,
			CustomMinimumSize = NoteTextEditMinimumSize,
			SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
			SizeFlagsVertical = Control.SizeFlags.ExpandFill,
			WrapMode = TextEdit.LineWrappingMode.Boundary,
			AutowrapMode = TextServer.AutowrapMode.WordSmart,
		};

		var buttonRow = new HBoxContainer
		{
			Name = "Note Actions",
			SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
		};

		_noteSaveButton = new Button
		{
			Name = "Save Note",
			Text = "Save",
		};

		var spacer = new Control
		{
			Name = "Note Action Spacer",
			SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
		};

		_noteCancelButton = new Button
		{
			Name = "Cancel Note",
			Text = "Cancel",
		};

		buttonRow.AddChild(_noteSaveButton);
		buttonRow.AddChild(spacer);
		buttonRow.AddChild(_noteCancelButton);
		content.AddChild(_noteTextEdit);
		content.AddChild(buttonRow);
		_noteDialog.AddChild(content);
	}

	private bool AreNoteDialogSignalSourcesValid(out string failureDetail)
	{
		failureDetail = "";

		if (!IsValidGodotObject(_noteDialog))
		{
			failureDetail = "The Note dialog is unavailable.";
			return false;
		}

		if (!IsValidGodotObject(_noteTextEdit))
		{
			failureDetail = "The Note TextEdit is unavailable.";
			return false;
		}

		if (!IsValidGodotObject(_noteSaveButton))
		{
			failureDetail = "The Note Save button is unavailable.";
			return false;
		}

		if (!IsValidGodotObject(_noteCancelButton))
		{
			failureDetail = "The Note Cancel button is unavailable.";
			return false;
		}

		return true;
	}

	private bool ConnectNoteDialogSignals()
	{
		if (!AreNoteDialogSignalSourcesValid(out string failureDetail))
		{
			DebugLogger.LogOperation("Note dialog signal connection failed", failureDetail);
			return false;
		}

		bool connected = true;
		connected &= TryConnectPluginSignal(
			_noteSaveButton,
			Button.SignalName.Pressed,
			nameof(OnNoteSavePressedSignal),
			nameof(_noteSaveButton)
		);
		connected &= TryConnectPluginSignal(
			_noteCancelButton,
			Button.SignalName.Pressed,
			nameof(OnNoteCancelPressedSignal),
			nameof(_noteCancelButton)
		);
		connected &= TryConnectPluginSignal(
			_noteDialog,
			AcceptDialog.SignalName.Canceled,
			nameof(OnNoteDialogCanceledSignal),
			nameof(_noteDialog)
		);

		return connected;
	}

	private void DisconnectNoteDialogSignals()
	{
		DisconnectPluginSignal(
			_noteSaveButton,
			Button.SignalName.Pressed,
			nameof(OnNoteSavePressedSignal),
			nameof(_noteSaveButton)
		);
		DisconnectPluginSignal(
			_noteCancelButton,
			Button.SignalName.Pressed,
			nameof(OnNoteCancelPressedSignal),
			nameof(_noteCancelButton)
		);
		DisconnectPluginSignal(
			_noteDialog,
			AcceptDialog.SignalName.Canceled,
			nameof(OnNoteDialogCanceledSignal),
			nameof(_noteDialog)
		);
	}

	private bool VerifyNoteDialogSignals()
	{
		return AreNoteDialogSignalSourcesValid(out _)
			&& IsPluginSignalConnected(
				_noteSaveButton,
				Button.SignalName.Pressed,
				nameof(OnNoteSavePressedSignal)
			)
			&& IsPluginSignalConnected(
				_noteCancelButton,
				Button.SignalName.Pressed,
				nameof(OnNoteCancelPressedSignal)
			)
			&& IsPluginSignalConnected(
				_noteDialog,
				AcceptDialog.SignalName.Canceled,
				nameof(OnNoteDialogCanceledSignal)
			);
	}

	private void ClearNoteDialogControlReferences()
	{
		_activeNoteMetadata = "";
		_noteDialog = null;
		_noteTextEdit = null;
		_noteSaveButton = null;
		_noteCancelButton = null;
	}

	private void ResetNoteDialogTransientStateAfterManagedAssemblyReload()
	{
		HideNoteDialogWindowsBestEffort();
		ClearActiveNoteState();
	}

	private void ResetNoteDialogTransientStateForTeardown()
	{
		HideNoteDialogWindowsBestEffort();
		ClearActiveNoteState();
	}

	private void HideNoteDialogWindowsBestEffort()
	{
		if (IsValidGodotObject(_noteDialog))
			_noteDialog.Hide();
	}

	private void OpenPendingNoteDialog()
	{
		string metadata = _pendingNoteMetadata;
		if (
			string.IsNullOrWhiteSpace(metadata)
			|| (_pendingContextNoteState != PendingContextNoteState.Missing
				&& _pendingContextNoteState != PendingContextNoteState.Exists)
		)
		{
			return;
		}

		if (!TryReadNoteForMetadata(metadata, out bool exists, out string text, out string failureDetail))
		{
			PushSystemExplorerWarning(
				"System Explorer could not open this Note.",
				mirrorToDebugLog: false
			);
			DebugLogger.LogOperation("Note open failed", failureDetail ?? "");
			return;
		}

		if (!AreNoteDialogSignalSourcesValid(out string uiFailureDetail))
		{
			PushSystemExplorerWarning(
				"System Explorer could not open this Note.",
				mirrorToDebugLog: false
			);
			DebugLogger.LogOperation("Note open failed: UI unavailable", uiFailureDetail);
			return;
		}

		_activeNoteMetadata = metadata;
		_noteDialog.Title = BuildNoteDialogTitle(metadata);
		_noteTextEdit.Text = exists ? text : "";
		_noteSaveButton.Visible = true;
		_noteCancelButton.Visible = true;
		_noteDialog.PopupCentered(NoteDialogMinimumSize);
		_noteTextEdit.GrabFocus(true);
	}

	private static string BuildNoteDialogTitle(string metadata)
	{
		string systemName = GetSystemNameFromMetadata(metadata);

		if (metadata.StartsWith("folder::", StringComparison.Ordinal))
		{
			string folderPath = GetFolderPathFromMetadata(metadata);
			return $"Note — {systemName} / {folderPath}";
		}

		return $"Note — {systemName}";
	}

	private void OnNoteSavePressedSignal()
	{
		if (EnsureManagedAssemblyStateCurrent("Save Note"))
			SaveActiveNote();
	}

	private void OnNoteCancelPressedSignal()
	{
		if (EnsureManagedAssemblyStateCurrent("Cancel Note"))
			CloseNoteDialogWithoutMutation();
	}

	private void OnNoteDialogCanceledSignal()
	{
		if (EnsureManagedAssemblyStateCurrent("Close Note Dialog"))
			CloseNoteDialogWithoutMutation();
	}

	private void SaveActiveNote()
	{
		if (
			string.IsNullOrWhiteSpace(_activeNoteMetadata)
			|| !IsValidGodotObject(_noteTextEdit)
			|| !IsValidGodotObject(_noteDialog)
			|| !_noteDialog.Visible
		)
		{
			return;
		}

		string exactText = _noteTextEdit.Text;

		if (!TrySaveNoteForMetadata(_activeNoteMetadata, exactText, out string failureDetail))
		{
			PushSystemExplorerWarning(
				"System Explorer could not save this Note.",
				mirrorToDebugLog: false
			);
			DebugLogger.LogOperation("Note save failed", failureDetail ?? "");
			_noteTextEdit.GrabFocus(true);
			return;
		}

		string savedMetadata = _activeNoteMetadata;
		bool hasNote = !string.IsNullOrWhiteSpace(exactText);

		if (!TrySynchronizeNotePresenceForMetadata(savedMetadata, hasNote, out bool presenceChanged))
		{
			DebugLogger.LogOperation(
				"Note content saved but tree presence could not be persisted",
				$"Metadata='{savedMetadata}', HasNote={hasNote}"
			);
		}
		else if (presenceChanged)
		{
			DebugLogger.LogOperation(
				"Note tree presence synchronized",
				$"Metadata='{savedMetadata}', HasNote={hasNote}"
			);
		}

		CloseNoteDialogAfterSuccessfulMutation();
	}

	private void CloseNoteDialogWithoutMutation()
	{
		HideNoteDialogWindowsBestEffort();
		ClearActiveNoteState();
	}

	private void CloseNoteDialogAfterSuccessfulMutation()
	{
		HideNoteDialogWindowsBestEffort();
		ClearActiveNoteState();
	}

	private void ClearActiveNoteState()
	{
		_activeNoteMetadata = "";

		if (IsValidGodotObject(_noteTextEdit))
			_noteTextEdit.Text = "";

		if (IsValidGodotObject(_noteSaveButton))
			_noteSaveButton.Visible = true;

		if (IsValidGodotObject(_noteCancelButton))
			_noteCancelButton.Visible = true;

		if (IsValidGodotObject(_noteDialog))
			_noteDialog.Title = "Note";
	}
}
#endif
