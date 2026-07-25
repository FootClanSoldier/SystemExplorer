#if TOOLS
using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using SystemExplorer.EditorIntegration.ScriptEditing;

namespace SystemExplorer.QuickActions.RefactorNamespace;

internal sealed class NamespaceRefactorDiagnosticTrace
{
	private const int MaximumListedPathCount = 50;
	private static int _operationSequence;
	private readonly Func<bool> _isDebugEnabled;
	private readonly Action<string> _debugLog;

	internal NamespaceRefactorDiagnosticTrace(
		Func<bool> isDebugEnabled,
		Action<string> debugLog
	)
	{
		_isDebugEnabled =
			isDebugEnabled ?? throw new ArgumentNullException(nameof(isDebugEnabled));
		_debugLog = debugLog ?? throw new ArgumentNullException(nameof(debugLog));
	}

	internal bool IsEnabled
	{
		get
		{
			try
			{
				return _isDebugEnabled();
			}
			catch
			{
				return false;
			}
		}
	}

	internal NamespaceRefactorDiagnosticContext CreateContext(string operationKind)
	{
		int operationNumber = Interlocked.Increment(ref _operationSequence);
		return new NamespaceRefactorDiagnosticContext(
			$"NR-{operationNumber:0000}",
			operationKind ?? "Unknown",
			this
		);
	}

	internal void Log(
		NamespaceRefactorDiagnosticContext context,
		string phase,
		Func<string> detailsFactory
	)
	{
		if (!IsEnabled || context == null)
			return;

		string details;

		try
		{
			details = detailsFactory?.Invoke() ?? "";
		}
		catch (Exception exception)
		{
			details = $"DiagnosticReadFailed: {exception.GetType().Name}: {exception.Message}";
		}

		string suffix = string.IsNullOrWhiteSpace(details) ? "" : $" {details}";

		try
		{
			_debugLog(
				$"Namespace Refactor [{context.OperationId}] [{phase ?? "Trace"}]{suffix}"
			);
		}
		catch
		{
			// Diagnostics must never escape into the operation being observed.
		}
	}

	internal string FormatPaths(IEnumerable<string> paths)
	{
		if (!IsEnabled)
			return "<debug-disabled>";

		List<string> orderedPaths = paths
			?.Where(path => !string.IsNullOrWhiteSpace(path))
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
			.ThenBy(path => path, StringComparer.Ordinal)
			.ToList()
			?? new List<string>();

		if (orderedPaths.Count == 0)
			return "[]";

		IEnumerable<string> displayedPaths = orderedPaths.Take(MaximumListedPathCount);
		string formatted = $"[{string.Join(", ", displayedPaths.Select(path => $"'{path}'"))}]";

		return orderedPaths.Count <= MaximumListedPathCount
			? formatted
			: $"{formatted} (+{orderedPaths.Count - MaximumListedPathCount} more)";
	}

	internal string SummarizeText(string text)
	{
		if (!IsEnabled)
			return "<debug-disabled>";

		string sourceText = text ?? "";
		string normalizedText = ScriptTextFileService.NormalizeForDiskVerification(sourceText);
		int lineCount = normalizedText.Length == 0
			? 0
			: 1 + normalizedText.Count(character => character == '\n');
		ulong fingerprint = ComputeFnv1A64(Encoding.UTF8.GetBytes(normalizedText));

		return $"Length={sourceText.Length}; NormalizedLength={normalizedText.Length}; Lines={lineCount}; Fingerprint=fnv1a64:{fingerprint:x16}";
	}

	internal string TryGetCurrentScriptPath(ScriptEditor scriptEditor)
	{
		if (!IsEnabled)
			return "<debug-disabled>";

		try
		{
			if (scriptEditor == null || !GodotObject.IsInstanceValid(scriptEditor))
				return "";

			Script currentScript = scriptEditor.GetCurrentScript();

			if (currentScript == null || !GodotObject.IsInstanceValid(currentScript))
				return "";

			return ScriptPathUtility.Normalize(currentScript.ResourcePath);
		}
		catch (Exception exception)
		{
			return $"<DiagnosticReadFailed:{exception.GetType().Name}>";
		}
	}

	private static ulong ComputeFnv1A64(byte[] bytes)
	{
		const ulong offsetBasis = 14695981039346656037UL;
		const ulong prime = 1099511628211UL;
		ulong hash = offsetBasis;

		foreach (byte value in bytes ?? Array.Empty<byte>())
		{
			hash ^= value;
			hash *= prime;
		}

		return hash;
	}
}
#endif
