#if TOOLS
using Godot;
using System;

namespace SystemExplorer.Autocomplete.Confirmation;

internal sealed class AutocompleteProjectTypeConfirmationService
{
	private readonly CSharpUsingInsertionPlanner _usingInsertionPlanner;
	private readonly Action<string, string> _debugLog;
	private readonly bool _automaticUsingInsertTextExecutionEnabled;
	private readonly bool _automaticUsingDeferInsertTextAfterGuiInputEnabled;
	private readonly bool _automaticUsingComplexOperationWrapperEnabled;

	internal AutocompleteProjectTypeConfirmationService(
		CSharpUsingInsertionPlanner usingInsertionPlanner,
		Action<string, string> debugLog,
		bool automaticUsingInsertTextExecutionEnabled,
		bool automaticUsingDeferInsertTextAfterGuiInputEnabled,
		bool automaticUsingComplexOperationWrapperEnabled
	)
	{
		_usingInsertionPlanner =
			usingInsertionPlanner
			?? throw new ArgumentNullException(nameof(usingInsertionPlanner));
		_debugLog = debugLog ?? throw new ArgumentNullException(nameof(debugLog));
		_automaticUsingInsertTextExecutionEnabled =
			automaticUsingInsertTextExecutionEnabled;
		_automaticUsingDeferInsertTextAfterGuiInputEnabled =
			automaticUsingDeferInsertTextAfterGuiInputEnabled;
		_automaticUsingComplexOperationWrapperEnabled =
			automaticUsingComplexOperationWrapperEnabled;
	}

	internal AutocompleteProjectTypeConfirmationPreparation PrepareConfirmation(
		CodeEdit codeEdit,
		AutocompleteCompletionOptionMetadata metadata,
		bool requestedReplace
	)
	{
		if (codeEdit == null)
			throw new ArgumentNullException(nameof(codeEdit));
		if (metadata == null)
			throw new ArgumentNullException(nameof(metadata));

		bool effectiveReplace = requestedReplace || metadata.UsesQualifiedInsertion;
		string usingAction = DetermineEligibilityAction(codeEdit, metadata);
		if (!string.Equals(usingAction, UsingActionEligible, StringComparison.Ordinal))
		{
			return new AutocompleteProjectTypeConfirmationPreparation(
				effectiveReplace,
				usingAction,
				AutocompleteProjectTypeConfirmationExecutionMode.NativeOnly
			);
		}

		CSharpUsingInsertionPlan plan;
		try
		{
			int caretLine = codeEdit.GetCaretLine();
			plan = _usingInsertionPlanner.Plan(
				codeEdit.Text,
				metadata.NamespaceName,
				caretLine
			);
		}
		catch (Exception exception)
		{
			LogPlannerFailure(metadata, exception);
			return new AutocompleteProjectTypeConfirmationPreparation(
				effectiveReplace,
				UsingActionPlannerUnsafe,
				AutocompleteProjectTypeConfirmationExecutionMode.NativeOnly
			);
		}

		if (plan == null || plan.Kind == CSharpUsingInsertionPlanKind.Unsafe)
		{
			LogPlannerUnsafe(metadata, plan);
			return new AutocompleteProjectTypeConfirmationPreparation(
				effectiveReplace,
				UsingActionPlannerUnsafe,
				AutocompleteProjectTypeConfirmationExecutionMode.NativeOnly
			);
		}

		if (plan.Kind == CSharpUsingInsertionPlanKind.NotRequired)
		{
			return new AutocompleteProjectTypeConfirmationPreparation(
				effectiveReplace,
				UsingActionNotRequired,
				AutocompleteProjectTypeConfirmationExecutionMode.NativeOnly
			);
		}

		if (!_automaticUsingInsertTextExecutionEnabled)
		{
			return new AutocompleteProjectTypeConfirmationPreparation(
				requestedReplace,
				UsingActionAutomaticUsingMutationIsolated,
				AutocompleteProjectTypeConfirmationExecutionMode.NativeOnly
			);
		}

		if (_automaticUsingDeferInsertTextAfterGuiInputEnabled)
		{
			return new AutocompleteProjectTypeConfirmationPreparation(
				requestedReplace,
				UsingActionDeferredInsertPending,
				AutocompleteProjectTypeConfirmationExecutionMode.DeferredUsingInsertion,
				plan
			);
		}

		return _automaticUsingComplexOperationWrapperEnabled
			? new AutocompleteProjectTypeConfirmationPreparation(
				requestedReplace,
				UsingActionInserted,
				AutocompleteProjectTypeConfirmationExecutionMode.ImmediateUsingInsertionComplexOperation,
				plan
			)
			: new AutocompleteProjectTypeConfirmationPreparation(
				requestedReplace,
				UsingActionInsertedWithoutComplexOperation,
				AutocompleteProjectTypeConfirmationExecutionMode.ImmediateUsingInsertionWithoutComplexOperation,
				plan
			);
	}

	internal AutocompleteProjectTypeConfirmationResult ExecutePreparedConfirmation(
		CodeEdit codeEdit,
		AutocompleteCompletionOptionMetadata metadata,
		AutocompleteProjectTypeConfirmationPreparation preparation
	)
	{
		if (codeEdit == null)
			throw new ArgumentNullException(nameof(codeEdit));
		if (metadata == null)
			throw new ArgumentNullException(nameof(metadata));
		if (preparation == null)
			throw new ArgumentNullException(nameof(preparation));

		return preparation.ExecutionMode switch
		{
			AutocompleteProjectTypeConfirmationExecutionMode.NativeOnly =>
				ExecuteNativeOnly(codeEdit, preparation),
			AutocompleteProjectTypeConfirmationExecutionMode.DeferredUsingInsertion =>
				ExecuteDeferredUsingInsertion(codeEdit, metadata, preparation),
			AutocompleteProjectTypeConfirmationExecutionMode.ImmediateUsingInsertionWithoutComplexOperation =>
				ExecuteUsingInsertionWithoutComplexOperation(
					codeEdit,
					metadata,
					preparation
				),
			AutocompleteProjectTypeConfirmationExecutionMode.ImmediateUsingInsertionComplexOperation =>
				ExecuteUsingInsertionComplexOperation(codeEdit, metadata, preparation),
			_ => throw new InvalidOperationException(
				$"Unknown project-type confirmation execution mode '{preparation.ExecutionMode}'."
			),
		};
	}

	private static AutocompleteProjectTypeConfirmationResult ExecuteNativeOnly(
		CodeEdit codeEdit,
		AutocompleteProjectTypeConfirmationPreparation preparation
	)
	{
		ConfirmNative(codeEdit, preparation.NativeReplace);
		return new AutocompleteProjectTypeConfirmationResult(
			true,
			preparation.UsingAction,
			preparation.NativeReplace
		);
	}

	private AutocompleteProjectTypeConfirmationResult ExecuteDeferredUsingInsertion(
		CodeEdit codeEdit,
		AutocompleteCompletionOptionMetadata metadata,
		AutocompleteProjectTypeConfirmationPreparation preparation
	)
	{
		CSharpUsingInsertionPlan plan = RequireInsertPlan(preparation);
		bool confirmationSucceeded = false;

		try
		{
			ConfirmNative(codeEdit, preparation.NativeReplace);
			confirmationSucceeded = true;
		}
		catch (Exception exception)
		{
			LogConfirmationFailure(metadata, confirmationSucceeded, exception);
			return new AutocompleteProjectTypeConfirmationResult(
				false,
				UsingActionConfirmationFailed,
				preparation.NativeReplace
			);
		}

		return new AutocompleteProjectTypeConfirmationResult(
			true,
			preparation.UsingAction,
			preparation.NativeReplace,
			plan
		);
	}

	private AutocompleteProjectTypeConfirmationResult ExecuteUsingInsertionWithoutComplexOperation(
		CodeEdit codeEdit,
		AutocompleteCompletionOptionMetadata metadata,
		AutocompleteProjectTypeConfirmationPreparation preparation
	)
	{
		CSharpUsingInsertionPlan plan = RequireInsertPlan(preparation);
		bool confirmationSucceeded = false;

		try
		{
			ConfirmNative(codeEdit, preparation.NativeReplace);
			confirmationSucceeded = true;
		}
		catch (Exception exception)
		{
			LogConfirmationFailure(metadata, confirmationSucceeded, exception);
			return new AutocompleteProjectTypeConfirmationResult(
				false,
				UsingActionConfirmationFailed,
				preparation.NativeReplace
			);
		}

		try
		{
			LogInsertTextBoundary(metadata, plan, "Begin");
			codeEdit.InsertText(
				plan.InsertionText,
				plan.InsertLine,
				plan.InsertColumn
			);
			LogInsertTextBoundary(metadata, plan, "Returned");

			return new AutocompleteProjectTypeConfirmationResult(
				true,
				preparation.UsingAction,
				preparation.NativeReplace
			);
		}
		catch (Exception exception)
		{
			LogInsertionFailure(metadata, plan, exception);
			return new AutocompleteProjectTypeConfirmationResult(
				true,
				UsingActionFailedAfterConfirmation,
				preparation.NativeReplace
			);
		}
	}

	private AutocompleteProjectTypeConfirmationResult ExecuteUsingInsertionComplexOperation(
		CodeEdit codeEdit,
		AutocompleteCompletionOptionMetadata metadata,
		AutocompleteProjectTypeConfirmationPreparation preparation
	)
	{
		CSharpUsingInsertionPlan plan = RequireInsertPlan(preparation);
		bool complexOperationStarted = false;
		bool confirmationSucceeded = false;
		string usingAction = preparation.UsingAction;

		try
		{
			codeEdit.BeginComplexOperation();
			complexOperationStarted = true;

			ConfirmNative(codeEdit, preparation.NativeReplace);
			confirmationSucceeded = true;

			try
			{
				codeEdit.InsertText(
					plan.InsertionText,
					plan.InsertLine,
					plan.InsertColumn
				);
			}
			catch (Exception exception)
			{
				usingAction = UsingActionFailedAfterConfirmation;
				LogInsertionFailure(metadata, plan, exception);
			}
		}
		catch (Exception exception)
		{
			LogConfirmationFailure(metadata, confirmationSucceeded, exception);
			return new AutocompleteProjectTypeConfirmationResult(
				confirmationSucceeded,
				confirmationSucceeded
					? UsingActionFailedAfterConfirmation
					: UsingActionConfirmationFailed,
				preparation.NativeReplace
			);
		}
		finally
		{
			if (complexOperationStarted)
			{
				try
				{
					codeEdit.EndComplexOperation();
				}
				catch (Exception exception)
				{
					if (confirmationSucceeded)
						usingAction = UsingActionFailedAfterConfirmation;
					LogComplexOperationEndFailure(metadata, confirmationSucceeded, exception);
				}
			}
		}

		return new AutocompleteProjectTypeConfirmationResult(
			confirmationSucceeded,
			usingAction,
			preparation.NativeReplace
		);
	}

	private static CSharpUsingInsertionPlan RequireInsertPlan(
		AutocompleteProjectTypeConfirmationPreparation preparation
	)
	{
		CSharpUsingInsertionPlan plan = preparation?.UsingInsertionPlan;
		if (plan == null || plan.Kind != CSharpUsingInsertionPlanKind.Insert)
			throw new InvalidOperationException("Prepared confirmation requires an Insert using plan.");

		return plan;
	}

	internal AutocompleteDeferredUsingInsertionApplyResult ApplyDeferredUsingInsertion(
		CodeEdit codeEdit,
		AutocompleteDeferredUsingInsertionRequest request,
		AutocompleteDeferredUsingInsertionExecutionContext executionContext
	)
	{
		if (codeEdit == null)
			throw new ArgumentNullException(nameof(codeEdit));
		if (request == null)
			throw new ArgumentNullException(nameof(request));
		if (executionContext == null)
			throw new ArgumentNullException(nameof(executionContext));

		CSharpUsingInsertionPlan plan = request.Plan;
		if (plan == null || plan.Kind != CSharpUsingInsertionPlanKind.Insert)
		{
			return AutocompleteDeferredUsingInsertionApplyResult.Rejected(
				"InvalidPlan",
				executionContext.CurrentCodeEditNativeInstanceId,
				executionContext.CurrentScriptPath,
				executionContext.CurrentBindingLease
			);
		}

		try
		{
			LogDeferredInsertTextBoundary(request, executionContext, "Begin");
			codeEdit.InsertText(
				plan.InsertionText,
				plan.InsertLine,
				plan.InsertColumn
			);
			LogDeferredInsertTextBoundary(request, executionContext, "Returned");
			return AutocompleteDeferredUsingInsertionApplyResult.Success(
				executionContext.CurrentCodeEditNativeInstanceId,
				executionContext.CurrentScriptPath,
				executionContext.CurrentBindingLease
			);
		}
		catch (Exception exception)
		{
			LogDeferredInsertionFailure(request, executionContext, exception);
			return AutocompleteDeferredUsingInsertionApplyResult.Rejected(
				UsingActionFailedAfterConfirmationDeferred,
				executionContext.CurrentCodeEditNativeInstanceId,
				executionContext.CurrentScriptPath,
				executionContext.CurrentBindingLease
			);
		}
	}

	private void LogInsertTextBoundary(
		AutocompleteCompletionOptionMetadata metadata,
		CSharpUsingInsertionPlan plan,
		string phase
	)
	{
		LogDiagnostic(
			"C# autocomplete automatic using InsertText boundary",
			metadata,
			$"Phase='{phase ?? ""}', InsertLine={plan?.InsertLine ?? -1}, "
				+ $"InsertColumn={plan?.InsertColumn ?? -1}, "
				+ $"InsertionTextLength={plan?.InsertionText?.Length ?? 0}"
		);
	}

	private void LogDeferredInsertTextBoundary(
		AutocompleteDeferredUsingInsertionRequest request,
		AutocompleteDeferredUsingInsertionExecutionContext executionContext,
		string phase
	)
	{
		CSharpUsingInsertionPlan plan = request?.Plan;
		LogDeferredDiagnostic(
			"C# autocomplete automatic using deferred InsertText boundary",
			request,
			executionContext,
			$"Phase='{phase ?? ""}', InsertLine='{plan?.InsertLine ?? -1}', "
				+ $"InsertColumn='{plan?.InsertColumn ?? -1}', "
				+ $"InsertionTextLength='{plan?.InsertionText?.Length ?? 0}'"
		);
	}

	private static void ConfirmNative(CodeEdit codeEdit, bool replace)
	{
		codeEdit.ConfirmCodeCompletion(replace);
	}

	private static string DetermineEligibilityAction(
		CodeEdit codeEdit,
		AutocompleteCompletionOptionMetadata metadata
	)
	{
		if (
			!string.Equals(
				metadata.Source,
				AutocompleteCompletionOptionMetadata.ProjectTypeSource,
				StringComparison.Ordinal
			)
			|| metadata.AvailabilityPriority != 4
		)
		{
			return UsingActionNotRequired;
		}

		if (string.IsNullOrWhiteSpace(metadata.NamespaceName))
			return UsingActionSkippedEmptyNamespace;
		if (metadata.HasSimpleNameConflict)
			return UsingActionSkippedConflict;
		if (metadata.IsNestedType)
			return UsingActionSkippedNestedType;
		if (codeEdit.GetCaretCount() != 1)
			return UsingActionSkippedMultiCaret;

		return UsingActionEligible;
	}

	private void LogPlannerFailure(
		AutocompleteCompletionOptionMetadata metadata,
		Exception exception
	)
	{
		LogDiagnostic(
			"C# autocomplete using planner failed",
			metadata,
			$"ExceptionType='{exception?.GetType().FullName ?? ""}', Exception='{exception}'"
		);
	}

	private void LogPlannerUnsafe(
		AutocompleteCompletionOptionMetadata metadata,
		CSharpUsingInsertionPlan plan
	)
	{
		LogDiagnostic(
			"C# autocomplete using planner returned unsafe",
			metadata,
			$"Reason='{plan?.Reason ?? "MissingPlan"}'"
		);
	}

	private void LogInsertionFailure(
		AutocompleteCompletionOptionMetadata metadata,
		CSharpUsingInsertionPlan plan,
		Exception exception
	)
	{
		LogDiagnostic(
			"C# autocomplete using insertion failed after confirmation",
			metadata,
			$"Line={plan?.InsertLine ?? -1}, Column={plan?.InsertColumn ?? -1}, "
				+ $"ExceptionType='{exception?.GetType().FullName ?? ""}', Exception='{exception}'"
		);
	}

	private void LogDeferredInsertionFailure(
		AutocompleteDeferredUsingInsertionRequest request,
		AutocompleteDeferredUsingInsertionExecutionContext executionContext,
		Exception exception
	)
	{
		CSharpUsingInsertionPlan plan = request?.Plan;
		LogDeferredDiagnostic(
			"C# autocomplete automatic using deferred InsertText failed after confirmation",
			request,
			executionContext,
			$"UsingAction='{UsingActionFailedAfterConfirmationDeferred}', "
				+ $"InsertLine='{plan?.InsertLine ?? -1}', InsertColumn='{plan?.InsertColumn ?? -1}', "
				+ $"InsertionTextLength='{plan?.InsertionText?.Length ?? 0}', "
				+ $"ExceptionType='{exception?.GetType().FullName ?? ""}', Exception='{exception}'"
		);
	}

	private void LogConfirmationFailure(
		AutocompleteCompletionOptionMetadata metadata,
		bool confirmationSucceeded,
		Exception exception
	)
	{
		LogDiagnostic(
			"C# autocomplete project-type confirmation transaction failed",
			metadata,
			$"ConfirmationSucceeded={confirmationSucceeded}, "
				+ $"ExceptionType='{exception?.GetType().FullName ?? ""}', Exception='{exception}'"
		);
	}

	private void LogComplexOperationEndFailure(
		AutocompleteCompletionOptionMetadata metadata,
		bool confirmationSucceeded,
		Exception exception
	)
	{
		LogDiagnostic(
			"C# autocomplete complex operation close failed",
			metadata,
			$"ConfirmationSucceeded={confirmationSucceeded}, "
				+ $"ExceptionType='{exception?.GetType().FullName ?? ""}', Exception='{exception}'"
		);
	}

	private void LogDiagnostic(
		string operation,
		AutocompleteCompletionOptionMetadata metadata,
		string detail
	)
	{
		try
		{
			_debugLog(
				operation,
				$"Name='{metadata?.Name ?? ""}', Namespace='{metadata?.NamespaceName ?? ""}', {detail}"
			);
		}
		catch
		{
			// Diagnostics must never escape the CodeEdit GuiInput callback.
		}
	}

	private void LogDeferredDiagnostic(
		string operation,
		AutocompleteDeferredUsingInsertionRequest request,
		AutocompleteDeferredUsingInsertionExecutionContext executionContext,
		string detail
	)
	{
		try
		{
			_debugLog(
				operation,
				$"Name='{request?.CompletionName ?? ""}', Namespace='{request?.NamespaceName ?? ""}', "
					+ $"OriginatingCompletionPublicationId='{request?.OriginatingCompletionPublicationId ?? 0}', "
					+ $"MutationTransactionId='{executionContext?.MutationTransactionId ?? 0}', "
					+ $"ExpectedCodeEditNativeInstanceId='{request?.CodeEditNativeInstanceId ?? 0UL}', "
					+ $"CurrentCodeEditNativeInstanceId='{executionContext?.CurrentCodeEditNativeInstanceId ?? 0UL}', "
					+ $"ScriptPath='{request?.ScriptPath ?? ""}', "
					+ $"ExpectedScriptTransitionId='{request?.BindingLease.ScriptTransitionId ?? 0}', "
					+ $"CurrentScriptTransitionId='{executionContext?.CurrentBindingLease.ScriptTransitionId ?? 0}', "
					+ $"ExpectedBindingEpoch='{request?.BindingLease.BindingEpoch ?? 0}', "
					+ $"CurrentBindingEpoch='{executionContext?.CurrentBindingLease.BindingEpoch ?? 0}', "
					+ $"ExpectedReloadReadyEpoch='{request?.BindingLease.ReloadReadyEpoch ?? 0}', "
					+ $"CurrentReloadReadyEpoch='{executionContext?.CurrentBindingLease.ReloadReadyEpoch ?? 0}', "
					+ $"HostInstanceToken='{executionContext?.HostInstanceToken ?? 0}', "
					+ $"ManagedAssemblyGeneration='{executionContext?.ManagedAssemblyGeneration ?? ""}', "
					+ $"GuiInputCallbackDepth='{executionContext?.GuiInputCallbackDepth ?? -1}', {detail}"
			);
		}
		catch
		{
			// Diagnostics must never escape a deferred CodeEdit mutation boundary.
		}
	}

	internal const string UsingActionNotRequired = "NotRequired";
	internal const string UsingActionInserted = "Inserted";
	internal const string UsingActionInsertedWithoutComplexOperation =
		"InsertedWithoutComplexOperation";
	internal const string UsingActionDeferredInsertPending = "DeferredInsertPending";
	internal const string UsingActionAutomaticUsingMutationIsolated =
		"AutomaticUsingMutationIsolated";
	internal const string UsingActionSkippedConflict = "SkippedConflict";
	internal const string UsingActionSkippedNestedType = "SkippedNestedType";
	internal const string UsingActionSkippedMultiCaret = "SkippedMultiCaret";
	internal const string UsingActionSkippedEmptyNamespace = "SkippedEmptyNamespace";
	internal const string UsingActionPlannerUnsafe = "PlannerUnsafe";
	internal const string UsingActionFailedAfterConfirmation = "FailedAfterConfirmation";
	internal const string UsingActionFailedAfterConfirmationDeferred =
		"FailedAfterConfirmationDeferred";
	internal const string UsingActionConfirmationFailed = "ConfirmationFailed";
	private const string UsingActionEligible = "Eligible";
}

internal enum AutocompleteProjectTypeConfirmationExecutionMode
{
	NativeOnly,
	DeferredUsingInsertion,
	ImmediateUsingInsertionWithoutComplexOperation,
	ImmediateUsingInsertionComplexOperation,
}

internal sealed record AutocompleteProjectTypeConfirmationPreparation(
	bool NativeReplace,
	string UsingAction,
	AutocompleteProjectTypeConfirmationExecutionMode ExecutionMode,
	CSharpUsingInsertionPlan UsingInsertionPlan = null
);

internal sealed record AutocompleteProjectTypeConfirmationResult(
	bool ConfirmationSucceeded,
	string UsingAction,
	bool EffectiveReplace,
	CSharpUsingInsertionPlan DeferredUsingInsertionPlan = null
);
#endif
