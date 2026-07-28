#if TOOLS
using Godot;
using System;
using System.IO;
using System.Text;

namespace SystemExplorer.EditorIntegration.ScriptEditing;

internal static class ScriptTextFileService
{
	private static readonly UTF8Encoding Utf8NoBomEncoding = new(false);
	private const int MaximumFailureDetailLength = 512;

	internal static ScriptTextFileReadResult TryReadText(string path)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			return ScriptTextFileReadResult.Failed(
				ScriptTextFileReadStatus.InvalidPath,
				"Path was null, empty, or whitespace."
			);
		}

		string globalPath;

		try
		{
			globalPath = GetGlobalTextFilePath(path);

			if (string.IsNullOrWhiteSpace(globalPath))
			{
				return ScriptTextFileReadResult.Failed(
					ScriptTextFileReadStatus.InvalidPath,
					"Path resolution produced an empty path."
				);
			}

			globalPath = Path.GetFullPath(globalPath);
		}
		catch (Exception exception)
		{
			return ScriptTextFileReadResult.Failed(
				ScriptTextFileReadStatus.InvalidPath,
				FormatFailureDetail("Path resolution failed", exception)
			);
		}

		try
		{
			return ScriptTextFileReadResult.Succeeded(
				File.ReadAllText(globalPath, Encoding.UTF8)
			);
		}
		catch (FileNotFoundException exception)
		{
			return ScriptTextFileReadResult.Failed(
				ScriptTextFileReadStatus.MissingFile,
				FormatFailureDetail("File was missing at read time", exception)
			);
		}
		catch (DirectoryNotFoundException exception)
		{
			return ScriptTextFileReadResult.Failed(
				ScriptTextFileReadStatus.MissingFile,
				FormatFailureDetail("File directory was missing at read time", exception)
			);
		}
		catch (DriveNotFoundException exception)
		{
			return ScriptTextFileReadResult.Failed(
				ScriptTextFileReadStatus.MissingFile,
				FormatFailureDetail("File drive was missing at read time", exception)
			);
		}
		catch (Exception exception)
		{
			return ScriptTextFileReadResult.Failed(
				ScriptTextFileReadStatus.ReadFailed,
				FormatFailureDetail("File read failed", exception)
			);
		}
	}

	internal static bool WriteText(string path, string text)
	{
		string globalPath = GetGlobalTextFilePath(path);

		if (string.IsNullOrWhiteSpace(globalPath))
			return false;

		try
		{
			File.WriteAllText(globalPath, text ?? "", Utf8NoBomEncoding);
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

	private static string FormatFailureDetail(string stage, Exception exception)
	{
		string exceptionType = exception?.GetType().Name ?? "UnknownException";
		string message = NormalizeDiagnosticText(exception?.Message);
		string detail = string.IsNullOrWhiteSpace(message)
			? $"{stage}: {exceptionType}."
			: $"{stage}: {exceptionType}: {message}";

		return detail.Length <= MaximumFailureDetailLength
			? detail
			: detail[..MaximumFailureDetailLength];
	}

	private static string NormalizeDiagnosticText(string text)
	{
		return string.IsNullOrWhiteSpace(text)
			? ""
			: text.Replace('\r', ' ').Replace('\n', ' ').Trim();
	}
}
#endif
