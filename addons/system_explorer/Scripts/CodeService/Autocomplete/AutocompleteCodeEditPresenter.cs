#if TOOLS
using Godot;
using System;
using System.Collections.Generic;

namespace SystemExplorer.CodeService.Autocomplete;

internal sealed class AutocompleteCodeEditPresenter
{
	private const string VisualRightPadding = "  ";

	internal bool TryPublish(CodeEdit codeEdit, IReadOnlyList<AutocompleteCompletionItem> items, out string detail)
	{
		detail = "";
		if (!IsValidGodotObject(codeEdit)) { detail = "A valid CodeEdit is required."; return false; }
		if (items == null) { detail = "Completion items are required."; return false; }

		int[] nativeLocations = new int[items.Count];
		for (int itemIndex = 0; itemIndex < items.Count; itemIndex++)
		{
			AutocompleteCompletionItem item = items[itemIndex];
			if (item == null || !item.HasValidCommitContract)
			{
				detail = "Completion item violates the managed commit contract; native publication was rejected.";
				return false;
			}
			if (item.RequiresImport && string.IsNullOrEmpty(item.DisplayText))
			{
				detail = "Import completion DisplayText is empty; native placeholder publication was rejected.";
				return false;
			}
			if (!AutocompleteCompletionLocationMapper.TryMap(item.SemanticOrigin, item.InheritanceDepth, out nativeLocations[itemIndex]))
			{
				detail = "Completion item semantic location metadata is internally inconsistent.";
				return false;
			}
		}

		for (int itemIndex = 0; itemIndex < items.Count; itemIndex++)
		{
			AutocompleteCompletionItem item = items[itemIndex];
			if (!item.RequiresImport)
			{
				// Preserve the ordinary native publication path exactly. Godot remains
				// the confirmation authority for ordinary completion items.
				codeEdit.AddCodeCompletionOption(
					item.Kind,
					GetNativeDisplayText(item),
					item.InsertText,
					location: nativeLocations[itemIndex]);
				continue;
			}

			// Import items deliberately have no Service-authorized direct InsertText.
			// DisplayText is only a native popup placeholder; the opaque Service handle
			// lives exclusively in CodeEdit's in-memory option value/default_value.
			codeEdit.AddCodeCompletionOption(
				item.Kind,
				GetNativeDisplayText(item),
				item.DisplayText,
				value: (Variant)item.CompletionHandle.Value.ToString("D"),
				location: nativeLocations[itemIndex]);
		}

		codeEdit.UpdateCodeCompletionOptions(true);
		TryApplyPreselectBestEffort(codeEdit, items);
		return true;
	}

	internal bool TryGetSelectedImportCompletion(
		CodeEdit codeEdit,
		AutocompleteCompletionSession session,
		out AutocompleteCompletionItem selectedItem,
		out string detail)
	{
		selectedItem = null;
		detail = "";
		if (!IsValidGodotObject(codeEdit) || session == null)
		{
			detail = "Current native completion/session is unavailable.";
			return false;
		}

		try
		{
			int selectedIndex = codeEdit.GetCodeCompletionSelectedIndex();
			if (selectedIndex < 0)
			{
				detail = "Native completion is not active.";
				return false;
			}

			var nativeOption = codeEdit.GetCodeCompletionOption(selectedIndex);
			Variant nativeDefaultValue = nativeOption["default_value"];
			if (nativeDefaultValue.VariantType != Variant.Type.String
				|| !TryParseCanonicalNonEmptyGuid(nativeDefaultValue.AsString(), out Guid handle))
			{
				detail = "Selected native option is not a managed import completion.";
				return false;
			}

			AutocompleteCompletionItem match = null;
			int matchCount = 0;
			foreach (AutocompleteCompletionItem item in session.PublishedItems)
			{
				if (item?.CompletionHandle != handle)
					continue;
				match = item;
				matchCount++;
				if (matchCount > 1)
					break;
			}
			if (matchCount != 1 || match == null || !match.HasValidCommitContract
				|| !match.RequiresImport || match.InsertText != null || !match.CompletionHandle.HasValue)
			{
				detail = "Selected import handle did not map to exactly one managed import item.";
				return false;
			}

			Variant nativeKind = nativeOption["kind"];
			Variant nativeDisplayText = nativeOption["display_text"];
			Variant nativeInsertText = nativeOption["insert_text"];
			if (nativeKind.VariantType != Variant.Type.Int
				|| nativeDisplayText.VariantType != Variant.Type.String
				|| nativeInsertText.VariantType != Variant.Type.String
				|| nativeKind.AsInt64() != (long)match.Kind
				|| !string.Equals(nativeDisplayText.AsString(), GetNativeDisplayText(match), StringComparison.Ordinal)
				|| !string.Equals(nativeInsertText.AsString(), match.DisplayText, StringComparison.Ordinal)
				|| !string.Equals(nativeDefaultValue.AsString(), match.CompletionHandle.Value.ToString("D"), StringComparison.Ordinal))
			{
				detail = "Selected native import option no longer exactly matches its managed item.";
				return false;
			}

			selectedItem = match;
			return true;
		}
		catch (Exception exception)
		{
			detail = "Selected native completion option could not be inspected: " + ToSingleLine(exception.Message);
			return false;
		}
	}

	private static string GetNativeDisplayText(AutocompleteCompletionItem item) => (item?.DisplayText ?? "") + VisualRightPadding;

	private static void TryApplyPreselectBestEffort(CodeEdit codeEdit, IReadOnlyList<AutocompleteCompletionItem> items)
	{
		bool hasPreselectedItem = false;
		foreach (AutocompleteCompletionItem item in items)
		{
			if (item?.Preselect == true) { hasPreselectedItem = true; break; }
		}
		if (!hasPreselectedItem) return;

		try
		{
			var nativeOptions = codeEdit.GetCodeCompletionOptions();
			foreach (AutocompleteCompletionItem item in items)
			{
				if (item?.Preselect != true || !item.HasValidCommitContract) continue;
				string expectedDisplayText = GetNativeDisplayText(item);
				string expectedInsertText = item.RequiresImport ? item.DisplayText : item.InsertText;
				string expectedHandle = item.RequiresImport ? item.CompletionHandle.Value.ToString("D") : null;
				int matchingIndex = -1;
				int matchingCount = 0;

				for (int nativeIndex = 0; nativeIndex < nativeOptions.Count; nativeIndex++)
				{
					var nativeOption = nativeOptions[nativeIndex];
					Variant nativeKind = nativeOption["kind"];
					Variant nativeDisplayText = nativeOption["display_text"];
					Variant nativeInsertText = nativeOption["insert_text"];
					Variant nativeDefaultValue = nativeOption["default_value"];
					bool valueMatches = !item.RequiresImport
						|| (nativeDefaultValue.VariantType == Variant.Type.String
							&& string.Equals(nativeDefaultValue.AsString(), expectedHandle, StringComparison.Ordinal));

					if (nativeKind.VariantType != Variant.Type.Int
						|| nativeDisplayText.VariantType != Variant.Type.String
						|| nativeInsertText.VariantType != Variant.Type.String
						|| nativeKind.AsInt64() != (long)item.Kind
						|| !string.Equals(nativeDisplayText.AsString(), expectedDisplayText, StringComparison.Ordinal)
						|| !string.Equals(nativeInsertText.AsString(), expectedInsertText, StringComparison.Ordinal)
						|| !valueMatches)
						continue;

					matchingIndex = nativeIndex;
					matchingCount++;
					if (matchingCount > 1) break;
				}

				if (matchingCount == 1)
				{
					codeEdit.SetCodeCompletionSelectedIndex(matchingIndex);
					return;
				}
			}
		}
		catch
		{
			// Preselect is presentation metadata only.
		}
	}

	private static bool TryParseCanonicalNonEmptyGuid(string text, out Guid guid)
	{
		guid = Guid.Empty;
		return !string.IsNullOrEmpty(text)
			&& Guid.TryParseExact(text, "D", out guid)
			&& guid != Guid.Empty
			&& string.Equals(guid.ToString("D"), text, StringComparison.Ordinal);
	}
	private static string ToSingleLine(string value) => (value ?? "").Replace('\r', ' ').Replace('\n', ' ');
	private static bool IsValidGodotObject(GodotObject source) => source != null && GodotObject.IsInstanceValid(source);
}
#endif
