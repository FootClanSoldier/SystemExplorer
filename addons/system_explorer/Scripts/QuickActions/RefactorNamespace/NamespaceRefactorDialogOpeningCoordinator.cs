#if TOOLS
using System;
using System.Collections.Generic;
using System.Linq;
using SystemExplorer.EditorIntegration.ScriptEditing;

namespace SystemExplorer.QuickActions.RefactorNamespace;

internal readonly record struct NamespaceRefactorDialogOpenResult(
	bool Success,
	string FailureMessage
)
{
	internal static NamespaceRefactorDialogOpenResult Succeeded() => new(true, "");

	internal static NamespaceRefactorDialogOpenResult Failed(string failureMessage) =>
		new(false, failureMessage ?? "");
}

internal sealed class NamespaceRefactorDialogOpeningCoordinator
{
	private readonly NamespaceRefactorDialogView _dialogView;
	private readonly NamespaceRefactorDialogSessionState _sessionState;
	private readonly NamespaceRefactorBatchDialogPreparationCoordinator _batchDialogPreparationCoordinator;
	private readonly Func<string, string> _normalizeScriptPath;
	private readonly Func<string, ScriptTextFileReadResult> _readText;
	private readonly Action<bool> _showConfiguredDialog;
	private readonly Action<string> _debugLog;

	internal NamespaceRefactorDialogOpeningCoordinator(
		NamespaceRefactorDialogView dialogView,
		NamespaceRefactorDialogSessionState sessionState,
		NamespaceRefactorBatchDialogPreparationCoordinator batchDialogPreparationCoordinator,
		Func<string, string> normalizeScriptPath,
		Func<string, ScriptTextFileReadResult> readText,
		Action<bool> showConfiguredDialog,
		Action<string> debugLog
	)
	{
		_dialogView = dialogView ?? throw new ArgumentNullException(nameof(dialogView));
		_sessionState =
			sessionState ?? throw new ArgumentNullException(nameof(sessionState));
		_batchDialogPreparationCoordinator =
			batchDialogPreparationCoordinator
			?? throw new ArgumentNullException(nameof(batchDialogPreparationCoordinator));
		_normalizeScriptPath =
			normalizeScriptPath
			?? throw new ArgumentNullException(nameof(normalizeScriptPath));
		_readText = readText ?? throw new ArgumentNullException(nameof(readText));
		_showConfiguredDialog =
			showConfiguredDialog
			?? throw new ArgumentNullException(nameof(showConfiguredDialog));
		_debugLog = debugLog ?? throw new ArgumentNullException(nameof(debugLog));
	}

	internal NamespaceRefactorDialogOpenResult OpenSingle(string metadata, string scriptPath)
	{
		_sessionState.BeginSingleReplacement(metadata);

		ScriptTextFileReadResult readResult;

		try
		{
			readResult = _readText(scriptPath);
		}
		catch (Exception exception)
		{
			readResult = ScriptTextFileReadResult.Failed(
				ScriptTextFileReadStatus.ReadFailed,
				$"Read delegate threw {exception.GetType().Name}: {NormalizeDiagnosticDetail(exception.Message)}"
			);
		}

		if (!readResult.IsSuccess)
		{
			_sessionState.Clear();
			_debugLog(
				$"Refactor Namespace single dialog read failed for '{scriptPath}': Status={readResult.Status}; FailureDetail='{NormalizeDiagnosticDetail(readResult.FailureDetail)}'"
			);
			return NamespaceRefactorDialogOpenResult.Failed(
				$"Refactor Namespace could not read '{scriptPath}'. The operation was cancelled."
			);
		}

		string currentNamespace = NamespaceTextRewriter.GetNamespaceFromText(readResult.Text);

		if (string.IsNullOrWhiteSpace(currentNamespace))
		{
			_debugLog(
				$"Refactor Namespace found no namespace in '{scriptPath}'. Opening add-namespace dialog."
			);

			string normalizedScriptPath;

			try
			{
				normalizedScriptPath = _normalizeScriptPath(scriptPath);
			}
			catch (Exception exception)
			{
				_sessionState.Clear();
				_debugLog(
					$"Refactor Namespace could not normalize '{scriptPath}' before opening the add-namespace dialog: {exception.GetType().Name}: {NormalizeDiagnosticDetail(exception.Message)}"
				);
				return NamespaceRefactorDialogOpenResult.Failed(
					$"Refactor Namespace could not use '{scriptPath}'. The operation was cancelled."
				);
			}

			if (string.IsNullOrWhiteSpace(normalizedScriptPath))
			{
				_sessionState.Clear();
				return NamespaceRefactorDialogOpenResult.Failed(
					$"Refactor Namespace could not use '{scriptPath}'. The operation was cancelled."
				);
			}

			_sessionState.TransitionToSingleAdd(normalizedScriptPath);
			_dialogView.ConfigureSingleAddNamespace();
			_showConfiguredDialog(false);
			return NamespaceRefactorDialogOpenResult.Succeeded();
		}

		_dialogView.ConfigureSingleExistingNamespace(currentNamespace);
		_showConfiguredDialog(true);
		return NamespaceRefactorDialogOpenResult.Succeeded();
	}

	internal void OpenBatch(string metadata, IReadOnlyList<string> scriptPaths)
	{
		NamespaceRefactorBatchDialogPreparationResult preparationResult =
			_batchDialogPreparationCoordinator.PrepareBatchDialog(
				scriptPaths,
				metadata
			);

		if (!preparationResult.Success)
			return;

		List<string> namespaces = preparationResult.Namespaces.ToList();

		_sessionState.BeginBatch(metadata, scriptPaths, namespaces);

		_dialogView.ConfigureBatch(
			namespaces,
			preparationResult.HasScriptsWithoutNamespace
		);
		_showConfiguredDialog(true);
	}

	private static string NormalizeDiagnosticDetail(string detail)
	{
		return string.IsNullOrWhiteSpace(detail)
			? ""
			: detail.Replace('\r', ' ').Replace('\n', ' ').Trim();
	}
}
#endif
