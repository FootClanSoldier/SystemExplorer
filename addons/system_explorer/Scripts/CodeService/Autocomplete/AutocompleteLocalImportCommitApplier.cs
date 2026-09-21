#if TOOLS
using Godot;
using System;

namespace SystemExplorer.CodeService.Autocomplete;

internal enum AutocompleteLocalImportCommitOutcome
{
	NotEligible,
	Applied,
	FailedClosed
}

internal readonly record struct AutocompleteLocalImportCommitApplyResult(
	AutocompleteLocalImportCommitOutcome Outcome,
	bool AddedUsingDirective,
	bool QualifiedNameReplacement,
	bool CaretRestored,
	string Detail)
{
	internal static AutocompleteLocalImportCommitApplyResult NotEligible(string detail)
		=> new(AutocompleteLocalImportCommitOutcome.NotEligible, false, false, false, detail ?? "NotEligible");

	internal static AutocompleteLocalImportCommitApplyResult Applied(
		bool addedUsingDirective,
		bool qualifiedNameReplacement,
		bool caretRestored,
		string detail = "")
		=> new(
			AutocompleteLocalImportCommitOutcome.Applied,
			addedUsingDirective,
			qualifiedNameReplacement,
			caretRestored,
			detail ?? "");

	internal static AutocompleteLocalImportCommitApplyResult FailedClosed(string detail)
		=> new(AutocompleteLocalImportCommitOutcome.FailedClosed, false, false, false, detail ?? "FailedClosed");
}

internal sealed class AutocompleteLocalImportCommitApplier
{
	private readonly record struct SimplePreambleInfo(
		int OriginalLineCount,
		bool HasSimpleUsing,
		bool TargetUsingExists,
		int LastSimpleUsingLine,
		bool FirstLineAlreadyBlank,
		bool HasLeadingHeaderComment,
		int HeaderAwareUsingInsertionLine);

	private readonly record struct LocalImportMutationPlan(
		string ReplacementText,
		bool QualifiedNameReplacement,
		bool AddUsingDirective,
		int UsingInsertionLine,
		string UsingInsertionText,
		int AddedLineCount,
		int OriginalLineCount);

	internal AutocompleteLocalImportCommitApplyResult TryApply(
		CodeEdit codeEdit,
		AutocompleteCompletionItem item,
		AutocompletePrefixCapture commitCapture,
		AutocompleteManagedCommitShape commitShape)
	{
		if (!TryCheckEligibility(item, commitShape, out AutocompleteLocalImportCommitApplyResult eligibilityFailure))
			return eligibilityFailure;

		if (!TryValidateCommitAnchor(codeEdit, commitCapture, out string anchorFailure))
			return AutocompleteLocalImportCommitApplyResult.FailedClosed(anchorFailure);

		LocalImportMutationPlan plan;
		switch (commitShape)
		{
			case AutocompleteManagedCommitShape.ImportWithUsing:
				PreambleAnalysisOutcome preambleOutcome = TryAnalyzeSimplePreamble(
					codeEdit,
					item.ContainingNamespace,
					out SimplePreambleInfo preambleInfo,
					out string preambleDetail);
				if (preambleOutcome == PreambleAnalysisOutcome.NotEligible)
					return AutocompleteLocalImportCommitApplyResult.NotEligible(preambleDetail);
				if (preambleOutcome == PreambleAnalysisOutcome.FailedClosed)
					return AutocompleteLocalImportCommitApplyResult.FailedClosed(preambleDetail);

				plan = CreateOrdinaryUsingMutationPlan(item, preambleInfo);
				break;

			case AutocompleteManagedCommitShape.QualifiedName:
				if (!TryGetOriginalLineCount(codeEdit, out int originalLineCount))
					return AutocompleteLocalImportCommitApplyResult.FailedClosed("EditorBufferUnavailable");

				plan = CreateQualifiedNameMutationPlan(item, originalLineCount);
				break;

			default:
				return AutocompleteLocalImportCommitApplyResult.NotEligible("UnsupportedManagedCommitShape");
		}

		bool operationStarted = false;
		bool mutationFailed = false;
		try
		{
			codeEdit.BeginComplexOperation();
			operationStarted = true;

			// Mutate bottom-to-top so the captured commit coordinates remain valid.
			codeEdit.RemoveText(
				commitCapture.Line,
				commitCapture.PrefixStartColumn,
				commitCapture.Line,
				commitCapture.GodotCaretColumn);
			codeEdit.InsertText(
				plan.ReplacementText,
				commitCapture.Line,
				commitCapture.PrefixStartColumn);

			if (plan.AddUsingDirective)
				codeEdit.InsertText(plan.UsingInsertionText, plan.UsingInsertionLine, 0);
		}
		catch
		{
			mutationFailed = true;
		}
		finally
		{
			if (operationStarted)
			{
				try { codeEdit.EndComplexOperation(); }
				catch { mutationFailed = true; }
			}
		}

		if (!operationStarted || mutationFailed)
			return AutocompleteLocalImportCommitApplyResult.FailedClosed("SourceMutationFailed");

		int finalCaretLine = commitCapture.Line + plan.AddedLineCount;
		int finalCaretColumn = commitCapture.PrefixStartColumn + plan.ReplacementText.Length;
		if (!TryVerifyAppliedMutation(
			codeEdit,
			item,
			commitCapture,
			plan,
			finalCaretLine,
			finalCaretColumn))
		{
			return AutocompleteLocalImportCommitApplyResult.FailedClosed("SourceMutationVerificationFailed");
		}

		try
		{
			codeEdit.SetCaretLine(finalCaretLine, false, true, -1, 0);
			codeEdit.SetCaretColumn(finalCaretColumn, false, 0);
			return AutocompleteLocalImportCommitApplyResult.Applied(
				plan.AddUsingDirective,
				plan.QualifiedNameReplacement,
				true);
		}
		catch
		{
			return AutocompleteLocalImportCommitApplyResult.Applied(
				plan.AddUsingDirective,
				plan.QualifiedNameReplacement,
				false,
				"CaretRestoreFailed");
		}
	}

	private static bool TryCheckEligibility(
		AutocompleteCompletionItem item,
		AutocompleteManagedCommitShape commitShape,
		out AutocompleteLocalImportCommitApplyResult failure)
	{
		failure = default;
		if (item == null || !item.HasValidCommitContract || !item.HasValidNamespaceContract)
		{
			failure = AutocompleteLocalImportCommitApplyResult.NotEligible("InvalidCommitContract");
			return false;
		}
		if (item.ContainingNamespace == null)
		{
			failure = AutocompleteLocalImportCommitApplyResult.NotEligible("ContainingNamespaceUnavailable");
			return false;
		}
		if (item.Kind != CodeEdit.CodeCompletionKind.Class && item.Kind != CodeEdit.CodeCompletionKind.Enum)
		{
			failure = AutocompleteLocalImportCommitApplyResult.NotEligible("UnsupportedCompletionKind");
			return false;
		}
		if (!IsSimpleAsciiIdentifier(item.DisplayText))
		{
			failure = AutocompleteLocalImportCommitApplyResult.NotEligible("NonSimpleTypeName");
			return false;
		}
		if (!IsSimpleDottedNamespace(item.ContainingNamespace))
		{
			failure = AutocompleteLocalImportCommitApplyResult.NotEligible("NonSimpleNamespace");
			return false;
		}

		switch (commitShape)
		{
			case AutocompleteManagedCommitShape.ImportWithUsing:
				if (!item.RequiresImport)
				{
					failure = AutocompleteLocalImportCommitApplyResult.NotEligible("ImportWithUsingRequiresImport");
					return false;
				}
				return true;

			case AutocompleteManagedCommitShape.QualifiedName:
				if (item.NamespaceDisambiguation == null)
				{
					failure = AutocompleteLocalImportCommitApplyResult.NotEligible("NamespaceDisambiguationUnavailable");
					return false;
				}
				if (!item.RequiresImport
					&& !string.Equals(item.InsertText, item.DisplayText, StringComparison.Ordinal))
				{
					failure = AutocompleteLocalImportCommitApplyResult.NotEligible("DirectInsertTextShapeUnsupported");
					return false;
				}
				return true;

			default:
				failure = AutocompleteLocalImportCommitApplyResult.NotEligible("UnsupportedManagedCommitShape");
				return false;
		}
	}

	private static bool TryValidateCommitAnchor(
		CodeEdit codeEdit,
		AutocompletePrefixCapture commitCapture,
		out string failure)
	{
		failure = "";
		if (!IsValidGodotObject(codeEdit))
		{
			failure = "CommitEditorUnavailable";
			return false;
		}

		try
		{
			if (!codeEdit.Editable || codeEdit.GetCaretCount() != 1 || codeEdit.HasSelection(0))
			{
				failure = "CommitAnchorChanged";
				return false;
			}

			int caretLine = codeEdit.GetCaretLine();
			int caretColumn = codeEdit.GetCaretColumn();
			if (caretLine != commitCapture.Line
				|| caretColumn != commitCapture.GodotCaretColumn
				|| commitCapture.PrefixStartColumn < 0
				|| commitCapture.PrefixStartColumn > caretColumn)
			{
				failure = "CommitAnchorChanged";
				return false;
			}

			int lineCount = codeEdit.GetLineCount();
			if (caretLine < 0 || caretLine >= lineCount)
			{
				failure = "CommitAnchorChanged";
				return false;
			}

			string line = codeEdit.GetLine(caretLine) ?? "";
			if (!AutocompleteTextPositionConverter.TryGodotColumnToUtf16Index(
				line,
				commitCapture.PrefixStartColumn,
				out int prefixStartUtf16)
				|| !AutocompleteTextPositionConverter.TryGodotColumnToUtf16Index(
					line,
					caretColumn,
					out int caretUtf16)
				|| caretUtf16 != commitCapture.LspCharacter
				|| prefixStartUtf16 > caretUtf16)
			{
				failure = "CommitAnchorChanged";
				return false;
			}

			string currentPrefix = line.Substring(prefixStartUtf16, caretUtf16 - prefixStartUtf16);
			if (!string.Equals(currentPrefix, commitCapture.Prefix ?? "", StringComparison.Ordinal))
			{
				failure = "CommitAnchorChanged";
				return false;
			}
			return true;
		}
		catch
		{
			failure = "CommitEditorStateUnavailable";
			return false;
		}
	}

	private enum PreambleAnalysisOutcome
	{
		Eligible,
		NotEligible,
		FailedClosed
	}

	private static PreambleAnalysisOutcome TryAnalyzeSimplePreamble(
		CodeEdit codeEdit,
		string containingNamespace,
		out SimplePreambleInfo info,
		out string detail)
	{
		info = default;
		detail = "";
		try
		{
			int lineCount = codeEdit.GetLineCount();
			if (lineCount <= 0)
			{
				detail = "EditorBufferUnavailable";
				return PreambleAnalysisOutcome.FailedClosed;
			}

			var lines = new string[lineCount];
			for (int lineIndex = 0; lineIndex < lineCount; lineIndex++)
				lines[lineIndex] = codeEdit.GetLine(lineIndex) ?? "";

			bool hasSimpleUsing = false;
			bool targetUsingExists = false;
			int lastSimpleUsingLine = -1;
			bool insideBlockComment = false;
			bool hasLeadingHeaderComment = false;
			int leadingHeaderEndLine = 0;

			for (int lineIndex = 0; lineIndex < lines.Length; lineIndex++)
			{
				string trimmed = GetPreambleContentAfterLeadingComments(
					lines[lineIndex],
					ref insideBlockComment,
					out bool hadLeadingCommentTrivia);
				if (trimmed.Length == 0)
				{
					if (!hasSimpleUsing)
					{
						leadingHeaderEndLine = lineIndex + 1;
						if (hadLeadingCommentTrivia)
							hasLeadingHeaderComment = true;
					}
					continue;
				}

				if (TryParseSimpleOrdinaryUsing(trimmed, out string namespaceText))
				{
					hasSimpleUsing = true;
					lastSimpleUsingLine = lineIndex;
					if (string.Equals(namespaceText, containingNamespace, StringComparison.Ordinal))
						targetUsingExists = true;
					continue;
				}

				if (LooksLikeUnsafePreamble(trimmed))
				{
					detail = "ComplexUsingPreamble";
					return PreambleAnalysisOutcome.NotEligible;
				}

				// A leading block comment followed by ordinary source on the same physical
				// line has no simple line-boundary insertion point that preserves the header.
				// Keep that uncommon layout outside this deliberately tiny fast path.
				if (!hasSimpleUsing && hadLeadingCommentTrivia)
				{
					detail = "ComplexUsingPreamble";
					return PreambleAnalysisOutcome.NotEligible;
				}

				// The first ordinary source line ends the tiny preamble parser. Everything
				// below it is deliberately outside this syntactic fast-path analysis.
				break;
			}

			if (insideBlockComment)
			{
				detail = "ComplexUsingPreamble";
				return PreambleAnalysisOutcome.NotEligible;
			}

			info = new SimplePreambleInfo(
				lineCount,
				hasSimpleUsing,
				targetUsingExists,
				lastSimpleUsingLine,
				string.IsNullOrWhiteSpace(lines[0]),
				hasLeadingHeaderComment,
				hasLeadingHeaderComment ? leadingHeaderEndLine : 0);
			return PreambleAnalysisOutcome.Eligible;
		}
		catch
		{
			detail = "EditorBufferUnavailable";
			return PreambleAnalysisOutcome.FailedClosed;
		}
	}

	private static LocalImportMutationPlan CreateOrdinaryUsingMutationPlan(
		AutocompleteCompletionItem item,
		SimplePreambleInfo preambleInfo)
	{
		if (preambleInfo.TargetUsingExists)
		{
			return new LocalImportMutationPlan(
				item.DisplayText,
				false,
				false,
				0,
				"",
				0,
				preambleInfo.OriginalLineCount);
		}

		if (preambleInfo.HasSimpleUsing)
		{
			return new LocalImportMutationPlan(
				item.DisplayText,
				false,
				true,
				preambleInfo.LastSimpleUsingLine + 1,
				"using " + item.ContainingNamespace + ";\n",
				1,
				preambleInfo.OriginalLineCount);
		}

		if (preambleInfo.HasLeadingHeaderComment)
		{
			return new LocalImportMutationPlan(
				item.DisplayText,
				false,
				true,
				preambleInfo.HeaderAwareUsingInsertionLine,
				"using " + item.ContainingNamespace + ";\n\n",
				2,
				preambleInfo.OriginalLineCount);
		}

		string insertionText = "using " + item.ContainingNamespace + ";\n"
			+ (preambleInfo.FirstLineAlreadyBlank ? "" : "\n");
		return new LocalImportMutationPlan(
			item.DisplayText,
			false,
			true,
			0,
			insertionText,
			preambleInfo.FirstLineAlreadyBlank ? 1 : 2,
			preambleInfo.OriginalLineCount);
	}

	private static bool TryGetOriginalLineCount(CodeEdit codeEdit, out int lineCount)
	{
		lineCount = 0;
		try
		{
			if (!IsValidGodotObject(codeEdit))
				return false;

			lineCount = codeEdit.GetLineCount();
			return lineCount > 0;
		}
		catch
		{
			lineCount = 0;
			return false;
		}
	}

	private static LocalImportMutationPlan CreateQualifiedNameMutationPlan(
		AutocompleteCompletionItem item,
		int originalLineCount)
	{
		return new LocalImportMutationPlan(
			item.ContainingNamespace + "." + item.DisplayText,
			true,
			false,
			0,
			"",
			0,
			originalLineCount);
	}

	private static bool TryVerifyAppliedMutation(
		CodeEdit codeEdit,
		AutocompleteCompletionItem item,
		AutocompletePrefixCapture commitCapture,
		LocalImportMutationPlan plan,
		int finalCaretLine,
		int finalCaretColumn)
	{
		try
		{
			if (!IsValidGodotObject(codeEdit)
				|| codeEdit.GetLineCount() != plan.OriginalLineCount + plan.AddedLineCount
				|| finalCaretLine < 0
				|| finalCaretLine >= codeEdit.GetLineCount()
				|| (plan.QualifiedNameReplacement
					&& (plan.AddUsingDirective || plan.AddedLineCount != 0)))
			{
				return false;
			}

			string finalLine = codeEdit.GetLine(finalCaretLine) ?? "";
			if (!AutocompleteTextPositionConverter.TryGodotColumnToUtf16Index(
				finalLine,
				commitCapture.PrefixStartColumn,
				out int replacementStartUtf16)
				|| !AutocompleteTextPositionConverter.TryGodotColumnToUtf16Index(
					finalLine,
					finalCaretColumn,
					out int replacementEndUtf16)
				|| replacementStartUtf16 > replacementEndUtf16
				|| !string.Equals(
					finalLine.Substring(replacementStartUtf16, replacementEndUtf16 - replacementStartUtf16),
					plan.ReplacementText,
					StringComparison.Ordinal))
			{
				return false;
			}

			if (plan.AddUsingDirective)
			{
				string usingLine = codeEdit.GetLine(plan.UsingInsertionLine) ?? "";
				if (!string.Equals(
					usingLine,
					"using " + item.ContainingNamespace + ";",
					StringComparison.Ordinal))
				{
					return false;
				}
			}
			return true;
		}
		catch
		{
			return false;
		}
	}

	private static string GetPreambleContentAfterLeadingComments(
		string line,
		ref bool insideBlockComment,
		out bool hadLeadingCommentTrivia)
	{
		line ??= "";
		hadLeadingCommentTrivia = insideBlockComment;
		int index = 0;

		while (true)
		{
			if (insideBlockComment)
			{
				int blockEnd = line.IndexOf("*/", index, StringComparison.Ordinal);
				if (blockEnd < 0)
					return "";

				insideBlockComment = false;
				hadLeadingCommentTrivia = true;
				index = blockEnd + 2;
				continue;
			}

			while (index < line.Length && char.IsWhiteSpace(line[index]))
				index++;

			if (index >= line.Length)
				return "";

			if (index + 1 < line.Length
				&& line[index] == '/'
				&& line[index + 1] == '/')
			{
				hadLeadingCommentTrivia = true;
				return "";
			}

			if (index + 1 < line.Length
				&& line[index] == '/'
				&& line[index + 1] == '*')
			{
				insideBlockComment = true;
				hadLeadingCommentTrivia = true;
				index += 2;
				continue;
			}

			return line.Substring(index).Trim();
		}
	}

	private static bool TryParseSimpleOrdinaryUsing(string trimmedLine, out string namespaceText)
	{
		namespaceText = "";
		if (trimmedLine == null
			|| !trimmedLine.StartsWith("using ", StringComparison.Ordinal)
			|| !trimmedLine.EndsWith(";", StringComparison.Ordinal))
		{
			return false;
		}

		string candidate = trimmedLine.Substring(6, trimmedLine.Length - 7);
		if (!IsSimpleDottedNamespace(candidate))
			return false;
		namespaceText = candidate;
		return true;
	}

	private static bool LooksLikeUnsafePreamble(string trimmedLine)
	{
		if (string.IsNullOrEmpty(trimmedLine))
			return false;
		if (trimmedLine[0] == '\uFEFF'
			|| trimmedLine.StartsWith("#", StringComparison.Ordinal))
		{
			return true;
		}
		if (trimmedLine.Equals("using", StringComparison.Ordinal)
			|| trimmedLine.StartsWith("using ", StringComparison.Ordinal)
			|| trimmedLine.StartsWith("using\t", StringComparison.Ordinal)
			|| trimmedLine.Equals("global", StringComparison.Ordinal)
			|| trimmedLine.StartsWith("global ", StringComparison.Ordinal)
			|| trimmedLine.StartsWith("global\t", StringComparison.Ordinal)
			|| trimmedLine.Equals("extern", StringComparison.Ordinal)
			|| trimmedLine.StartsWith("extern ", StringComparison.Ordinal)
			|| trimmedLine.StartsWith("extern\t", StringComparison.Ordinal))
		{
			return true;
		}
		return false;
	}

	private static bool IsSimpleDottedNamespace(string value)
	{
		if (string.IsNullOrEmpty(value))
			return false;
		int segmentStart = 0;
		for (int index = 0; index <= value.Length; index++)
		{
			if (index != value.Length && value[index] != '.')
				continue;
			int segmentLength = index - segmentStart;
			if (segmentLength <= 0
				|| !IsSimpleAsciiIdentifier(value, segmentStart, segmentLength))
			{
				return false;
			}
			segmentStart = index + 1;
		}
		return true;
	}

	private static bool IsSimpleAsciiIdentifier(string value)
		=> value != null && IsSimpleAsciiIdentifier(value, 0, value.Length);

	private static bool IsSimpleAsciiIdentifier(string value, int start, int length)
	{
		if (value == null || length <= 0 || start < 0 || start > value.Length - length)
			return false;
		char first = value[start];
		if (!IsAsciiLetter(first) && first != '_')
			return false;
		for (int offset = 1; offset < length; offset++)
		{
			char current = value[start + offset];
			if (!IsAsciiLetter(current) && !IsAsciiDigit(current) && current != '_')
				return false;
		}
		return true;
	}

	private static bool IsAsciiLetter(char value)
		=> (value >= 'A' && value <= 'Z') || (value >= 'a' && value <= 'z');

	private static bool IsAsciiDigit(char value) => value >= '0' && value <= '9';
	private static bool IsValidGodotObject(GodotObject source) => source != null && GodotObject.IsInstanceValid(source);
}
#endif
