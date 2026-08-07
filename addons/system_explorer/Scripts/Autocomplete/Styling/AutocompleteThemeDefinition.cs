#if TOOLS
using Godot;

namespace SystemExplorer.Autocomplete.Styling;

internal sealed class AutocompleteThemeDefinition
{
	internal Color? CompletionBackgroundColor { get; init; }
	internal Color? CompletionSelectedColor { get; init; }
	internal Color? CompletionExistingColor { get; init; }
	internal Color? CompletionScrollColor { get; init; }
	internal Color? CompletionScrollHoveredColor { get; init; }

	internal int? CompletionLines { get; init; }
	internal int? CompletionMaxWidth { get; init; }
	internal int? CompletionScrollWidth { get; init; }
	internal int? HorizontalSeparation { get; init; }

	internal Color? PopupBackgroundColor { get; init; }
	internal Color? BorderColor { get; init; }
	internal Color? ShadowColor { get; init; }
	internal Vector2? ShadowOffset { get; init; }

	internal int? CornerRadius { get; init; }
	internal int? BorderWidth { get; init; }
	internal int? ShadowSize { get; init; }

	internal float? ContentMarginLeft { get; init; }
	internal float? ContentMarginTop { get; init; }
	internal float? ContentMarginRight { get; init; }
	internal float? ContentMarginBottom { get; init; }
}
#endif
