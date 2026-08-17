#if TOOLS
using System;
using SystemExplorer.EditorIntegration.ScriptEditing;

namespace SystemExplorer.Autocomplete;

internal enum AutocompleteEditorBindingCandidateObservationKind
{
	Unavailable,
	NonCSharpTarget,
	Candidate,
}

internal readonly record struct AutocompleteEditorBindingCandidate(
	string ManagedAssemblyGeneration,
	long HostInstanceToken,
	long ScriptTransitionId,
	ulong ScriptEditorInstanceId,
	ulong ScriptEditorBaseInstanceId,
	ulong CodeEditInstanceId,
	string ScriptResourcePath
)
{
	internal bool IsValid =>
		!string.IsNullOrWhiteSpace(ManagedAssemblyGeneration)
		&& HostInstanceToken > 0
		&& ScriptTransitionId > 0
		&& ScriptEditorInstanceId > 0
		&& ScriptEditorBaseInstanceId > 0
		&& CodeEditInstanceId > 0
		&& !string.IsNullOrWhiteSpace(ScriptPathUtility.Normalize(ScriptResourcePath));

	internal AutocompleteEditorBindingCandidate Normalized()
	{
		return this with
		{
			ScriptResourcePath = ScriptPathUtility.Normalize(ScriptResourcePath),
		};
	}

	internal bool AuthorityEquals(AutocompleteEditorBindingCandidate other)
	{
		AutocompleteEditorBindingCandidate left = Normalized();
		AutocompleteEditorBindingCandidate right = other.Normalized();
		return string.Equals(
				left.ManagedAssemblyGeneration,
				right.ManagedAssemblyGeneration,
				StringComparison.Ordinal
			)
			&& left.HostInstanceToken == right.HostInstanceToken
			&& left.ScriptTransitionId == right.ScriptTransitionId
			&& left.ScriptEditorInstanceId == right.ScriptEditorInstanceId
			&& left.ScriptEditorBaseInstanceId == right.ScriptEditorBaseInstanceId
			&& left.CodeEditInstanceId == right.CodeEditInstanceId
			&& string.Equals(
				left.ScriptResourcePath,
				right.ScriptResourcePath,
				StringComparison.OrdinalIgnoreCase
			);
	}
}
#endif
