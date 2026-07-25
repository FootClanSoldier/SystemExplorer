#if TOOLS
using Godot;
using System;
using System.Collections.Generic;

namespace SystemExplorer.EditorIntegration.ScriptEditing;

internal readonly record struct ScriptEditorBufferBatchAutosaveResult(
	bool Success,
	bool DidAutosaveAny,
	ScriptEditorBufferAutosaveResult FailedAutosave
)
{
	internal static ScriptEditorBufferBatchAutosaveResult Succeeded(bool didAutosaveAny) =>
		new(true, didAutosaveAny, default);

	internal static ScriptEditorBufferBatchAutosaveResult Failed(
		bool didAutosaveAny,
		ScriptEditorBufferAutosaveResult failedAutosave
	) => new(false, didAutosaveAny, failedAutosave);
}

internal sealed class ScriptEditorBufferBatchService
{
	private readonly ScriptEditorBufferAutosaveService _autosaveService;

	internal ScriptEditorBufferBatchService(ScriptEditorBufferAutosaveService autosaveService)
	{
		_autosaveService =
			autosaveService ?? throw new ArgumentNullException(nameof(autosaveService));
	}

	internal IReadOnlyList<string> GetUnsavedPaths(IEnumerable<OpenScriptEditorBuffer> openEditors)
	{
		return ScriptEditorBufferStateService.GetUnsavedPaths(openEditors);
	}

	internal IReadOnlyList<string> GetUnsavedPaths(
		IEnumerable<OpenScriptEditorBufferGroup> openEditorGroups
	)
	{
		List<string> unsavedPaths = new();
		HashSet<string> seenPaths = new(StringComparer.OrdinalIgnoreCase);

		if (openEditorGroups == null)
			return unsavedPaths;

		foreach (OpenScriptEditorBufferGroup group in openEditorGroups)
		{
			if (group == null || !seenPaths.Add(group.Path))
				continue;

			foreach (OpenScriptEditorBuffer member in group.Buffers)
			{
				if (!ScriptEditorBufferStateService.IsUnsaved(member.TextEditor))
					continue;

				unsavedPaths.Add(group.Path);
				break;
			}
		}

		return unsavedPaths;
	}

	internal ScriptEditorBufferBatchAutosaveResult TryAutosaveIfNeeded(
		IEnumerable<OpenScriptEditorBuffer> openEditors,
		bool failOnSavedDiskMismatch,
		ScriptEditorBufferDiagnosticSink diagnostics = null
	)
	{
		bool didAutosaveAny = false;

		if (openEditors == null)
			return ScriptEditorBufferBatchAutosaveResult.Succeeded(didAutosaveAny);

		foreach (OpenScriptEditorBuffer openEditor in openEditors)
		{
			ScriptEditorBufferAutosaveResult autosaveResult = _autosaveService.TryAutosaveIfNeeded(
				openEditor,
				failOnSavedDiskMismatch,
				diagnostics
			);

			if (autosaveResult.DidAutosave)
				didAutosaveAny = true;

			if (!autosaveResult.Success)
			{
				return ScriptEditorBufferBatchAutosaveResult.Failed(didAutosaveAny, autosaveResult);
			}
		}

		return ScriptEditorBufferBatchAutosaveResult.Succeeded(didAutosaveAny);
	}

	internal ScriptEditorBufferBatchAutosaveResult TryAutosaveGroupsIfNeeded(
		IEnumerable<OpenScriptEditorBufferGroup> openEditorGroups,
		ScriptEditorBufferDiagnosticSink diagnostics = null
	)
	{
		bool didAutosaveAny = false;

		if (openEditorGroups == null)
			return ScriptEditorBufferBatchAutosaveResult.Succeeded(didAutosaveAny);

		foreach (OpenScriptEditorBufferGroup group in openEditorGroups)
		{
			ScriptEditorBufferAutosaveResult autosaveResult =
				_autosaveService.TryAutosaveGroupIfNeeded(group, diagnostics);

			if (autosaveResult.DidAutosave)
				didAutosaveAny = true;

			if (!autosaveResult.Success)
			{
				return ScriptEditorBufferBatchAutosaveResult.Failed(didAutosaveAny, autosaveResult);
			}
		}

		return ScriptEditorBufferBatchAutosaveResult.Succeeded(didAutosaveAny);
	}

	internal void ApplyCommittedTexts(
		IReadOnlyDictionary<string, OpenScriptEditorBuffer> openEditorsByPath,
		IReadOnlyDictionary<string, string> updatedTextsByPath
	)
	{
		if (openEditorsByPath == null || updatedTextsByPath == null || openEditorsByPath.Count == 0)
			return;

		foreach (KeyValuePair<string, OpenScriptEditorBuffer> openEditorPair in openEditorsByPath)
		{
			if (!updatedTextsByPath.TryGetValue(openEditorPair.Key, out string updatedText))
				continue;

			ScriptEditorBufferStateService.ApplyCommittedText(
				openEditorPair.Value.TextEditor,
				updatedText
			);
		}
	}

	internal void ApplyCommittedTexts(
		IReadOnlyDictionary<string, OpenScriptEditorBufferGroup> openEditorGroupsByPath,
		IReadOnlyDictionary<string, string> updatedTextsByPath,
		ScriptEditorBufferDiagnosticSink diagnostics = null
	)
	{
		if (
			openEditorGroupsByPath == null
			|| updatedTextsByPath == null
			|| openEditorGroupsByPath.Count == 0
		)
		{
			diagnostics?.Log(
				"ImmediateSync",
				() =>
					$"ApplyCommittedTexts skipped; GroupDictionaryNull={openEditorGroupsByPath == null}; UpdatedTextDictionaryNull={updatedTextsByPath == null}; GroupCount={openEditorGroupsByPath?.Count ?? 0}"
			);
			return;
		}

		foreach (
			KeyValuePair<string, OpenScriptEditorBufferGroup> groupPair
			in openEditorGroupsByPath
		)
		{
			if (!updatedTextsByPath.TryGetValue(groupPair.Key, out string updatedText))
			{
				diagnostics?.Log(
					"ImmediateSync",
					() => $"Path='{groupPair.Key}'; GroupFound=true; UpdatedTextFound=false"
				);
				continue;
			}

			OpenScriptEditorBufferGroup group = groupPair.Value;
			int memberCount = group?.Buffers.Count ?? 0;
			int validMemberCount = 0;
			int nullMemberCount = 0;
			int invalidMemberCount = 0;
			int textAssignmentNeededCount = 0;
			int alreadyMatchingTextCount = 0;
			int preApplyReadFailureCount = 0;
			List<bool> textAssignmentNeededByMember = null;

			if (diagnostics?.IsEnabled == true && group != null)
			{
				textAssignmentNeededByMember = new List<bool>(group.Buffers.Count);
				foreach (OpenScriptEditorBuffer member in group.Buffers)
				{
					bool textAssignmentNeeded = false;
					TextEdit textEditor = member.TextEditor;
					if (textEditor == null)
					{
						nullMemberCount++;
						textAssignmentNeededByMember.Add(false);
						continue;
					}

					try
					{
						if (!GodotObject.IsInstanceValid(textEditor))
						{
							invalidMemberCount++;
							textAssignmentNeededByMember.Add(false);
							continue;
						}

						validMemberCount++;
						textAssignmentNeeded = !string.Equals(
							textEditor.Text ?? "",
							updatedText ?? "",
							StringComparison.Ordinal
						);
						if (textAssignmentNeeded)
							textAssignmentNeededCount++;
						else
							alreadyMatchingTextCount++;
					}
					catch
					{
						preApplyReadFailureCount++;
					}

					textAssignmentNeededByMember.Add(textAssignmentNeeded);
				}
			}

			diagnostics?.Log(
				"ImmediateSync",
				() =>
					$"Path='{groupPair.Key}'; GroupFound={group != null}; MemberCount={memberCount}; ValidMembers={validMemberCount}; NullMembers={nullMemberCount}; InvalidMembers={invalidMemberCount}; TextAssignmentNeeded={textAssignmentNeededCount}; AlreadyMatchingText={alreadyMatchingTextCount}; PreApplyReadFailures={preApplyReadFailureCount}; UpdatedText={diagnostics.DescribeText(updatedText)}; ApplyStarted=true"
			);

			foreach (OpenScriptEditorBuffer member in group.Buffers)
				ScriptEditorBufferStateService.ApplyCommittedText(member.TextEditor, updatedText);

			if (diagnostics?.IsEnabled == true)
			{
				int verifiedSynchronizedMemberCount = 0;
				int verifiedTextAssignmentCount = 0;
				int verificationReadFailureCount = 0;
				int memberIndex = 0;

				foreach (OpenScriptEditorBuffer member in group.Buffers)
				{
					try
					{
						TextEdit textEditor = member.TextEditor;
						bool synchronized =
							textEditor != null
							&& GodotObject.IsInstanceValid(textEditor)
							&& string.Equals(textEditor.Text ?? "", updatedText ?? "", StringComparison.Ordinal)
							&& !ScriptEditorBufferStateService.IsUnsaved(textEditor);
						if (synchronized)
						{
							verifiedSynchronizedMemberCount++;
							if (
								textAssignmentNeededByMember != null
								&& memberIndex < textAssignmentNeededByMember.Count
								&& textAssignmentNeededByMember[memberIndex]
							)
							{
								verifiedTextAssignmentCount++;
							}
						}
					}
					catch
					{
						verificationReadFailureCount++;
					}
					finally
					{
						memberIndex++;
					}
				}

				diagnostics.Log(
					"ImmediateSync",
					() =>
						$"Path='{groupPair.Key}'; ApplyCompleted=true; VerifiedSynchronizedMembers={verifiedSynchronizedMemberCount}; VerifiedTextAssignments={verifiedTextAssignmentCount}; TextAssignmentNeededBeforeApply={textAssignmentNeededCount}; VerificationReadFailures={verificationReadFailureCount}; NullMembersSkippedByExistingService={nullMemberCount}; InvalidMembersObserved={invalidMemberCount}"
				);
			}
		}
	}
}
#endif
