#if TOOLS
using Godot;
using System;
using SystemExplorer.Notes;

public partial class SystemExplorerPlugin
{
	private static readonly Vector2I NoteDialogMinimumSize = new(740, 540);
	private static readonly Vector2 NoteTextEditMinimumSize = new(700, 420);
	private const string NoteMinimizedRestoreButtonName =
		"SystemExplorerNoteMinimizedRestoreButton";
	private const string NoteMinimizedRestoreButtonText = "\U0001F4DD";

	private AcceptDialog _noteDialog;
	private TextEdit _noteTextEdit;
	private string _pendingNoteDialogOpenMetadata = "";
	private bool _noteDialogOpenQueued;
	private string _activeNoteMetadata = "";
	private string _noteSaveSuppressedRemovedTargetMetadata = "";
	private int _activeNoteFontSize;
	private bool _noteFontSizeDirty;
	private Vector2I _openedNoteDialogSize;
	private Vector2I _openedNoteDialogPosition;
	private bool _hasOpenedNoteDialogGeometryBaseline;
	private bool _awaitingOpenedNoteDialogWindowedGeometryBaseline;
	private bool _openedNoteDialogEmbeddedSubwindow;
	private bool _openedNoteDialogMaximized;
	private bool _hasOpenedNoteDialogMaximizedBaseline;
	private Window.ModeEnum _lastNonMinimizedNoteDialogMode;
	private bool _hasLastNonMinimizedNoteDialogMode;
	private bool _noteMinimizedRestoreObservationFaulted;
	private bool _noteBackgroundRestoreGeometryUnavailable;
	private bool _noteCloseSaveRecoveryQueued;
	private long _noteDialogSessionEpoch;
	private Button _noteMinimizedRestoreButton;
	private HBoxContainer _noteMinimizedRestoreStatusBar;
	private Control _noteMinimizedRestoreBaseEditor;

	private void CreateNoteDialogUi()
	{
		_noteDialog = new AcceptDialog
		{
			Title = "Note",
			MinSize = NoteDialogMinimumSize,
			DialogHideOnOk = false,
			Exclusive = false,
			MinimizeDisabled = false,
			MaximizeDisabled = false,
		};

		if (!TryCreateNoteDialogPanelStyle(out StyleBox notePanelStyle, out string panelFailureDetail))
		{
			DebugLogger.LogOperation(
				"Note dialog UI creation failed",
				panelFailureDetail
			);
			return;
		}

		_noteDialog.AddThemeStyleboxOverride("panel", notePanelStyle);
		_noteDialog.AddThemeConstantOverride("buttons_separation", 0);

		Button defaultOkButton = _noteDialog.GetOkButton();
		if (defaultOkButton == null)
		{
			DebugLogger.LogOperation(
				"Note dialog UI creation failed",
				"AcceptDialog.GetOkButton() returned null; the built-in OK action cannot be hidden."
			);
			return;
		}

		defaultOkButton.Visible = false;

		if (
			!TryCreateNoteTextEditNormalStyle(
				NoteTextLeftPaddingDefault,
				out StyleBox noteTextEditNormalStyle,
				out string textEditStyleFailureDetail
			)
		)
		{
			DebugLogger.LogOperation(
				"Note dialog UI creation failed",
				textEditStyleFailureDetail
			);
			return;
		}

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

		_noteTextEdit.AddThemeStyleboxOverride("normal", noteTextEditNormalStyle);

		// Keep the Note editor fully focusable while suppressing Godot's
		// editor-theme focus outline. In particular, opening TextEdit's
		// context menu with right-click must not leave a blue frame around
		// the Note field.
		_noteTextEdit.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());

		_noteDialog.AddChild(_noteTextEdit);
	}

	private static bool TryCreateNoteDialogPanelStyle(
		out StyleBox notePanelStyle,
		out string failureDetail
	)
	{
		notePanelStyle = null;
		failureDetail = "";

		try
		{
			Control editorBaseControl = EditorInterface.Singleton?.GetBaseControl();
			if (!IsValidGodotObject(editorBaseControl))
			{
				failureDetail =
					"EditorInterface base control is unavailable; the Note AcceptDialog panel style cannot be resolved.";
				return false;
			}

			if (!editorBaseControl.HasThemeStylebox("panel", "AcceptDialog"))
			{
				failureDetail =
					"The editor theme does not expose the AcceptDialog panel StyleBox.";
				return false;
			}

			StyleBox sourcePanelStyle = editorBaseControl.GetThemeStylebox(
				"panel",
				"AcceptDialog"
			);
			if (!IsValidGodotObject(sourcePanelStyle))
			{
				failureDetail =
					"The editor-theme AcceptDialog panel StyleBox is unavailable.";
				return false;
			}

			if (sourcePanelStyle.Duplicate() is not StyleBox duplicatedPanelStyle
				|| !IsValidGodotObject(duplicatedPanelStyle))
			{
				failureDetail =
					"The editor-theme AcceptDialog panel StyleBox could not be duplicated for the Note dialog.";
				return false;
			}

			duplicatedPanelStyle.SetContentMargin(Side.Left, 0.0f);
			duplicatedPanelStyle.SetContentMargin(Side.Top, 0.0f);
			duplicatedPanelStyle.SetContentMargin(Side.Right, 0.0f);
			duplicatedPanelStyle.SetContentMargin(Side.Bottom, 0.0f);

			notePanelStyle = duplicatedPanelStyle;
			return true;
		}
		catch (Exception exception)
		{
			failureDetail =
				"Resolving or duplicating the editor-theme AcceptDialog panel StyleBox threw: "
				+ exception.Message;
			return false;
		}
	}

	private static bool TryCreateNoteTextEditNormalStyle(
		float leftPadding,
		out StyleBox noteTextEditNormalStyle,
		out string failureDetail
	)
	{
		noteTextEditNormalStyle = null;
		failureDetail = "";

		try
		{
			Control editorBaseControl = EditorInterface.Singleton?.GetBaseControl();
			if (!IsValidGodotObject(editorBaseControl))
			{
				failureDetail =
					"EditorInterface base control is unavailable; the Note TextEdit normal style cannot be resolved.";
				return false;
			}

			if (!editorBaseControl.HasThemeStylebox("normal", "TextEdit"))
			{
				failureDetail =
					"The editor theme does not expose the TextEdit normal StyleBox.";
				return false;
			}

			StyleBox sourceNormalStyle = editorBaseControl.GetThemeStylebox(
				"normal",
				"TextEdit"
			);
			if (!IsValidGodotObject(sourceNormalStyle))
			{
				failureDetail =
					"The editor-theme TextEdit normal StyleBox is unavailable.";
				return false;
			}

			if (sourceNormalStyle.Duplicate() is not StyleBox duplicatedNormalStyle
				|| !IsValidGodotObject(duplicatedNormalStyle))
			{
				failureDetail =
					"The editor-theme TextEdit normal StyleBox could not be duplicated for the Note editor.";
				return false;
			}

			duplicatedNormalStyle.SetContentMargin(
				Side.Left,
				leftPadding
			);

			noteTextEditNormalStyle = duplicatedNormalStyle;
			return true;
		}
		catch (Exception exception)
		{
			failureDetail =
				"Resolving or duplicating the editor-theme TextEdit normal StyleBox threw: "
				+ exception.Message;
			return false;
		}
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
			_noteTextEdit,
			Control.SignalName.GuiInput,
			nameof(OnNoteTextEditGuiInputSignal),
			nameof(_noteTextEdit)
		);
		connected &= TryConnectPluginSignal(
			_noteDialog,
			AcceptDialog.SignalName.Canceled,
			nameof(OnNoteDialogCanceledSignal),
			nameof(_noteDialog)
		);
		connected &= TryConnectPluginSignal(
			_noteDialog,
			Viewport.SignalName.SizeChanged,
			nameof(OnNoteDialogSizeChangedSignal),
			nameof(_noteDialog)
		);

		return connected;
	}

	private void DisconnectNoteDialogSignals()
	{
		DisconnectPluginSignal(
			_noteTextEdit,
			Control.SignalName.GuiInput,
			nameof(OnNoteTextEditGuiInputSignal),
			nameof(_noteTextEdit)
		);
		DisconnectPluginSignal(
			_noteDialog,
			AcceptDialog.SignalName.Canceled,
			nameof(OnNoteDialogCanceledSignal),
			nameof(_noteDialog)
		);
		DisconnectPluginSignal(
			_noteDialog,
			Viewport.SignalName.SizeChanged,
			nameof(OnNoteDialogSizeChangedSignal),
			nameof(_noteDialog)
		);
	}

	private bool VerifyNoteDialogSignals()
	{
		return AreNoteDialogSignalSourcesValid(out _)
			&& IsPluginSignalConnected(
				_noteTextEdit,
				Control.SignalName.GuiInput,
				nameof(OnNoteTextEditGuiInputSignal)
			)
			&& IsPluginSignalConnected(
				_noteDialog,
				AcceptDialog.SignalName.Canceled,
				nameof(OnNoteDialogCanceledSignal)
			)
			&& IsPluginSignalConnected(
				_noteDialog,
				Viewport.SignalName.SizeChanged,
				nameof(OnNoteDialogSizeChangedSignal)
			);
	}

	private void ClearNoteDialogControlReferences()
	{
		ClearPendingNoteDialogOpenState();
		ClearNoteMinimizedRestoreSessionState();
		_noteCloseSaveRecoveryQueued = false;
		_activeNoteMetadata = "";
		_noteSaveSuppressedRemovedTargetMetadata = "";
		_activeNoteFontSize = 0;
		_noteFontSizeDirty = false;
		ClearOpenedNoteDialogWindowStateBaseline();
		_noteDialog = null;
		_noteTextEdit = null;
	}

	private void ResetNoteDialogTransientStateAfterManagedAssemblyReload()
	{
		HideNoteDialogWindowsBestEffort();
		ClearPendingNoteDialogOpenState();
		ClearActiveNoteState();
		RefreshEditorPluginProcessingState();
	}

	private void ResetNoteDialogTransientStateForTeardown()
	{
		HideNoteDialogWindowsBestEffort();
		ClearPendingNoteDialogOpenState();
		ClearActiveNoteState();
		RefreshEditorPluginProcessingState();
	}

	private void HideNoteDialogWindowsBestEffort()
	{
		if (IsValidGodotObject(_noteDialog))
			_noteDialog.Hide();
	}

	private bool IsNoteDialogSessionActive()
	{
		return !string.IsNullOrWhiteSpace(_activeNoteMetadata);
	}

	private bool HasActiveNoteDialogWindowObservationProcessWork()
	{
		return IsNoteDialogSessionActive()
			&& !_openedNoteDialogEmbeddedSubwindow
			&& !_noteMinimizedRestoreObservationFaulted;
	}

	private void ProcessActiveNoteDialogWindowObservation()
	{
		if (
			!IsNoteDialogSessionActive()
			|| _openedNoteDialogEmbeddedSubwindow
			|| _noteMinimizedRestoreObservationFaulted
		)
		{
			HideNoteMinimizedRestoreButton();
			return;
		}

		if (!IsValidGodotObject(_noteDialog))
		{
			FaultNoteMinimizedRestoreObservation(
				"Note window observation stopped because the active dialog is unavailable",
				""
			);
			return;
		}

		if (!TryReadNoteDialogMode(out Window.ModeEnum currentMode))
		{
			FaultNoteMinimizedRestoreObservation();
			return;
		}

		if (
			currentMode == Window.ModeEnum.Windowed
			|| currentMode == Window.ModeEnum.Maximized
		)
		{
			_lastNonMinimizedNoteDialogMode = currentMode;
			_hasLastNonMinimizedNoteDialogMode = true;

			if (!TryReadNoteDialogHasFocus(out bool hasFocus))
			{
				FaultNoteMinimizedRestoreObservation();
				return;
			}

			if (hasFocus)
			{
				HideNoteMinimizedRestoreButton();
				return;
			}

			if (
				TryShouldShowBackgroundNoteRestoreButton(out bool shouldShow)
				&& shouldShow
			)
			{
				ShowNoteMinimizedRestoreButtonForCurrentScriptEditor();
				return;
			}

			HideNoteMinimizedRestoreButton();
			return;
		}

		if (currentMode == Window.ModeEnum.Minimized)
		{
			ShowNoteMinimizedRestoreButtonForCurrentScriptEditor();
			return;
		}

		HideNoteMinimizedRestoreButton();
	}

	private void QueuePendingNoteDialogOpen()
	{
		if (IsNoteDialogSessionActive() || _noteDialogOpenQueued)
			return;

		string metadata = _pendingNoteMetadata;
		if (
			string.IsNullOrWhiteSpace(metadata)
			|| (_pendingContextNoteState != PendingContextNoteState.Missing
				&& _pendingContextNoteState != PendingContextNoteState.Exists)
		)
		{
			return;
		}

		_pendingNoteDialogOpenMetadata = metadata;
		_noteDialogOpenQueued = true;
		RefreshEditorPluginProcessingState();
	}

	private bool HasPendingNoteDialogOpenProcessWork()
	{
		return _noteDialogOpenQueued;
	}

	private void ProcessPendingNoteDialogOpen()
	{
		if (!_noteDialogOpenQueued)
			return;

		string metadata = _pendingNoteDialogOpenMetadata;
		ClearPendingNoteDialogOpenState();

		if (
			IsNoteDialogSessionActive()
			|| string.IsNullOrWhiteSpace(metadata)
			|| !IsValidGodotObject(this)
		)
		{
			return;
		}

		OpenNoteDialogFromProcess(metadata);
	}

	private void ClearPendingNoteDialogOpenState()
	{
		_pendingNoteDialogOpenMetadata = "";
		_noteDialogOpenQueued = false;
	}

	private void DiscardNoteSessionForRemovedStructure(string removedMetadata)
	{
		bool discardedPendingOpen =
			_noteDialogOpenQueued
			&& IsNoteTargetRemovedByStructure(
				_pendingNoteDialogOpenMetadata,
				removedMetadata
			);

		if (discardedPendingOpen)
			ClearPendingNoteDialogOpenState();

		bool discardedActiveSession =
			!string.IsNullOrWhiteSpace(_activeNoteMetadata)
			&& IsNoteTargetRemovedByStructure(_activeNoteMetadata, removedMetadata);

		if (!discardedActiveSession)
		{
			if (discardedPendingOpen)
			{
				DebugLogger.LogOperation(
					"Queued Note open discarded because its structure target was removed",
					$"RemovedMetadata='{removedMetadata}'"
				);
				RefreshEditorPluginProcessingState();
			}

			return;
		}

		string discardedMetadata = _activeNoteMetadata;
		_noteSaveSuppressedRemovedTargetMetadata = discardedMetadata;

		HideNoteDialogWindowsBestEffort();
		ClearActiveNoteState();

		DebugLogger.LogOperation(
			"Active Note session discarded because its structure target was removed",
			$"NoteMetadata='{discardedMetadata}', RemovedMetadata='{removedMetadata}', PendingOpenDiscarded={discardedPendingOpen}"
		);
	}

	private void RetargetNoteSessionForRenamedStructure(
		string oldStructureMetadata,
		string newStructureMetadata
	)
	{
		if (
			_noteDialogOpenQueued
			&& TryGetRenamedNoteMetadata(
				_pendingNoteDialogOpenMetadata,
				oldStructureMetadata,
				newStructureMetadata,
				out string renamedPendingMetadata
			)
		)
		{
			_pendingNoteDialogOpenMetadata = renamedPendingMetadata;
		}

		if (
			string.IsNullOrWhiteSpace(_activeNoteMetadata)
			|| !TryGetRenamedNoteMetadata(
				_activeNoteMetadata,
				oldStructureMetadata,
				newStructureMetadata,
				out string renamedActiveMetadata
			)
		)
		{
			return;
		}

		_activeNoteMetadata = renamedActiveMetadata;

		if (!IsValidGodotObject(_noteDialog))
			return;

		try
		{
			_noteDialog.Title = BuildNoteDialogTitle(renamedActiveMetadata);
		}
		catch (Exception exception)
		{
			DebugLogger.LogOperation(
				"Note dialog title retarget skipped after successful rename",
				$"OldStructureMetadata='{oldStructureMetadata}', NewStructureMetadata='{newStructureMetadata}', NoteMetadata='{renamedActiveMetadata}', Exception='{exception.Message}'"
			);
		}
	}

	private void OpenNoteDialogFromProcess(string metadata)
	{
		if (IsNoteDialogSessionActive() || string.IsNullOrWhiteSpace(metadata))
			return;

		if (
			!TryReadNoteForMetadataWithViewState(
				metadata,
				out bool exists,
				out string text,
				out NoteViewState viewState,
				out string failureDetail
			)
		)
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

		NoteEditorSettings editorSettings = ReadNoteEditorSettingsForOpen();
		PrepareNoteTextLeftPaddingForOpen(editorSettings);
		PrepareNoteFontSizeStateForOpen(editorSettings);

		unchecked
		{
			_noteDialogSessionEpoch++;
		}

		_noteSaveSuppressedRemovedTargetMetadata = "";
		_activeNoteMetadata = metadata;
		_noteDialog.Title = BuildNoteDialogTitle(metadata);
		_noteTextEdit.Text = exists ? text : "";

		bool isEmbeddedSubwindow = IsNoteDialogEmbeddedSubwindow();
		bool openedWithPreconfiguredMaximizedMode =
			PrepareOpenedNoteDialogWindowStateForOpen(
				editorSettings,
				isEmbeddedSubwindow
			);

		Vector2I targetWindowSize = editorSettings.HasWindowSize
			? editorSettings.WindowSize
			: NoteDialogMinimumSize;

		PopupNoteDialogWithEditorSettings(editorSettings, targetWindowSize);
		FinalizeOpenedNoteDialogWindowStateAfterPopup(
			editorSettings,
			isEmbeddedSubwindow,
			openedWithPreconfiguredMaximizedMode
		);
		_noteTextEdit.GrabFocus(true);
		RestoreNoteCaretState(viewState);
	}

	private NoteEditorSettings ReadNoteEditorSettingsForOpen()
	{
		if (
			TryReadGlobalNoteEditorSettings(
				out NoteEditorSettings editorSettings,
				out string settingsFailureDetail
			)
		)
		{
			return editorSettings;
		}

		DebugLogger.LogOperation(
			"Note editor settings read failed; using runtime defaults",
			settingsFailureDetail ?? ""
		);
		return new NoteEditorSettings();
	}

	private void PrepareNoteTextLeftPaddingForOpen(NoteEditorSettings editorSettings)
	{
		if (
			!TryCreateNoteTextEditNormalStyle(
				editorSettings.TextLeftPadding,
				out StyleBox noteTextEditNormalStyle,
				out string failureDetail
			)
		)
		{
			DebugLogger.LogOperation(
				"Note text-left-padding style apply failed",
				failureDetail ?? ""
			);
			return;
		}

		try
		{
			_noteTextEdit.AddThemeStyleboxOverride("normal", noteTextEditNormalStyle);
		}
		catch (Exception exception)
		{
			DebugLogger.LogOperation(
				"Note text-left-padding style apply failed",
				$"Padding={editorSettings.TextLeftPadding}, Exception='{exception}'"
			);
		}
	}

	private void PrepareNoteFontSizeStateForOpen(NoteEditorSettings editorSettings)
	{
		_noteFontSizeDirty = false;
		_activeNoteFontSize = NoteFontSizeFallback;

		try
		{
			_noteTextEdit.RemoveThemeFontSizeOverride("font_size");
		}
		catch (Exception exception)
		{
			DebugLogger.LogOperation(
				"Note font-size override reset failed",
				$"Exception='{exception}'"
			);
		}

		if (editorSettings.HasFontSize)
		{
			_activeNoteFontSize = editorSettings.FontSize;
			_noteTextEdit.AddThemeFontSizeOverride("font_size", _activeNoteFontSize);
			return;
		}

		try
		{
			int effectiveThemeFontSize = _noteTextEdit.GetThemeFontSize("font_size");
			if (effectiveThemeFontSize > 0)
			{
				_activeNoteFontSize = Math.Clamp(
					effectiveThemeFontSize,
					NoteFontSizeMinimum,
					NoteFontSizeMaximum
				);
			}
		}
		catch (Exception exception)
		{
			DebugLogger.LogOperation(
				"Note theme font-size lookup failed; using internal fallback",
				$"Fallback={NoteFontSizeFallback}, Exception='{exception}'"
			);
		}
	}

	private void PopupNoteDialogWithEditorSettings(
		NoteEditorSettings editorSettings,
		Vector2I targetWindowSize
	)
	{
		if (editorSettings.CenterPosition)
		{
			_noteDialog.PopupCentered(targetWindowSize);
			return;
		}

		if (!editorSettings.HasPosition)
		{
			_noteDialog.PopupCentered(targetWindowSize);
			return;
		}

		Rect2I persistedWindowRect = new(editorSettings.Position, targetWindowSize);
		if (!IsPersistedNoteDialogRectUsable(persistedWindowRect))
		{
			DebugLogger.LogOperation(
				"Persisted Note window position is off-screen; using centered fallback",
				$"Rect='{persistedWindowRect}'"
			);
			_noteDialog.PopupCentered(targetWindowSize);
			return;
		}

		_noteDialog.Popup(persistedWindowRect);
	}

	private bool IsPersistedNoteDialogRectUsable(Rect2I persistedWindowRect)
	{
		if (IsNoteDialogEmbeddedSubwindow())
			return true;

		try
		{
			int screenCount = DisplayServer.GetScreenCount();
			if (screenCount <= 0)
			{
				DebugLogger.LogOperation(
					"Note window screen validation unavailable",
					$"ScreenCount={screenCount}"
				);
				return false;
			}

			for (int screenIndex = 0; screenIndex < screenCount; screenIndex++)
			{
				Rect2I usableScreenRect = DisplayServer.ScreenGetUsableRect(screenIndex);
				if (
					usableScreenRect.Size.X > 0
					&& usableScreenRect.Size.Y > 0
					&& persistedWindowRect.Intersects(usableScreenRect)
				)
				{
					return true;
				}
			}

			return false;
		}
		catch (Exception exception)
		{
			DebugLogger.LogOperation(
				"Note window screen validation failed",
				$"Rect='{persistedWindowRect}', Exception='{exception}'"
			);
			return false;
		}
	}

	private bool IsNoteDialogEmbeddedSubwindow()
	{
		try
		{
			return _noteDialog.IsEmbedded();
		}
		catch (Exception exception)
		{
			DebugLogger.LogOperation(
				"Note window embedding state lookup failed; treating dialog as embedded",
				$"Exception='{exception}'"
			);
			return true;
		}
	}

	private bool PrepareOpenedNoteDialogWindowStateForOpen(
		NoteEditorSettings editorSettings,
		bool isEmbeddedSubwindow
	)
	{
		ClearOpenedNoteDialogWindowStateBaseline();
		_openedNoteDialogEmbeddedSubwindow = isEmbeddedSubwindow;

		if (isEmbeddedSubwindow)
			return false;

		Window.ModeEnum initialMode = editorSettings.Maximized
			? Window.ModeEnum.Maximized
			: Window.ModeEnum.Windowed;

		bool modeConfigured = TrySetNoteDialogMode(
			initialMode,
			"Note initial window mode preconfiguration failed before open"
		);

		if (!editorSettings.Maximized || !modeConfigured)
			return false;

		return TryReadNoteDialogMode(out Window.ModeEnum configuredMode)
			&& configuredMode == Window.ModeEnum.Maximized;
	}

	private void FinalizeOpenedNoteDialogWindowStateAfterPopup(
		NoteEditorSettings editorSettings,
		bool isEmbeddedSubwindow,
		bool openedWithPreconfiguredMaximizedMode
	)
	{
		if (isEmbeddedSubwindow)
		{
			CaptureOpenedNoteDialogGeometryBaseline(isEmbeddedSubwindow: true);
			CaptureOpenedNoteDialogMaximizedBaseline(isEmbeddedSubwindow: true);
			return;
		}

		if (!TryReadNoteDialogMode(out Window.ModeEnum currentMode))
			return;

		if (currentMode == Window.ModeEnum.Windowed)
		{
			CaptureOpenedNoteDialogGeometryBaseline(isEmbeddedSubwindow: false);
			_awaitingOpenedNoteDialogWindowedGeometryBaseline = false;

			if (editorSettings.Maximized && openedWithPreconfiguredMaximizedMode)
			{
				DebugLogger.LogOperation(
					"Preconfigured Note maximized mode did not survive popup",
					$"ActualMode='{currentMode}'"
				);
			}
		}
		else if (currentMode == Window.ModeEnum.Maximized)
		{
			ClearOpenedNoteDialogGeometryBaseline();
			_awaitingOpenedNoteDialogWindowedGeometryBaseline = true;
		}
		else
		{
			ClearOpenedNoteDialogGeometryBaseline();
			_awaitingOpenedNoteDialogWindowedGeometryBaseline = false;
			DebugLogger.LogOperation(
				"Note post-popup window-state finalization skipped",
				$"Unsupported current Mode='{currentMode}'."
			);
		}

		CaptureOpenedNoteDialogMaximizedBaseline(isEmbeddedSubwindow: false);
	}

	private bool TrySetNoteDialogMode(Window.ModeEnum mode, string operation)
	{
		try
		{
			_noteDialog.Mode = mode;
			return true;
		}
		catch (Exception exception)
		{
			DebugLogger.LogOperation(
				operation,
				$"Mode='{mode}', Exception='{exception}'"
			);
			return false;
		}
	}

	private bool TryReadNoteDialogMode(out Window.ModeEnum mode)
	{
		mode = Window.ModeEnum.Windowed;

		try
		{
			mode = _noteDialog.Mode;
			return true;
		}
		catch (Exception exception)
		{
			DebugLogger.LogOperation(
				"Note window mode lookup failed",
				$"Exception='{exception}'"
			);
			return false;
		}
	}

	private bool TryReadNoteDialogHasFocus(out bool hasFocus)
	{
		hasFocus = false;

		try
		{
			hasFocus = _noteDialog.HasFocus();
			return true;
		}
		catch (Exception exception)
		{
			DebugLogger.LogOperation(
				"Note window focus lookup failed",
				$"Exception='{exception}'"
			);
			return false;
		}
	}

	private bool TryShouldShowBackgroundNoteRestoreButton(out bool shouldShow)
	{
		shouldShow = false;

		if (_noteBackgroundRestoreGeometryUnavailable)
			return true;

		if (
			!TryGetCurrentNoteMinimizedRestoreBaseEditor(
				out _,
				out TextEdit baseEditor,
				out _
			)
		)
		{
			return false;
		}

		Window editorHostWindow;
		try
		{
			editorHostWindow = baseEditor.GetWindow();
		}
		catch
		{
			return false;
		}

		if (
			!IsValidGodotObject(editorHostWindow)
			|| editorHostWindow.IsQueuedForDeletion()
			|| !editorHostWindow.IsInsideTree()
		)
		{
			return false;
		}

		if (
			!TryReadNativeWindowRect(
				_noteDialog,
				out Rect2I noteWindowRect,
				out string noteFailureDetail
			)
		)
		{
			FaultNoteBackgroundRestoreGeometry(
				"Note",
				noteFailureDetail
			);
			return false;
		}

		if (
			!TryReadNativeWindowRect(
				editorHostWindow,
				out Rect2I editorWindowRect,
				out string editorFailureDetail
			)
		)
		{
			FaultNoteBackgroundRestoreGeometry(
				"ScriptEditorHost",
				editorFailureDetail
			);
			return false;
		}

		shouldShow = noteWindowRect.Intersects(editorWindowRect);
		return true;
	}

	private static bool TryReadNativeWindowRect(
		Window window,
		out Rect2I windowRect,
		out string failureDetail
	)
	{
		windowRect = default;
		failureDetail = "";

		if (!IsValidGodotObject(window))
		{
			failureDetail = "Window is unavailable.";
			return false;
		}

		try
		{
			Vector2I position = window.GetPositionWithDecorations();
			Vector2I size = window.GetSizeWithDecorations();
			if (size.X <= 0 || size.Y <= 0)
			{
				failureDetail = $"DecoratedSize='{size}'";
				return false;
			}

			windowRect = new Rect2I(position, size);
			return true;
		}
		catch (Exception exception)
		{
			failureDetail = $"Exception='{exception}'";
			return false;
		}
	}

	private void FaultNoteBackgroundRestoreGeometry(
		string windowRole,
		string detail
	)
	{
		if (_noteBackgroundRestoreGeometryUnavailable)
			return;

		_noteBackgroundRestoreGeometryUnavailable = true;
		HideNoteMinimizedRestoreButton();
		DebugLogger.LogOperation(
			"Note background restore geometry observation unavailable",
			$"Window='{windowRole}', {detail ?? ""}"
		);
	}

	private void FaultNoteMinimizedRestoreObservation(
		string operation = "",
		string detail = ""
	)
	{
		if (_noteMinimizedRestoreObservationFaulted)
			return;

		_noteMinimizedRestoreObservationFaulted = true;
		HideNoteMinimizedRestoreButton();

		if (!string.IsNullOrWhiteSpace(operation))
			DebugLogger.LogOperation(operation, detail ?? "");
	}

	private void ShowNoteMinimizedRestoreButtonForCurrentScriptEditor()
	{
		if (
			!TryGetCurrentNoteMinimizedRestoreBaseEditor(
				out ScriptEditor scriptEditor,
				out TextEdit baseEditor,
				out Node codeTextEditor
			)
		)
		{
			HideNoteMinimizedRestoreButton();
			return;
		}

		if (
			IsValidGodotObject(_noteMinimizedRestoreButton)
			&& IsSameGodotObject(_noteMinimizedRestoreBaseEditor, baseEditor)
			&& IsCachedNoteMinimizedRestoreHostUsable(
				baseEditor,
				codeTextEditor,
				out Button cachedToggleFilesButton
			)
		)
		{
			MountNoteMinimizedRestoreButton(
				baseEditor,
				_noteMinimizedRestoreStatusBar,
				cachedToggleFilesButton
			);
			return;
		}

		if (
			!TryResolveNoteMinimizedRestoreStatusBar(
				baseEditor,
				codeTextEditor,
				out HBoxContainer statusBar,
				out Button toggleFilesButton
			)
		)
		{
			HideNoteMinimizedRestoreButton();
			return;
		}

		if (!EnsureNoteMinimizedRestoreButtonCreated(scriptEditor))
		{
			HideNoteMinimizedRestoreButton();
			return;
		}

		MountNoteMinimizedRestoreButton(baseEditor, statusBar, toggleFilesButton);
	}

	private static bool TryGetCurrentNoteMinimizedRestoreBaseEditor(
		out ScriptEditor scriptEditor,
		out TextEdit baseEditor,
		out Node codeTextEditor
	)
	{
		scriptEditor = null;
		baseEditor = null;
		codeTextEditor = null;

		try
		{
			EditorInterface editorInterface = EditorInterface.Singleton;
			if (!IsValidGodotObject(editorInterface))
				return false;

			scriptEditor = editorInterface.GetScriptEditor();
			if (!IsValidGodotObject(scriptEditor))
				return false;

			ScriptEditorBase currentEditor = scriptEditor.GetCurrentEditor();
			if (!IsValidGodotObject(currentEditor))
				return false;

			Control currentBaseEditor = currentEditor.GetBaseEditor();
			if (currentBaseEditor is not TextEdit currentTextEdit)
				return false;

			if (
				!IsValidGodotObject(currentTextEdit)
				|| currentTextEdit.IsQueuedForDeletion()
				|| !currentTextEdit.IsInsideTree()
			)
			{
				return false;
			}

			Node parent = currentTextEdit.GetParent();
			if (
				!IsValidGodotObject(parent)
				|| parent.IsQueuedForDeletion()
				|| !parent.IsInsideTree()
			)
			{
				return false;
			}

			baseEditor = currentTextEdit;
			codeTextEditor = parent;
			return true;
		}
		catch
		{
			scriptEditor = null;
			baseEditor = null;
			codeTextEditor = null;
			return false;
		}
	}

	private bool IsCachedNoteMinimizedRestoreHostUsable(
		TextEdit baseEditor,
		Node codeTextEditor,
		out Button toggleFilesButton
	)
	{
		toggleFilesButton = null;

		if (
			!IsValidGodotObject(_noteMinimizedRestoreStatusBar)
			|| _noteMinimizedRestoreStatusBar.IsQueuedForDeletion()
			|| !_noteMinimizedRestoreStatusBar.IsInsideTree()
			|| !IsSameGodotObject(_noteMinimizedRestoreStatusBar.GetParent(), codeTextEditor)
			|| _noteMinimizedRestoreStatusBar.GetIndex() <= baseEditor.GetIndex()
			|| _noteMinimizedRestoreStatusBar.GetChildCount() < 1
		)
		{
			return false;
		}

		if (_noteMinimizedRestoreStatusBar.GetChild(0) is not Button firstButton)
			return false;

		if (!IsFlatButton(firstButton))
			return false;

		toggleFilesButton = firstButton;
		return true;
	}

	private static bool TryResolveNoteMinimizedRestoreStatusBar(
		TextEdit baseEditor,
		Node codeTextEditor,
		out HBoxContainer statusBar,
		out Button toggleFilesButton
	)
	{
		statusBar = null;
		toggleFilesButton = null;

		try
		{
			int matchCount = 0;
			HBoxContainer matchedStatusBar = null;
			Button matchedToggleFilesButton = null;
			int baseEditorIndex = baseEditor.GetIndex();

			for (int childIndex = 0; childIndex < codeTextEditor.GetChildCount(); childIndex++)
			{
				Node child = codeTextEditor.GetChild(childIndex);
				if (child is not HBoxContainer candidate)
					continue;

				if (candidate.GetIndex() <= baseEditorIndex)
					continue;

				if (
					!TryValidateNoteMinimizedRestoreStatusBarCandidate(
						candidate,
						codeTextEditor,
						out Button candidateToggleFilesButton
					)
				)
				{
					continue;
				}

				matchCount++;
				matchedStatusBar = candidate;
				matchedToggleFilesButton = candidateToggleFilesButton;

				if (matchCount > 1)
					return false;
			}

			if (matchCount != 1)
				return false;

			statusBar = matchedStatusBar;
			toggleFilesButton = matchedToggleFilesButton;
			return true;
		}
		catch
		{
			statusBar = null;
			toggleFilesButton = null;
			return false;
		}
	}

	private static bool TryValidateNoteMinimizedRestoreStatusBarCandidate(
		HBoxContainer candidate,
		Node expectedParent,
		out Button toggleFilesButton
	)
	{
		toggleFilesButton = null;

		if (
			!IsValidGodotObject(candidate)
			|| candidate.IsQueuedForDeletion()
			|| !candidate.IsInsideTree()
			|| !IsSameGodotObject(candidate.GetParent(), expectedParent)
			|| candidate.GetChildCount() < 1
		)
		{
			return false;
		}

		if (candidate.GetChild(0) is not Button firstButton || !IsFlatButton(firstButton))
			return false;

		bool hasRichTextLabel = false;
		bool hasMenuButton = false;
		int labelCount = 0;
		int separatorCount = 0;

		for (int childIndex = 0; childIndex < candidate.GetChildCount(); childIndex++)
		{
			Node child = candidate.GetChild(childIndex);

			if (child is RichTextLabel)
				hasRichTextLabel = true;

			if (child is MenuButton)
				hasMenuButton = true;

			if (child is Label)
				labelCount++;

			if (child is VSeparator)
				separatorCount++;
		}

		if (!hasRichTextLabel || !hasMenuButton || labelCount < 2 || separatorCount < 2)
			return false;

		toggleFilesButton = firstButton;
		return true;
	}

	private static bool IsFlatButton(Button button)
	{
		return IsValidGodotObject(button)
			&& string.Equals(
				button.ThemeTypeVariation.ToString(),
				"FlatButton",
				StringComparison.Ordinal
			);
	}

	private bool EnsureNoteMinimizedRestoreButtonCreated(ScriptEditor scriptEditor)
	{
		if (IsValidGodotObject(_noteMinimizedRestoreButton))
			return true;

		_noteMinimizedRestoreButton = null;
		_noteMinimizedRestoreStatusBar = null;
		_noteMinimizedRestoreBaseEditor = null;

		try
		{
			RemoveStaleNoteMinimizedRestoreButtonsFromScriptEditor(scriptEditor);

			var button = new Button
			{
				Name = NoteMinimizedRestoreButtonName,
				Text = NoteMinimizedRestoreButtonText,
				TooltipText = "Restore Note",
				FocusMode = Control.FocusModeEnum.None,
				Visible = false,
				ThemeTypeVariation = "FlatButton",
				SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
			};

			if (
				!TryConnectPluginSignal(
					button,
					Button.SignalName.Pressed,
					nameof(OnNoteMinimizedRestorePressedSignal),
					NoteMinimizedRestoreButtonName
				)
			)
			{
				button.QueueFree();
				return false;
			}

			_noteMinimizedRestoreButton = button;
			return true;
		}
		catch
		{
			_noteMinimizedRestoreButton = null;
			return false;
		}
	}

	private void MountNoteMinimizedRestoreButton(
		TextEdit baseEditor,
		HBoxContainer statusBar,
		Button toggleFilesButton
	)
	{
		if (
			!IsValidGodotObject(_noteMinimizedRestoreButton)
			|| !IsValidGodotObject(baseEditor)
			|| !IsValidGodotObject(statusBar)
			|| !IsValidGodotObject(toggleFilesButton)
		)
		{
			HideNoteMinimizedRestoreButton();
			return;
		}

		try
		{
			Node currentParent = _noteMinimizedRestoreButton.GetParent();
			if (!IsSameGodotObject(currentParent, statusBar))
			{
				if (IsValidGodotObject(currentParent))
					currentParent.RemoveChild(_noteMinimizedRestoreButton);

				statusBar.AddChild(_noteMinimizedRestoreButton);
			}

			int targetIndex = toggleFilesButton.GetIndex() + 1;
			if (_noteMinimizedRestoreButton.GetIndex() != targetIndex)
				statusBar.MoveChild(_noteMinimizedRestoreButton, targetIndex);

			_noteMinimizedRestoreBaseEditor = baseEditor;
			_noteMinimizedRestoreStatusBar = statusBar;
			_noteMinimizedRestoreButton.Visible = true;
		}
		catch
		{
			HideNoteMinimizedRestoreButton();
		}
	}

	private void HideNoteMinimizedRestoreButton()
	{
		if (!IsValidGodotObject(_noteMinimizedRestoreButton))
			return;

		try
		{
			_noteMinimizedRestoreButton.Visible = false;
		}
		catch
		{
		}
	}

	private static bool IsSameGodotObject(GodotObject left, GodotObject right)
	{
		return IsValidGodotObject(left)
			&& IsValidGodotObject(right)
			&& left.GetInstanceId() == right.GetInstanceId();
	}

	private static void RemoveStaleNoteMinimizedRestoreButtonsFromCurrentScriptEditor()
	{
		try
		{
			ScriptEditor scriptEditor = EditorInterface.Singleton?.GetScriptEditor();
			RemoveStaleNoteMinimizedRestoreButtonsFromScriptEditor(scriptEditor);
		}
		catch
		{
		}
	}

	private static void RemoveStaleNoteMinimizedRestoreButtonsFromScriptEditor(
		ScriptEditor scriptEditor
	)
	{
		if (!IsValidGodotObject(scriptEditor))
			return;

		try
		{
			RemoveStaleNoteMinimizedRestoreButtonsRecursive(scriptEditor);
		}
		catch
		{
		}
	}

	private static void RemoveStaleNoteMinimizedRestoreButtonsRecursive(Node parent)
	{
		if (!IsValidGodotObject(parent))
			return;

		for (int childIndex = parent.GetChildCount() - 1; childIndex >= 0; childIndex--)
		{
			Node child = parent.GetChild(childIndex);
			if (!IsValidGodotObject(child))
				continue;

			RemoveStaleNoteMinimizedRestoreButtonsRecursive(child);

			if (
				child is not Button staleButton
				|| !string.Equals(
					staleButton.Name.ToString(),
					NoteMinimizedRestoreButtonName,
					StringComparison.Ordinal
				)
			)
			{
				continue;
			}

			try
			{
				staleButton.QueueFree();
				parent.RemoveChild(staleButton);
			}
			catch
			{
			}
		}
	}

	private void ClearNoteMinimizedRestoreSessionState()
	{
		Button button = _noteMinimizedRestoreButton;
		if (IsValidGodotObject(button))
		{
			DisconnectPluginSignal(
				button,
				Button.SignalName.Pressed,
				nameof(OnNoteMinimizedRestorePressedSignal),
				NoteMinimizedRestoreButtonName
			);

			try
			{
				button.Visible = false;
				Node parent = button.GetParent();
				button.QueueFree();

				if (IsValidGodotObject(parent))
					parent.RemoveChild(button);
			}
			catch
			{
			}
		}

		_noteMinimizedRestoreButton = null;
		_noteMinimizedRestoreStatusBar = null;
		_noteMinimizedRestoreBaseEditor = null;
		RemoveStaleNoteMinimizedRestoreButtonsFromCurrentScriptEditor();
		_lastNonMinimizedNoteDialogMode = Window.ModeEnum.Windowed;
		_hasLastNonMinimizedNoteDialogMode = false;
		_noteMinimizedRestoreObservationFaulted = false;
		_noteBackgroundRestoreGeometryUnavailable = false;
	}

	private void RestoreOrFocusActiveNoteDialog()
	{
		if (
			!IsNoteDialogSessionActive()
			|| !IsValidGodotObject(_noteDialog)
			|| _openedNoteDialogEmbeddedSubwindow
		)
		{
			HideNoteMinimizedRestoreButton();
			return;
		}

		if (!TryReadNoteDialogMode(out Window.ModeEnum currentMode))
		{
			FaultNoteMinimizedRestoreObservation();
			RefreshEditorPluginProcessingState();
			return;
		}

		if (currentMode == Window.ModeEnum.Minimized)
		{
			if (
				!_hasLastNonMinimizedNoteDialogMode
				|| (_lastNonMinimizedNoteDialogMode != Window.ModeEnum.Windowed
					&& _lastNonMinimizedNoteDialogMode != Window.ModeEnum.Maximized)
			)
			{
				return;
			}

			if (
				!TrySetNoteDialogMode(
					_lastNonMinimizedNoteDialogMode,
					"Note minimized restore mode request failed"
				)
			)
			{
				return;
			}
		}
		else if (
			currentMode != Window.ModeEnum.Windowed
			&& currentMode != Window.ModeEnum.Maximized
		)
		{
			HideNoteMinimizedRestoreButton();
			return;
		}

		HideNoteMinimizedRestoreButton();

		try
		{
			_noteDialog.GrabFocus();
		}
		catch
		{
		}
	}

	private void CaptureOpenedNoteDialogGeometryBaseline(bool isEmbeddedSubwindow)
	{
		ClearOpenedNoteDialogGeometryBaseline();

		if (!isEmbeddedSubwindow)
		{
			if (!TryReadNoteDialogMode(out Window.ModeEnum currentMode))
				return;

			if (currentMode != Window.ModeEnum.Windowed)
			{
				DebugLogger.LogOperation(
					"Note windowed geometry baseline skipped",
					$"Expected actual Mode='{Window.ModeEnum.Windowed}' but found Mode='{currentMode}'."
				);
				return;
			}
		}

		try
		{
			_openedNoteDialogSize = _noteDialog.Size;
			_openedNoteDialogPosition = _noteDialog.Position;
			_hasOpenedNoteDialogGeometryBaseline = true;
		}
		catch (Exception exception)
		{
			ClearOpenedNoteDialogGeometryBaseline();
			DebugLogger.LogOperation(
				"Note windowed geometry baseline capture failed",
				$"Exception='{exception}'"
			);
		}
	}

	private void CaptureOpenedNoteDialogMaximizedBaseline(bool isEmbeddedSubwindow)
	{
		ClearOpenedNoteDialogMaximizedBaseline();

		if (isEmbeddedSubwindow)
			return;

		if (!TryReadNoteDialogMode(out Window.ModeEnum currentMode))
			return;

		if (
			currentMode != Window.ModeEnum.Windowed
			&& currentMode != Window.ModeEnum.Maximized
		)
		{
			DebugLogger.LogOperation(
				"Note maximized-state baseline skipped",
				$"Unsupported current Mode='{currentMode}'."
			);
			return;
		}

		_openedNoteDialogMaximized = currentMode == Window.ModeEnum.Maximized;
		_hasOpenedNoteDialogMaximizedBaseline = true;
	}

	private void ClearOpenedNoteDialogGeometryBaseline()
	{
		_openedNoteDialogSize = default;
		_openedNoteDialogPosition = default;
		_hasOpenedNoteDialogGeometryBaseline = false;
	}

	private void ClearOpenedNoteDialogMaximizedBaseline()
	{
		_openedNoteDialogMaximized = false;
		_hasOpenedNoteDialogMaximizedBaseline = false;
	}

	private void ClearOpenedNoteDialogWindowStateBaseline()
	{
		ClearOpenedNoteDialogGeometryBaseline();
		ClearOpenedNoteDialogMaximizedBaseline();
		_awaitingOpenedNoteDialogWindowedGeometryBaseline = false;
		_openedNoteDialogEmbeddedSubwindow = false;
	}

	private void RestoreNoteCaretState(NoteViewState viewState)
	{
		if (!IsValidGodotObject(_noteTextEdit))
			return;

		int savedLine = viewState?.CaretLine ?? 0;
		int savedColumn = viewState?.CaretColumn ?? 0;
		int lineCount = Math.Max(1, _noteTextEdit.GetLineCount());
		int caretLine = Math.Clamp(savedLine, 0, lineCount - 1);
		int caretColumn = Math.Clamp(
			savedColumn,
			0,
			(_noteTextEdit.GetLine(caretLine) ?? "").Length
		);

		_noteTextEdit.RemoveSecondaryCarets();
		_noteTextEdit.Deselect();
		_noteTextEdit.SetCaretLine(caretLine, true);
		_noteTextEdit.SetCaretColumn(caretColumn, true);
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

	private void OnNoteMinimizedRestorePressedSignal()
	{
		if (EnsureManagedAssemblyStateCurrent("Restore Note"))
			RestoreOrFocusActiveNoteDialog();
	}

	private void OnNoteDialogCanceledSignal()
	{
		if (EnsureManagedAssemblyStateCurrent("Close Note Dialog"))
			TrySaveActiveNoteAndClose();
	}

	private void OnNoteDialogSizeChangedSignal()
	{
		if (
			!_awaitingOpenedNoteDialogWindowedGeometryBaseline
			|| !IsNoteDialogSessionActive()
			|| !IsValidGodotObject(_noteDialog)
			|| _openedNoteDialogEmbeddedSubwindow
			|| _hasOpenedNoteDialogGeometryBaseline
		)
		{
			return;
		}

		if (!TryReadNoteDialogMode(out Window.ModeEnum currentMode))
			return;

		if (currentMode != Window.ModeEnum.Windowed)
			return;

		CaptureOpenedNoteDialogGeometryBaseline(isEmbeddedSubwindow: false);
		if (_hasOpenedNoteDialogGeometryBaseline)
			_awaitingOpenedNoteDialogWindowedGeometryBaseline = false;
	}

	private void OnNoteTextEditGuiInputSignal(InputEvent inputEvent)
	{
		if (EnsureManagedAssemblyStateCurrent("Note Text Input"))
			OnNoteTextEditGuiInput(inputEvent);
	}

	private void OnNoteTextEditGuiInput(InputEvent inputEvent)
	{
		if (
			!IsValidGodotObject(_noteTextEdit)
			|| !IsValidGodotObject(_noteDialog)
			|| !_noteDialog.Visible
			|| inputEvent is not InputEventMouseButton mouseButton
			|| !mouseButton.Pressed
			|| !mouseButton.CtrlPressed
		)
		{
			return;
		}

		int delta;
		if (mouseButton.ButtonIndex == MouseButton.WheelUp)
			delta = 1;
		else if (mouseButton.ButtonIndex == MouseButton.WheelDown)
			delta = -1;
		else
			return;

		_noteTextEdit.AcceptEvent();

		int currentFontSize = Math.Clamp(
			_activeNoteFontSize > 0 ? _activeNoteFontSize : NoteFontSizeFallback,
			NoteFontSizeMinimum,
			NoteFontSizeMaximum
		);
		int nextFontSize = Math.Clamp(
			currentFontSize + delta,
			NoteFontSizeMinimum,
			NoteFontSizeMaximum
		);

		if (nextFontSize == currentFontSize)
			return;

		_activeNoteFontSize = nextFontSize;
		_noteFontSizeDirty = true;
		_noteTextEdit.AddThemeFontSizeOverride("font_size", _activeNoteFontSize);
	}

	private bool TrySaveActiveNoteAndClose()
	{
		if (
			!string.IsNullOrWhiteSpace(_activeNoteMetadata)
			&& string.Equals(
				_activeNoteMetadata,
				_noteSaveSuppressedRemovedTargetMetadata,
				StringComparison.Ordinal
			)
		)
		{
			ClearActiveNoteState();
			return true;
		}

		if (
			string.IsNullOrWhiteSpace(_activeNoteMetadata)
			|| !IsValidGodotObject(_noteTextEdit)
			|| !IsValidGodotObject(_noteDialog)
		)
		{
			return false;
		}

		string exactText = _noteTextEdit.Text;
		int caretLine = Math.Max(0, _noteTextEdit.GetCaretLine());
		int caretColumn = Math.Max(0, _noteTextEdit.GetCaretColumn());
		NoteViewState viewState = new(caretLine, caretColumn);

		if (
			!TrySaveNoteForMetadata(
				_activeNoteMetadata,
				exactText,
				viewState,
				out string failureDetail
			)
		)
		{
			PushSystemExplorerWarning(
				"System Explorer could not save this Note.",
				mirrorToDebugLog: false
			);
			DebugLogger.LogOperation("Note save failed", failureDetail ?? "");
			QueueNoteDialogFailedCloseSaveRecovery();
			return false;
		}

		string savedMetadata = _activeNoteMetadata;
		bool hasNote = !string.IsNullOrWhiteSpace(exactText);

		NoteEditorSettingsMutation settingsMutation = BuildNoteEditorSettingsMutationForSave();
		if (
			settingsMutation.HasAny
			&& !TrySaveGlobalNoteEditorSettings(
				settingsMutation,
				out string settingsFailureDetail
			)
		)
		{
			DebugLogger.LogOperation(
				"Note content saved but global editor preferences could not be persisted",
				settingsFailureDetail ?? ""
			);
		}

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
		return true;
	}

	private void QueueNoteDialogFailedCloseSaveRecovery()
	{
		if (
			_noteCloseSaveRecoveryQueued
			|| string.IsNullOrWhiteSpace(_activeNoteMetadata)
		)
		{
			return;
		}

		_noteCloseSaveRecoveryQueued = true;
		long scheduledNoteSessionEpoch = _noteDialogSessionEpoch;
		string scheduledManagedAssemblyGeneration = ManagedAssemblyGeneration;

		CallDeferred(
			nameof(RestoreNoteDialogAfterFailedCloseSaveDeferred),
			scheduledNoteSessionEpoch,
			scheduledManagedAssemblyGeneration
		);
	}

	private void RestoreNoteDialogAfterFailedCloseSaveDeferred(
		long scheduledNoteSessionEpoch,
		string scheduledManagedAssemblyGeneration
	)
	{
		if (
			!string.Equals(
				scheduledManagedAssemblyGeneration,
				ManagedAssemblyGeneration,
				StringComparison.Ordinal
			)
		)
		{
			return;
		}

		if (!_noteCloseSaveRecoveryQueued)
			return;

		if (scheduledNoteSessionEpoch != _noteDialogSessionEpoch)
			return;

		_noteCloseSaveRecoveryQueued = false;

		if (
			!GodotObject.IsInstanceValid(this)
			|| !IsInsideTree()
			|| !IsNoteDialogSessionActive()
			|| !IsValidGodotObject(_noteDialog)
			|| !IsValidGodotObject(_noteTextEdit)
		)
		{
			return;
		}

		if (!_noteDialog.Visible)
			_noteDialog.Show();

		try
		{
			_noteDialog.GrabFocus();
		}
		catch
		{
		}

		try
		{
			_noteTextEdit.GrabFocus(true);
		}
		catch
		{
		}
	}


	private NoteEditorSettingsMutation BuildNoteEditorSettingsMutationForSave()
	{
		var mutation = new NoteEditorSettingsMutation();

		if (_noteFontSizeDirty)
		{
			mutation.HasFontSize = true;
			mutation.FontSize = _activeNoteFontSize;
		}

		if (_openedNoteDialogEmbeddedSubwindow)
		{
			AppendWindowedNoteDialogGeometryMutation(mutation);
			return mutation;
		}

		if (!_hasOpenedNoteDialogMaximizedBaseline)
			return mutation;

		if (!TryReadNoteDialogMode(out Window.ModeEnum currentMode))
			return mutation;

		if (
			currentMode != Window.ModeEnum.Windowed
			&& currentMode != Window.ModeEnum.Maximized
		)
		{
			DebugLogger.LogOperation(
				"Note window-state preference mutation skipped",
				$"Unsupported current Mode='{currentMode}'."
			);
			return mutation;
		}

		bool currentMaximized = currentMode == Window.ModeEnum.Maximized;
		if (currentMaximized != _openedNoteDialogMaximized)
		{
			mutation.HasMaximized = true;
			mutation.Maximized = currentMaximized;
		}

		if (!currentMaximized)
			AppendWindowedNoteDialogGeometryMutation(mutation);

		return mutation;
	}

	private void AppendWindowedNoteDialogGeometryMutation(
		NoteEditorSettingsMutation mutation
	)
	{
		if (!_hasOpenedNoteDialogGeometryBaseline)
			return;

		try
		{
			Vector2I currentWindowSize = _noteDialog.Size;
			if (currentWindowSize != _openedNoteDialogSize)
			{
				mutation.HasWindowSize = true;
				mutation.WindowSize = currentWindowSize;
			}

			Vector2I currentWindowPosition = _noteDialog.Position;
			if (currentWindowPosition != _openedNoteDialogPosition)
			{
				mutation.HasCenterPosition = true;
				mutation.CenterPosition = false;
				mutation.HasPosition = true;
				mutation.Position = currentWindowPosition;
			}
		}
		catch (Exception exception)
		{
			DebugLogger.LogOperation(
				"Note windowed geometry preference mutation skipped",
				$"Exception='{exception}'"
			);
		}
	}

	private void CloseNoteDialogAfterSuccessfulMutation()
	{
		HideNoteDialogWindowsBestEffort();
		ClearActiveNoteState();
	}

	private void ClearActiveNoteState()
	{
		ClearNoteMinimizedRestoreSessionState();
		_noteCloseSaveRecoveryQueued = false;
		_activeNoteMetadata = "";
		_noteSaveSuppressedRemovedTargetMetadata = "";
		_activeNoteFontSize = 0;
		_noteFontSizeDirty = false;
		ClearOpenedNoteDialogWindowStateBaseline();

		if (IsValidGodotObject(_noteTextEdit))
			_noteTextEdit.Text = "";

		if (IsValidGodotObject(_noteDialog))
			_noteDialog.Title = "Note";

		RefreshEditorPluginProcessingState();
	}
}
#endif
