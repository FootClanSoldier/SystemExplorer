#if TOOLS
using Godot;
using System;
using System.Collections.Generic;
using SystemExplorer.Autocomplete;

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
				if (IsSnapshotStillExactlyApplied(codeEdit, activeSnapshot))
					return;

				ForgetOwnedState(codeEdit);
				throw new InvalidOperationException(
					"Autocomplete theme ownership changed before repeated Apply."
				);
			}

			AutocompletePresentationRestoreResult previousRestore = Restore(activeSnapshot.CodeEdit);
			if (!previousRestore.Succeeded)
			{
				throw new InvalidOperationException(
					"Previous autocomplete theme ownership could not be restored safely."
				);
			}
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

	internal bool TryCaptureCompletionExistingColorNativeOwnershipState(
		CodeEdit codeEdit,
		out ulong codeEditInstanceId,
		out bool completionExistingColorOwned,
		out bool hadPreviousOverride,
		out Color previousColor,
		out Color appliedColor
	)
	{
		codeEditInstanceId = 0;
		completionExistingColorOwned = false;
		hadPreviousOverride = false;
		previousColor = default;
		appliedColor = default;

		if (codeEdit == null)
			return false;

		AutocompleteThemeSnapshot snapshot = _snapshot;
		if (snapshot == null)
			return true;

		if (!ReferenceEquals(snapshot.CodeEdit, codeEdit))
			return false;

		codeEditInstanceId = snapshot.CodeEditInstanceId;

		if (
			!snapshot.ColorOverrides.TryGetValue(
				CompletionExistingColorThemeKey,
				out AutocompleteThemeSnapshot.ColorOverrideSnapshot colorSnapshot
			)
		)
		{
			return true;
		}

		completionExistingColorOwned = true;
		hadPreviousOverride = colorSnapshot.HadOverride;
		previousColor = colorSnapshot.PreviousValue;
		appliedColor = colorSnapshot.AppliedValue;
		return true;
	}

	internal AutocompletePresentationRestoreResult TryRestoreCompletionExistingColorFromNativeBridge(
		CodeEdit codeEdit,
		bool hadPreviousOverride,
		Color previousColor,
		Color expectedAppliedColor
	)
	{
		if (!IsValidGodotObject(codeEdit))
			return AutocompletePresentationRestoreResult.Failure();

		try
		{
			bool hasCurrentOverride = codeEdit.HasThemeColorOverride(
				CompletionExistingColorThemeKey
			);
			if (!hasCurrentOverride)
				return AutocompletePresentationRestoreResult.Success(currentStateChanged: true);

			Color currentOverride = codeEdit.GetThemeColor(CompletionExistingColorThemeKey);
			if (!currentOverride.Equals(expectedAppliedColor))
				return AutocompletePresentationRestoreResult.Success(currentStateChanged: true);

			codeEdit.BeginBulkThemeOverride();
			try
			{
				if (hadPreviousOverride)
				{
					codeEdit.AddThemeColorOverride(
						CompletionExistingColorThemeKey,
						previousColor
					);
				}
				else
				{
					codeEdit.RemoveThemeColorOverride(CompletionExistingColorThemeKey);
				}
			}
			finally
			{
				codeEdit.EndBulkThemeOverride();
			}

			return AutocompletePresentationRestoreResult.Success();
		}
		catch
		{
			return AutocompletePresentationRestoreResult.Failure();
		}
	}

	internal AutocompletePresentationRestoreResult Restore(CodeEdit codeEdit)
	{
		AutocompleteThemeSnapshot snapshot = _snapshot;

		if (snapshot == null)
			return AutocompletePresentationRestoreResult.Success();
		if (!ReferenceEquals(snapshot.CodeEdit, codeEdit))
			return AutocompletePresentationRestoreResult.Success();

		try
		{
			if (!IsValidGodotObject(codeEdit))
				return AutocompletePresentationRestoreResult.Failure();
			if (snapshot.CodeEditInstanceId != codeEdit.GetInstanceId())
				return AutocompletePresentationRestoreResult.Success(currentStateChanged: true);

			var colorRestoreKeys = new List<string>();
			var constantRestoreKeys = new List<string>();
			bool restoreStylebox = false;
			bool currentStateChanged = false;

			foreach (
				KeyValuePair<string, AutocompleteThemeSnapshot.ColorOverrideSnapshot> entry
					in snapshot.ColorOverrides
			)
			{
				bool hasOverride = codeEdit.HasThemeColorOverride(entry.Key);
				if (
					hasOverride
					&& codeEdit.GetThemeColor(entry.Key).Equals(entry.Value.AppliedValue)
				)
				{
					colorRestoreKeys.Add(entry.Key);
				}
				else
				{
					currentStateChanged = true;
				}
			}

			foreach (
				KeyValuePair<string, AutocompleteThemeSnapshot.ConstantOverrideSnapshot> entry
					in snapshot.ConstantOverrides
			)
			{
				bool hasOverride = codeEdit.HasThemeConstantOverride(entry.Key);
				if (
					hasOverride
					&& codeEdit.GetThemeConstant(entry.Key) == entry.Value.AppliedValue
				)
				{
					constantRestoreKeys.Add(entry.Key);
				}
				else
				{
					currentStateChanged = true;
				}
			}

			if (snapshot.HasCompletionStyleboxSnapshot)
			{
				bool hasOverride = codeEdit.HasThemeStyleboxOverride(
					CompletionStyleboxThemeKey
				);
				StyleBox currentOverride = hasOverride
					? codeEdit.GetThemeStylebox(CompletionStyleboxThemeKey)
					: null;
				if (
					hasOverride
					&& IsValidGodotObject(currentOverride)
					&& snapshot.AppliedCompletionStyleboxInstanceId != 0
					&& currentOverride.GetInstanceId()
						== snapshot.AppliedCompletionStyleboxInstanceId
				)
				{
					if (
						snapshot.HadCompletionStyleboxOverride
						&& !IsValidGodotObject(snapshot.PreviousCompletionStylebox)
					)
					{
						return AutocompletePresentationRestoreResult.Failure();
					}

					restoreStylebox = true;
				}
				else
				{
					currentStateChanged = true;
				}
			}

			if (
				colorRestoreKeys.Count > 0
				|| constantRestoreKeys.Count > 0
				|| restoreStylebox
			)
			{
				codeEdit.BeginBulkThemeOverride();
				try
				{
					foreach (string themeKey in colorRestoreKeys)
					{
						AutocompleteThemeSnapshot.ColorOverrideSnapshot value =
							snapshot.ColorOverrides[themeKey];
						if (value.HadOverride)
							codeEdit.AddThemeColorOverride(themeKey, value.PreviousValue);
						else
							codeEdit.RemoveThemeColorOverride(themeKey);
					}

					foreach (string themeKey in constantRestoreKeys)
					{
						AutocompleteThemeSnapshot.ConstantOverrideSnapshot value =
							snapshot.ConstantOverrides[themeKey];
						if (value.HadOverride)
							codeEdit.AddThemeConstantOverride(themeKey, value.PreviousValue);
						else
							codeEdit.RemoveThemeConstantOverride(themeKey);
					}

					if (restoreStylebox)
					{
						if (snapshot.HadCompletionStyleboxOverride)
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

			return AutocompletePresentationRestoreResult.Success(currentStateChanged);
		}
		catch
		{
			return AutocompletePresentationRestoreResult.Failure();
		}
		finally
		{
			if (ReferenceEquals(_snapshot, snapshot))
				_snapshot = null;
		}
	}

	internal AutocompletePresentationRestoreResult RestoreRemainingOwnedStateAfterCompletionExistingColorBridge(
		CodeEdit codeEdit
	)
	{
		AutocompleteThemeSnapshot snapshot = _snapshot;
		if (snapshot == null)
			return AutocompletePresentationRestoreResult.Success();
		if (!ReferenceEquals(snapshot.CodeEdit, codeEdit))
			return AutocompletePresentationRestoreResult.Success();

		snapshot.ColorOverrides.Remove(CompletionExistingColorThemeKey);
		return Restore(codeEdit);
	}

	internal void ForgetOwnedState(CodeEdit codeEdit)
	{
		AutocompleteThemeSnapshot snapshot = _snapshot;
		if (snapshot == null)
			return;

		if (
			ReferenceEquals(snapshot.CodeEdit, codeEdit)
			|| (
				IsValidGodotObject(codeEdit)
				&& snapshot.CodeEditInstanceId == codeEdit.GetInstanceId()
			)
		)
		{
			_snapshot = null;
		}
	}

	internal void Reset()
	{
		AutocompleteThemeSnapshot snapshot = _snapshot;

		if (snapshot == null)
			return;

		Restore(snapshot.CodeEdit);
		if (ReferenceEquals(_snapshot, snapshot))
			_snapshot = null;
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
		Color appliedValue = value.Value;

		snapshot.ColorOverrides[themeKey] =
			new AutocompleteThemeSnapshot.ColorOverrideSnapshot(
				hadOverride,
				previousValue,
				appliedValue
			);
		codeEdit.AddThemeColorOverride(themeKey, appliedValue);
		if (
			!codeEdit.HasThemeColorOverride(themeKey)
			|| !codeEdit.GetThemeColor(themeKey).Equals(appliedValue)
		)
		{
			throw new InvalidOperationException(
				$"Theme color override '{themeKey}' did not retain the applied value."
			);
		}
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
		int appliedValue = value.Value;

		snapshot.ConstantOverrides[themeKey] =
			new AutocompleteThemeSnapshot.ConstantOverrideSnapshot(
				hadOverride,
				previousValue,
				appliedValue
			);
		codeEdit.AddThemeConstantOverride(themeKey, appliedValue);
		if (
			!codeEdit.HasThemeConstantOverride(themeKey)
			|| codeEdit.GetThemeConstant(themeKey) != appliedValue
		)
		{
			throw new InvalidOperationException(
				$"Theme constant override '{themeKey}' did not retain the applied value."
			);
		}
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
		{
			snapshot.PreviousCompletionStylebox = codeEdit.GetThemeStylebox(
				CompletionStyleboxThemeKey
			);
		}

		snapshot.AppliedCompletionStylebox = completionStyleboxOverride;
		snapshot.AppliedCompletionStyleboxInstanceId = completionStyleboxOverride.GetInstanceId();
		codeEdit.AddThemeStyleboxOverride(
			CompletionStyleboxThemeKey,
			completionStyleboxOverride
		);

		if (!codeEdit.HasThemeStyleboxOverride(CompletionStyleboxThemeKey))
		{
			throw new InvalidOperationException(
				"Completion StyleBox override was not retained."
			);
		}

		StyleBox currentOverride = codeEdit.GetThemeStylebox(CompletionStyleboxThemeKey);
		if (
			!IsValidGodotObject(currentOverride)
			|| currentOverride.GetInstanceId() != snapshot.AppliedCompletionStyleboxInstanceId
		)
		{
			throw new InvalidOperationException(
				"Completion StyleBox override identity changed during Apply."
			);
		}
	}

	private static bool IsSnapshotStillExactlyApplied(
		CodeEdit codeEdit,
		AutocompleteThemeSnapshot snapshot
	)
	{
		try
		{
			foreach (
				KeyValuePair<string, AutocompleteThemeSnapshot.ColorOverrideSnapshot> entry
					in snapshot.ColorOverrides
			)
			{
				if (
					!codeEdit.HasThemeColorOverride(entry.Key)
					|| !codeEdit.GetThemeColor(entry.Key).Equals(entry.Value.AppliedValue)
				)
				{
					return false;
				}
			}

			foreach (
				KeyValuePair<string, AutocompleteThemeSnapshot.ConstantOverrideSnapshot> entry
					in snapshot.ConstantOverrides
			)
			{
				if (
					!codeEdit.HasThemeConstantOverride(entry.Key)
					|| codeEdit.GetThemeConstant(entry.Key) != entry.Value.AppliedValue
				)
				{
					return false;
				}
			}

			if (snapshot.HasCompletionStyleboxSnapshot)
			{
				if (!codeEdit.HasThemeStyleboxOverride(CompletionStyleboxThemeKey))
					return false;
				StyleBox currentOverride = codeEdit.GetThemeStylebox(CompletionStyleboxThemeKey);
				if (
					!IsValidGodotObject(currentOverride)
					|| currentOverride.GetInstanceId()
						!= snapshot.AppliedCompletionStyleboxInstanceId
				)
				{
					return false;
				}
			}

			return true;
		}
		catch
		{
			return false;
		}
	}

	private static bool IsValidGodotObject(GodotObject source)
	{
		return source != null && GodotObject.IsInstanceValid(source);
	}
}
#endif
