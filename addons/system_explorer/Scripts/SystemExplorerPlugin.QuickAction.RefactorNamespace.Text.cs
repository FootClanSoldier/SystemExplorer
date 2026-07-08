#if TOOLS
using Godot;
using System;
using System.Text;
using System.Text.RegularExpressions;

public partial class SystemExplorerPlugin
{
	#region Quick Actions - Refactor Namespace Text
	private static readonly Regex NamespaceDeclarationRegex = new(
		@"(?m)^(\s*namespace\s+)([A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*)(\s*(?:;|\{))",
		RegexOptions.Compiled
	);

	private static readonly Regex NamespaceIdentifierRegex = new(
		@"^[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*$",
		RegexOptions.Compiled
	);

	private static bool IsValidNamespaceName(string namespaceName)
	{
		return !string.IsNullOrWhiteSpace(namespaceName)
			&& NamespaceIdentifierRegex.IsMatch(namespaceName);
	}

	private static string ReadNamespaceFromScript(string scriptPath)
	{
		if (!FileAccess.FileExists(scriptPath))
			return "";

		return GetNamespaceFromText(ReadTextFile(scriptPath));
	}

	private static string GetNamespaceFromText(string scriptText)
	{
		if (string.IsNullOrWhiteSpace(scriptText))
			return "";

		Match match = NamespaceDeclarationRegex.Match(scriptText);
		return match.Success ? match.Groups[2].Value : "";
	}

	private static string ReplaceNamespaceDeclaration(
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

	private static string AddNamespaceBlockToScriptText(
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

	private static string ReplaceUsingStatements(
		string scriptText,
		string oldNamespace,
		string newNamespace,
		out bool changed
	)
	{
		bool didChange = false;

		Regex usingRegex = new(
			$@"(?m)^(\s*using\s+){Regex.Escape(oldNamespace)}(\s*;)",
			RegexOptions.Compiled
		);

		string updatedText = usingRegex.Replace(
			scriptText,
			match =>
			{
				didChange = true;
				return $"{match.Groups[1].Value}{newNamespace}{match.Groups[2].Value}";
			}
		);

		changed = didChange;
		return updatedText;
	}

	#endregion
}
#endif
