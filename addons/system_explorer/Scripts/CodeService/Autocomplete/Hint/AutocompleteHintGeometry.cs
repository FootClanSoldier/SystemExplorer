#if TOOLS
using Godot;
using System;

namespace SystemExplorer.CodeService.Autocomplete.Hint;

internal readonly struct AutocompleteHintLayout
{
	internal AutocompleteHintLayout(
		Vector2 hintWindowPosition,
		Rect2 completionBodyRect,
		Rect2 completionScrollRect,
		int lineOffset,
		int optionCount,
		int visibleLines)
	{
		HintWindowPosition = hintWindowPosition;
		CompletionBodyRect = completionBodyRect;
		CompletionScrollRect = completionScrollRect;
		LineOffset = lineOffset;
		OptionCount = optionCount;
		VisibleLines = visibleLines;
	}

	internal Vector2 HintWindowPosition { get; }
	internal Rect2 CompletionBodyRect { get; }
	internal Rect2 CompletionScrollRect { get; }
	internal int LineOffset { get; }
	internal int OptionCount { get; }
	internal int VisibleLines { get; }
}

internal static class AutocompleteHintGeometry
{
	internal const float HintGap = 0.0f;

	private const string FontThemeKey = "font";
	private const string FontSizeThemeKey = "font_size";
	private const string CompletionStyleboxThemeKey = "completion";
	private const string CompletionLinesThemeKey = "completion_lines";
	private const string CompletionMaxWidthThemeKey = "completion_max_width";
	private const string CompletionScrollWidthThemeKey = "completion_scroll_width";
	private const string HorizontalSeparationThemeKey = "h_separation";
	private const string ItemListThemeType = "ItemList";
	private const string LineSpacingThemeKey = "line_spacing";

	internal static bool TryGetHintLayout(
		CodeEdit codeEdit,
		int selectedIndex,
		string currentPrefix,
		Vector2 hintSize,
		int? preservedLineOffset,
		out AutocompleteHintLayout layout)
	{
		layout = default;
		if (!IsValidGodotObject(codeEdit)
			|| selectedIndex < 0
			|| currentPrefix == null
			|| !IsFinitePositive(hintSize.X)
			|| !IsFinitePositive(hintSize.Y))
		{
			return false;
		}

		try
		{
			Window editorWindow = codeEdit.GetWindow();
			if (!IsValidGodotObject(editorWindow))
				return false;

			Vector2I editorWindowSizePixels = editorWindow.Size;
			var editorWindowSize = new Vector2(editorWindowSizePixels.X, editorWindowSizePixels.Y);
			if (!IsFinitePositive(editorWindowSize.X) || !IsFinitePositive(editorWindowSize.Y))
				return false;

			Transform2D codeEditToWindow = codeEdit.GetGlobalTransformWithCanvas();
			Vector2 codeEditWindowOrigin = codeEditToWindow.Origin;
			if (!IsFinite(codeEditWindowOrigin.X) || !IsFinite(codeEditWindowOrigin.Y))
				return false;

			var options = codeEdit.GetCodeCompletionOptions();
			int optionCount = options?.Count ?? 0;
			if (optionCount <= 0 || selectedIndex >= optionCount)
				return false;

			int rowHeight = codeEdit.GetLineHeight();
			if (rowHeight <= 0)
				return false;

			Vector2 editorSize = codeEdit.Size;
			if (!IsFinitePositive(editorSize.X) || !IsFinitePositive(editorSize.Y))
				return false;

			Font font = codeEdit.GetThemeFont(FontThemeKey);
			int fontSize = codeEdit.GetThemeFontSize(FontSizeThemeKey);
			StyleBox completionStyle = codeEdit.GetThemeStylebox(CompletionStyleboxThemeKey);
			if (!IsValidGodotObject(font) || fontSize <= 0 || !IsValidGodotObject(completionStyle))
				return false;

			int configuredCompletionLines = codeEdit.GetThemeConstant(CompletionLinesThemeKey);
			int completionMaxWidth = codeEdit.GetThemeConstant(CompletionMaxWidthThemeKey);
			int completionScrollWidth = codeEdit.GetThemeConstant(CompletionScrollWidthThemeKey);
			int horizontalSeparation = codeEdit.GetThemeConstant(
				HorizontalSeparationThemeKey,
				ItemListThemeType
			);
			int lineSpacing = codeEdit.GetThemeConstant(LineSpacingThemeKey);
			if (configuredCompletionLines <= 0 || completionMaxWidth <= 0)
				return false;

			completionScrollWidth = Math.Max(0, completionScrollWidth);
			horizontalSeparation = Math.Max(0, horizontalSeparation);

			Vector2 styleMinimumSize = completionStyle.GetMinimumSize();
			if (!IsFiniteNonNegative(styleMinimumSize.X) || !IsFiniteNonNegative(styleMinimumSize.Y))
				return false;

			Vector2 caretPosition = codeEdit.GetCaretDrawPos();
			if (!IsFinite(caretPosition.X) || !IsFinite(caretPosition.Y))
				return false;

			long maximumTextWidthLong = (long)completionMaxWidth * fontSize;
			if (maximumTextWidthLong <= 0)
				return false;

			int maximumTextWidth = maximumTextWidthLong > int.MaxValue
				? int.MaxValue
				: (int)maximumTextWidthLong;
			int maximumMeasuredOptionWidth = 0;
			bool measuredAnyDisplayText = false;
			for (int optionIndex = 0; optionIndex < optionCount; optionIndex++)
			{
				var option = options[optionIndex];
				if (option == null)
					continue;

				Variant displayTextValue = option["display_text"];
				if (displayTextValue.VariantType != Variant.Type.String)
					continue;

				float measuredWidth = font.GetStringSize(
					displayTextValue.AsString(),
					HorizontalAlignment.Left,
					-1.0f,
					fontSize
				).X;
				if (!IsFiniteNonNegative(measuredWidth))
					continue;

				// Godot stores max_width/code_completion_longest_line as ints. Mirror
				// that truncation before taking the maximum so right-clamping does not
				// drift by fractional glyph widths. Color-valued completion options
				// reserve one additional line-height-wide swatch slot.
				int optionWidth = (int)measuredWidth;
				Variant defaultValue = option["default_value"];
				if (defaultValue.VariantType == Variant.Type.Color)
					optionWidth = SaturatingAdd(optionWidth, rowHeight);

				measuredAnyDisplayText = true;
				maximumMeasuredOptionWidth = Math.Max(maximumMeasuredOptionWidth, optionWidth);
			}
			if (!measuredAnyDisplayText)
				return false;

			int longestDisplayWidth = Math.Min(maximumMeasuredOptionWidth, maximumTextWidth);

			float measuredPrefixWidth = font.GetStringSize(
				currentPrefix,
				HorizontalAlignment.Left,
				-1.0f,
				fontSize
			).X;
			if (!IsFiniteNonNegative(measuredPrefixWidth))
				return false;

			// code_completion_base_width is also an int in Godot 4.6.
			int prefixWidth = (int)measuredPrefixWidth;
			int completionContentWidth = SaturatingAdd(
				SaturatingAdd(longestDisplayWidth, rowHeight),
				SaturatingAdd(horizontalSeparation, 2)
			);
			if (completionContentWidth <= 0)
				return false;

			int visibleLines = Math.Min(optionCount, configuredCompletionLines);
			if (visibleLines <= 0)
				return false;

			float completionRowsHeight = visibleLines * (float)rowHeight;
			float totalCompletionHeight = styleMinimumSize.Y + completionRowsHeight;
			float minY = caretPosition.Y - rowHeight;
			float maxY = caretPosition.Y + rowHeight + totalCompletionHeight;
			bool canFitAbove = minY > totalCompletionHeight;
			bool canFitBelow = maxY <= editorSize.Y;
			bool shouldPlaceAbove = !canFitBelow && canFitAbove;

			if (!canFitBelow && !canFitAbove)
			{
				float spaceAbove = caretPosition.Y - rowHeight;
				float spaceBelow = editorSize.Y - caretPosition.Y;
				shouldPlaceAbove = spaceAbove > spaceBelow;

				float spaceAvailable = shouldPlaceAbove
					? spaceAbove - styleMinimumSize.Y
					: spaceBelow - styleMinimumSize.Y;
				int maxLinesFit = Math.Max(1, (int)(spaceAvailable / rowHeight));
				visibleLines = Math.Min(visibleLines, maxLinesFit);
				completionRowsHeight = visibleLines * (float)rowHeight;
				totalCompletionHeight = styleMinimumSize.Y + completionRowsHeight;
			}

			int maximumLineOffset = Math.Max(0, optionCount - visibleLines);
			int centeredLineOffset = Math.Clamp(
				selectedIndex - visibleLines / 2,
				0,
				maximumLineOffset
			);
			int lineOffset = centeredLineOffset;
			if (preservedLineOffset.HasValue)
			{
				int candidateLineOffset = Math.Clamp(
					preservedLineOffset.Value,
					0,
					maximumLineOffset
				);
				int candidateVisibleRow = selectedIndex - candidateLineOffset;
				if (candidateVisibleRow >= 0 && candidateVisibleRow < visibleLines)
					lineOffset = candidateLineOffset;
			}

			int visibleSelectedRow = selectedIndex - lineOffset;
			if (visibleSelectedRow < 0 || visibleSelectedRow >= visibleLines)
				return false;

			float completionPopupY = shouldPlaceAbove
				? (caretPosition.Y - totalCompletionHeight - rowHeight) + lineSpacing
				: caretPosition.Y + lineSpacing / 2.0f;
			if (!IsFinite(completionPopupY))
				return false;

			int scrollWidth = optionCount > configuredCompletionLines
				? completionScrollWidth
				: 0;
			int completionTotalBodyWidth = SaturatingAdd(completionContentWidth, scrollWidth);

			// code_completion_rect is Rect2i in Godot 4.6. Its X assignment
			// therefore truncates the float result after the same right-clamp test.
			float unclampedCompletionLeft = caretPosition.X - prefixWidth;
			int completionLeft = unclampedCompletionLeft + completionTotalBodyWidth > editorSize.X
				? (int)(editorSize.X - completionTotalBodyWidth)
				: (int)unclampedCompletionLeft;

			Vector2 styleOffset = completionStyle.GetOffset();
			if (!IsFiniteNonNegative(styleOffset.X) || !IsFiniteNonNegative(styleOffset.Y))
				return false;

			// Native CodeEdit draws the completion StyleBox around the body rect as
			// Rect2(position - style.offset, size + style.minimum_size + scrollWidth).
			// Keep these visual edges CodeEdit-local, then convert only the sidecar
			// placement to the containing editor Window's viewport coordinate space.
			float completionVisualLeft = completionLeft - styleOffset.X;
			float completionVisualRight = completionVisualLeft
				+ completionTotalBodyWidth
				+ styleMinimumSize.X;
			if (!IsFinite(completionVisualLeft) || !IsFinite(completionVisualRight))
				return false;

			Vector2 completionWindowLeftPoint = codeEditToWindow * new Vector2(completionVisualLeft, 0.0f);
			Vector2 completionWindowRightPoint = codeEditToWindow * new Vector2(completionVisualRight, 0.0f);
			if (!IsFinite(completionWindowLeftPoint.X) || !IsFinite(completionWindowRightPoint.X))
				return false;

			float completionWindowLeft = Math.Min(
				completionWindowLeftPoint.X,
				completionWindowRightPoint.X
			);
			float completionWindowRight = Math.Max(
				completionWindowLeftPoint.X,
				completionWindowRightPoint.X
			);
			float windowLeft = 0.0f;
			float windowRight = editorWindowSize.X;
			float rightCandidateX = completionWindowRight + HintGap;
			float leftCandidateX = completionWindowLeft - HintGap - hintSize.X;

			float hintX;
			if (FitsHorizontally(rightCandidateX, hintSize.X, windowLeft, windowRight))
			{
				hintX = rightCandidateX;
			}
			else if (FitsHorizontally(leftCandidateX, hintSize.X, windowLeft, windowRight))
			{
				hintX = leftCandidateX;
			}
			else
			{
				float rightSpace = Math.Max(0.0f, windowRight - (completionWindowRight + HintGap));
				float leftSpace = Math.Max(0.0f, (completionWindowLeft - HintGap) - windowLeft);
				float preferredFallbackX = rightSpace >= leftSpace
					? rightCandidateX
					: leftCandidateX;
				float maximumHintX = Math.Max(windowLeft, windowRight - hintSize.X);
				hintX = Mathf.Clamp(preferredFallbackX, windowLeft, maximumHintX);
			}

			// Y remains selected-row-driven and is first clamped to the visible CodeEdit
			// area. Only after that do we convert it to the Window coordinate space and
			// apply a final defensive Window-edge clamp.
			float selectedRowTop = completionPopupY + visibleSelectedRow * rowHeight;
			float maximumCodeEditHintY = Math.Max(0.0f, editorSize.Y - hintSize.Y);
			float codeEditHintY = Mathf.Clamp(selectedRowTop, 0.0f, maximumCodeEditHintY);
			Vector2 hintWindowYPoint = codeEditToWindow * new Vector2(0.0f, codeEditHintY);
			float maximumWindowHintY = Math.Max(0.0f, editorWindowSize.Y - hintSize.Y);
			float hintY = Mathf.Clamp(hintWindowYPoint.Y, 0.0f, maximumWindowHintY);

			// Keep the final horizontal safety boundary at the editor Window, never at
			// CodeEdit. This remains defensive for windows narrower than the fixed hint.
			float maximumWindowHintX = Math.Max(0.0f, editorWindowSize.X - hintSize.X);
			hintX = Mathf.Clamp(hintX, 0.0f, maximumWindowHintX);
			if (!IsFinite(hintX) || !IsFinite(hintY))
				return false;

			// These rects intentionally stay CodeEdit-local because GuiInput mouse
			// positions are CodeEdit-local and mouse-row pinning depends on that contract.
			var completionBodyRect = new Rect2(
				new Vector2(completionLeft, completionPopupY),
				new Vector2(completionContentWidth, completionRowsHeight)
			);
			var completionScrollRect = new Rect2(
				new Vector2(completionLeft + completionContentWidth, completionPopupY),
				new Vector2(scrollWidth, completionRowsHeight)
			);

			layout = new AutocompleteHintLayout(
				new Vector2(hintX, hintY),
				completionBodyRect,
				completionScrollRect,
				lineOffset,
				optionCount,
				visibleLines
			);
			return true;
		}
		catch
		{
			return false;
		}
	}

	private static bool FitsHorizontally(
		float candidateX,
		float hintWidth,
		float windowLeft,
		float windowRight)
	{
		return IsFinite(candidateX)
			&& candidateX >= windowLeft
			&& candidateX + hintWidth <= windowRight;
	}

	private static int SaturatingAdd(int left, int right)
	{
		long result = (long)left + right;
		if (result > int.MaxValue)
			return int.MaxValue;
		if (result < int.MinValue)
			return int.MinValue;

		return (int)result;
	}

	private static bool IsFinite(float value) => float.IsFinite(value);
	private static bool IsFinitePositive(float value) => float.IsFinite(value) && value > 0.0f;
	private static bool IsFiniteNonNegative(float value) => float.IsFinite(value) && value >= 0.0f;
	private static bool IsValidGodotObject(GodotObject source) => source != null && GodotObject.IsInstanceValid(source);
}
#endif
