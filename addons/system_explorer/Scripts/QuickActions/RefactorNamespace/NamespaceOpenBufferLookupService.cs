#if TOOLS
using Godot;
using System;
using System.Collections.Generic;
using SystemExplorer.EditorIntegration.ScriptEditing;

namespace SystemExplorer.QuickActions.RefactorNamespace;

internal sealed class NamespaceOpenBufferLookupService
{
	private readonly ScriptEditorBufferLocator _bufferLocator;

	internal NamespaceOpenBufferLookupService(ScriptEditorBufferLocator bufferLocator)
	{
		_bufferLocator = bufferLocator ?? throw new ArgumentNullException(nameof(bufferLocator));
	}

	internal ScriptEditorBufferGroupLookupResult GetOpenScriptEditorGroupsWithoutActivation(
		ScriptEditor scriptEditor,
		IEnumerable<string> targetPaths,
		IEnumerable<string> requiredPaths = null,
		NamespaceRefactorDiagnosticContext diagnosticContext = null
	)
	{
		ScriptEditorBufferGroupLookupResult result =
			_bufferLocator.LocateOpenScriptEditorGroupsWithoutActivation(
				scriptEditor,
				targetPaths,
				requiredPaths,
				diagnosticContext?.BufferDiagnostics
			);

		diagnosticContext?.Log(
			"BufferLookup",
			() =>
				$"Namespace lookup service result; Success={result.Success}; Failure={result.Failure}; FailurePath='{result.FailurePath}'; MatchedGroupCount={result.OpenEditorGroupsByPath.Count}; Unsafe={diagnosticContext.FormatPaths(result.UnsafeOpenScriptPaths)}; Ambiguous={diagnosticContext.FormatPaths(result.AmbiguousOpenScriptPaths)}; UnmatchedRequired={diagnosticContext.FormatPaths(result.UnmatchedRequiredPaths)}"
		);
		return result;
	}

	internal static string BuildScriptEditorBufferLookupFailureMessage(
		ScriptEditorBufferGroupLookupResult lookupResult
	)
	{
		if (lookupResult == null)
			return "";

		string scriptPath = lookupResult.FailurePath;

		return lookupResult.Failure switch
		{
			ScriptEditorBufferLookupFailure.AmbiguousRequiredOpenBufferGroup =>
				$"Refactor Namespace cancelled: System Explorer found multiple open script entries for '{scriptPath}', but could not safely verify every editor buffer as the same saved script. Save or close the duplicate entries before refactoring.",
			ScriptEditorBufferLookupFailure.UnmatchedRequiredOpenScripts =>
				$"Refactor Namespace cancelled: System Explorer could not safely match required open script editor buffer(s) without changing the active editor tab. Save/reopen before refactoring:\n{string.Join("\n", lookupResult.UnmatchedRequiredPaths)}",
			_ => "",
		};
	}
}
#endif
