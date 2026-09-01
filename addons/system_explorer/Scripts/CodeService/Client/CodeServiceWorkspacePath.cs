#if TOOLS
using System;
using System.IO;

namespace SystemExplorer.CodeService.Client;

internal static class CodeServiceWorkspacePath
{
	private static readonly StringComparer PlatformComparer =
		OperatingSystem.IsWindows()
			? StringComparer.OrdinalIgnoreCase
			: StringComparer.Ordinal;

	internal static bool TryNormalize(
		string projectRoot,
		out string normalizedProjectRoot,
		out string detail
	)
	{
		normalizedProjectRoot = "";
		detail = "";

		if (string.IsNullOrWhiteSpace(projectRoot))
		{
			detail = "projectRoot must be a non-empty absolute filesystem path.";
			return false;
		}

		if (projectRoot.Length > CodeServiceClientProtocol.MaxWorkspaceProjectRootLength)
		{
			detail =
				$"projectRoot exceeds the maximum length of {CodeServiceClientProtocol.MaxWorkspaceProjectRootLength} characters.";
			return false;
		}

		try
		{
			if (!Path.IsPathFullyQualified(projectRoot))
			{
				detail = "projectRoot must be a fully-qualified absolute filesystem path.";
				return false;
			}

			string fullPath = Path.GetFullPath(projectRoot);
			string normalized = Path.TrimEndingDirectorySeparator(fullPath);
			if (
				string.IsNullOrEmpty(normalized)
				|| normalized.Length > CodeServiceClientProtocol.MaxWorkspaceProjectRootLength
			)
			{
				detail = "projectRoot could not be normalized to a bounded absolute path.";
				return false;
			}

			normalizedProjectRoot = normalized;
			return true;
		}
		catch (Exception exception) when (IsControlledPathException(exception))
		{
			detail =
				"projectRoot is not a valid absolute filesystem path: "
				+ ToSingleLine(exception.Message);
			return false;
		}
	}

	internal static bool EqualsNormalized(string left, string right)
	{
		return PlatformComparer.Equals(left ?? "", right ?? "");
	}

	private static bool IsControlledPathException(Exception exception)
	{
		return exception is ArgumentException
			or NotSupportedException
			or PathTooLongException
			or IOException
			or UnauthorizedAccessException;
	}

	private static string ToSingleLine(string message)
	{
		return (message ?? "").Replace('\r', ' ').Replace('\n', ' ');
	}
}
#endif
