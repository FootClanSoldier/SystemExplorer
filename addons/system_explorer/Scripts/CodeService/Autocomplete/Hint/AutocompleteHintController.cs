#if TOOLS
using Godot;
using System;

namespace SystemExplorer.CodeService.Autocomplete.Hint;

internal sealed class AutocompleteHintController
{
	internal const double SelectionDwellSeconds = 0.50;

	private const float CompactWidthSafetyMargin = 6.0f;
	private const float CompactWidthSearchResolution = 1.0f;
	private const int CompactWidthSearchIterationLimit = 12;

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
	private long _dwellRequestGeneration = long.MinValue;
	private long _lastObservedRequestGeneration = long.MinValue;
	private double _dwellElapsedSeconds;
	private NativeSelectionNavigationHold _heldNavigationKeys;
	private PreparedHintPresentation? _preparedPresentation;
	private ActiveHintPresentation? _activePresentation;

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

		if (!AutocompleteHintGeometry.TryGetHintAnchorLayout(
			codeEdit,
			selectedIndex,
			prefixCapture.Prefix,
			_mousePinnedLineOffset,
			out AutocompleteHintAnchorLayout layout))
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

	internal void ProcessFrame(CodeEdit codeEdit, AutocompleteHintContent content, double delta)
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

		if (content == null)
		{
			RestartDwellObservation();
			_lastObservedSelectedIndex = selectedIndex;
			_mouseRowClickAwaitingObservation = false;
			return;
		}

		ObservePublicationGeneration(content.RequestGeneration);

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

		if (!AutocompleteHintGeometry.TryGetHintAnchorLayout(
			codeEdit,
			selectedIndex,
			prefixCapture.Prefix,
			_mousePinnedLineOffset,
			out AutocompleteHintAnchorLayout layout))
		{
			RestartDwellObservation();
			_lastObservedSelectedIndex = selectedIndex;
			_mouseRowClickAwaitingObservation = false;
			return;
		}

		if (!IsDwellCandidateCurrent(
			selectedIndex,
			prefixCapture.Prefix,
			optionCount,
			content.RequestGeneration))
		{
			BeginDwellObservation(
				selectedIndex,
				prefixCapture.Prefix,
				optionCount,
				content.RequestGeneration);
		}
		else
		{
			_dwellElapsedSeconds += SanitizeFrameDelta(delta);
		}

		if (_dwellElapsedSeconds >= SelectionDwellSeconds)
		{
			var identity = new HintSelectionIdentity(
				selectedIndex,
				prefixCapture.Prefix ?? "",
				optionCount,
				content.RequestGeneration
			);

			if (!ProcessStablePresentation(identity, content, layout))
				ClearPresentation();
		}
		else
		{
			ClearPresentation();
		}

		_lastObservedSelectedIndex = selectedIndex;
		_mouseRowClickAwaitingObservation = false;
	}

	internal void Retire()
	{
		ClearPresentation();
		ResetSelectionObservation();
		SetTrackingActive(false);
	}

	internal void Reset()
	{
		SetTrackingActive(false);
		_preparedPresentation = null;
		_activePresentation = null;
		_view.Reset();
		ResetSelectionObservation();
		_boundCodeEdit = null;
		_boundCodeEditInstanceId = 0;
	}

	internal void Shutdown()
	{
		Reset();
	}

	private bool ProcessStablePresentation(
		HintSelectionIdentity identity,
		AutocompleteHintContent content,
		AutocompleteHintAnchorLayout layout)
	{
		if (_activePresentation is ActiveHintPresentation active
			&& active.Identity == identity)
		{
			if (!TryGetHorizontalPlacement(
				layout,
				active.NaturalOuterWidth,
				out AutocompleteHintHorizontalPlacement activeBaselinePlacement))
			{
				return false;
			}

			if (!Mathf.IsEqualApprox(
				active.BaselineOuterWidth,
				activeBaselinePlacement.Width))
			{
				return TryMeasureAndPreparePresentation(
					identity,
					active.Text,
					active.NaturalOuterWidth,
					layout,
					activeBaselinePlacement);
			}

			if (!TryGetHorizontalPlacement(
				layout,
				active.MeasurementOuterWidth,
				out AutocompleteHintHorizontalPlacement activeHorizontalPlacement)
				|| !Mathf.IsEqualApprox(
					active.MeasurementOuterWidth,
					activeHorizontalPlacement.Width))
			{
				return TryMeasureAndPreparePresentation(
					identity,
					active.Text,
					active.NaturalOuterWidth,
					layout,
					activeBaselinePlacement);
			}

			if (!AutocompleteHintGeometry.TryGetFinalHintLayout(
				layout,
				activeHorizontalPlacement,
				active.MeasuredHeight,
				out Vector2 activeWindowPosition,
				out Vector2 activeHintSize))
			{
				return false;
			}

			if (!ApproximatelyEqual(active.HintSize, activeHintSize))
			{
				return TryPreparePresentation(
					identity,
					active.Text,
					active.NaturalOuterWidth,
					active.BaselineOuterWidth,
					active.MeasurementOuterWidth,
					active.MeasuredHeight,
					activeHintSize);
			}

			return _view.TryMoveVisible(activeWindowPosition);
		}

		if (_preparedPresentation is PreparedHintPresentation prepared
			&& prepared.Identity == identity)
		{
			if (!TryGetHorizontalPlacement(
				layout,
				prepared.NaturalOuterWidth,
				out AutocompleteHintHorizontalPlacement preparedBaselinePlacement))
			{
				return false;
			}

			if (!Mathf.IsEqualApprox(
				prepared.BaselineOuterWidth,
				preparedBaselinePlacement.Width))
			{
				return TryMeasureAndPreparePresentation(
					identity,
					prepared.Text,
					prepared.NaturalOuterWidth,
					layout,
					preparedBaselinePlacement);
			}

			if (!TryGetHorizontalPlacement(
				layout,
				prepared.MeasurementOuterWidth,
				out AutocompleteHintHorizontalPlacement preparedHorizontalPlacement)
				|| !Mathf.IsEqualApprox(
					prepared.MeasurementOuterWidth,
					preparedHorizontalPlacement.Width))
			{
				return TryMeasureAndPreparePresentation(
					identity,
					prepared.Text,
					prepared.NaturalOuterWidth,
					layout,
					preparedBaselinePlacement);
			}

			if (!AutocompleteHintGeometry.TryGetFinalHintLayout(
				layout,
				preparedHorizontalPlacement,
				prepared.MeasuredHeight,
				out Vector2 preparedWindowPosition,
				out Vector2 preparedHintSize))
			{
				return false;
			}

			if (!ApproximatelyEqual(prepared.PreparedHintSize, preparedHintSize))
			{
				return TryPreparePresentation(
					identity,
					prepared.Text,
					prepared.NaturalOuterWidth,
					prepared.BaselineOuterWidth,
					prepared.MeasurementOuterWidth,
					prepared.MeasuredHeight,
					preparedHintSize);
			}

			if (!_view.TryRevealPreparedAtWindowPosition(
				preparedHintSize,
				preparedWindowPosition))
			{
				return false;
			}

			_activePresentation = new ActiveHintPresentation(
				identity,
				prepared.Text,
				prepared.NaturalOuterWidth,
				prepared.BaselineOuterWidth,
				prepared.MeasurementOuterWidth,
				prepared.MeasuredHeight,
				preparedHintSize
			);
			_preparedPresentation = null;
			return true;
		}

		string hintText = AutocompleteHintTextFormatter.Format(content);
		if (!_view.TryMeasurePreferredWidth(hintText, out float naturalOuterWidth)
			|| !TryGetHorizontalPlacement(
				layout,
				naturalOuterWidth,
				out AutocompleteHintHorizontalPlacement baselineHorizontalPlacement))
		{
			return false;
		}

		return TryMeasureAndPreparePresentation(
			identity,
			hintText,
			naturalOuterWidth,
			layout,
			baselineHorizontalPlacement);
	}

	private bool TryMeasureAndPreparePresentation(
		HintSelectionIdentity identity,
		string text,
		float naturalOuterWidth,
		AutocompleteHintAnchorLayout layout,
		AutocompleteHintHorizontalPlacement baselineHorizontalPlacement)
	{
		if (!TryResolveCompactMeasurement(
				text,
				baselineHorizontalPlacement.Width,
				out float measurementOuterWidth,
				out Vector2 measuredHintSize)
			|| !TryGetHorizontalPlacement(
				layout,
				measurementOuterWidth,
				out AutocompleteHintHorizontalPlacement measuredHorizontalPlacement))
		{
			return false;
		}

		if (!Mathf.IsEqualApprox(
			measurementOuterWidth,
			measuredHorizontalPlacement.Width))
		{
			measurementOuterWidth = measuredHorizontalPlacement.Width;
			if (!_view.TryMeasureForWidth(
				text,
				measurementOuterWidth,
				out measuredHintSize,
				out _))
			{
				return false;
			}
		}

		if (!AutocompleteHintGeometry.TryGetFinalHintLayout(
			layout,
			measuredHorizontalPlacement,
			measuredHintSize.Y,
			out _,
			out Vector2 finalHintSize))
		{
			return false;
		}

		return TryPreparePresentation(
			identity,
			text,
			naturalOuterWidth,
			baselineHorizontalPlacement.Width,
			measurementOuterWidth,
			measuredHintSize.Y,
			finalHintSize);
	}

	private bool TryResolveCompactMeasurement(
		string text,
		float baselineOuterWidth,
		out float measurementOuterWidth,
		out Vector2 measuredHintSize)
	{
		measurementOuterWidth = 0.0f;
		measuredHintSize = default;
		if (!_view.TryMeasureForWidth(
			text,
			baselineOuterWidth,
			out Vector2 baselineHintSize,
			out int baselineVisualLineCount))
		{
			return false;
		}

		measurementOuterWidth = baselineOuterWidth;
		measuredHintSize = baselineHintSize;

		int logicalLineCount = CountLogicalLines(text);
		float minimumSearchWidth = Math.Min(
			AutocompleteHintView.WrappingMinimumHintWidth,
			baselineOuterWidth
		);
		if (baselineVisualLineCount <= logicalLineCount
			|| baselineOuterWidth - minimumSearchWidth <= CompactWidthSearchResolution)
		{
			return true;
		}

		if (!_view.TryMeasureForWidth(
			text,
			minimumSearchWidth,
			out Vector2 minimumHintSize,
			out int minimumVisualLineCount))
		{
			return true;
		}

		float smallestSameLineWidth;
		Vector2 smallestSameLineHintSize;
		if (minimumVisualLineCount == baselineVisualLineCount)
		{
			smallestSameLineWidth = minimumSearchWidth;
			smallestSameLineHintSize = minimumHintSize;
		}
		else if (minimumVisualLineCount < baselineVisualLineCount)
		{
			// Text wrapping should not decrease as width becomes smaller. Fail-soft by
			// retaining the baseline measurement if the shaping engine violates that
			// monotonic assumption for this string/theme combination.
			return true;
		}
		else
		{
			float tooNarrowWidth = minimumSearchWidth;
			float sameLineWidth = baselineOuterWidth;
			Vector2 sameLineHintSize = baselineHintSize;

			for (int iteration = 0;
				iteration < CompactWidthSearchIterationLimit
					&& sameLineWidth - tooNarrowWidth > CompactWidthSearchResolution;
				iteration++)
			{
				float candidateWidth = (tooNarrowWidth + sameLineWidth) * 0.5f;
				if (!_view.TryMeasureForWidth(
					text,
					candidateWidth,
					out Vector2 candidateHintSize,
					out int candidateVisualLineCount))
				{
					return true;
				}

				if (candidateVisualLineCount == baselineVisualLineCount)
				{
					sameLineWidth = candidateWidth;
					sameLineHintSize = candidateHintSize;
				}
				else if (candidateVisualLineCount > baselineVisualLineCount)
				{
					tooNarrowWidth = candidateWidth;
				}
				else
				{
					return true;
				}
			}

			smallestSameLineWidth = sameLineWidth;
			smallestSameLineHintSize = sameLineHintSize;
		}

		float compactWidth = Math.Min(
			baselineOuterWidth,
			MathF.Ceiling(smallestSameLineWidth + CompactWidthSafetyMargin)
		);
		if (compactWidth >= baselineOuterWidth - CompactWidthSearchResolution)
			return true;

		if (Mathf.IsEqualApprox(compactWidth, smallestSameLineWidth))
		{
			measurementOuterWidth = compactWidth;
			measuredHintSize = smallestSameLineHintSize;
			return true;
		}

		if (!_view.TryMeasureForWidth(
			text,
			compactWidth,
			out Vector2 compactHintSize,
			out int compactVisualLineCount)
			|| compactVisualLineCount != baselineVisualLineCount)
		{
			return true;
		}

		measurementOuterWidth = compactWidth;
		measuredHintSize = compactHintSize;
		return true;
	}

	private static int CountLogicalLines(string text)
	{
		if (text == null)
			return 0;

		int lineCount = 1;
		for (int index = 0; index < text.Length; index++)
		{
			if (text[index] == '\n')
				lineCount++;
		}

		return lineCount;
	}

	private bool TryPreparePresentation(
		HintSelectionIdentity identity,
		string text,
		float naturalOuterWidth,
		float baselineOuterWidth,
		float measurementOuterWidth,
		float measuredHeight,
		Vector2 hintSize)
	{
		_activePresentation = null;
		_preparedPresentation = null;
		if (!_view.TryPrepareHidden(hintSize, text))
			return false;

		_preparedPresentation = new PreparedHintPresentation(
			identity,
			text,
			naturalOuterWidth,
			baselineOuterWidth,
			measurementOuterWidth,
			measuredHeight,
			hintSize
		);
		return true;
	}

	private static bool TryGetHorizontalPlacement(
		AutocompleteHintAnchorLayout layout,
		float naturalOuterWidth,
		out AutocompleteHintHorizontalPlacement horizontalPlacement)
	{
		float minimumPresentationWidth = Math.Min(
			AutocompleteHintView.WrappingMinimumHintWidth,
			Math.Max(AutocompleteHintView.CompactMinimumHintWidth, naturalOuterWidth)
		);

		return AutocompleteHintGeometry.TryGetHorizontalPlacement(
			layout,
			naturalOuterWidth,
			minimumPresentationWidth,
			AutocompleteHintView.MaximumHintWidth,
			out horizontalPlacement);
	}

	private void ClearPresentation()
	{
		_preparedPresentation = null;
		_activePresentation = null;
		_view.Hide();
	}

	private static bool ApproximatelyEqual(Vector2 left, Vector2 right)
	{
		return Mathf.IsEqualApprox(left.X, right.X)
			&& Mathf.IsEqualApprox(left.Y, right.Y);
	}

	private readonly record struct HintSelectionIdentity(
		int SelectedIndex,
		string Prefix,
		int OptionCount,
		long RequestGeneration);

	private readonly record struct PreparedHintPresentation(
		HintSelectionIdentity Identity,
		string Text,
		float NaturalOuterWidth,
		float BaselineOuterWidth,
		float MeasurementOuterWidth,
		float MeasuredHeight,
		Vector2 PreparedHintSize);

	private readonly record struct ActiveHintPresentation(
		HintSelectionIdentity Identity,
		string Text,
		float NaturalOuterWidth,
		float BaselineOuterWidth,
		float MeasurementOuterWidth,
		float MeasuredHeight,
		Vector2 HintSize);

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

	private bool IsDwellCandidateCurrent(
		int selectedIndex,
		string currentPrefix,
		int optionCount,
		long requestGeneration)
	{
		return selectedIndex == _dwellSelectedIndex
			&& optionCount == _dwellOptionCount
			&& requestGeneration == _dwellRequestGeneration
			&& string.Equals(currentPrefix ?? "", _dwellPrefix, StringComparison.Ordinal);
	}

	private void BeginDwellObservation(
		int selectedIndex,
		string currentPrefix,
		int optionCount,
		long requestGeneration)
	{
		ClearPresentation();
		_dwellSelectedIndex = selectedIndex;
		_dwellPrefix = currentPrefix ?? "";
		_dwellOptionCount = optionCount;
		_dwellRequestGeneration = requestGeneration;
		_dwellElapsedSeconds = 0.0;
	}

	private void RestartDwellObservation()
	{
		ClearPresentation();
		_dwellSelectedIndex = -1;
		_dwellPrefix = "";
		_dwellOptionCount = 0;
		_dwellRequestGeneration = long.MinValue;
		_dwellElapsedSeconds = 0.0;
	}

	private void ObservePublicationGeneration(long requestGeneration)
	{
		if (_lastObservedRequestGeneration == requestGeneration)
			return;

		if (_lastObservedRequestGeneration != long.MinValue)
			ClearMouseLineOffsetPin();
		RestartDwellObservation();
		_lastObservedRequestGeneration = requestGeneration;
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
		_lastObservedRequestGeneration = long.MinValue;
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
