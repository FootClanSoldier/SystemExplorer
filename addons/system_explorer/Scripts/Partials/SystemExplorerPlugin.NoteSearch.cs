#if TOOLS
using Godot;
using System;
using System.Text;

public partial class SystemExplorerPlugin
{
	private const float NoteSearchOverlayWidth = 712.0f;
	private const float NoteSearchOverlayHeight = 70.0f;
	private const float NoteSearchOverlayTopInset = 18.0f;
	private static readonly Vector2 NoteSearchButtonMinimumSize = new(40.0f, 40.0f);
	private static readonly Vector2 NoteSearchInputMinimumSize = new(0.0f, 40.0f);

	private PanelContainer _noteSearchOverlay;
	private MarginContainer _noteSearchOverlayInnerMargin;
	private HBoxContainer _noteSearchOverlayContent;
	private LineEdit _noteSearchInput;
	private Button _noteSearchNextButton;
	private Button _noteSearchPreviousButton;
	private Button _noteSearchCloseButton;
	private string _noteSearchQuery = "";
	private bool _resettingNoteSearchState;

	private void CreateNoteSearchUi()
	{
		if (!IsValidGodotObject(_noteTextEdit))
			return;

		if (
			!TryCreateNoteSearchOverlayPanelStyle(
				out StyleBoxFlat overlayPanelStyle,
				out string overlayStyleFailureDetail
			)
		)
		{
			DebugLogger.LogOperation(
				"Note Search UI creation failed",
				overlayStyleFailureDetail
			);
			return;
		}

		if (
			!TryCreateNoteSearchInputStyles(
				out StyleBoxFlat searchInputNormalStyle,
				out StyleBoxFlat searchInputFocusStyle,
				out string inputStyleFailureDetail
			)
		)
		{
			DebugLogger.LogOperation(
				"Note Search UI creation failed",
				inputStyleFailureDetail
			);
			return;
		}

		if (
			!TryCreateNoteSearchButtonStyles(
				out StyleBoxFlat buttonNormalStyle,
				out StyleBoxFlat buttonHoverStyle,
				out StyleBoxFlat buttonPressedStyle,
				out string buttonStyleFailureDetail
			)
		)
		{
			DebugLogger.LogOperation(
				"Note Search UI creation failed",
				buttonStyleFailureDetail
			);
			return;
		}

		_noteSearchOverlay = new PanelContainer
		{
			Name = "Note Search Overlay",
			Visible = false,
			AnchorLeft = 0.5f,
			AnchorRight = 0.5f,
			AnchorTop = 0.0f,
			AnchorBottom = 0.0f,
			OffsetLeft = -NoteSearchOverlayWidth / 2.0f,
			OffsetRight = NoteSearchOverlayWidth / 2.0f,
			OffsetTop = NoteSearchOverlayTopInset,
			OffsetBottom = NoteSearchOverlayTopInset + NoteSearchOverlayHeight,
			ZIndex = 10,
			MouseFilter = Control.MouseFilterEnum.Stop,
		};
		_noteSearchOverlay.AddThemeStyleboxOverride("panel", overlayPanelStyle);

		_noteSearchOverlayInnerMargin = new MarginContainer
		{
			Name = "Note Search Overlay Inner Margin",
			MouseFilter = Control.MouseFilterEnum.Pass,
			SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
			SizeFlagsVertical = Control.SizeFlags.ExpandFill,
		};
		_noteSearchOverlayInnerMargin.AddThemeConstantOverride("margin_left", 14);
		_noteSearchOverlayInnerMargin.AddThemeConstantOverride("margin_top", 11);
		_noteSearchOverlayInnerMargin.AddThemeConstantOverride("margin_right", 14);
		_noteSearchOverlayInnerMargin.AddThemeConstantOverride("margin_bottom", 11);

		_noteSearchOverlayContent = new HBoxContainer
		{
			Name = "Note Search Overlay Content",
			MouseFilter = Control.MouseFilterEnum.Pass,
			SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
			SizeFlagsVertical = Control.SizeFlags.ExpandFill,
		};
		_noteSearchOverlayContent.AddThemeConstantOverride("separation", 10);

		_noteSearchInput = new LineEdit
		{
			Name = "Search",
			PlaceholderText = "Search",
			ClearButtonEnabled = false,
			KeepEditingOnTextSubmit = true,
			SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
			CustomMinimumSize = NoteSearchInputMinimumSize,
			RightIcon = _scriptFilterSearchIcon,
		};
		_noteSearchInput.AddThemeStyleboxOverride("normal", searchInputNormalStyle);
		_noteSearchInput.AddThemeStyleboxOverride("focus", searchInputFocusStyle);
		_noteSearchInput.AddThemeColorOverride("font_color", Color.FromHtml("#F3F3F3"));
		_noteSearchInput.AddThemeColorOverride("font_placeholder_color", Color.FromHtml("#BBBBBB"));
		_noteSearchInput.AddThemeColorOverride("font_uneditable_color", Color.FromHtml("#F3F3F3"));
		_noteSearchInput.AddThemeColorOverride("caret_color", Color.FromHtml("#F3F3F3"));
		_noteSearchInput.AddThemeColorOverride("font_selected_color", Color.FromHtml("#F3F3F3"));
		_noteSearchInput.AddThemeColorOverride("selection_color", new Color(1.0f, 1.0f, 1.0f, 0.16f));

		_noteSearchNextButton = CreateNoteSearchButton(
			"Next",
			null,
			"↓",
			"Next match",
			buttonNormalStyle,
			buttonHoverStyle,
			buttonPressedStyle
		);
		_noteSearchPreviousButton = CreateNoteSearchButton(
			"Previous",
			null,
			"↑",
			"Previous match",
			buttonNormalStyle,
			buttonHoverStyle,
			buttonPressedStyle
		);
		_noteSearchCloseButton = CreateNoteSearchButton(
			"Close",
			null,
			"×",
			"Close search",
			buttonNormalStyle,
			buttonHoverStyle,
			buttonPressedStyle
		);

		_noteSearchOverlayContent.AddChild(_noteSearchInput);
		_noteSearchOverlayContent.AddChild(_noteSearchNextButton);
		_noteSearchOverlayContent.AddChild(_noteSearchPreviousButton);
		_noteSearchOverlayContent.AddChild(_noteSearchCloseButton);

		_noteSearchOverlayInnerMargin.AddChild(_noteSearchOverlayContent);
		_noteSearchOverlay.AddChild(_noteSearchOverlayInnerMargin);
		_noteTextEdit.AddChild(_noteSearchOverlay);
	}

	private static bool TryCreateNoteSearchOverlayPanelStyle(
		out StyleBoxFlat overlayStyle,
		out string failureDetail
	)
	{
		overlayStyle = null;
		failureDetail = "";

		try
		{
			overlayStyle = new StyleBoxFlat
			{
				BgColor = Color.FromHtml("#292929"),
				BorderColor = Color.FromHtml("#4E4E4E"),
				BorderWidthLeft = 1,
				BorderWidthTop = 1,
				BorderWidthRight = 1,
				BorderWidthBottom = 1,
				CornerRadiusTopLeft = 12,
				CornerRadiusTopRight = 12,
				CornerRadiusBottomRight = 12,
				CornerRadiusBottomLeft = 12,
				ShadowColor = new Color(0.0f, 0.0f, 0.0f, 0.32f),
				ShadowSize = 8,
				ShadowOffset = new Vector2(0.0f, 3.0f),
			};

			return true;
		}
		catch (Exception exception)
		{
			failureDetail =
				"Creating the Note Search overlay panel StyleBox threw: "
				+ exception.Message;
			return false;
		}
	}

	private static bool TryCreateNoteSearchInputStyles(
		out StyleBoxFlat searchInputNormalStyle,
		out StyleBoxFlat searchInputFocusStyle,
		out string failureDetail
	)
	{
		searchInputNormalStyle = null;
		searchInputFocusStyle = null;
		failureDetail = "";

		try
		{
			searchInputNormalStyle = new StyleBoxFlat
			{
				BgColor = Color.FromHtml("#393939"),
				BorderColor = Color.FromHtml("#5B5B5B"),
				BorderWidthLeft = 1,
				BorderWidthTop = 1,
				BorderWidthRight = 1,
				BorderWidthBottom = 1,
				CornerRadiusTopLeft = 7,
				CornerRadiusTopRight = 7,
				CornerRadiusBottomRight = 7,
				CornerRadiusBottomLeft = 7,
				ContentMarginLeft = 12.0f,
				ContentMarginTop = 7.0f,
				ContentMarginRight = 12.0f,
				ContentMarginBottom = 7.0f,
			};

			searchInputFocusStyle = (StyleBoxFlat)searchInputNormalStyle.Duplicate();
			searchInputFocusStyle.BorderColor = Color.FromHtml("#727272");

			return true;
		}
		catch (Exception exception)
		{
			failureDetail =
				"Creating the Note Search input StyleBoxes threw: "
				+ exception.Message;
			return false;
		}
	}

	private static bool TryCreateNoteSearchButtonStyles(
		out StyleBoxFlat buttonNormalStyle,
		out StyleBoxFlat buttonHoverStyle,
		out StyleBoxFlat buttonPressedStyle,
		out string failureDetail
	)
	{
		buttonNormalStyle = null;
		buttonHoverStyle = null;
		buttonPressedStyle = null;
		failureDetail = "";

		try
		{
			buttonNormalStyle = new StyleBoxFlat
			{
				BgColor = new Color(0.0f, 0.0f, 0.0f, 0.0f),
				BorderWidthLeft = 0,
				BorderWidthTop = 0,
				BorderWidthRight = 0,
				BorderWidthBottom = 0,
				CornerRadiusTopLeft = 7,
				CornerRadiusTopRight = 7,
				CornerRadiusBottomRight = 7,
				CornerRadiusBottomLeft = 7,
				ContentMarginLeft = 0.0f,
				ContentMarginTop = 0.0f,
				ContentMarginRight = 0.0f,
				ContentMarginBottom = 0.0f,
			};

			buttonHoverStyle = (StyleBoxFlat)buttonNormalStyle.Duplicate();
			buttonHoverStyle.BgColor = new Color(1.0f, 1.0f, 1.0f, 0.07f);

			buttonPressedStyle = (StyleBoxFlat)buttonNormalStyle.Duplicate();
			buttonPressedStyle.BgColor = new Color(1.0f, 1.0f, 1.0f, 0.12f);

			return true;
		}
		catch (Exception exception)
		{
			failureDetail =
				"Creating the Note Search button StyleBoxes threw: "
				+ exception.Message;
			return false;
		}
	}

	private static Button CreateNoteSearchButton(
		string name,
		Texture2D icon,
		string fallbackText,
		string tooltipText,
		StyleBoxFlat normalStyle,
		StyleBoxFlat hoverStyle,
		StyleBoxFlat pressedStyle
	)
	{
		Button button = new()
		{
			Name = name,
			Flat = false,
			FocusMode = Control.FocusModeEnum.None,
			Icon = icon,
			Text = icon == null ? fallbackText : "",
			TooltipText = tooltipText,
			CustomMinimumSize = NoteSearchButtonMinimumSize,
			MouseDefaultCursorShape = Control.CursorShape.PointingHand,
		};
		button.AddThemeStyleboxOverride("normal", normalStyle);
		button.AddThemeStyleboxOverride("hover", hoverStyle);
		button.AddThemeStyleboxOverride("pressed", pressedStyle);
		button.AddThemeStyleboxOverride("focus", hoverStyle);
		button.AddThemeColorOverride("font_color", Color.FromHtml("#F3F3F3"));
		button.AddThemeColorOverride("font_hover_color", Color.FromHtml("#FFFFFF"));
		button.AddThemeColorOverride("font_pressed_color", Color.FromHtml("#FFFFFF"));
		button.AddThemeFontSizeOverride("font_size", 24);
		button.AddThemeConstantOverride("h_separation", 0);
		return button;
	}

	private static Texture2D ResolveNoteSearchEditorIcon(string iconName)
	{
		try
		{
			Theme editorTheme = EditorInterface.Singleton?.GetEditorTheme();
			return IsValidGodotObject(editorTheme)
				? GetEditorIcon(editorTheme, iconName)
				: null;
		}
		catch
		{
			return null;
		}
	}

	private bool AreNoteSearchSignalSourcesValid(out string failureDetail)
	{
		failureDetail = "";

		if (!IsValidGodotObject(_noteDialog))
		{
			failureDetail = "The Note dialog is unavailable for Search signal wiring.";
			return false;
		}

		if (!IsValidGodotObject(_noteTextEdit))
		{
			failureDetail = "The Note TextEdit is unavailable for Search signal wiring.";
			return false;
		}

		if (
			!IsValidGodotObject(_noteSearchOverlay)
			|| !IsValidGodotObject(_noteSearchOverlayInnerMargin)
			|| !IsValidGodotObject(_noteSearchOverlayContent)
			|| !IsValidGodotObject(_noteSearchInput)
			|| !IsValidGodotObject(_noteSearchNextButton)
			|| !IsValidGodotObject(_noteSearchPreviousButton)
			|| !IsValidGodotObject(_noteSearchCloseButton)
		)
		{
			failureDetail = "One or more Note Search controls are unavailable.";
			return false;
		}

		return true;
	}

	private bool ConnectNoteSearchSignals()
	{
		if (!AreNoteSearchSignalSourcesValid(out string failureDetail))
		{
			DebugLogger.LogOperation("Note Search signal connection failed", failureDetail);
			return false;
		}

		bool connected = true;
		connected &= TryConnectPluginSignal(
			_noteDialog,
			Window.SignalName.WindowInput,
			nameof(OnNoteDialogWindowInputSignal),
			nameof(_noteDialog)
		);
		connected &= TryConnectPluginSignal(
			_noteSearchInput,
			LineEdit.SignalName.TextChanged,
			nameof(OnNoteSearchTextChangedSignal),
			nameof(_noteSearchInput)
		);
		connected &= TryConnectPluginSignal(
			_noteSearchInput,
			Control.SignalName.GuiInput,
			nameof(OnNoteSearchInputGuiInputSignal),
			nameof(_noteSearchInput)
		);
		connected &= TryConnectPluginSignal(
			_noteSearchNextButton,
			Button.SignalName.Pressed,
			nameof(OnNoteSearchNextPressedSignal),
			nameof(_noteSearchNextButton)
		);
		connected &= TryConnectPluginSignal(
			_noteSearchPreviousButton,
			Button.SignalName.Pressed,
			nameof(OnNoteSearchPreviousPressedSignal),
			nameof(_noteSearchPreviousButton)
		);
		connected &= TryConnectPluginSignal(
			_noteSearchCloseButton,
			Button.SignalName.Pressed,
			nameof(OnNoteSearchClosePressedSignal),
			nameof(_noteSearchCloseButton)
		);

		return connected;
	}

	private void DisconnectNoteSearchSignals()
	{
		DisconnectPluginSignal(
			_noteDialog,
			Window.SignalName.WindowInput,
			nameof(OnNoteDialogWindowInputSignal),
			nameof(_noteDialog)
		);
		DisconnectPluginSignal(
			_noteSearchInput,
			LineEdit.SignalName.TextChanged,
			nameof(OnNoteSearchTextChangedSignal),
			nameof(_noteSearchInput)
		);
		DisconnectPluginSignal(
			_noteSearchInput,
			Control.SignalName.GuiInput,
			nameof(OnNoteSearchInputGuiInputSignal),
			nameof(_noteSearchInput)
		);
		DisconnectPluginSignal(
			_noteSearchNextButton,
			Button.SignalName.Pressed,
			nameof(OnNoteSearchNextPressedSignal),
			nameof(_noteSearchNextButton)
		);
		DisconnectPluginSignal(
			_noteSearchPreviousButton,
			Button.SignalName.Pressed,
			nameof(OnNoteSearchPreviousPressedSignal),
			nameof(_noteSearchPreviousButton)
		);
		DisconnectPluginSignal(
			_noteSearchCloseButton,
			Button.SignalName.Pressed,
			nameof(OnNoteSearchClosePressedSignal),
			nameof(_noteSearchCloseButton)
		);
	}

	private bool VerifyNoteSearchSignals()
	{
		return AreNoteSearchSignalSourcesValid(out _)
			&& IsPluginSignalConnected(
				_noteDialog,
				Window.SignalName.WindowInput,
				nameof(OnNoteDialogWindowInputSignal)
			)
			&& IsPluginSignalConnected(
				_noteSearchInput,
				LineEdit.SignalName.TextChanged,
				nameof(OnNoteSearchTextChangedSignal)
			)
			&& IsPluginSignalConnected(
				_noteSearchInput,
				Control.SignalName.GuiInput,
				nameof(OnNoteSearchInputGuiInputSignal)
			)
			&& IsPluginSignalConnected(
				_noteSearchNextButton,
				Button.SignalName.Pressed,
				nameof(OnNoteSearchNextPressedSignal)
			)
			&& IsPluginSignalConnected(
				_noteSearchPreviousButton,
				Button.SignalName.Pressed,
				nameof(OnNoteSearchPreviousPressedSignal)
			)
			&& IsPluginSignalConnected(
				_noteSearchCloseButton,
				Button.SignalName.Pressed,
				nameof(OnNoteSearchClosePressedSignal)
			);
	}

	private void OnNoteDialogWindowInputSignal(InputEvent inputEvent)
	{
		if (!EnsureManagedAssemblyStateCurrent("Note Search Window Input"))
			return;

		if (
			!IsNoteDialogSessionActive()
			|| !IsValidGodotObject(_noteDialog)
			|| !_noteDialog.Visible
			|| inputEvent is not InputEventKey keyEvent
			|| !keyEvent.Pressed
			|| keyEvent.Echo
		)
		{
			return;
		}

		if (IsNoteSearchShortcut(keyEvent))
		{
			ShowOrRefocusNoteSearch();
			_noteDialog.SetInputAsHandled();
			return;
		}

		if (keyEvent.Keycode == Key.Escape && IsNoteSearchVisible())
		{
			CloseNoteSearchOverlay(restoreNoteFocus: true);
			_noteDialog.SetInputAsHandled();
		}
	}

	private static bool IsNoteSearchShortcut(InputEventKey keyEvent)
	{
		return keyEvent.Keycode == Key.F
			&& keyEvent.CtrlPressed
			&& !keyEvent.ShiftPressed
			&& !keyEvent.AltPressed
			&& !keyEvent.MetaPressed;
	}

	private void OnNoteSearchTextChangedSignal(string query)
	{
		if (_resettingNoteSearchState)
			return;

		if (!EnsureManagedAssemblyStateCurrent("Note Search Query Changed"))
			return;

		_noteSearchQuery = query ?? "";

		if (!IsValidGodotObject(_noteTextEdit))
			return;

		if (string.IsNullOrEmpty(_noteSearchQuery))
		{
			_noteTextEdit.SetSearchText("");
			return;
		}

		_noteTextEdit.SetSearchFlags(0u);
		_noteTextEdit.SetSearchText(_noteSearchQuery);
		NavigateToLiveNoteSearchMatch();
	}

	private void OnNoteSearchInputGuiInputSignal(InputEvent inputEvent)
	{
		if (!EnsureManagedAssemblyStateCurrent("Note Search Input"))
			return;

		if (
			!IsNoteSearchVisible()
			|| inputEvent is not InputEventKey keyEvent
			|| !keyEvent.Pressed
			|| keyEvent.Echo
			|| (keyEvent.Keycode != Key.Enter && keyEvent.Keycode != Key.KpEnter)
		)
		{
			return;
		}

		if (keyEvent.ShiftPressed)
			NavigateToPreviousNoteSearchMatch();
		else
			NavigateToNextNoteSearchMatch();

		_noteSearchInput.AcceptEvent();
	}

	private void OnNoteSearchNextPressedSignal()
	{
		if (EnsureManagedAssemblyStateCurrent("Note Search Next"))
			NavigateToNextNoteSearchMatch();
	}

	private void OnNoteSearchPreviousPressedSignal()
	{
		if (EnsureManagedAssemblyStateCurrent("Note Search Previous"))
			NavigateToPreviousNoteSearchMatch();
	}

	private void OnNoteSearchClosePressedSignal()
	{
		if (EnsureManagedAssemblyStateCurrent("Close Note Search"))
			CloseNoteSearchOverlay(restoreNoteFocus: true);
	}

	private void ShowOrRefocusNoteSearch()
	{
		if (
			!IsValidGodotObject(_noteDialog)
			|| !IsValidGodotObject(_noteTextEdit)
			|| !IsValidGodotObject(_noteSearchOverlay)
			|| !IsValidGodotObject(_noteSearchInput)
		)
		{
			return;
		}

		_noteSearchOverlay.Visible = true;
		_noteDialog.DialogCloseOnEscape = false;

		if (!string.IsNullOrEmpty(_noteSearchQuery))
		{
			_noteTextEdit.SetSearchFlags(0u);
			_noteTextEdit.SetSearchText(_noteSearchQuery);
		}

		_noteSearchInput.GrabFocus();
		_noteSearchInput.Edit(true);

		if (!string.IsNullOrEmpty(_noteSearchQuery))
			_noteSearchInput.SelectAll();
	}

	private void CloseNoteSearchOverlay(bool restoreNoteFocus)
	{
		if (IsValidGodotObject(_noteSearchOverlay))
			_noteSearchOverlay.Visible = false;

		if (IsValidGodotObject(_noteTextEdit))
			_noteTextEdit.SetSearchText("");

		if (IsValidGodotObject(_noteDialog))
			_noteDialog.DialogCloseOnEscape = true;

		if (IsValidGodotObject(_noteSearchInput))
			_noteSearchInput.Unedit();

		if (restoreNoteFocus && IsValidGodotObject(_noteTextEdit))
			_noteTextEdit.GrabFocus(true);
	}

	private bool IsNoteSearchVisible()
	{
		return IsValidGodotObject(_noteSearchOverlay) && _noteSearchOverlay.Visible;
	}

	private void NavigateToLiveNoteSearchMatch()
	{
		if (!CanNavigateNoteSearch())
			return;

		int startLine;
		int startColumn;

		if (_noteTextEdit.HasSelection(0))
		{
			startLine = Math.Max(0, _noteTextEdit.GetSelectionFromLine(0));
			startColumn = Math.Max(0, _noteTextEdit.GetSelectionFromColumn(0));
		}
		else
		{
			startLine = Math.Max(0, _noteTextEdit.GetCaretLine(0));
			startColumn = Math.Max(0, _noteTextEdit.GetCaretColumn(0));
		}

		TryNavigateNoteSearch(
			_noteSearchQuery,
			0u,
			startLine,
			startColumn,
			0,
			0
		);
	}

	private void NavigateToNextNoteSearchMatch()
	{
		if (!CanNavigateNoteSearch())
			return;

		int startLine;
		int startColumn;

		if (DoesPrimaryNoteSelectionMatchQuery(_noteSearchQuery))
		{
			startLine = Math.Max(0, _noteTextEdit.GetSelectionToLine(0));
			startColumn = Math.Max(0, _noteTextEdit.GetSelectionToColumn(0));
		}
		else
		{
			startLine = Math.Max(0, _noteTextEdit.GetCaretLine(0));
			startColumn = Math.Max(0, _noteTextEdit.GetCaretColumn(0));
		}

		TryNavigateNoteSearch(
			_noteSearchQuery,
			0u,
			startLine,
			startColumn,
			0,
			0
		);
	}

	private void NavigateToPreviousNoteSearchMatch()
	{
		if (!CanNavigateNoteSearch())
			return;

		int startLine;
		int startColumn;

		bool wrapImmediately = false;

		if (DoesPrimaryNoteSelectionMatchQuery(_noteSearchQuery))
		{
			startLine = Math.Max(0, _noteTextEdit.GetSelectionFromLine(0));
			startColumn = Math.Max(0, _noteTextEdit.GetSelectionFromColumn(0));

			if (startLine == 0 && startColumn == 0)
				wrapImmediately = true;
			else
				MoveNoteSearchPositionOneScalarBackward(ref startLine, ref startColumn);
		}
		else
		{
			startLine = Math.Max(0, _noteTextEdit.GetCaretLine(0));
			startColumn = Math.Max(0, _noteTextEdit.GetCaretColumn(0));
		}

		int lastLine = Math.Max(0, _noteTextEdit.GetLineCount() - 1);
		int lastColumn = CountUnicodeScalars(_noteTextEdit.GetLine(lastLine) ?? "");

		if (wrapImmediately)
		{
			Vector2I wrappedResult = _noteTextEdit.Search(
				_noteSearchQuery,
				(uint)TextEdit.SearchFlags.Backwards,
				lastLine,
				lastColumn
			);

			if (wrappedResult.X >= 0 && wrappedResult.Y >= 0)
				SelectNoteSearchMatch(wrappedResult, _noteSearchQuery);

			return;
		}

		TryNavigateNoteSearch(
			_noteSearchQuery,
			(uint)TextEdit.SearchFlags.Backwards,
			startLine,
			startColumn,
			lastLine,
			lastColumn
		);
	}

	private bool CanNavigateNoteSearch()
	{
		return IsNoteSearchVisible()
			&& IsValidGodotObject(_noteTextEdit)
			&& IsValidGodotObject(_noteSearchInput)
			&& !string.IsNullOrEmpty(_noteSearchQuery);
	}

	private bool TryNavigateNoteSearch(
		string query,
		uint flags,
		int startLine,
		int startColumn,
		int wrapLine,
		int wrapColumn
	)
	{
		Vector2I result = _noteTextEdit.Search(query, flags, startLine, startColumn);
		if (result.X < 0 || result.Y < 0)
			result = _noteTextEdit.Search(query, flags, wrapLine, wrapColumn);

		if (result.X < 0 || result.Y < 0)
			return false;

		SelectNoteSearchMatch(result, query);
		return true;
	}

	private void SelectNoteSearchMatch(Vector2I result, string query)
	{
		int matchLength = CountUnicodeScalars(query);
		if (matchLength <= 0)
			return;

		int line = Math.Max(0, result.Y);
		int column = Math.Max(0, result.X);

		_noteTextEdit.RemoveSecondaryCarets();
		_noteTextEdit.Select(line, column, line, column + matchLength, 0);
		_noteTextEdit.AdjustViewportToCaret(0);
	}

	private bool DoesPrimaryNoteSelectionMatchQuery(string query)
	{
		return !string.IsNullOrEmpty(query)
			&& _noteTextEdit.HasSelection(0)
			&& string.Equals(
				_noteTextEdit.GetSelectedText(0),
				query,
				StringComparison.OrdinalIgnoreCase
			);
	}

	private void MoveNoteSearchPositionOneScalarBackward(ref int line, ref int column)
	{
		if (column > 0)
		{
			column--;
			return;
		}

		if (line <= 0)
			return;

		line--;
		column = CountUnicodeScalars(_noteTextEdit.GetLine(line) ?? "");
	}

	private static int CountUnicodeScalars(string value)
	{
		if (string.IsNullOrEmpty(value))
			return 0;

		int count = 0;
		foreach (Rune _ in value.EnumerateRunes())
			count++;

		return count;
	}

	private void RestoreNoteEditingFocusAfterFailedCloseSave()
	{
		if (IsNoteSearchVisible() && IsValidGodotObject(_noteSearchInput))
		{
			_noteSearchInput.GrabFocus();
			_noteSearchInput.Edit(true);
			return;
		}

		if (IsValidGodotObject(_noteTextEdit))
			_noteTextEdit.GrabFocus(true);
	}

	private void ResetNoteSearchStateForSessionEnd()
	{
		_noteSearchQuery = "";
		_resettingNoteSearchState = true;

		try
		{
			if (IsValidGodotObject(_noteSearchOverlay))
				_noteSearchOverlay.Visible = false;

			if (IsValidGodotObject(_noteSearchInput))
			{
				_noteSearchInput.Text = "";
				_noteSearchInput.Unedit();
			}

			if (IsValidGodotObject(_noteTextEdit))
				_noteTextEdit.SetSearchText("");

			if (IsValidGodotObject(_noteDialog))
				_noteDialog.DialogCloseOnEscape = true;
		}
		finally
		{
			_resettingNoteSearchState = false;
		}
	}

	private void ClearNoteSearchControlReferences()
	{
		_noteSearchOverlayContent = null;
		_noteSearchOverlayInnerMargin = null;
		_noteSearchOverlay = null;
		_noteSearchInput = null;
		_noteSearchNextButton = null;
		_noteSearchPreviousButton = null;
		_noteSearchCloseButton = null;
		_noteSearchQuery = "";
		_resettingNoteSearchState = false;
	}
}
#endif
