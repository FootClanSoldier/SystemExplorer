#if TOOLS
#nullable enable annotations
using Godot;
using System;
using System.Collections.Generic;

namespace SystemExplorer.CodeService.Autocomplete.Styling;

internal sealed class AutocompleteGodotTypeIconProvider
{
	private const int ClassServiceKind = 7;
	private const int StructServiceKind = 22;
	private const string GodotNamespace = "Godot";
	private const string EditorIconsThemeType = "EditorIcons";
	private const int MaximumParentHops = 64;

	private readonly Func<Theme?> _editorThemeProvider;

	internal AutocompleteGodotTypeIconProvider(Func<Theme?> editorThemeProvider)
	{
		_editorThemeProvider = editorThemeProvider
			?? throw new ArgumentNullException(nameof(editorThemeProvider));
	}

	internal Texture2D? ResolveIcon(
		int? serviceKind,
		string displayText,
		string? containingNamespace)
	{
		if (serviceKind != ClassServiceKind && serviceKind != StructServiceKind)
			return null;
		if (!string.Equals(containingNamespace, GodotNamespace, StringComparison.Ordinal))
			return null;
		if (string.IsNullOrWhiteSpace(displayText))
			return null;

		try
		{
			Theme? editorTheme = _editorThemeProvider();
			if (!IsValidGodotObject(editorTheme))
				return null;

			string nativeTypeName = AutocompleteGodotTypeIconNameMap.ResolveNativeName(displayText);
			if (TryGetEditorIcon(editorTheme, nativeTypeName, out Texture2D? exactIcon))
				return exactIcon;

			return TryResolveParentIcon(editorTheme, nativeTypeName);
		}
		catch
		{
			return null;
		}
	}

	private static Texture2D? TryResolveParentIcon(Theme editorTheme, string nativeTypeName)
	{
		try
		{
			if (!ClassDB.ClassExists(nativeTypeName))
				return null;

			var visited = new HashSet<string>(StringComparer.Ordinal) { nativeTypeName };
			string currentTypeName = nativeTypeName;

			for (int hop = 0; hop < MaximumParentHops; hop++)
			{
				StringName parentName = ClassDB.GetParentClass(currentTypeName);
				string parentTypeName = parentName.ToString();
				if (string.IsNullOrEmpty(parentTypeName) || !visited.Add(parentTypeName))
					return null;

				if (TryGetEditorIcon(editorTheme, parentTypeName, out Texture2D? parentIcon))
					return parentIcon;

				if (!ClassDB.ClassExists(parentTypeName))
					return null;

				currentTypeName = parentTypeName;
			}
		}
		catch
		{
			return null;
		}

		return null;
	}

	private static bool TryGetEditorIcon(Theme editorTheme, string iconName, out Texture2D? icon)
	{
		icon = null;
		if (!IsValidGodotObject(editorTheme) || string.IsNullOrEmpty(iconName))
			return false;

		try
		{
			if (!editorTheme.HasIcon(iconName, EditorIconsThemeType))
				return false;

			Texture2D candidate = editorTheme.GetIcon(iconName, EditorIconsThemeType);
			if (!IsValidGodotObject(candidate))
				return false;

			icon = candidate;
			return true;
		}
		catch
		{
			return false;
		}
	}

	private static bool IsValidGodotObject(GodotObject? source)
	{
		return source != null && GodotObject.IsInstanceValid(source);
	}
}
#endif
