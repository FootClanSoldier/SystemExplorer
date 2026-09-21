#if TOOLS
using Godot;
using System;

namespace SystemExplorer.CodeService.Autocomplete.Hint;

internal sealed class AutocompleteHintView
{
	internal const float HintWidth = 360.0f;
	internal const float HintHeight = 90.0f;
	internal static readonly Vector2 HintSize = new(HintWidth, HintHeight);

	private const string HintNodeName = "SystemExplorerAutocompleteHintSidecar";
	private const string HintLabelNodeName = "SystemExplorerAutocompleteHintLabel";
	private const string PlaceholderText = "Completion hint";
	private const string PanelStyleboxThemeKey = "panel";
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

	private PanelContainer _panel;
	private CodeEdit _boundCodeEdit;
	private ulong _boundCodeEditInstanceId;
	private Window _boundEditorWindow;
	private ulong _boundEditorWindowInstanceId;

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
			&& IsValidGodotObject(_boundCodeEdit)
			&& IsValidGodotObject(_boundEditorWindow)
			&& _boundCodeEditInstanceId == codeEditInstanceId
			&& _boundEditorWindowInstanceId == editorWindowInstanceId
			&& HasInstanceId(_boundCodeEdit, codeEditInstanceId)
			&& HasInstanceId(_boundEditorWindow, editorWindowInstanceId)
			&& HasExpectedParent(_panel, editorWindow)
		)
		{
			return true;
		}

		Reset();

		PanelContainer panel = null;
		try
		{
			// Clean both supported generations: older builds parented the stable node
			// directly to CodeEdit, while the current build parents it to the existing
			// editor Window as a transient overlay Control.
			RemoveStaleHintNodes(codeEdit);
			RemoveStaleHintNodes(editorWindow);

			panel = new PanelContainer
			{
				Name = HintNodeName,
				Visible = false,
				CustomMinimumSize = HintSize,
				Size = HintSize,
				FocusMode = Control.FocusModeEnum.None,
				MouseFilter = Control.MouseFilterEnum.Stop,
				ThemeTypeVariation = "TooltipPanel",
				ZIndex = 100,
			};

			var label = new Label
			{
				Name = HintLabelNodeName,
				Text = PlaceholderText,
				FocusMode = Control.FocusModeEnum.None,
				MouseFilter = Control.MouseFilterEnum.Ignore,
				ThemeTypeVariation = "TooltipLabel",
			};

			ApplyCompletionThemeBestEffort(codeEdit, panel);
			panel.AddChild(label);
			editorWindow.AddChild(panel);
			panel.Size = HintSize;

			_panel = panel;
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

	internal void ShowAtWindowPosition(Vector2 windowPosition)
	{
		if (!IsValidGodotObject(_panel)
			|| !IsValidGodotObject(_boundEditorWindow)
			|| !HasInstanceId(_boundEditorWindow, _boundEditorWindowInstanceId)
			|| !HasExpectedParent(_panel, _boundEditorWindow))
		{
			return;
		}

		try
		{
			if (!_panel.Visible && IsValidGodotObject(_boundCodeEdit))
				ApplyCompletionThemeBestEffort(_boundCodeEdit, _panel);

			_panel.Position = windowPosition;
			_panel.Size = HintSize;
			_panel.Visible = true;
		}
		catch
		{
		}
	}

	internal void Hide()
	{
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
		PanelContainer panel = _panel;
		_panel = null;
		_boundCodeEdit = null;
		_boundCodeEditInstanceId = 0;
		_boundEditorWindow = null;
		_boundEditorWindowInstanceId = 0;

		DestroyNodeBestEffort(panel);
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

	private static void ApplyCompletionThemeBestEffort(CodeEdit codeEdit, PanelContainer panel)
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

	private static bool IsValidGodotObject(GodotObject source)
	{
		return source != null && GodotObject.IsInstanceValid(source);
	}
}
#endif
