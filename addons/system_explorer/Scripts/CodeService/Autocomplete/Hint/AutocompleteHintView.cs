#if TOOLS
using Godot;
using System;

namespace SystemExplorer.CodeService.Autocomplete.Hint;

internal sealed class AutocompleteHintView
{
	internal const float CompactMinimumHintWidth = 120.0f;
	internal const float WrappingMinimumHintWidth = 260.0f;
	internal const float MaximumHintWidth = 640.0f;

	private const string HintNodeName = "SystemExplorerAutocompleteHintSidecar";
	private const string HintLabelNodeName = "SystemExplorerAutocompleteHintLabel";
	private const string PanelStyleboxThemeKey = "panel";
	private const string LabelStyleboxThemeKey = "normal";
	private const string FontThemeKey = "font";
	private const string FontSizeThemeKey = "font_size";
	private const string LineSpacingThemeKey = "line_spacing";
	private const string ParagraphSpacingThemeKey = "paragraph_spacing";
	private const string CompletionStyleboxThemeKey = "completion";
	private const string CompletionBackgroundColorThemeKey = "completion_background_color";

	private const int VisualBorderWidth = 1;
	private const int VisualCornerRadius = 6;
	private const int VisualShadowSize = 6;
	private const float VisualBorderDarkenMultiplier = 0.42f;
	private const float VisualBorderMinimumAlpha = 0.90f;
	private const float VisualShadowAlpha = 0.30f;
	private const float VisualHorizontalContentMargin = 10.0f;
	private const float VisualVerticalContentMargin = 7.0f;
	private static readonly Vector2 VisualShadowOffset = new(0.0f, 2.0f);

	private Panel _panel;
	private Label _label;
	private CodeEdit _boundCodeEdit;
	private ulong _boundCodeEditInstanceId;
	private Window _boundEditorWindow;
	private ulong _boundEditorWindowInstanceId;
	private bool _hasPreparedPresentation;

	internal bool TryBind(CodeEdit codeEdit)
	{
		if (!TryGetBindingIdentity(
			codeEdit,
			out Window editorWindow,
			out ulong codeEditInstanceId,
			out ulong editorWindowInstanceId))
		{
			return false;
		}

		if (
			IsValidGodotObject(_panel)
			&& IsValidGodotObject(_label)
			&& IsValidGodotObject(_boundCodeEdit)
			&& IsValidGodotObject(_boundEditorWindow)
			&& _boundCodeEditInstanceId == codeEditInstanceId
			&& _boundEditorWindowInstanceId == editorWindowInstanceId
			&& HasInstanceId(_boundCodeEdit, codeEditInstanceId)
			&& HasInstanceId(_boundEditorWindow, editorWindowInstanceId)
			&& HasExpectedParent(_panel, editorWindow)
			&& HasExpectedParent(_label, _panel)
		)
		{
			return true;
		}

		Reset();

		Panel panel = null;
		try
		{
			// Clean both supported generations: older builds parented the stable node
			// directly to CodeEdit, while the current build parents it to the existing
			// editor Window as a transient overlay Control.
			RemoveStaleHintNodes(codeEdit);
			RemoveStaleHintNodes(editorWindow);

			panel = new Panel
			{
				Name = HintNodeName,
				Visible = false,
				FocusMode = Control.FocusModeEnum.None,
				MouseFilter = Control.MouseFilterEnum.Stop,
				ThemeTypeVariation = "TooltipPanel",
				ZIndex = 100,
			};

			var label = new Label
			{
				Name = HintLabelNodeName,
				Text = "",
				AutowrapMode = TextServer.AutowrapMode.WordSmart,
				FocusMode = Control.FocusModeEnum.None,
				MouseFilter = Control.MouseFilterEnum.Ignore,
				ThemeTypeVariation = "TooltipLabel",
			};

			ApplyCompletionThemeBestEffort(codeEdit, panel);
			panel.AddChild(label);
			editorWindow.AddChild(panel);

			_panel = panel;
			_label = label;
			_boundCodeEdit = codeEdit;
			_boundCodeEditInstanceId = codeEditInstanceId;
			_boundEditorWindow = editorWindow;
			_boundEditorWindowInstanceId = editorWindowInstanceId;
			return true;
		}
		catch
		{
			DestroyNodeBestEffort(panel);
			Reset();
			return false;
		}
	}

	internal bool TryMeasurePreferredWidth(string text, out float preferredOuterWidth)
	{
		preferredOuterWidth = 0.0f;
		if (text == null || !TryGetMeasurementMetrics(
			out Font font,
			out int fontSize,
			out Vector2 panelMinimumSize,
			out Vector2 labelMinimumSize,
			out _,
			out _))
		{
			return false;
		}

		try
		{
			float widestLine = 0.0f;
			string[] logicalLines = text.Split('\n');
			for (int lineIndex = 0; lineIndex < logicalLines.Length; lineIndex++)
			{
				float lineWidth = font.GetStringSize(
					logicalLines[lineIndex],
					_label.HorizontalAlignment,
					-1.0f,
					fontSize
				).X;
				if (!IsFiniteNonNegative(lineWidth))
					return false;

				widestLine = Math.Max(widestLine, lineWidth);
			}

			float measuredWidth = widestLine + labelMinimumSize.X + panelMinimumSize.X;
			if (!IsFinitePositive(measuredWidth))
				return false;

			preferredOuterWidth = MathF.Ceiling(measuredWidth);
			return IsFinitePositive(preferredOuterWidth);
		}
		catch
		{
			preferredOuterWidth = 0.0f;
			return false;
		}
	}

	internal bool TryMeasureForWidth(
		string text,
		float outerWidth,
		out Vector2 hintSize,
		out int visualLineCount)
	{
		hintSize = default;
		visualLineCount = 0;
		if (text == null
			|| !IsFinitePositive(outerWidth)
			|| !TryGetMeasurementMetrics(
				out Font font,
				out int fontSize,
				out Vector2 panelMinimumSize,
				out Vector2 labelMinimumSize,
				out int lineSpacing,
				out int paragraphSpacing))
		{
			return false;
		}

		try
		{
			float labelOuterWidth = outerWidth - panelMinimumSize.X;
			float textWidth = labelOuterWidth - labelMinimumSize.X;
			if (!IsFinitePositive(labelOuterWidth) || !IsFinitePositive(textWidth))
				return false;

			float fontHeight = font.GetHeight(fontSize);
			if (!IsFinitePositive(fontHeight))
				return false;

			TextServer.LineBreakFlag breakFlags = TextServer.LineBreakFlag.Mandatory
				| TextServer.LineBreakFlag.WordBound
				| TextServer.LineBreakFlag.Adaptive
				| _label.AutowrapTrimFlags;

			float textHeight = 0.0f;
			string[] paragraphs = text.Split('\n');
			for (int paragraphIndex = 0; paragraphIndex < paragraphs.Length; paragraphIndex++)
			{
				var paragraph = new TextParagraph();
				try
				{
					// Label internally gives every explicit paragraph a zero-width shaping
					// character, including empty lines. Mirror that presentation detail only
					// in measurement; the semantic text itself remains untouched.
					if (!paragraph.AddString(
						paragraphs[paragraphIndex] + "\u200B",
						font,
						fontSize,
						_label.Language ?? ""))
					{
						return false;
					}

					paragraph.Width = textWidth;
					paragraph.BreakFlags = breakFlags;
					paragraph.JustificationFlags = _label.JustificationFlags;
					paragraph.Alignment = _label.HorizontalAlignment;
					paragraph.LineSpacing = lineSpacing;

					int lineCount = paragraph.GetLineCount();
					if (lineCount <= 0
						|| visualLineCount > int.MaxValue - lineCount)
					{
						return false;
					}

					visualLineCount += lineCount;
					for (int lineIndex = 0; lineIndex < lineCount; lineIndex++)
					{
						float shapedLineHeight = paragraph.GetLineAscent(lineIndex)
							+ paragraph.GetLineDescent(lineIndex);
						if (!IsFiniteNonNegative(shapedLineHeight))
							return false;

						textHeight += Math.Max(fontHeight, shapedLineHeight) + lineSpacing;
					}

					textHeight += paragraphSpacing;
				}
				finally
				{
					paragraph.Dispose();
				}
			}

			if (textHeight > 0.0f)
				textHeight -= lineSpacing + paragraphSpacing;

			float measuredHeight = textHeight + labelMinimumSize.Y + panelMinimumSize.Y;
			if (!IsFinitePositive(measuredHeight))
				return false;

			hintSize = new Vector2(
				outerWidth,
				MathF.Ceiling(measuredHeight)
			);
			return visualLineCount > 0
				&& IsFinitePositive(hintSize.X)
				&& IsFinitePositive(hintSize.Y);
		}
		catch
		{
			hintSize = default;
			visualLineCount = 0;
			return false;
		}
	}

	internal bool TryPrepareHidden(Vector2 hintSize, string text)
	{
		if (text == null
			|| !IsFinitePositive(hintSize.X)
			|| !IsFinitePositive(hintSize.Y)
			|| !HasUsablePresentationBinding())
		{
			return false;
		}

		try
		{
			_hasPreparedPresentation = false;
			_panel.Visible = false;
			if (IsValidGodotObject(_boundCodeEdit))
				ApplyCompletionThemeBestEffort(_boundCodeEdit, _panel);

			_label.Text = text;
			if (!TryApplyPresentationLayout(hintSize))
				return false;

			_hasPreparedPresentation = true;
			return true;
		}
		catch
		{
			return false;
		}
	}

	internal bool TryRevealPreparedAtWindowPosition(
		Vector2 hintSize,
		Vector2 windowPosition)
	{
		if (!IsFinitePositive(hintSize.X)
			|| !IsFinitePositive(hintSize.Y)
			|| !IsFinite(windowPosition.X)
			|| !IsFinite(windowPosition.Y)
			|| !_hasPreparedPresentation
			|| !HasUsablePresentationBinding())
		{
			return false;
		}

		try
		{
			_panel.Visible = false;
			if (!TryApplyPresentationLayout(hintSize))
				return false;

			_panel.Position = windowPosition;
			_panel.Visible = true;
			return true;
		}
		catch
		{
			return false;
		}
	}

	internal bool TryMoveVisible(Vector2 windowPosition)
	{
		if (!IsFinite(windowPosition.X)
			|| !IsFinite(windowPosition.Y)
			|| !_hasPreparedPresentation
			|| !HasUsablePresentationBinding())
		{
			return false;
		}

		try
		{
			if (!_panel.Visible)
				return false;

			_panel.Position = windowPosition;
			return true;
		}
		catch
		{
			return false;
		}
	}

	internal void Hide()
	{
		_hasPreparedPresentation = false;
		if (!IsValidGodotObject(_panel))
			return;

		try
		{
			_panel.Visible = false;
		}
		catch
		{
		}
	}

	internal void Reset()
	{
		_hasPreparedPresentation = false;
		Panel panel = _panel;
		_panel = null;
		_label = null;
		_boundCodeEdit = null;
		_boundCodeEditInstanceId = 0;
		_boundEditorWindow = null;
		_boundEditorWindowInstanceId = 0;

		DestroyNodeBestEffort(panel);
	}

	private bool TryApplyPresentationLayout(Vector2 hintSize)
	{
		if (!IsFinitePositive(hintSize.X)
			|| !IsFinitePositive(hintSize.Y)
			|| !HasUsablePresentationBinding())
		{
			return false;
		}

		StyleBox panelStyle = _panel.GetThemeStylebox(PanelStyleboxThemeKey);
		if (!IsValidGodotObject(panelStyle))
			return false;

		float leftMargin = panelStyle.GetContentMargin(Side.Left);
		float topMargin = panelStyle.GetContentMargin(Side.Top);
		float rightMargin = panelStyle.GetContentMargin(Side.Right);
		float bottomMargin = panelStyle.GetContentMargin(Side.Bottom);
		if (!IsFiniteNonNegative(leftMargin)
			|| !IsFiniteNonNegative(topMargin)
			|| !IsFiniteNonNegative(rightMargin)
			|| !IsFiniteNonNegative(bottomMargin))
		{
			return false;
		}

		float labelWidth = hintSize.X - leftMargin - rightMargin;
		float labelHeight = hintSize.Y - topMargin - bottomMargin;
		if (!IsFinitePositive(labelWidth) || !IsFiniteNonNegative(labelHeight))
			return false;

		_panel.Size = hintSize;
		_label.CustomMinimumSize = Vector2.Zero;
		_label.Position = new Vector2(leftMargin, topMargin);
		_label.Size = new Vector2(labelWidth, labelHeight);
		return true;
	}

	private bool HasUsablePresentationBinding()
	{
		return IsValidGodotObject(_panel)
			&& IsValidGodotObject(_label)
			&& IsValidGodotObject(_boundEditorWindow)
			&& HasInstanceId(_boundEditorWindow, _boundEditorWindowInstanceId)
			&& HasExpectedParent(_panel, _boundEditorWindow)
			&& HasExpectedParent(_label, _panel);
	}

	private bool TryGetMeasurementMetrics(
		out Font font,
		out int fontSize,
		out Vector2 panelMinimumSize,
		out Vector2 labelMinimumSize,
		out int lineSpacing,
		out int paragraphSpacing)
	{
		font = null;
		fontSize = 0;
		panelMinimumSize = default;
		labelMinimumSize = default;
		lineSpacing = 0;
		paragraphSpacing = 0;
		if (!IsValidGodotObject(_panel)
			|| !IsValidGodotObject(_label)
			|| !IsValidGodotObject(_boundCodeEdit)
			|| !IsValidGodotObject(_boundEditorWindow)
			|| !HasInstanceId(_boundEditorWindow, _boundEditorWindowInstanceId)
			|| !HasExpectedParent(_panel, _boundEditorWindow)
			|| !HasExpectedParent(_label, _panel))
		{
			return false;
		}

		try
		{
			ApplyCompletionThemeBestEffort(_boundCodeEdit, _panel);

			font = _label.GetThemeFont(FontThemeKey);
			fontSize = _label.GetThemeFontSize(FontSizeThemeKey);
			StyleBox panelStyle = _panel.GetThemeStylebox(PanelStyleboxThemeKey);
			StyleBox labelStyle = _label.GetThemeStylebox(LabelStyleboxThemeKey);
			if (!IsValidGodotObject(font)
				|| fontSize <= 0
				|| !IsValidGodotObject(panelStyle)
				|| !IsValidGodotObject(labelStyle))
			{
				return false;
			}

			panelMinimumSize = panelStyle.GetMinimumSize();
			labelMinimumSize = labelStyle.GetMinimumSize();
			lineSpacing = _label.GetThemeConstant(LineSpacingThemeKey);
			paragraphSpacing = _label.GetThemeConstant(ParagraphSpacingThemeKey);

			return IsFiniteNonNegative(panelMinimumSize.X)
				&& IsFiniteNonNegative(panelMinimumSize.Y)
				&& IsFiniteNonNegative(labelMinimumSize.X)
				&& IsFiniteNonNegative(labelMinimumSize.Y);
		}
		catch
		{
			font = null;
			fontSize = 0;
			panelMinimumSize = default;
			labelMinimumSize = default;
			lineSpacing = 0;
			paragraphSpacing = 0;
			return false;
		}
	}

	private static bool TryGetBindingIdentity(
		CodeEdit codeEdit,
		out Window editorWindow,
		out ulong codeEditInstanceId,
		out ulong editorWindowInstanceId)
	{
		editorWindow = null;
		codeEditInstanceId = 0;
		editorWindowInstanceId = 0;
		if (!IsValidGodotObject(codeEdit))
			return false;

		try
		{
			editorWindow = codeEdit.GetWindow();
			if (!IsValidGodotObject(editorWindow))
				return false;

			codeEditInstanceId = codeEdit.GetInstanceId();
			editorWindowInstanceId = editorWindow.GetInstanceId();
			return codeEditInstanceId != 0 && editorWindowInstanceId != 0;
		}
		catch
		{
			editorWindow = null;
			codeEditInstanceId = 0;
			editorWindowInstanceId = 0;
			return false;
		}
	}

	private static void ApplyCompletionThemeBestEffort(CodeEdit codeEdit, Panel panel)
	{
		if (!IsValidGodotObject(codeEdit) || !IsValidGodotObject(panel))
			return;

		try
		{
			Color completionBackground = codeEdit.GetThemeColor(
				CompletionBackgroundColorThemeKey
			);
			StyleBox panelStyle = CreateCompletionMatchedPanelStyle(
				codeEdit,
				completionBackground
			);
			if (IsValidGodotObject(panelStyle))
				panel.AddThemeStyleboxOverride(PanelStyleboxThemeKey, panelStyle);
		}
		catch
		{
		}
	}

	private static StyleBox CreateCompletionMatchedPanelStyle(
		CodeEdit codeEdit,
		Color completionBackground)
	{
		StyleBoxFlat panelStyle = TryDuplicateCompletionStylebox(codeEdit);
		try
		{
			panelStyle ??= new StyleBoxFlat();

			Color borderColor = CreateThemeDerivedBorderColor(completionBackground);
			Color shadowColor = new(
				borderColor.R,
				borderColor.G,
				borderColor.B,
				VisualShadowAlpha
			);

			panelStyle.BgColor = completionBackground;
			panelStyle.BorderColor = borderColor;
			panelStyle.BorderWidthLeft = VisualBorderWidth;
			panelStyle.BorderWidthTop = VisualBorderWidth;
			panelStyle.BorderWidthRight = VisualBorderWidth;
			panelStyle.BorderWidthBottom = VisualBorderWidth;

			panelStyle.CornerRadiusTopLeft = VisualCornerRadius;
			panelStyle.CornerRadiusTopRight = VisualCornerRadius;
			panelStyle.CornerRadiusBottomRight = VisualCornerRadius;
			panelStyle.CornerRadiusBottomLeft = VisualCornerRadius;

			panelStyle.ShadowColor = shadowColor;
			panelStyle.ShadowSize = VisualShadowSize;
			panelStyle.ShadowOffset = VisualShadowOffset;

			panelStyle.ContentMarginLeft = VisualHorizontalContentMargin;
			panelStyle.ContentMarginTop = VisualVerticalContentMargin;
			panelStyle.ContentMarginRight = VisualHorizontalContentMargin;
			panelStyle.ContentMarginBottom = VisualVerticalContentMargin;

			return panelStyle;
		}
		catch
		{
			return null;
		}
	}

	private static StyleBoxFlat TryDuplicateCompletionStylebox(CodeEdit codeEdit)
	{
		try
		{
			StyleBox completionStyle = codeEdit.GetThemeStylebox(CompletionStyleboxThemeKey);
			if (completionStyle is StyleBoxFlat completionFlat
				&& completionFlat.Duplicate() is StyleBoxFlat duplicate
				&& IsValidGodotObject(duplicate))
			{
				return duplicate;
			}
		}
		catch
		{
		}

		return null;
	}

	private static Color CreateThemeDerivedBorderColor(Color completionBackground)
	{
		float alpha = Math.Max(completionBackground.A, VisualBorderMinimumAlpha);
		return new Color(
			Mathf.Clamp(completionBackground.R * VisualBorderDarkenMultiplier, 0.0f, 1.0f),
			Mathf.Clamp(completionBackground.G * VisualBorderDarkenMultiplier, 0.0f, 1.0f),
			Mathf.Clamp(completionBackground.B * VisualBorderDarkenMultiplier, 0.0f, 1.0f),
			Mathf.Clamp(alpha, 0.0f, 1.0f)
		);
	}

	private static void RemoveStaleHintNodes(Node parent)
	{
		if (!IsValidGodotObject(parent))
			return;

		try
		{
			foreach (Node child in parent.GetChildren())
			{
				if (!IsValidGodotObject(child)
					|| !string.Equals(child.Name.ToString(), HintNodeName, StringComparison.Ordinal))
				{
					continue;
				}

				DestroyNodeBestEffort(child);
			}
		}
		catch
		{
		}
	}

	private static bool HasExpectedParent(Node child, Node expectedParent)
	{
		if (!IsValidGodotObject(child) || !IsValidGodotObject(expectedParent))
			return false;

		try
		{
			Node parent = child.GetParent();
			return IsValidGodotObject(parent)
				&& parent.GetInstanceId() == expectedParent.GetInstanceId();
		}
		catch
		{
			return false;
		}
	}

	private static bool HasInstanceId(GodotObject source, ulong expectedInstanceId)
	{
		if (!IsValidGodotObject(source) || expectedInstanceId == 0)
			return false;

		try
		{
			return source.GetInstanceId() == expectedInstanceId;
		}
		catch
		{
			return false;
		}
	}

	private static void DestroyNodeBestEffort(Node node)
	{
		if (!IsValidGodotObject(node))
			return;

		try
		{
			Node parent = node.GetParent();
			if (IsValidGodotObject(parent))
				parent.RemoveChild(node);
		}
		catch
		{
		}

		try
		{
			node.QueueFree();
		}
		catch
		{
		}
	}


	private static bool IsFinite(float value) => float.IsFinite(value);
	private static bool IsFinitePositive(float value) => float.IsFinite(value) && value > 0.0f;
	private static bool IsFiniteNonNegative(float value) => float.IsFinite(value) && value >= 0.0f;

	private static bool IsValidGodotObject(GodotObject source)
	{
		return source != null && GodotObject.IsInstanceValid(source);
	}
}
#endif
