#if TOOLS
using System;
using System.Collections.Generic;

namespace SystemExplorer.CodeService.Documents;

internal static class CodeServiceDocumentPath
{
	internal static StringComparer PlatformComparer { get; } =
		OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

	internal static bool TryFromResourcePath(
		string resourcePath,
		out string documentPath,
		out string detail
	)
	{
		documentPath = "";
		detail = "";

		if (string.IsNullOrEmpty(resourcePath))
		{
			detail = "Document resource path is empty.";
			return false;
		}

		const string prefix = "res://";
		if (!resourcePath.StartsWith(prefix, StringComparison.Ordinal))
		{
			detail = "Document resource path must begin with res://.";
			return false;
		}

		if (resourcePath.IndexOf('\\') >= 0)
		{
			detail = "Document resource path must use forward slashes only.";
			return false;
		}

		string relative = resourcePath.Substring(prefix.Length);
		if (!TryValidateWirePath(relative, out detail))
			return false;

		documentPath = relative;
		return true;
	}

	internal static bool TryValidateWirePath(string documentPath, out string detail)
	{
		detail = "";
		if (string.IsNullOrEmpty(documentPath))
		{
			detail = "Document path is empty.";
			return false;
		}

		if (documentPath.Length > CodeServiceDocumentSynchronizationLimits.MaxDocumentPathLength)
		{
			detail = $"Document path exceeds {CodeServiceDocumentSynchronizationLimits.MaxDocumentPathLength} characters.";
			return false;
		}

		if (!documentPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
		{
			detail = "Document path must identify a C# .cs file.";
			return false;
		}

		if (documentPath[0] == '/' || documentPath[^1] == '/')
		{
			detail = "Document path must not begin or end with '/'.";
			return false;
		}

		if (documentPath.IndexOf('\\') >= 0)
		{
			detail = "Document path must use forward slashes only.";
			return false;
		}

		if (documentPath.IndexOf(':') >= 0)
		{
			detail = "Document path must not be rooted or drive-qualified.";
			return false;
		}

		string[] segments = documentPath.Split('/');
		foreach (string segment in segments)
		{
			if (segment.Length == 0 || segment == "." || segment == "..")
			{
				detail = "Document path contains an empty, '.' or '..' segment.";
				return false;
			}
		}

		return true;
	}

	internal static bool Equals(string left, string right) =>
		PlatformComparer.Equals(left ?? "", right ?? "");

	internal static HashSet<string> CreateSet(IEnumerable<string> paths) =>
		new(paths ?? Array.Empty<string>(), PlatformComparer);
}
#endif
