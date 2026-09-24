#if TOOLS
#nullable enable annotations
using Godot;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace SystemExplorer.CodeService.Autocomplete.Styling;

internal sealed class AutocompleteCompletionIconProvider
{
	private const string FontColorThemeKey = "font_color";
	private const string CompletionBackgroundColorThemeKey = "completion_background_color";
	private const string FallbackIconFileName = "symbol-misc.svg";
	private const string CurrentColorFillAttribute = "fill=\"currentColor\"";
	private const float PaletteLuminanceThreshold = 0.5f;

	private static readonly Color DarkPurple = FromRgb(0xB1, 0x80, 0xD7);
	private static readonly Color DarkBlue = FromRgb(0x75, 0xBE, 0xFF);
	private static readonly Color DarkOrange = FromRgb(0xEE, 0x9D, 0x28);
	private static readonly Color DarkNeutralFallback = FromRgb(0xCC, 0xCC, 0xCC);
	private static readonly Color LightPurple = FromRgb(0x65, 0x2D, 0x90);
	private static readonly Color LightBlue = FromRgb(0x00, 0x7A, 0xCC);
	private static readonly Color LightOrange = FromRgb(0xD6, 0x7E, 0x00);
	private static readonly Color LightNeutralFallback = FromRgb(0x61, 0x61, 0x61);

	private readonly string _iconRootPath;
	private readonly AutocompleteGodotTypeIconProvider _godotTypeIconProvider;
	private readonly Dictionary<string, string> _svgSourceCache = new(StringComparer.Ordinal);
	private readonly HashSet<string> _unavailableSvgSources = new(StringComparer.Ordinal);
	private readonly Dictionary<(string FileName, uint ColorKey), Texture2D> _textureCache = new();
	private readonly HashSet<(string FileName, uint ColorKey)> _unavailableTextures = new();

	private enum IconColorRole
	{
		Neutral,
		Purple,
		Blue,
		Orange,
	}

	private enum PaletteMode
	{
		Dark,
		Light,
	}

	internal AutocompleteCompletionIconProvider(
		string iconRootPath,
		AutocompleteGodotTypeIconProvider godotTypeIconProvider)
	{
		if (string.IsNullOrWhiteSpace(iconRootPath))
			throw new ArgumentException("Autocomplete icon root is required.", nameof(iconRootPath));

		_iconRootPath = iconRootPath.TrimEnd('/');
		_godotTypeIconProvider = godotTypeIconProvider
			?? throw new ArgumentNullException(nameof(godotTypeIconProvider));
	}

	internal Texture2D? ResolveIcon(
		CodeEdit codeEdit,
		int? serviceKind,
		string displayText,
		string? containingNamespace)
	{
		try
		{
			Texture2D? godotTypeIcon = _godotTypeIconProvider.ResolveIcon(
				serviceKind,
				displayText,
				containingNamespace);
			if (IsValidGodotObject(godotTypeIcon))
				return godotTypeIcon;
		}
		catch
		{
			// A Godot-native presentation failure must never bypass Codicon fallback.
		}

		try
		{
			string fileName = GetIconFileName(serviceKind);
			Color effectiveColor = ResolveIconColor(codeEdit, serviceKind);
			uint colorKey = PackColor(effectiveColor);
			var cacheKey = (fileName, colorKey);

			if (_textureCache.TryGetValue(cacheKey, out Texture2D? cachedTexture))
				return IsValidGodotObject(cachedTexture) ? cachedTexture : null;
			if (_unavailableTextures.Contains(cacheKey))
				return null;

			string? source = GetSvgSource(fileName);
			if (source == null)
			{
				_unavailableTextures.Add(cacheKey);
				return null;
			}

			string transformedSource = ApplyCurrentColor(source, colorKey);
			DpiTexture texture = DpiTexture.CreateFromString(transformedSource);
			if (!IsValidGodotObject(texture))
			{
				_unavailableTextures.Add(cacheKey);
				return null;
			}

			_textureCache[cacheKey] = texture;
			return texture;
		}
		catch
		{
			return null;
		}
	}

	private static string GetIconFileName(int? serviceKind)
	{
		return serviceKind switch
		{
			1 => "symbol-key.svg",
			2 => "symbol-method.svg",
			3 => "symbol-method.svg",
			4 => "symbol-method.svg",
			5 => "symbol-field.svg",
			6 => "symbol-variable.svg",
			7 => "symbol-class.svg",
			8 => "symbol-interface.svg",
			9 => "symbol-namespace.svg",
			10 => "symbol-property.svg",
			11 => "symbol-ruler.svg",
			12 => "symbol-enum.svg",
			13 => "symbol-enum.svg",
			14 => "symbol-keyword.svg",
			15 => "symbol-snippet.svg",
			16 => "symbol-color.svg",
			17 => "symbol-file.svg",
			18 => "go-to-file.svg",
			19 => "folder.svg",
			20 => "symbol-enum-member.svg",
			21 => "symbol-constant.svg",
			22 => "symbol-structure.svg",
			23 => "symbol-event.svg",
			24 => "symbol-operator.svg",
			25 => "symbol-parameter.svg",
			_ => FallbackIconFileName,
		};
	}

	private static IconColorRole GetIconColorRole(int? serviceKind)
	{
		return serviceKind switch
		{
			2 or 3 or 4 => IconColorRole.Purple,
			5 or 6 or 8 or 20 => IconColorRole.Blue,
			7 or 12 or 13 or 23 => IconColorRole.Orange,
			_ => IconColorRole.Neutral,
		};
	}

	private string? GetSvgSource(string fileName)
	{
		if (_svgSourceCache.TryGetValue(fileName, out string? cachedSource))
			return cachedSource;
		if (_unavailableSvgSources.Contains(fileName))
			return null;

		try
		{
			string path = _iconRootPath + "/" + fileName;
			if (!FileAccess.FileExists(path))
			{
				_unavailableSvgSources.Add(fileName);
				return null;
			}

			using FileAccess file = FileAccess.Open(path, FileAccess.ModeFlags.Read);
			if (file == null)
			{
				_unavailableSvgSources.Add(fileName);
				return null;
			}

			string source = file.GetAsText();
			if (string.IsNullOrWhiteSpace(source))
			{
				_unavailableSvgSources.Add(fileName);
				return null;
			}

			_svgSourceCache[fileName] = source;
			return source;
		}
		catch
		{
			_unavailableSvgSources.Add(fileName);
			return null;
		}
	}

	private static Color ResolveIconColor(CodeEdit codeEdit, int? serviceKind)
	{
		PaletteMode paletteMode = ResolvePaletteMode(codeEdit);
		return GetIconColorRole(serviceKind) switch
		{
			IconColorRole.Purple => paletteMode == PaletteMode.Dark ? DarkPurple : LightPurple,
			IconColorRole.Blue => paletteMode == PaletteMode.Dark ? DarkBlue : LightBlue,
			IconColorRole.Orange => paletteMode == PaletteMode.Dark ? DarkOrange : LightOrange,
			_ => ResolveNeutralColor(codeEdit, paletteMode),
		};
	}

	private static Color ResolveNeutralColor(CodeEdit codeEdit, PaletteMode paletteMode)
	{
		if (TryGetThemeColor(codeEdit, FontColorThemeKey, out Color fontColor))
			return fontColor;

		return paletteMode == PaletteMode.Dark ? DarkNeutralFallback : LightNeutralFallback;
	}

	private static PaletteMode ResolvePaletteMode(CodeEdit codeEdit)
	{
		if (TryGetThemeColor(codeEdit, CompletionBackgroundColorThemeKey, out Color completionBackgroundColor))
		{
			return GetLuminance(completionBackgroundColor) < PaletteLuminanceThreshold
				? PaletteMode.Dark
				: PaletteMode.Light;
		}

		if (TryGetThemeColor(codeEdit, FontColorThemeKey, out Color fontColor))
		{
			return GetLuminance(fontColor) >= PaletteLuminanceThreshold
				? PaletteMode.Dark
				: PaletteMode.Light;
		}

		return PaletteMode.Dark;
	}

	private static bool TryGetThemeColor(CodeEdit codeEdit, string themeKey, out Color color)
	{
		color = default;
		if (!IsValidGodotObject(codeEdit))
			return false;

		try
		{
			Color candidate = codeEdit.GetThemeColor(themeKey);
			if (!IsFinite(candidate))
				return false;

			color = candidate;
			return true;
		}
		catch
		{
			return false;
		}
	}

	private static float GetLuminance(Color color)
	{
		return (0.2126f * color.R)
			+ (0.7152f * color.G)
			+ (0.0722f * color.B);
	}

	private static Color FromRgb(byte red, byte green, byte blue)
	{
		return new Color(red / 255.0f, green / 255.0f, blue / 255.0f, 1.0f);
	}

	private static string ApplyCurrentColor(string source, uint packedColor)
	{
		byte red = (byte)(packedColor >> 24);
		byte green = (byte)(packedColor >> 16);
		byte blue = (byte)(packedColor >> 8);
		byte alpha = (byte)packedColor;
		string rgb = $"#{red:X2}{green:X2}{blue:X2}";

		if (source.Contains(CurrentColorFillAttribute, StringComparison.Ordinal))
		{
			string fill = $"fill=\"{rgb}\"";
			if (alpha < byte.MaxValue)
			{
				string opacity = (alpha / 255.0).ToString("0.###", CultureInfo.InvariantCulture);
				fill += $" fill-opacity=\"{opacity}\"";
			}
			return source.Replace(CurrentColorFillAttribute, fill, StringComparison.Ordinal);
		}

		return source.Replace("currentColor", rgb, StringComparison.Ordinal);
	}

	private static uint PackColor(Color color)
	{
		byte red = ToColorByte(color.R);
		byte green = ToColorByte(color.G);
		byte blue = ToColorByte(color.B);
		byte alpha = ToColorByte(color.A);
		return ((uint)red << 24) | ((uint)green << 16) | ((uint)blue << 8) | alpha;
	}

	private static byte ToColorByte(float channel)
	{
		float clamped = Math.Clamp(channel, 0.0f, 1.0f);
		return (byte)Math.Clamp((int)Math.Round(clamped * 255.0f), 0, byte.MaxValue);
	}

	private static bool IsFinite(Color color)
	{
		return float.IsFinite(color.R)
			&& float.IsFinite(color.G)
			&& float.IsFinite(color.B)
			&& float.IsFinite(color.A);
	}

	private static bool IsValidGodotObject(GodotObject? source)
	{
		return source != null && GodotObject.IsInstanceValid(source);
	}
}
#endif
