#if TOOLS
using Godot;
using System;
using System.Collections.Generic;
using SystemExplorer.Autocomplete.Confirmation;

namespace SystemExplorer.Autocomplete;

internal sealed class AutocompleteCodeEditPresenter
{
	private const string VisualRightPadding = "  ";

	private readonly AutocompleteCompletionOptionMetadataCodec _metadataCodec;

	internal AutocompleteCodeEditPresenter(
		AutocompleteCompletionOptionMetadataCodec metadataCodec
	)
	{
		_metadataCodec =
			metadataCodec ?? throw new ArgumentNullException(nameof(metadataCodec));
	}

	internal void Publish(
		CodeEdit codeEdit,
		IReadOnlyList<AutocompleteCompletionItem> items
	)
	{
		if (!IsValidGodotObject(codeEdit))
			throw new ArgumentException("A valid CodeEdit is required.", nameof(codeEdit));
		if (items == null)
			throw new ArgumentNullException(nameof(items));

		foreach (AutocompleteCompletionItem item in items)
		{
			if (item == null)
				continue;

			string displayText = (item.DisplayText ?? "") + VisualRightPadding;

			if (item.Metadata == null)
			{
				codeEdit.AddCodeCompletionOption(
					item.Kind,
					displayText,
					item.InsertText ?? ""
				);
				continue;
			}

			Godot.Collections.Dictionary metadataValue = _metadataCodec.Encode(
				item.Metadata
			);
			Variant encodedValue = metadataValue;

			codeEdit.AddCodeCompletionOption(
				item.Kind,
				displayText,
				item.InsertText ?? "",
				null,
				null,
				encodedValue
			);
		}

		codeEdit.UpdateCodeCompletionOptions(true);
	}

	private static bool IsValidGodotObject(GodotObject source)
	{
		return source != null && GodotObject.IsInstanceValid(source);
	}
}
#endif
