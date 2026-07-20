#if TOOLS
using System;
using System.Text;
using System.Text.RegularExpressions;

namespace SystemExplorer.QuickActions.RefactorNamespace;

internal static class NamespaceTextRewriter
{
	private static readonly Regex NamespaceDeclarationRegex = new(
		@"(?m)^(\s*namespace\s+)([A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*)(\s*(?:;|\{))",
		RegexOptions.Compiled
	);

	private static readonly Regex NamespaceIdentifierRegex = new(
		@"^[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*$",
		RegexOptions.Compiled
	);

	internal static bool IsValidNamespaceName(string namespaceName)
	{
		return !string.IsNullOrWhiteSpace(namespaceName)
			&& NamespaceIdentifierRegex.IsMatch(namespaceName);
	}

	internal static string GetNamespaceFromText(string scriptText)
	{
		if (string.IsNullOrWhiteSpace(scriptText))
			return "";

		Match match = NamespaceDeclarationRegex.Match(scriptText);
		return match.Success ? match.Groups[2].Value : "";
	}

	internal static string ReplaceNamespaceDeclaration(
		string scriptText,
		string oldNamespace,
		string newNamespace,
		out bool changed
	)
	{
		bool didChange = false;

		string updatedText = NamespaceDeclarationRegex.Replace(
			scriptText,
			match =>
			{
				if (didChange || match.Groups[2].Value != oldNamespace)
					return match.Value;

				didChange = true;
				return $"{match.Groups[1].Value}{newNamespace}{match.Groups[3].Value}";
			},
			1
		);

		changed = didChange;
		return updatedText;
	}

	internal static string AddNamespaceBlock(
		string scriptText,
		string newNamespace,
		out bool changed
	)
	{
		changed = false;

		if (string.IsNullOrWhiteSpace(scriptText) || !IsValidNamespaceName(newNamespace))
			return scriptText ?? "";

		if (!string.IsNullOrWhiteSpace(GetNamespaceFromText(scriptText)))
			return scriptText;

		string normalizedText = (scriptText ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
		int insertionIndex = GetNamespaceInsertionIndexAfterTopUsingDirectives(normalizedText);
		string prefix = normalizedText.Substring(0, insertionIndex).TrimEnd();
		string body = normalizedText.Substring(insertionIndex).TrimStart('\n');

		if (string.IsNullOrWhiteSpace(body))
			return scriptText;

		string indentedBody = IndentNamespaceBody(body);
		StringBuilder builder = new();

		if (!string.IsNullOrWhiteSpace(prefix))
		{
			builder.Append(prefix);
			builder.Append("\n\n");
		}

		builder.Append("namespace ");
		builder.Append(newNamespace);
		builder.Append("\n{\n");
		builder.Append(indentedBody.TrimEnd('\n'));
		builder.Append("\n}\n");

		changed = true;
		return builder.ToString();
	}

	internal static string AddUsingStatementIfMissing(
		string scriptText,
		string namespaceName,
		string insertAfterNamespace,
		out bool changed
	)
	{
		changed = false;

		if (
			string.IsNullOrEmpty(scriptText)
			|| !IsValidNamespaceName(namespaceName)
			|| !IsValidNamespaceName(insertAfterNamespace)
		)
		{
			return scriptText ?? "";
		}

		Regex existingUsingRegex = new(
			$@"(?m)^\s*using\s+{Regex.Escape(namespaceName)}\s*;",
			RegexOptions.Compiled
		);

		if (existingUsingRegex.IsMatch(scriptText))
			return scriptText;

		Regex insertionUsingRegex = new(
			$@"(?m)^(?<indent>[ \t]*)using\s+{Regex.Escape(insertAfterNamespace)}\s*;(?<suffix>[^\r\n]*)(?<lineEnding>\r\n|\n|\r|$)",
			RegexOptions.Compiled
		);

		Match match = insertionUsingRegex.Match(scriptText);

		if (!match.Success)
			return scriptText;

		string lineEnding = match.Groups["lineEnding"].Value;

		if (lineEnding.Length == 0)
			lineEnding = DetectLineEnding(scriptText);

		string insertedUsing = $"{match.Groups["indent"].Value}using {namespaceName};";
		string replacement = match.Value;

		if (match.Groups["lineEnding"].Value.Length == 0)
			replacement += lineEnding;

		replacement += insertedUsing;

		if (match.Groups["lineEnding"].Value.Length > 0)
			replacement += lineEnding;

		changed = true;
		return scriptText.Substring(0, match.Index)
			+ replacement
			+ scriptText.Substring(match.Index + match.Length);
	}

	internal static string ReplaceUsingStatements(
		string scriptText,
		string oldNamespace,
		string newNamespace,
		out bool changed
	)
	{
		changed = false;

		if (
			string.IsNullOrEmpty(scriptText)
			|| !IsValidNamespaceName(oldNamespace)
			|| !IsValidNamespaceName(newNamespace)
			|| oldNamespace == newNamespace
		)
		{
			return scriptText ?? "";
		}

		Regex oldUsingRegex = CreateNormalUsingDirectiveRegex(oldNamespace);
		bool replacedUsing = false;
		string updatedText = oldUsingRegex.Replace(
			scriptText,
			match =>
			{
				replacedUsing = true;
				return match.Groups["indent"].Value
					+ match.Groups["prefix"].Value
					+ newNamespace
					+ match.Groups["suffix"].Value
					+ match.Groups["lineEnding"].Value;
			}
		);

		if (!replacedUsing)
			return scriptText;

		string deduplicatedText = DeduplicateNormalUsingDirective(
			updatedText,
			newNamespace,
			out bool removedDuplicate
		);

		changed = replacedUsing || removedDuplicate;
		return deduplicatedText;
	}

	private static Regex CreateNormalUsingDirectiveRegex(string namespaceName)
	{
		return new Regex(
			$@"(?m)^(?<indent>[ \t]*)(?<prefix>using[ \t]+){Regex.Escape(namespaceName)}(?<suffix>[ \t]*;[^\r\n]*)(?<lineEnding>\r\n|\n|\r|$)",
			RegexOptions.Compiled
		);
	}

	private static string DeduplicateNormalUsingDirective(
		string scriptText,
		string namespaceName,
		out bool changed
	)
	{
		bool foundUsing = false;
		bool removedDuplicate = false;
		Regex usingRegex = CreateNormalUsingDirectiveRegex(namespaceName);

		string updatedText = usingRegex.Replace(
			scriptText,
			match =>
			{
				if (!foundUsing)
				{
					foundUsing = true;
					return match.Value;
				}

				removedDuplicate = true;
				string trailingText = GetTrailingUsingText(match.Groups["suffix"].Value);

				if (trailingText.Length == 0)
					return "";

				return $"{match.Groups["indent"].Value}{trailingText}{match.Groups["lineEnding"].Value}";
			}
		);

		changed = removedDuplicate;
		return updatedText;
	}

	private static string GetTrailingUsingText(string usingSuffix)
	{
		int semicolonIndex = usingSuffix.IndexOf(';');

		if (semicolonIndex < 0 || semicolonIndex + 1 >= usingSuffix.Length)
			return "";

		return usingSuffix.Substring(semicolonIndex + 1).TrimStart();
	}

	private static string DetectLineEnding(string scriptText)
	{
		if (scriptText?.Contains("\r\n", StringComparison.Ordinal) == true)
			return "\r\n";

		if (scriptText?.Contains('\r') == true)
			return "\r";

		return "\n";
	}

	private static int GetNamespaceInsertionIndexAfterTopUsingDirectives(string scriptText)
	{
		if (string.IsNullOrEmpty(scriptText))
			return 0;

		int lineStart = 0;
		int insertionIndex = 0;
		bool foundUsingDirective = false;

		while (lineStart < scriptText.Length)
		{
			int lineEnd = scriptText.IndexOf('\n', lineStart);
			if (lineEnd < 0)
				lineEnd = scriptText.Length;

			string line = scriptText.Substring(lineStart, lineEnd - lineStart);
			string trimmedLine = line.Trim();
			int nextLineStart = lineEnd < scriptText.Length ? lineEnd + 1 : lineEnd;

			if (trimmedLine.Length == 0)
			{
				lineStart = nextLineStart;
				continue;
			}

			if (
				(
					trimmedLine.StartsWith("using ", StringComparison.Ordinal)
					|| trimmedLine.StartsWith("global using ", StringComparison.Ordinal)
				) && trimmedLine.EndsWith(";", StringComparison.Ordinal)
			)
			{
				foundUsingDirective = true;
				insertionIndex = nextLineStart;
				lineStart = nextLineStart;
				continue;
			}

			break;
		}

		return foundUsingDirective ? insertionIndex : 0;
	}

	private static string IndentNamespaceBody(string body)
	{
		string normalizedBody = (body ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
		string[] lines = normalizedBody.Split('\n');
		StringBuilder builder = new();

		for (int i = 0; i < lines.Length; i++)
		{
			string line = lines[i];

			if (line.Length > 0)
				builder.Append('\t');

			builder.Append(line);

			if (i < lines.Length - 1)
				builder.Append('\n');
		}

		return builder.ToString();
	}
}
#endif
