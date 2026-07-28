#if TOOLS
using Godot;
using System;
using System.Collections.Generic;

namespace SystemExplorer.EditorIntegration.ScriptEditing;

internal enum ScriptEditorBufferAutosaveFailure
{
	None,
	DiskReadFailed,
	SavedBufferDiskMismatch,
	WriteFailed,
	AutosaveVerificationReadFailed,
	AutosaveVerificationMismatch,
	UnsafeOpenBufferGroupState,
}

internal enum ScriptEditorBufferAutosaveDiagnosticReason
{
	None,
	EmptyOrMissingGroup,
	NullTextEditor,
	InvalidTextEditor,
	GroupMemberPathMismatch,
	MultipleUnsavedMembers,
	UnsavedMemberIsNotCurrent,
	SavedMemberBecameUnsaved,
	SavedMemberDiskMismatch,
	DiskReadFailed,
	SavedBufferDiskMismatch,
	WriteFailed,
	AutosaveVerificationReadFailed,
	AutosaveVerificationMismatch,
}

internal readonly record struct ScriptEditorBufferAutosaveResult(
	bool Success,
	bool DidAutosave,
	string ScriptPath,
	ScriptEditorBufferAutosaveFailure Failure,
	ScriptEditorBufferAutosaveDiagnosticReason DiagnosticReason
)
{
	internal static ScriptEditorBufferAutosaveResult Succeeded(
		string scriptPath,
		bool didAutosave = false
	) => new(
		true,
		didAutosave,
		scriptPath ?? "",
		ScriptEditorBufferAutosaveFailure.None,
		ScriptEditorBufferAutosaveDiagnosticReason.None
	);

	internal static ScriptEditorBufferAutosaveResult Failed(
		string scriptPath,
		ScriptEditorBufferAutosaveFailure failure,
		ScriptEditorBufferAutosaveDiagnosticReason diagnosticReason =
			ScriptEditorBufferAutosaveDiagnosticReason.None,
		bool didAutosave = false
	) => new(false, didAutosave, scriptPath ?? "", failure, diagnosticReason);
}

internal sealed class ScriptEditorBufferAutosaveService
{
	private readonly Func<string, ScriptTextFileReadResult> _readTextFile;
	private readonly Func<string, string, bool> _writeTextFile;
	private readonly Func<string, string, bool> _textsMatchForDiskVerification;

	internal ScriptEditorBufferAutosaveService(
		Func<string, ScriptTextFileReadResult> readTextFile,
		Func<string, string, bool> writeTextFile,
		Func<string, string, bool> textsMatchForDiskVerification
	)
	{
		_readTextFile = readTextFile ?? throw new ArgumentNullException(nameof(readTextFile));
		_writeTextFile = writeTextFile ?? throw new ArgumentNullException(nameof(writeTextFile));
		_textsMatchForDiskVerification =
			textsMatchForDiskVerification
			?? throw new ArgumentNullException(nameof(textsMatchForDiskVerification));
	}

	internal ScriptEditorBufferAutosaveResult TryAutosaveGroupIfNeeded(
		OpenScriptEditorBufferGroup openEditorGroup,
		ScriptEditorBufferDiagnosticSink diagnostics = null
	)
	{
		diagnostics?.Log("Autosave", () => BuildGroupSummary("Group verification started", openEditorGroup, diagnostics));

		if (openEditorGroup == null || openEditorGroup.Buffers.Count == 0)
		{
			return FailUnsafeGroup(
				openEditorGroup,
				ScriptEditorBufferAutosaveDiagnosticReason.EmptyOrMissingGroup,
				diagnostics
			);
		}

		List<OpenScriptEditorBuffer> unsavedMembers = new();
		int memberNumber = 0;

		foreach (OpenScriptEditorBuffer member in openEditorGroup.Buffers)
		{
			memberNumber++;

			if (member.TextEditor == null)
			{
				return FailUnsafeGroup(
					openEditorGroup,
					ScriptEditorBufferAutosaveDiagnosticReason.NullTextEditor,
					diagnostics,
					member,
					memberNumber
				);
			}

			if (!GodotObject.IsInstanceValid(member.TextEditor))
			{
				return FailUnsafeGroup(
					openEditorGroup,
					ScriptEditorBufferAutosaveDiagnosticReason.InvalidTextEditor,
					diagnostics,
					member,
					memberNumber
				);
			}

			if (
				!string.Equals(
					member.Path,
					openEditorGroup.Path,
					StringComparison.OrdinalIgnoreCase
				)
			)
			{
				return FailUnsafeGroup(
					openEditorGroup,
					ScriptEditorBufferAutosaveDiagnosticReason.GroupMemberPathMismatch,
					diagnostics,
					member,
					memberNumber
				);
			}

			if (ScriptEditorBufferStateService.IsUnsaved(member.TextEditor))
				unsavedMembers.Add(member);
		}

		if (unsavedMembers.Count > 1)
		{
			return FailUnsafeGroup(
				openEditorGroup,
				ScriptEditorBufferAutosaveDiagnosticReason.MultipleUnsavedMembers,
				diagnostics,
				additionalDetailsFactory: () => $"UnsavedMemberCount={unsavedMembers.Count}"
			);
		}

		OpenScriptEditorBuffer unsavedMember =
			unsavedMembers.Count == 1 ? unsavedMembers[0] : default;

		if (
			unsavedMembers.Count == 1
			&& (
				!openEditorGroup.HasCurrentEditorBuffer
				|| !ReferenceEquals(
					unsavedMember.TextEditor,
					openEditorGroup.CurrentEditorBuffer.TextEditor
				)
			)
		)
		{
			return FailUnsafeGroup(
				openEditorGroup,
				ScriptEditorBufferAutosaveDiagnosticReason.UnsavedMemberIsNotCurrent,
				diagnostics
			);
		}

		ScriptTextFileReadResult diskReadResult = ReadTextSafely(openEditorGroup.Path);

		if (!diskReadResult.IsSuccess)
		{
			diagnostics?.Log(
				"Autosave",
				() =>
					$"Group verification failed before write; Path='{openEditorGroup.Path}'; Failure={ScriptEditorBufferAutosaveFailure.DiskReadFailed}; Reason={ScriptEditorBufferAutosaveDiagnosticReason.DiskReadFailed}; ReadStatus={diskReadResult.Status}; FailureDetail='{NormalizeDiagnosticDetail(diskReadResult.FailureDetail)}'"
			);
			return ScriptEditorBufferAutosaveResult.Failed(
				openEditorGroup.Path,
				ScriptEditorBufferAutosaveFailure.DiskReadFailed,
				ScriptEditorBufferAutosaveDiagnosticReason.DiskReadFailed
			);
		}

		string diskText = diskReadResult.Text;
		memberNumber = 0;

		foreach (OpenScriptEditorBuffer member in openEditorGroup.Buffers)
		{
			memberNumber++;

			if (
				unsavedMembers.Count == 1
				&& ReferenceEquals(member.TextEditor, unsavedMember.TextEditor)
			)
			{
				continue;
			}

			if (ScriptEditorBufferStateService.IsUnsaved(member.TextEditor))
			{
				return FailUnsafeGroup(
					openEditorGroup,
					ScriptEditorBufferAutosaveDiagnosticReason.SavedMemberBecameUnsaved,
					diagnostics,
					member,
					memberNumber,
					() => $"DiskText={diagnostics.DescribeText(diskText)}"
				);
			}

			string memberText = member.TextEditor.Text ?? "";

			if (!_textsMatchForDiskVerification(memberText, diskText))
			{
				return FailUnsafeGroup(
					openEditorGroup,
					ScriptEditorBufferAutosaveDiagnosticReason.SavedMemberDiskMismatch,
					diagnostics,
					member,
					memberNumber,
					() => $"BufferText={diagnostics.DescribeText(memberText)}; DiskText={diagnostics.DescribeText(diskText)}; TextsMatch=false"
				);
			}
		}

		if (unsavedMembers.Count == 0)
		{
			diagnostics?.Log(
				"Autosave",
				() => BuildGroupSummary(
					"Group verification succeeded; autosave not required",
					openEditorGroup,
					diagnostics
				)
			);
			return ScriptEditorBufferAutosaveResult.Succeeded(openEditorGroup.Path);
		}

		ScriptEditorBufferAutosaveResult autosaveResult = TryAutosaveIfNeeded(
			unsavedMember,
			failOnSavedDiskMismatch: true,
			diagnostics: diagnostics
		);

		if (!autosaveResult.Success)
			return autosaveResult;

		ScriptTextFileReadResult committedReadResult = ReadTextSafely(openEditorGroup.Path);

		if (!committedReadResult.IsSuccess)
		{
			diagnostics?.Log(
				"Autosave",
				() =>
					$"Group committed-text read failed after autosave; Path='{openEditorGroup.Path}'; Failure={ScriptEditorBufferAutosaveFailure.AutosaveVerificationReadFailed}; Reason={ScriptEditorBufferAutosaveDiagnosticReason.AutosaveVerificationReadFailed}; ReadStatus={committedReadResult.Status}; FailureDetail='{NormalizeDiagnosticDetail(committedReadResult.FailureDetail)}'"
			);
			return ScriptEditorBufferAutosaveResult.Failed(
				openEditorGroup.Path,
				ScriptEditorBufferAutosaveFailure.AutosaveVerificationReadFailed,
				ScriptEditorBufferAutosaveDiagnosticReason.AutosaveVerificationReadFailed,
				didAutosave: autosaveResult.DidAutosave
			);
		}

		string committedText = committedReadResult.Text;

		foreach (OpenScriptEditorBuffer member in openEditorGroup.Buffers)
		{
			if (ReferenceEquals(member.TextEditor, unsavedMember.TextEditor))
				continue;

			ScriptEditorBufferStateService.ApplyCommittedText(
				member.TextEditor,
				committedText
			);
		}

		diagnostics?.Log(
			"Autosave",
			() =>
				$"Group autosave succeeded; Path='{openEditorGroup.Path}'; MemberCount={openEditorGroup.Buffers.Count}; CommittedText={diagnostics.DescribeText(committedText)}"
		);
		return autosaveResult;
	}

	internal ScriptEditorBufferAutosaveResult TryAutosaveIfNeeded(
		OpenScriptEditorBuffer openEditor,
		bool failOnSavedDiskMismatch,
		ScriptEditorBufferDiagnosticSink diagnostics = null
	)
	{
		TextEdit textEditor = openEditor.TextEditor;
		string scriptPath = openEditor.Path;

		if (textEditor == null || string.IsNullOrWhiteSpace(scriptPath))
		{
			diagnostics?.Log(
				"Autosave",
				() => $"Single buffer autosave skipped; Path='{scriptPath}'; TextEditNull={textEditor == null}"
			);
			return ScriptEditorBufferAutosaveResult.Succeeded(scriptPath);
		}

		string editorText = textEditor.Text ?? "";

		if (!ScriptEditorBufferStateService.IsUnsaved(textEditor))
		{
			ScriptTextFileReadResult diskReadResult = ReadTextSafely(scriptPath);

			if (!diskReadResult.IsSuccess)
			{
				diagnostics?.Log(
					"Autosave",
					() =>
						$"Single buffer verification failed before write; Path='{scriptPath}'; Failure={ScriptEditorBufferAutosaveFailure.DiskReadFailed}; Reason={ScriptEditorBufferAutosaveDiagnosticReason.DiskReadFailed}; ReadStatus={diskReadResult.Status}; FailureDetail='{NormalizeDiagnosticDetail(diskReadResult.FailureDetail)}'"
				);
				return ScriptEditorBufferAutosaveResult.Failed(
					scriptPath,
					ScriptEditorBufferAutosaveFailure.DiskReadFailed,
					ScriptEditorBufferAutosaveDiagnosticReason.DiskReadFailed
				);
			}

			string diskText = diskReadResult.Text;

			if (
				failOnSavedDiskMismatch
				&& !_textsMatchForDiskVerification(editorText, diskText)
			)
			{
				diagnostics?.Log(
					"Autosave",
					() =>
						$"Single buffer verification failed; Path='{scriptPath}'; Failure={ScriptEditorBufferAutosaveFailure.SavedBufferDiskMismatch}; Reason={ScriptEditorBufferAutosaveDiagnosticReason.SavedBufferDiskMismatch}; BufferText={diagnostics.DescribeText(editorText)}; DiskText={diagnostics.DescribeText(diskText)}; TextsMatch=false"
				);
				return ScriptEditorBufferAutosaveResult.Failed(
					scriptPath,
					ScriptEditorBufferAutosaveFailure.SavedBufferDiskMismatch,
					ScriptEditorBufferAutosaveDiagnosticReason.SavedBufferDiskMismatch
				);
			}

			diagnostics?.Log(
				"Autosave",
				() =>
					$"Single buffer verification succeeded; Path='{scriptPath}'; IsUnsaved=false; FailOnSavedDiskMismatch={failOnSavedDiskMismatch}"
			);
			return ScriptEditorBufferAutosaveResult.Succeeded(scriptPath);
		}

		ScriptTextFileReadResult preAutosaveReadResult = ReadTextSafely(scriptPath);

		if (!preAutosaveReadResult.IsSuccess)
		{
			diagnostics?.Log(
				"Autosave",
				() =>
					$"Single buffer disk read failed before autosave; Path='{scriptPath}'; Failure={ScriptEditorBufferAutosaveFailure.DiskReadFailed}; Reason={ScriptEditorBufferAutosaveDiagnosticReason.DiskReadFailed}; ReadStatus={preAutosaveReadResult.Status}; FailureDetail='{NormalizeDiagnosticDetail(preAutosaveReadResult.FailureDetail)}'"
			);
			return ScriptEditorBufferAutosaveResult.Failed(
				scriptPath,
				ScriptEditorBufferAutosaveFailure.DiskReadFailed,
				ScriptEditorBufferAutosaveDiagnosticReason.DiskReadFailed
			);
		}

		if (!_writeTextFile(scriptPath, editorText))
		{
			diagnostics?.Log(
				"Autosave",
				() =>
					$"Single buffer autosave failed; Path='{scriptPath}'; Failure={ScriptEditorBufferAutosaveFailure.WriteFailed}; Reason={ScriptEditorBufferAutosaveDiagnosticReason.WriteFailed}; BufferText={diagnostics.DescribeText(editorText)}"
			);
			return ScriptEditorBufferAutosaveResult.Failed(
				scriptPath,
				ScriptEditorBufferAutosaveFailure.WriteFailed,
				ScriptEditorBufferAutosaveDiagnosticReason.WriteFailed
			);
		}

		ScriptTextFileReadResult savedEditorReadResult = ReadTextSafely(scriptPath);

		if (!savedEditorReadResult.IsSuccess)
		{
			diagnostics?.Log(
				"Autosave",
				() =>
					$"Single buffer autosave read-back failed; Path='{scriptPath}'; Failure={ScriptEditorBufferAutosaveFailure.AutosaveVerificationReadFailed}; Reason={ScriptEditorBufferAutosaveDiagnosticReason.AutosaveVerificationReadFailed}; ReadStatus={savedEditorReadResult.Status}; FailureDetail='{NormalizeDiagnosticDetail(savedEditorReadResult.FailureDetail)}'"
			);
			return ScriptEditorBufferAutosaveResult.Failed(
				scriptPath,
				ScriptEditorBufferAutosaveFailure.AutosaveVerificationReadFailed,
				ScriptEditorBufferAutosaveDiagnosticReason.AutosaveVerificationReadFailed,
				didAutosave: true
			);
		}

		string savedEditorText = savedEditorReadResult.Text;

		if (!_textsMatchForDiskVerification(savedEditorText, editorText))
		{
			diagnostics?.Log(
				"Autosave",
				() =>
					$"Single buffer autosave verification failed; Path='{scriptPath}'; Failure={ScriptEditorBufferAutosaveFailure.AutosaveVerificationMismatch}; Reason={ScriptEditorBufferAutosaveDiagnosticReason.AutosaveVerificationMismatch}; BufferText={diagnostics.DescribeText(editorText)}; DiskText={diagnostics.DescribeText(savedEditorText)}; TextsMatch=false"
			);
			return ScriptEditorBufferAutosaveResult.Failed(
				scriptPath,
				ScriptEditorBufferAutosaveFailure.AutosaveVerificationMismatch,
				ScriptEditorBufferAutosaveDiagnosticReason.AutosaveVerificationMismatch,
				didAutosave: true
			);
		}

		ScriptEditorBufferStateService.MarkCurrentVersionSaved(textEditor);
		diagnostics?.Log(
			"Autosave",
			() =>
				$"Single buffer autosave succeeded; Path='{scriptPath}'; BufferText={diagnostics.DescribeText(editorText)}"
		);
		return ScriptEditorBufferAutosaveResult.Succeeded(scriptPath, didAutosave: true);
	}

	private ScriptTextFileReadResult ReadTextSafely(string scriptPath)
	{
		try
		{
			return _readTextFile(scriptPath);
		}
		catch (Exception exception)
		{
			return ScriptTextFileReadResult.Failed(
				ScriptTextFileReadStatus.ReadFailed,
				$"Read delegate threw {exception.GetType().Name}: {NormalizeDiagnosticDetail(exception.Message)}"
			);
		}
	}

	private static string NormalizeDiagnosticDetail(string detail)
	{
		return string.IsNullOrWhiteSpace(detail)
			? ""
			: detail.Replace('\r', ' ').Replace('\n', ' ').Trim();
	}

	private static ScriptEditorBufferAutosaveResult FailUnsafeGroup(
		OpenScriptEditorBufferGroup group,
		ScriptEditorBufferAutosaveDiagnosticReason reason,
		ScriptEditorBufferDiagnosticSink diagnostics,
		OpenScriptEditorBuffer member = default,
		int memberNumber = 0,
		Func<string> additionalDetailsFactory = null
	)
	{
		diagnostics?.Log(
			"Autosave",
			() =>
			{
				string groupSummary = BuildGroupSummary(
					"Group verification failed",
					group,
					diagnostics
				);
				string memberSummary = memberNumber <= 0
					? ""
					: $"; FailedMember={diagnostics.DescribeTextEditor(member.TextEditor, member.Path, IsCurrentMember(group, member), memberNumber)}";
				string additionalDetails = additionalDetailsFactory?.Invoke() ?? "";
				string extra = string.IsNullOrWhiteSpace(additionalDetails)
					? ""
					: $"; {additionalDetails}";
				return $"{groupSummary}; Failure={ScriptEditorBufferAutosaveFailure.UnsafeOpenBufferGroupState}; Reason={reason}{memberSummary}{extra}";
			}
		);

		return ScriptEditorBufferAutosaveResult.Failed(
			group?.Path ?? member.Path ?? "",
			ScriptEditorBufferAutosaveFailure.UnsafeOpenBufferGroupState,
			reason
		);
	}

	private static string BuildGroupSummary(
		string heading,
		OpenScriptEditorBufferGroup group,
		ScriptEditorBufferDiagnosticSink diagnostics
	)
	{
		if (group == null)
			return $"{heading}; GroupNull=true; Path=''; MemberCount=0; HasCurrentEditorBuffer=false";

		var memberDescriptions = new List<string>();
		int memberNumber = 0;
		int unsavedCount = 0;

		foreach (OpenScriptEditorBuffer member in group.Buffers)
		{
			memberNumber++;
			memberDescriptions.Add(
				diagnostics.DescribeTextEditor(
					member.TextEditor,
					member.Path,
					IsCurrentMember(group, member),
					memberNumber
				)
			);

			try
			{
				if (ScriptEditorBufferStateService.IsUnsaved(member.TextEditor))
					unsavedCount++;
			}
			catch
			{
				// The per-member description already records the diagnostic read failure.
			}
		}

		return
			$"{heading}; Path='{group.Path}'; MemberCount={group.Buffers.Count}; HasCurrentEditorBuffer={group.HasCurrentEditorBuffer}; UnsavedMemberCount={unsavedCount}; Members=[{string.Join(" | ", memberDescriptions)}]";
	}

	private static bool IsCurrentMember(
		OpenScriptEditorBufferGroup group,
		OpenScriptEditorBuffer member
	)
	{
		return group != null
			&& group.HasCurrentEditorBuffer
			&& ReferenceEquals(
				group.CurrentEditorBuffer.TextEditor,
				member.TextEditor
			);
	}
}
#endif
