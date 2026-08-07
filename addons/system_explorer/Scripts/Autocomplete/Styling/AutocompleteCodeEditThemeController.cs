#if TOOLS
using Godot;
using System;
using System.Collections.Generic;

namespace SystemExplorer.Autocomplete.Styling;

internal sealed class AutocompleteCodeEditThemeController
{
	// Fills the inner completion area after the completion StyleBox has been drawn.
	private const string CompletionBackgroundColorThemeKey = "completion_background_color";

	// Draws the background for the selected completion row.
	private const string CompletionSelectedColorThemeKey = "completion_selected_color";

	// Highlights the characters that match the prefix already typed by the user.
	private const string CompletionExistingColorThemeKey = "completion_existing_color";

	// Colors the completion list scroll indicator.
	private const string CompletionScrollColorThemeKey = "completion_scroll_color";

	// Colors the completion list scroll indicator while hovered or pressed.
	private const string CompletionScrollHoveredColorThemeKey =
		"completion_scroll_hovered_color";

	// Sets the maximum visible row count before the completion list needs scrolling.
	private const string CompletionLinesThemeKey = "completion_lines";

	// Sets maximum text width relative to editor font size rather than pure pixels.
	private const string CompletionMaxWidthThemeKey = "completion_max_width";

	// Sets the scroll indicator width when options exceed completion_lines.
	private const string CompletionScrollWidthThemeKey = "completion_scroll_width";

	// CodeEdit reads this ItemList theme value as spacing between its icon area and text.
	private const string HorizontalSeparationThemeKey = "h_separation";

	// Owns the completion frame, outer background, corners, margins, and shadow.
	private const string CompletionStyleboxThemeKey = "completion";

	private readonly AutocompleteThemeDefinition _definition;
	private AutocompleteThemeSnapshot _snapshot;

	internal AutocompleteCodeEditThemeController(AutocompleteThemeDefinition definition)
	{
		_definition = definition ?? throw new ArgumentNullException(nameof(definition));
	}

	internal void Apply(CodeEdit codeEdit)
	{
		if (!IsValidGodotObject(codeEdit))
			return;

		AutocompleteThemeSnapshot activeSnapshot = _snapshot;

		if (activeSnapshot != null)
		{
			if (
				IsValidGodotObject(activeSnapshot.CodeEdit)
				&& activeSnapshot.CodeEditInstanceId == codeEdit.GetInstanceId()
			)
			{
				return;
			}

			Restore(activeSnapshot.CodeEdit);
		}

		StyleBoxFlat completionStyleboxOverride = CreateCompletionStyleOverride(codeEdit);

		if (!HasColorOrConstantOverrides() && !IsValidGodotObject(completionStyleboxOverride))
			return;

		var snapshot = new AutocompleteThemeSnapshot(codeEdit);
		_snapshot = snapshot;

		codeEdit.BeginBulkThemeOverride();

		try
		{
			ApplyColorOverrideIfSet(
				codeEdit,
				snapshot,
				CompletionBackgroundColorThemeKey,
				_definition.CompletionBackgroundColor
			);
			ApplyColorOverrideIfSet(
				codeEdit,
				snapshot,
				CompletionSelectedColorThemeKey,
				_definition.CompletionSelectedColor
			);
			ApplyColorOverrideIfSet(
				codeEdit,
				snapshot,
				CompletionExistingColorThemeKey,
				_definition.CompletionExistingColor
			);
			ApplyColorOverrideIfSet(
				codeEdit,
				snapshot,
				CompletionScrollColorThemeKey,
				_definition.CompletionScrollColor
			);
			ApplyColorOverrideIfSet(
				codeEdit,
				snapshot,
				CompletionScrollHoveredColorThemeKey,
				_definition.CompletionScrollHoveredColor
			);

			ApplyConstantOverrideIfSet(
				codeEdit,
				snapshot,
				CompletionLinesThemeKey,
				_definition.CompletionLines
			);
			ApplyConstantOverrideIfSet(
				codeEdit,
				snapshot,
				CompletionMaxWidthThemeKey,
				_definition.CompletionMaxWidth
			);
			ApplyConstantOverrideIfSet(
				codeEdit,
				snapshot,
				CompletionScrollWidthThemeKey,
				_definition.CompletionScrollWidth
			);
			ApplyConstantOverrideIfSet(
				codeEdit,
				snapshot,
				HorizontalSeparationThemeKey,
				_definition.HorizontalSeparation
			);

			ApplyStyleboxOverrideIfSet(codeEdit, snapshot, completionStyleboxOverride);
		}
		finally
		{
			codeEdit.EndBulkThemeOverride();
		}
	}

	internal void Restore(CodeEdit codeEdit)
	{
		AutocompleteThemeSnapshot snapshot = _snapshot;

		if (snapshot == null)
			return;

		if (!IsValidGodotObject(codeEdit))
		{
			_snapshot = null;
			return;
		}

		if (snapshot.CodeEditInstanceId != codeEdit.GetInstanceId())
			return;

		try
		{
			codeEdit.BeginBulkThemeOverride();

			try
			{
				foreach (
					KeyValuePair<
						string,
						AutocompleteThemeSnapshot.ColorOverrideSnapshot
					> entry in snapshot.ColorOverrides
				)
				{
					if (entry.Value.HadOverride)
						codeEdit.AddThemeColorOverride(entry.Key, entry.Value.PreviousValue);
					else
						codeEdit.RemoveThemeColorOverride(entry.Key);
				}

				foreach (
					KeyValuePair<
						string,
						AutocompleteThemeSnapshot.ConstantOverrideSnapshot
					> entry in snapshot.ConstantOverrides
				)
				{
					if (entry.Value.HadOverride)
						codeEdit.AddThemeConstantOverride(entry.Key, entry.Value.PreviousValue);
					else
						codeEdit.RemoveThemeConstantOverride(entry.Key);
				}

				if (snapshot.HasCompletionStyleboxSnapshot)
				{
					if (
						snapshot.HadCompletionStyleboxOverride
						&& IsValidGodotObject(snapshot.PreviousCompletionStylebox)
					)
					{
						codeEdit.AddThemeStyleboxOverride(
							CompletionStyleboxThemeKey,
							snapshot.PreviousCompletionStylebox
						);
					}
					else
					{
						codeEdit.RemoveThemeStyleboxOverride(CompletionStyleboxThemeKey);
					}
				}
			}
			finally
			{
				codeEdit.EndBulkThemeOverride();
			}
		}
		finally
		{
			if (ReferenceEquals(_snapshot, snapshot))
				_snapshot = null;
		}
	}

	internal void Reset()
	{
		AutocompleteThemeSnapshot snapshot = _snapshot;

		if (snapshot == null)
			return;

		Restore(snapshot.CodeEdit);
	}

	private bool HasColorOrConstantOverrides()
	{
		return _definition.CompletionBackgroundColor.HasValue
			|| _definition.CompletionSelectedColor.HasValue
			|| _definition.CompletionExistingColor.HasValue
			|| _definition.CompletionScrollColor.HasValue
			|| _definition.CompletionScrollHoveredColor.HasValue
			|| _definition.CompletionLines.HasValue
			|| _definition.CompletionMaxWidth.HasValue
			|| _definition.CompletionScrollWidth.HasValue
			|| _definition.HorizontalSeparation.HasValue;
	}

	private bool HasStyleboxOverrides()
	{
		return _definition.PopupBackgroundColor.HasValue
			|| _definition.BorderColor.HasValue
			|| _definition.ShadowColor.HasValue
			|| _definition.ShadowOffset.HasValue
			|| _definition.CornerRadius.HasValue
			|| _definition.BorderWidth.HasValue
			|| _definition.ShadowSize.HasValue
			|| _definition.ContentMarginLeft.HasValue
			|| _definition.ContentMarginTop.HasValue
			|| _definition.ContentMarginRight.HasValue
			|| _definition.ContentMarginBottom.HasValue;
	}

	private static void ApplyColorOverrideIfSet(
		CodeEdit codeEdit,
		AutocompleteThemeSnapshot snapshot,
		string themeKey,
		Color? value
	)
	{
		if (!value.HasValue)
			return;

		bool hadOverride = codeEdit.HasThemeColorOverride(themeKey);
		Color previousValue = hadOverride ? codeEdit.GetThemeColor(themeKey) : default;

		snapshot.ColorOverrides[themeKey] =
			new AutocompleteThemeSnapshot.ColorOverrideSnapshot(hadOverride, previousValue);
		codeEdit.AddThemeColorOverride(themeKey, value.Value);
	}

	private static void ApplyConstantOverrideIfSet(
		CodeEdit codeEdit,
		AutocompleteThemeSnapshot snapshot,
		string themeKey,
		int? value
	)
	{
		if (!value.HasValue)
			return;

		bool hadOverride = codeEdit.HasThemeConstantOverride(themeKey);
		int previousValue = hadOverride ? codeEdit.GetThemeConstant(themeKey) : default;

		snapshot.ConstantOverrides[themeKey] =
			new AutocompleteThemeSnapshot.ConstantOverrideSnapshot(hadOverride, previousValue);
		codeEdit.AddThemeConstantOverride(themeKey, value.Value);
	}

	private StyleBoxFlat CreateCompletionStyleOverride(CodeEdit codeEdit)
	{
		if (!HasStyleboxOverrides())
			return null;

		StyleBox effectiveStylebox = codeEdit.GetThemeStylebox(CompletionStyleboxThemeKey);

		if (effectiveStylebox is not StyleBoxFlat effectiveStyleboxFlat)
			return null;

		StyleBoxFlat completionStylebox = effectiveStyleboxFlat.Duplicate() as StyleBoxFlat;

		if (!IsValidGodotObject(completionStylebox))
			return null;

		ApplyStyleboxValues(completionStylebox);
		return completionStylebox;
	}

	private void ApplyStyleboxValues(StyleBoxFlat completionStylebox)
	{
		if (_definition.PopupBackgroundColor.HasValue)
			completionStylebox.BgColor = _definition.PopupBackgroundColor.Value;

		if (_definition.BorderColor.HasValue)
			completionStylebox.BorderColor = _definition.BorderColor.Value;

		if (_definition.ShadowColor.HasValue)
			completionStylebox.ShadowColor = _definition.ShadowColor.Value;

		if (_definition.ShadowOffset.HasValue)
			completionStylebox.ShadowOffset = _definition.ShadowOffset.Value;

		if (_definition.CornerRadius.HasValue)
		{
			int cornerRadius = _definition.CornerRadius.Value;
			completionStylebox.CornerRadiusTopLeft = cornerRadius;
			completionStylebox.CornerRadiusTopRight = cornerRadius;
			completionStylebox.CornerRadiusBottomRight = cornerRadius;
			completionStylebox.CornerRadiusBottomLeft = cornerRadius;
		}

		if (_definition.BorderWidth.HasValue)
		{
			int borderWidth = _definition.BorderWidth.Value;
			completionStylebox.BorderWidthLeft = borderWidth;
			completionStylebox.BorderWidthTop = borderWidth;
			completionStylebox.BorderWidthRight = borderWidth;
			completionStylebox.BorderWidthBottom = borderWidth;
		}

		if (_definition.ShadowSize.HasValue)
			completionStylebox.ShadowSize = _definition.ShadowSize.Value;

		if (_definition.ContentMarginLeft.HasValue)
			completionStylebox.ContentMarginLeft = _definition.ContentMarginLeft.Value;

		if (_definition.ContentMarginTop.HasValue)
			completionStylebox.ContentMarginTop = _definition.ContentMarginTop.Value;

		if (_definition.ContentMarginRight.HasValue)
			completionStylebox.ContentMarginRight = _definition.ContentMarginRight.Value;

		if (_definition.ContentMarginBottom.HasValue)
			completionStylebox.ContentMarginBottom = _definition.ContentMarginBottom.Value;
	}

	private static void ApplyStyleboxOverrideIfSet(
		CodeEdit codeEdit,
		AutocompleteThemeSnapshot snapshot,
		StyleBoxFlat completionStyleboxOverride
	)
	{
		if (!IsValidGodotObject(completionStyleboxOverride))
			return;

		snapshot.HasCompletionStyleboxSnapshot = true;
		snapshot.HadCompletionStyleboxOverride = codeEdit.HasThemeStyleboxOverride(
			CompletionStyleboxThemeKey
		);

		if (snapshot.HadCompletionStyleboxOverride)
			snapshot.PreviousCompletionStylebox = codeEdit.GetThemeStylebox(
				CompletionStyleboxThemeKey
			);

		codeEdit.AddThemeStyleboxOverride(
			CompletionStyleboxThemeKey,
			completionStyleboxOverride
		);
	}

	private static bool IsValidGodotObject(GodotObject source)
	{
		return source != null && GodotObject.IsInstanceValid(source);
	}
}
#endif
