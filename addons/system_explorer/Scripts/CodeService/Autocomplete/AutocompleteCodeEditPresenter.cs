#if TOOLS
using Godot;
using System;
using System.Collections.Generic;

namespace SystemExplorer.CodeService.Autocomplete;

internal sealed class AutocompleteCodeEditPresenter
{
	private const int VisualNamespaceMinimumGapColumns = 6;
	private const string VisualRightPadding = "  ";

	private readonly struct NativeDisplayLayout
	{
		internal NativeDisplayLayout(int namespaceRightEdgeColumn)
		{
			NamespaceRightEdgeColumn = namespaceRightEdgeColumn;
		}

		internal int NamespaceRightEdgeColumn { get; }
	}

	internal bool TryPublish(CodeEdit codeEdit, IReadOnlyList<AutocompleteCompletionItem> items, out string detail)
	{
		detail = "";
		if (!IsValidGodotObject(codeEdit)) { detail = "A valid CodeEdit is required."; return false; }
		if (items == null) { detail = "Completion items are required."; return false; }

		int[] nativeLocations = new int[items.Count];
		for (int itemIndex = 0; itemIndex < items.Count; itemIndex++)
		{
			AutocompleteCompletionItem item = items[itemIndex];
			if (item == null || !item.HasValidCommitContract || !item.HasValidNamespaceContract)
			{
				detail = "Completion item violates the managed commit/namespace contract; native publication was rejected.";
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

		NativeDisplayLayout displayLayout = BuildNativeDisplayLayout(items);

		for (int itemIndex = 0; itemIndex < items.Count; itemIndex++)
		{
			AutocompleteCompletionItem item = items[itemIndex];
			if (!item.RequiresImport)
			{
				// Preserve the ordinary native publication path exactly. Godot remains
				// confirmation authority except for the narrow direct/direct namespace
				// collision rows identified synchronously at commit time.
				codeEdit.AddCodeCompletionOption(
					item.Kind,
					GetNativeDisplayText(item, displayLayout),
					item.InsertText,
					location: nativeLocations[itemIndex]);
				continue;
			}

			// Import items deliberately have no Service-authorized direct InsertText.
			// DisplayText is only a native popup placeholder; the opaque Service handle
			// lives exclusively in CodeEdit's in-memory option value/default_value.
			codeEdit.AddCodeCompletionOption(
				item.Kind,
				GetNativeDisplayText(item, displayLayout),
				item.DisplayText,
				value: (Variant)item.CompletionHandle.Value.ToString("D"),
				location: nativeLocations[itemIndex]);
		}

		codeEdit.UpdateCodeCompletionOptions(true);
		TryApplyPreselectBestEffort(codeEdit, items, displayLayout);
		return true;
	}

	internal bool TryGetSelectedManagedCommitCompletion(
		CodeEdit codeEdit,
		AutocompleteCompletionSession session,
		out AutocompleteCompletionItem selectedItem,
		out AutocompleteManagedCommitShape commitShape,
		out bool selectedManagedCommitIdentified,
		out string detail)
	{
		selectedItem = null;
		commitShape = default;
		selectedManagedCommitIdentified = false;
		detail = "";
		if (!IsValidGodotObject(codeEdit) || session == null)
		{
			detail = "Current native completion/session is unavailable.";
			return false;
		}

		try
		{
			NativeDisplayLayout displayLayout = BuildNativeDisplayLayout(session.PublishedItems);
			int selectedIndex = codeEdit.GetCodeCompletionSelectedIndex();
			if (selectedIndex < 0)
			{
				detail = "Native completion is not active.";
				return false;
			}

			var nativeOption = codeEdit.GetCodeCompletionOption(selectedIndex);
			Variant nativeDefaultValue = nativeOption["default_value"];
			if (nativeDefaultValue.VariantType == Variant.Type.String
				&& TryParseCanonicalNonEmptyGuid(nativeDefaultValue.AsString(), out Guid handle))
			{
				return TryIdentifySelectedImportCompletion(
					nativeOption,
					nativeDefaultValue.AsString(),
					handle,
					session,
					displayLayout,
					out selectedItem,
					out commitShape,
					out selectedManagedCommitIdentified,
					out detail);
			}

			return TryIdentifySelectedDirectCollisionCompletion(
				nativeOption,
				session,
				displayLayout,
				out selectedItem,
				out commitShape,
				out selectedManagedCommitIdentified,
				out detail);
		}
		catch (Exception exception)
		{
			detail = "Selected native completion option could not be inspected: " + ToSingleLine(exception.Message);
			return false;
		}
	}

	private static bool TryIdentifySelectedImportCompletion(
		Godot.Collections.Dictionary nativeOption,
		string nativeHandleText,
		Guid handle,
		AutocompleteCompletionSession session,
		NativeDisplayLayout displayLayout,
		out AutocompleteCompletionItem selectedItem,
		out AutocompleteManagedCommitShape commitShape,
		out bool selectedManagedCommitIdentified,
		out string detail)
	{
		selectedItem = null;
		commitShape = default;
		selectedManagedCommitIdentified = false;
		detail = "";

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
		if (matchCount != 1 || match == null || !match.HasValidCommitContract || !match.HasValidNamespaceContract
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
			|| !string.Equals(nativeDisplayText.AsString(), GetNativeDisplayText(match, displayLayout), StringComparison.Ordinal)
			|| !string.Equals(nativeInsertText.AsString(), match.DisplayText, StringComparison.Ordinal)
			|| !string.Equals(nativeHandleText, match.CompletionHandle.Value.ToString("D"), StringComparison.Ordinal))
		{
			detail = "Selected native import option no longer exactly matches its managed item.";
			return false;
		}

		selectedItem = match;
		commitShape = HasDirectNamespaceDistinctSameLabelPeer(match, session.PublishedItems)
			? AutocompleteManagedCommitShape.QualifiedName
			: AutocompleteManagedCommitShape.ImportWithUsing;
		selectedManagedCommitIdentified = true;
		return true;
	}

	private static bool TryIdentifySelectedDirectCollisionCompletion(
		Godot.Collections.Dictionary nativeOption,
		AutocompleteCompletionSession session,
		NativeDisplayLayout displayLayout,
		out AutocompleteCompletionItem selectedItem,
		out AutocompleteManagedCommitShape commitShape,
		out bool selectedManagedCommitIdentified,
		out string detail)
	{
		selectedItem = null;
		commitShape = default;
		selectedManagedCommitIdentified = false;
		detail = "";

		Variant nativeKind = nativeOption["kind"];
		Variant nativeDisplayText = nativeOption["display_text"];
		Variant nativeInsertText = nativeOption["insert_text"];
		if (nativeKind.VariantType != Variant.Type.Int
			|| nativeDisplayText.VariantType != Variant.Type.String
			|| nativeInsertText.VariantType != Variant.Type.String)
		{
			detail = "Selected native option is not an exact managed ordinary collision candidate.";
			return false;
		}

		AutocompleteCompletionItem match = null;
		int matchCount = 0;
		foreach (AutocompleteCompletionItem item in session.PublishedItems)
		{
			if (item == null
				|| item.RequiresImport
				|| (item.Kind != CodeEdit.CodeCompletionKind.Class && item.Kind != CodeEdit.CodeCompletionKind.Enum)
				|| !item.HasValidCommitContract
				|| !item.HasValidNamespaceContract
				|| item.NamespaceDisambiguation == null
				|| item.ContainingNamespace == null
				|| nativeKind.AsInt64() != (long)item.Kind
				|| !string.Equals(nativeDisplayText.AsString(), GetNativeDisplayText(item, displayLayout), StringComparison.Ordinal)
				|| !string.Equals(nativeInsertText.AsString(), item.InsertText, StringComparison.Ordinal)
				|| !HasDirectNamespaceDistinctSameLabelPeer(item, session.PublishedItems))
			{
				continue;
			}

			match = item;
			matchCount++;
			if (matchCount > 1)
				break;
		}

		if (matchCount != 1 || match == null)
		{
			selectedManagedCommitIdentified = matchCount > 1;
			detail = matchCount > 1
				? "Selected native ordinary collision option matched multiple managed items."
				: "Selected native option is not a managed direct namespace collision requiring interception.";
			return false;
		}

		selectedItem = match;
		commitShape = AutocompleteManagedCommitShape.QualifiedName;
		selectedManagedCommitIdentified = true;
		return true;
	}

	private static bool HasDirectNamespaceDistinctSameLabelPeer(
		AutocompleteCompletionItem selected,
		IReadOnlyList<AutocompleteCompletionItem> publishedItems)
	{
		if (selected == null
			|| publishedItems == null
			|| !selected.HasValidCommitContract
			|| !selected.HasValidNamespaceContract
			|| selected.ContainingNamespace == null)
		{
			return false;
		}

		foreach (AutocompleteCompletionItem peer in publishedItems)
		{
			if (ReferenceEquals(peer, selected)
				|| peer == null
				|| !peer.HasValidCommitContract
				|| !peer.HasValidNamespaceContract
				|| peer.RequiresImport
				|| peer.NamespaceDisambiguation == null
				|| peer.ContainingNamespace == null
				|| !string.Equals(peer.DisplayText, selected.DisplayText, StringComparison.Ordinal)
				|| string.Equals(peer.ContainingNamespace, selected.ContainingNamespace, StringComparison.Ordinal))
			{
				continue;
			}

			return true;
		}
		return false;
	}

	private static NativeDisplayLayout BuildNativeDisplayLayout(IReadOnlyList<AutocompleteCompletionItem> items)
	{
		int naturalContentWidth = 0;
		int widestRequiredNamespaceRowWidth = 0;

		foreach (AutocompleteCompletionItem item in items)
		{
			string displayText = item?.DisplayText ?? "";
			naturalContentWidth = Math.Max(naturalContentWidth, displayText.Length);

			if (item?.NamespaceDisambiguation is string namespaceDisambiguation)
			{
				int requiredWidth = displayText.Length
					+ VisualNamespaceMinimumGapColumns
					+ namespaceDisambiguation.Length;
				widestRequiredNamespaceRowWidth = Math.Max(widestRequiredNamespaceRowWidth, requiredWidth);
			}
		}

		return new NativeDisplayLayout(Math.Max(naturalContentWidth, widestRequiredNamespaceRowWidth));
	}

	private static string GetNativeDisplayText(AutocompleteCompletionItem item, NativeDisplayLayout displayLayout)
	{
		string displayText = item?.DisplayText ?? "";
		if (item?.NamespaceDisambiguation is string namespaceDisambiguation)
		{
			int requiredGap = displayLayout.NamespaceRightEdgeColumn
				- displayText.Length
				- namespaceDisambiguation.Length;
			int gapColumns = Math.Max(VisualNamespaceMinimumGapColumns, requiredGap);
			displayText += new string(' ', gapColumns) + namespaceDisambiguation;
		}
		return displayText + VisualRightPadding;
	}

	private static void TryApplyPreselectBestEffort(
		CodeEdit codeEdit,
		IReadOnlyList<AutocompleteCompletionItem> items,
		NativeDisplayLayout displayLayout)
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
				string expectedDisplayText = GetNativeDisplayText(item, displayLayout);
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
