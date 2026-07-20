#if TOOLS
using System;
using System.Collections.Generic;

namespace SystemExplorer.QuickActions.RefactorNamespace;

internal sealed class NamespaceRefactorPendingWriteSet
{
	internal string SelectedScriptPath { get; }
	internal Dictionary<string, string> OriginalTextsByPath { get; }
	internal Dictionary<string, string> PendingWrites { get; }

	internal NamespaceRefactorPendingWriteSet(
		string selectedScriptPath,
		Dictionary<string, string> originalTextsByPath,
		Dictionary<string, string> pendingWrites
	)
	{
		SelectedScriptPath = selectedScriptPath ?? "";
		OriginalTextsByPath =
			originalTextsByPath
			?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		PendingWrites =
			pendingWrites ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
	}

	internal static NamespaceRefactorPendingWriteSet FromPlan(
		NamespaceRefactorPlan plan
	)
	{
		return new NamespaceRefactorPendingWriteSet(
			plan?.SelectedScriptPath ?? "",
			plan == null
				? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
				: new Dictionary<string, string>(
					plan.OriginalTextsByPath,
					StringComparer.OrdinalIgnoreCase
				),
			plan == null
				? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
				: new Dictionary<string, string>(
					plan.PendingWrites,
					StringComparer.OrdinalIgnoreCase
				)
		);
	}
}

internal sealed class NamespaceRefactorPendingWriteBuildResult
{
	internal bool Success { get; }
	internal NamespaceRefactorPendingWriteSet WriteSet { get; }

	private NamespaceRefactorPendingWriteBuildResult(
		bool success,
		NamespaceRefactorPendingWriteSet writeSet
	)
	{
		Success = success;
		WriteSet = writeSet;
	}

	internal static NamespaceRefactorPendingWriteBuildResult Succeeded(
		NamespaceRefactorPendingWriteSet writeSet
	) => new(writeSet != null, writeSet);

	internal static NamespaceRefactorPendingWriteBuildResult Failed() => new(false, null);
}
#endif
