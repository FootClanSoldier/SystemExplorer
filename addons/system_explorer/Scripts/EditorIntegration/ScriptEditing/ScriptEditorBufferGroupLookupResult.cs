#if TOOLS
using System;
using System.Collections.Generic;

namespace SystemExplorer.EditorIntegration.ScriptEditing;

internal sealed class ScriptEditorBufferGroupLookupResult
{
	internal Dictionary<string, OpenScriptEditorBufferGroup> OpenEditorGroupsByPath { get; }
	internal IReadOnlyList<string> UnsafeOpenScriptPaths { get; }
	internal IReadOnlyList<string> AmbiguousOpenScriptPaths { get; }
	internal IReadOnlyList<string> UnmatchedRequiredPaths { get; }
	internal ScriptEditorBufferLookupFailure Failure { get; }
	internal string FailurePath { get; }
	internal bool Success => Failure == ScriptEditorBufferLookupFailure.None;

	internal ScriptEditorBufferGroupLookupResult(
		Dictionary<string, OpenScriptEditorBufferGroup> openEditorGroupsByPath,
		ScriptEditorBufferLookupFailure failure = ScriptEditorBufferLookupFailure.None,
		string failurePath = "",
		IEnumerable<string> unsafeOpenScriptPaths = null,
		IEnumerable<string> ambiguousOpenScriptPaths = null,
		IEnumerable<string> unmatchedRequiredPaths = null
	)
	{
		OpenEditorGroupsByPath =
			openEditorGroupsByPath
			?? new Dictionary<string, OpenScriptEditorBufferGroup>(StringComparer.OrdinalIgnoreCase);
		UnsafeOpenScriptPaths = CreateReadOnlyList(unsafeOpenScriptPaths);
		AmbiguousOpenScriptPaths = CreateReadOnlyList(ambiguousOpenScriptPaths);
		UnmatchedRequiredPaths = CreateReadOnlyList(unmatchedRequiredPaths);
		Failure = failure;
		FailurePath = failurePath ?? "";
	}

	private static IReadOnlyList<string> CreateReadOnlyList(IEnumerable<string> source)
	{
		List<string> copy = source == null ? new List<string>() : new List<string>(source);
		return copy.AsReadOnly();
	}
}
#endif
