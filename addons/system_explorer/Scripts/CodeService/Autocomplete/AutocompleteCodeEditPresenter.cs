#if TOOLS
using Godot;
using System;
using System.Collections.Generic;

namespace SystemExplorer.CodeService.Autocomplete;

internal sealed class AutocompleteCodeEditPresenter
{
	private const string VisualRightPadding = "  ";

	internal bool TryPublish(
		CodeEdit codeEdit,
		IReadOnlyList<AutocompleteCompletionItem> items,
		out string detail
	)
	{
		detail = "";
		if (!IsValidGodotObject(codeEdit))
		{
			detail = "A valid CodeEdit is required.";
			return false;
		}
		if (items == null)
		{
			detail = "Completion items are required.";
			return false;
		}

		// Preflight every item before crossing the native CodeEdit boundary.
		// Godot's native completion confirmation indexes the final InsertText
		// character, so an empty value is unsafe to publish at all. Semantic
		// location mapping is also preflighted to prevent partial publication
		// if an impossible internal origin/depth combination reaches this layer.
		int[] nativeLocations = new int[items.Count];
		for (int itemIndex = 0; itemIndex < items.Count; itemIndex++)
		{
			AutocompleteCompletionItem item = items[itemIndex];
			if (item == null)
				continue;
			if (string.IsNullOrEmpty(item.InsertText))
			{
				detail = "Completion item InsertText is empty; native publication was rejected.";
				return false;
			}
			if (!AutocompleteCompletionLocationMapper.TryMap(
				item.SemanticOrigin,
				item.InheritanceDepth,
				out nativeLocations[itemIndex]
			))
			{
				detail = "Completion item semantic location metadata is internally inconsistent.";
				return false;
			}
		}

		for (int itemIndex = 0; itemIndex < items.Count; itemIndex++)
		{
			AutocompleteCompletionItem item = items[itemIndex];
			if (item == null)
				continue;

			codeEdit.AddCodeCompletionOption(
				item.Kind,
				GetNativeDisplayText(item),
				item.InsertText,
				location: nativeLocations[itemIndex]
			);
		}

		codeEdit.UpdateCodeCompletionOptions(true);
		TryApplyPreselectBestEffort(codeEdit, items);
		return true;
	}

	private static string GetNativeDisplayText(AutocompleteCompletionItem item)
	{
		return (item?.DisplayText ?? "") + VisualRightPadding;
	}

	private static void TryApplyPreselectBestEffort(
		CodeEdit codeEdit,
		IReadOnlyList<AutocompleteCompletionItem> items
	)
	{
		bool hasPreselectedItem = false;
		foreach (AutocompleteCompletionItem item in items)
		{
			if (item?.Preselect == true)
			{
				hasPreselectedItem = true;
				break;
			}
		}
		if (!hasPreselectedItem)
			return;

		try
		{
			var nativeOptions = codeEdit.GetCodeCompletionOptions();
			foreach (AutocompleteCompletionItem item in items)
			{
				if (item?.Preselect != true)
					continue;

				string expectedDisplayText = GetNativeDisplayText(item);
				int matchingIndex = -1;
				int matchingCount = 0;

				for (int nativeIndex = 0; nativeIndex < nativeOptions.Count; nativeIndex++)
				{
					var nativeOption = nativeOptions[nativeIndex];
					Variant nativeKind = nativeOption["kind"];
					Variant nativeDisplayText = nativeOption["display_text"];
					Variant nativeInsertText = nativeOption["insert_text"];

					if (nativeKind.VariantType != Variant.Type.Int
						|| nativeDisplayText.VariantType != Variant.Type.String
						|| nativeInsertText.VariantType != Variant.Type.String
						|| nativeKind.AsInt64() != (long)item.Kind
						|| !string.Equals(
							nativeDisplayText.AsString(),
							expectedDisplayText,
							StringComparison.Ordinal
						)
						|| !string.Equals(
							nativeInsertText.AsString(),
							item.InsertText,
							StringComparison.Ordinal
						))
					{
						continue;
					}

					matchingIndex = nativeIndex;
					matchingCount++;
					if (matchingCount > 1)
						break;
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
			// Preselect is presentation metadata only. Native inspection or selection
			// failure must not make an otherwise safe completion publication fatal.
		}
	}

	private static bool IsValidGodotObject(GodotObject source)
	{
		return source != null && GodotObject.IsInstanceValid(source);
	}
}
#endif
