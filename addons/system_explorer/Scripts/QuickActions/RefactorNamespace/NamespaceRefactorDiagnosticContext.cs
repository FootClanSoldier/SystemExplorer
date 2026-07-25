#if TOOLS
using System;
using Godot;
using System.Collections.Generic;
using SystemExplorer.EditorIntegration.ScriptEditing;

namespace SystemExplorer.QuickActions.RefactorNamespace;

internal sealed class NamespaceRefactorDiagnosticContext
{
	private readonly NamespaceRefactorDiagnosticTrace _trace;

	internal NamespaceRefactorDiagnosticContext(
		string operationId,
		string operationKind,
		NamespaceRefactorDiagnosticTrace trace
	)
	{
		OperationId = operationId ?? "";
		OperationKind = operationKind ?? "Unknown";
		_trace = trace ?? throw new ArgumentNullException(nameof(trace));
		BufferDiagnostics = new ScriptEditorBufferDiagnosticSink(
			() => IsEnabled,
			(phase, details) => Log(phase, details),
			_trace.SummarizeText
		);
	}

	internal string OperationId { get; }
	internal string OperationKind { get; }
	internal bool IsEnabled => _trace.IsEnabled;
	internal ScriptEditorBufferDiagnosticSink BufferDiagnostics { get; }

	internal void Log(string phase, string details = "")
	{
		_trace.Log(this, phase, () => details ?? "");
	}

	internal void Log(string phase, Func<string> detailsFactory)
	{
		_trace.Log(this, phase, detailsFactory);
	}

	internal string FormatPaths(IEnumerable<string> paths) => _trace.FormatPaths(paths);
	internal string SummarizeText(string text) => _trace.SummarizeText(text);
	internal string TryGetCurrentScriptPath(ScriptEditor scriptEditor) =>
		_trace.TryGetCurrentScriptPath(scriptEditor);
}
#endif
