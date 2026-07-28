#if TOOLS
using System;
using System.Collections.Generic;
using System.Linq;
using SystemExplorer.EditorIntegration.ScriptEditing;

namespace SystemExplorer.QuickActions.RefactorNamespace;

internal sealed class NamespaceRefactorDialogConfirmationCoordinator
{
	private readonly NamespaceRefactorDialogView _dialogView;
	private readonly NamespaceRefactorDialogSessionState _sessionState;
	private readonly NamespaceRefactorDiagnosticTrace _diagnosticTrace;
	private readonly Func<string, bool> _isValidNamespaceName;
	private readonly Func<string, ScriptTextFileReadResult> _readText;
	private readonly Action<NamespaceRefactorDiagnosticContext, string, string, string> _singleReplacement;
	private readonly Action<NamespaceRefactorDiagnosticContext, IReadOnlyList<string>, string, string> _addNamespace;
	private readonly Action<NamespaceRefactorDiagnosticContext, IReadOnlyList<string>, string, string> _batchReplacement;
	private readonly Action<string> _debugLog;
	private readonly Action<string> _showWarning;
	private readonly Action<string, string> _logOperation;

	internal NamespaceRefactorDialogConfirmationCoordinator(
		NamespaceRefactorDialogView dialogView,
		NamespaceRefactorDialogSessionState sessionState,
		NamespaceRefactorDiagnosticTrace diagnosticTrace,
		Func<string, bool> isValidNamespaceName,
		Func<string, ScriptTextFileReadResult> readText,
		Action<NamespaceRefactorDiagnosticContext, string, string, string> singleReplacement,
		Action<NamespaceRefactorDiagnosticContext, IReadOnlyList<string>, string, string> addNamespace,
		Action<NamespaceRefactorDiagnosticContext, IReadOnlyList<string>, string, string> batchReplacement,
		Action<string> debugLog,
		Action<string> showWarning,
		Action<string, string> logOperation
	)
	{
		_dialogView = dialogView ?? throw new ArgumentNullException(nameof(dialogView));
		_sessionState = sessionState ?? throw new ArgumentNullException(nameof(sessionState));
		_diagnosticTrace = diagnosticTrace ?? throw new ArgumentNullException(nameof(diagnosticTrace));
		_isValidNamespaceName = isValidNamespaceName ?? throw new ArgumentNullException(nameof(isValidNamespaceName));
		_readText = readText ?? throw new ArgumentNullException(nameof(readText));
		_singleReplacement = singleReplacement ?? throw new ArgumentNullException(nameof(singleReplacement));
		_addNamespace = addNamespace ?? throw new ArgumentNullException(nameof(addNamespace));
		_batchReplacement = batchReplacement ?? throw new ArgumentNullException(nameof(batchReplacement));
		_debugLog = debugLog ?? throw new ArgumentNullException(nameof(debugLog));
		_showWarning = showWarning ?? throw new ArgumentNullException(nameof(showWarning));
		_logOperation = logOperation ?? throw new ArgumentNullException(nameof(logOperation));
	}

	internal void Confirm()
	{
		if (string.IsNullOrWhiteSpace(_sessionState.Metadata))
			return;

		string newNamespace = _dialogView.NewNamespaceText.Trim();

		switch (_sessionState.Mode)
		{
			case NamespaceRefactorDialogMode.None:
				return;
			case NamespaceRefactorDialogMode.SingleReplacement:
				ConfirmSingleReplacement(newNamespace);
				return;
			case NamespaceRefactorDialogMode.SingleAdd:
				ConfirmSingleAdd(newNamespace);
				return;
			case NamespaceRefactorDialogMode.Batch:
				ConfirmBatch(newNamespace);
				return;
		}
	}

	private void ConfirmSingleAdd(string newNamespace)
	{
		_logOperation("Refactor Namespace Add Confirmed", newNamespace);

		if (!_isValidNamespaceName(newNamespace))
		{
			_debugLog("Refactor Namespace add cancelled: new namespace must be a valid C# namespace name.");
			return;
		}

		NamespaceRefactorDiagnosticContext diagnosticContext =
			_diagnosticTrace.CreateContext("Single Add Namespace");
		string[] capturedScriptPaths = _sessionState.ScriptPaths.ToArray();
		diagnosticContext.Log("Request", "Confirmed operation started.");
		_addNamespace(diagnosticContext, capturedScriptPaths, newNamespace, "Refactor Namespace Add");
		_sessionState.Clear();
	}

	private void ConfirmSingleReplacement(string newNamespace)
	{
		string oldNamespace = _dialogView.OldNamespaceText.Trim();

		_logOperation("Refactor Namespace Confirmed", $"{oldNamespace} -> {newNamespace}");

		if (!_isValidNamespaceName(oldNamespace) || !_isValidNamespaceName(newNamespace))
		{
			_showWarning("Refactor Namespace cancelled: namespace values must be valid C# namespace names.");
			return;
		}

		if (oldNamespace == newNamespace)
		{
			_debugLog("Refactor Namespace cancelled: namespace is unchanged.");
			return;
		}

		NamespaceRefactorDiagnosticContext diagnosticContext =
			_diagnosticTrace.CreateContext("Single Replacement");
		string capturedMetadata = _sessionState.Metadata;
		diagnosticContext.Log("Request", "Confirmed operation started.");
		_singleReplacement(diagnosticContext, capturedMetadata, oldNamespace, newNamespace);
		_sessionState.Clear();
	}

	private void ConfirmBatch(string newNamespace)
	{
		if (!_isValidNamespaceName(newNamespace))
		{
			_debugLog("Refactor Namespace batch cancelled: new namespace must be a valid C# namespace name.");
			return;
		}

		if (_dialogView.IsWithoutNamespaceSelected)
		{
			ConfirmBatchAdd(newNamespace);
			return;
		}

		ConfirmBatchReplacement(newNamespace);
	}

	private void ConfirmBatchAdd(string newNamespace)
	{
		_logOperation("Refactor Namespace Batch Add Confirmed", newNamespace);
		NamespaceRefactorDiagnosticContext diagnosticContext =
			_diagnosticTrace.CreateContext(GetBatchOperationKind("Add Namespace"));

		List<string> targetScriptPaths = new();
		List<string> missingPaths = new();
		List<string> failedPaths = new();
		HashSet<string> seenPaths = new(StringComparer.OrdinalIgnoreCase);

		foreach (string scriptPath in _sessionState.ScriptPaths)
		{
			if (string.IsNullOrWhiteSpace(scriptPath) || !seenPaths.Add(scriptPath))
				continue;

			ScriptTextFileReadResult readResult = ReadTextSafely(scriptPath);

			if (readResult.IsSuccess)
			{
				string currentNamespace = NamespaceTextRewriter.GetNamespaceFromText(
					readResult.Text
				);

				if (string.IsNullOrWhiteSpace(currentNamespace))
					targetScriptPaths.Add(scriptPath);

				continue;
			}

			diagnosticContext.Log(
				"Request",
				() =>
					$"Batch Add confirmation read failed; Path='{scriptPath}'; Status={readResult.Status}; FailureDetail='{NormalizeDiagnosticDetail(readResult.FailureDetail)}'"
			);

			if (readResult.Status == ScriptTextFileReadStatus.MissingFile)
				missingPaths.Add(scriptPath);
			else
				failedPaths.Add(scriptPath);
		}

		if (missingPaths.Count > 0 || failedPaths.Count > 0)
		{
			_showWarning(
				BuildBatchReadFailureSummary(
					missingPaths,
					failedPaths,
					targetScriptPaths.Count == 0
				)
			);
		}
		else if (targetScriptPaths.Count == 0)
		{
			_showWarning(
				"Refactor Namespace Batch Add cancelled: no current scripts without a namespace were found."
			);
		}

		if (targetScriptPaths.Count > 0)
		{
			diagnosticContext.Log("Request", "Confirmed operation started.");
			_addNamespace(
				diagnosticContext,
				targetScriptPaths,
				newNamespace,
				"Refactor Namespace Batch Add"
			);
		}

		_sessionState.Clear();
	}

	private void ConfirmBatchReplacement(string newNamespace)
	{
		string oldNamespace = _dialogView.GetSelectedExistingNamespace();

		if (!_isValidNamespaceName(oldNamespace))
		{
			_debugLog("Refactor Namespace batch cancelled: no valid old namespace was selected.");
			return;
		}

		if (oldNamespace == newNamespace)
		{
			_debugLog("Refactor Namespace batch cancelled: namespace is unchanged.");
			return;
		}

		_logOperation("Refactor Namespace Batch Confirmed", $"{oldNamespace} -> {newNamespace}");
		NamespaceRefactorDiagnosticContext diagnosticContext =
			_diagnosticTrace.CreateContext(GetBatchOperationKind("Replacement"));
		string[] capturedScriptPaths = _sessionState.ScriptPaths.ToArray();
		diagnosticContext.Log("Request", "Confirmed operation started.");
		_batchReplacement(diagnosticContext, capturedScriptPaths, oldNamespace, newNamespace);
		_sessionState.Clear();
	}

	private ScriptTextFileReadResult ReadTextSafely(string scriptPath)
	{
		try
		{
			return _readText(scriptPath);
		}
		catch (Exception exception)
		{
			return ScriptTextFileReadResult.Failed(
				ScriptTextFileReadStatus.ReadFailed,
				$"Read delegate threw {exception.GetType().Name}: {NormalizeDiagnosticDetail(exception.Message)}"
			);
		}
	}

	private static string BuildBatchReadFailureSummary(
		IReadOnlyList<string> missingPaths,
		IReadOnlyList<string> failedPaths,
		bool noCandidatesRemain
	)
	{
		List<string> sections = new()
		{
			noCandidatesRemain
				? "Refactor Namespace Batch Add cancelled: no readable current scripts without a namespace remained."
				: "Refactor Namespace Batch Add skipped scripts that could not be read at confirmation.",
		};

		if (missingPaths?.Count > 0)
			sections.Add($"Missing scripts:\n{string.Join("\n", missingPaths)}");

		if (failedPaths?.Count > 0)
			sections.Add($"Unreadable scripts:\n{string.Join("\n", failedPaths)}");

		return string.Join("\n\n", sections);
	}

	private static string NormalizeDiagnosticDetail(string detail)
	{
		return string.IsNullOrWhiteSpace(detail)
			? ""
			: detail.Replace('\r', ' ').Replace('\n', ' ').Trim();
	}

	private string GetBatchOperationKind(string operation)
	{
		string scope = _sessionState.Metadata.StartsWith("folder::", StringComparison.Ordinal)
			? "Folder Batch"
			: "System Batch";
		return $"{scope} {operation}";
	}
}
#endif
