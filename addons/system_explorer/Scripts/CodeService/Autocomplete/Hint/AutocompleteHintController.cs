#if TOOLS
using Godot;
using System;

namespace SystemExplorer.CodeService.Autocomplete.Hint;

internal sealed class AutocompleteHintController
{
	internal const double SelectionDwellSeconds = 0.50;

	private readonly AutocompletePrefixExtractor _prefixExtractor;
	private readonly Action _processWorkChanged;
	private readonly AutocompleteHintView _view = new();

	private CodeEdit _boundCodeEdit;
	private ulong _boundCodeEditInstanceId;
	private bool _trackingActive;
	private int? _mousePinnedLineOffset;
	private string _mousePinnedPrefix = "";
	private int _mousePinnedOptionCount;
	private int _lastObservedSelectedIndex = -1;
	private bool _mouseRowClickAwaitingObservation;
	private int _dwellSelectedIndex = -1;
	private string _dwellPrefix = "";
	private int _dwellOptionCount;
	private double _dwellElapsedSeconds;
	private NativeSelectionNavigationHold _heldNavigationKeys;

	internal AutocompleteHintController(
		AutocompletePrefixExtractor prefixExtractor,
		Action processWorkChanged)
	{
		_prefixExtractor = prefixExtractor ?? throw new ArgumentNullException(nameof(prefixExtractor));
		_processWorkChanged = processWorkChanged ?? throw new ArgumentNullException(nameof(processWorkChanged));
	}

	internal bool HasProcessWork => _trackingActive;

	internal void Activate(CodeEdit codeEdit)
	{
		if (!IsValidGodotObject(codeEdit))
		{
			Retire();
			return;
		}

		ulong instanceId;
		try
		{
			instanceId = codeEdit.GetInstanceId();
		}
		catch
		{
			Retire();
			return;
		}

		if (
			IsValidGodotObject(_boundCodeEdit)
			&& _boundCodeEditInstanceId == instanceId
			&& _boundCodeEdit.GetInstanceId() == instanceId
		)
		{
			if (!_view.TryBind(codeEdit))
			{
				Reset();
				return;
			}

			SetTrackingActive(true);
			return;
		}

		_view.Reset();
		ResetSelectionObservation();
		_boundCodeEdit = codeEdit;
		_boundCodeEditInstanceId = instanceId;
		if (!_view.TryBind(codeEdit))
		{
			_boundCodeEdit = null;
			_boundCodeEditInstanceId = 0;
			SetTrackingActive(false);
			return;
		}

		SetTrackingActive(true);
	}

	internal void ObserveGuiInput(InputEvent inputEvent)
	{
		if (!_trackingActive || inputEvent == null || !IsValidGodotObject(_boundCodeEdit))
			return;

		if (inputEvent is InputEventKey keyEvent)
		{
			NativeSelectionNavigationHold navigationKeys = GetNativeSelectionNavigationHold(keyEvent);
			if (navigationKeys != NativeSelectionNavigationHold.None)
			{
				ClearMouseLineOffsetPin();
				UpdateHeldNavigationKeys(navigationKeys, keyEvent.Pressed);
				RestartDwellObservation();
				return;
			}
		}

		if (IsNativePointerSelectionNavigation(inputEvent))
		{
			ClearMouseLineOffsetPin();
			RestartDwellObservation();
			return;
		}

		if (inputEvent is not InputEventMouseButton mouseButton
			|| !mouseButton.Pressed
			|| mouseButton.ButtonIndex != MouseButton.Left)
		{
			return;
		}

		CodeEdit codeEdit = _boundCodeEdit;
		if (!TryGetActiveNativeSelection(codeEdit, out int selectedIndex, out int optionCount))
			return;

		if (!_prefixExtractor.TryExtract(codeEdit, out AutocompletePrefixCapture prefixCapture))
			return;

		if (!IsMousePinCompatible(prefixCapture.Prefix, optionCount))
			ClearMouseLineOffsetPin();

		if (!AutocompleteHintGeometry.TryGetHintLayout(
			codeEdit,
			selectedIndex,
			prefixCapture.Prefix,
			AutocompleteHintView.HintSize,
			_mousePinnedLineOffset,
			out AutocompleteHintLayout layout))
		{
			return;
		}

		if (layout.CompletionScrollRect.Size.X > 0.0f
			&& layout.CompletionScrollRect.HasPoint(mouseButton.Position))
		{
			ClearMouseLineOffsetPin();
			RestartDwellObservation();
			return;
		}

		if (!layout.CompletionBodyRect.HasPoint(mouseButton.Position))
			return;

		_mousePinnedLineOffset = layout.LineOffset;
		_mousePinnedPrefix = prefixCapture.Prefix;
		_mousePinnedOptionCount = optionCount;
		_lastObservedSelectedIndex = selectedIndex;
		_mouseRowClickAwaitingObservation = true;
		RestartDwellObservation();
	}

	internal void ProcessFrame(CodeEdit codeEdit, double delta)
	{
		if (!_trackingActive)
			return;

		if (!IsCurrentBoundCodeEdit(codeEdit))
		{
			Reset();
			return;
		}

		// The same CodeEdit can be reparented to a different editor Window. Keep the
		// transient overlay binding current without moving Window identity into the
		// controller's selection/dwell geometry state.
		if (!_view.TryBind(codeEdit))
		{
			Reset();
			return;
		}

		if (!TryGetActiveNativeSelection(codeEdit, out int selectedIndex, out int optionCount))
		{
			Retire();
			return;
		}

		if (_heldNavigationKeys != NativeSelectionNavigationHold.None)
		{
			RestartDwellObservation();
			_lastObservedSelectedIndex = selectedIndex;
			_mouseRowClickAwaitingObservation = false;
			return;
		}

		if (!_prefixExtractor.TryExtract(codeEdit, out AutocompletePrefixCapture prefixCapture))
		{
			RestartDwellObservation();
			return;
		}

		if (_mousePinnedLineOffset.HasValue)
		{
			if (!IsMousePinCompatible(prefixCapture.Prefix, optionCount))
			{
				ClearMouseLineOffsetPin();
			}
			else if (!_mouseRowClickAwaitingObservation
				&& _lastObservedSelectedIndex >= 0
				&& selectedIndex != _lastObservedSelectedIndex)
			{
				ClearMouseLineOffsetPin();
			}
		}

		if (!AutocompleteHintGeometry.TryGetHintLayout(
			codeEdit,
			selectedIndex,
			prefixCapture.Prefix,
			AutocompleteHintView.HintSize,
			_mousePinnedLineOffset,
			out AutocompleteHintLayout layout))
		{
			RestartDwellObservation();
			_lastObservedSelectedIndex = selectedIndex;
			_mouseRowClickAwaitingObservation = false;
			return;
		}

		if (!IsDwellCandidateCurrent(selectedIndex, prefixCapture.Prefix, optionCount))
		{
			BeginDwellObservation(selectedIndex, prefixCapture.Prefix, optionCount);
		}
		else
		{
			_dwellElapsedSeconds += SanitizeFrameDelta(delta);
		}

		if (_dwellElapsedSeconds >= SelectionDwellSeconds)
			_view.ShowAtWindowPosition(layout.HintWindowPosition);
		else
			_view.Hide();

		_lastObservedSelectedIndex = selectedIndex;
		_mouseRowClickAwaitingObservation = false;
	}

	internal void Retire()
	{
		_view.Hide();
		ResetSelectionObservation();
		SetTrackingActive(false);
	}

	internal void Reset()
	{
		SetTrackingActive(false);
		_view.Reset();
		ResetSelectionObservation();
		_boundCodeEdit = null;
		_boundCodeEditInstanceId = 0;
	}

	internal void Shutdown()
	{
		Reset();
	}

	private bool IsCurrentBoundCodeEdit(CodeEdit codeEdit)
	{
		if (!IsValidGodotObject(codeEdit) || !IsValidGodotObject(_boundCodeEdit))
			return false;

		try
		{
			ulong instanceId = codeEdit.GetInstanceId();
			return instanceId == _boundCodeEditInstanceId
				&& _boundCodeEdit.GetInstanceId() == _boundCodeEditInstanceId;
		}
		catch
		{
			return false;
		}
	}

	private static bool TryGetActiveNativeSelection(
		CodeEdit codeEdit,
		out int selectedIndex,
		out int optionCount)
	{
		selectedIndex = -1;
		optionCount = 0;
		if (!IsValidGodotObject(codeEdit))
			return false;

		try
		{
			selectedIndex = codeEdit.GetCodeCompletionSelectedIndex();
			if (selectedIndex < 0)
				return false;

			var options = codeEdit.GetCodeCompletionOptions();
			optionCount = options?.Count ?? 0;
			return optionCount > 0 && selectedIndex < optionCount;
		}
		catch
		{
			selectedIndex = -1;
			optionCount = 0;
			return false;
		}
	}

	private bool IsMousePinCompatible(string currentPrefix, int optionCount)
	{
		return _mousePinnedLineOffset.HasValue
			&& optionCount == _mousePinnedOptionCount
			&& string.Equals(currentPrefix ?? "", _mousePinnedPrefix, StringComparison.Ordinal);
	}

	private bool IsDwellCandidateCurrent(int selectedIndex, string currentPrefix, int optionCount)
	{
		return selectedIndex == _dwellSelectedIndex
			&& optionCount == _dwellOptionCount
			&& string.Equals(currentPrefix ?? "", _dwellPrefix, StringComparison.Ordinal);
	}

	private void BeginDwellObservation(int selectedIndex, string currentPrefix, int optionCount)
	{
		_view.Hide();
		_dwellSelectedIndex = selectedIndex;
		_dwellPrefix = currentPrefix ?? "";
		_dwellOptionCount = optionCount;
		_dwellElapsedSeconds = 0.0;
	}

	private void RestartDwellObservation()
	{
		_view.Hide();
		_dwellSelectedIndex = -1;
		_dwellPrefix = "";
		_dwellOptionCount = 0;
		_dwellElapsedSeconds = 0.0;
	}

	private static NativeSelectionNavigationHold GetNativeSelectionNavigationHold(InputEventKey keyEvent)
	{
		NativeSelectionNavigationHold result = NativeSelectionNavigationHold.None;
		if (keyEvent.IsAction("ui_up", true))
			result |= NativeSelectionNavigationHold.Up;
		if (keyEvent.IsAction("ui_down", true))
			result |= NativeSelectionNavigationHold.Down;
		if (keyEvent.IsAction("ui_page_up", true))
			result |= NativeSelectionNavigationHold.PageUp;
		if (keyEvent.IsAction("ui_page_down", true))
			result |= NativeSelectionNavigationHold.PageDown;

		return result;
	}

	private void UpdateHeldNavigationKeys(
		NativeSelectionNavigationHold navigationKeys,
		bool pressed)
	{
		if (pressed)
			_heldNavigationKeys |= navigationKeys;
		else
			_heldNavigationKeys &= ~navigationKeys;
	}

	private static bool IsNativePointerSelectionNavigation(InputEvent inputEvent)
	{
		if (inputEvent is InputEventPanGesture)
			return true;

		if (inputEvent is not InputEventMouseButton mouseButton || !mouseButton.Pressed)
			return false;

		return mouseButton.ButtonIndex == MouseButton.WheelUp
			|| mouseButton.ButtonIndex == MouseButton.WheelDown;
	}

	private void ClearMouseLineOffsetPin()
	{
		_mousePinnedLineOffset = null;
		_mousePinnedPrefix = "";
		_mousePinnedOptionCount = 0;
		_mouseRowClickAwaitingObservation = false;
	}

	private void ResetSelectionObservation()
	{
		ClearMouseLineOffsetPin();
		_lastObservedSelectedIndex = -1;
		_heldNavigationKeys = NativeSelectionNavigationHold.None;
		RestartDwellObservation();
	}

	private void SetTrackingActive(bool active)
	{
		if (_trackingActive == active)
			return;

		_trackingActive = active;
		try
		{
			_processWorkChanged();
		}
		catch
		{
		}
	}

	private static double SanitizeFrameDelta(double delta)
	{
		if (double.IsNaN(delta) || double.IsInfinity(delta) || delta <= 0.0)
			return 0.0;

		return delta;
	}

	[Flags]
	private enum NativeSelectionNavigationHold
	{
		None = 0,
		Up = 1 << 0,
		Down = 1 << 1,
		PageUp = 1 << 2,
		PageDown = 1 << 3
	}

	private static bool IsValidGodotObject(GodotObject source)
	{
		return source != null && GodotObject.IsInstanceValid(source);
	}
}
#endif
