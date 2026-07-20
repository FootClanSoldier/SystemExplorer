#if TOOLS
using Godot;
using System;
using System.Text;

namespace SystemExplorer.EditorIntegration.ScriptEditing;

internal static class ScriptTextFileService
{
	private static readonly UTF8Encoding Utf8NoBomEncoding = new(false);

	internal static string ReadText(string path)
	{
		string globalPath = GetGlobalTextFilePath(path);

		if (string.IsNullOrWhiteSpace(globalPath) || !System.IO.File.Exists(globalPath))
			return "";

		try
		{
			return System.IO.File.ReadAllText(globalPath, Encoding.UTF8);
		}
		catch
		{
			return "";
		}
	}

	internal static bool WriteText(string path, string text)
	{
		string globalPath = GetGlobalTextFilePath(path);

		if (string.IsNullOrWhiteSpace(globalPath))
			return false;

		try
		{
			System.IO.File.WriteAllText(globalPath, text ?? "", Utf8NoBomEncoding);
			return true;
		}
		catch
		{
			return false;
		}
	}

	internal static bool TextsMatchForDiskVerification(string left, string right)
	{
		return NormalizeForDiskVerification(left) == NormalizeForDiskVerification(right);
	}

	internal static string NormalizeForDiskVerification(string text)
	{
		return (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
	}

	private static string GetGlobalTextFilePath(string path)
	{
		if (string.IsNullOrWhiteSpace(path))
			return "";

		if (
			path.StartsWith("res://", StringComparison.OrdinalIgnoreCase)
			|| path.StartsWith("user://", StringComparison.OrdinalIgnoreCase)
		)
		{
			return ProjectSettings.GlobalizePath(path);
		}

		return path;
	}
}
#endif
