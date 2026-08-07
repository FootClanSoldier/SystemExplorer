#if TOOLS
using Godot;
using System;

namespace SystemExplorer.Autocomplete.Confirmation;

internal sealed class AutocompleteProjectTypeConfirmationService
{
	private readonly CSharpUsingInsertionPlanner _usingInsertionPlanner;
	private readonly Action<string, string> _debugLog;

	internal AutocompleteProjectTypeConfirmationService(
		CSharpUsingInsertionPlanner usingInsertionPlanner,
		Action<string, string> debugLog
	)
	{
		_usingInsertionPlanner =
			usingInsertionPlanner
			?? throw new ArgumentNullException(nameof(usingInsertionPlanner));
		_debugLog = debugLog ?? throw new ArgumentNullException(nameof(debugLog));
	}

	internal AutocompleteProjectTypeConfirmationResult Confirm(
		CodeEdit codeEdit,
		AutocompleteCompletionOptionMetadata metadata,
		bool replace
	)
	{
		if (codeEdit == null)
			throw new ArgumentNullException(nameof(codeEdit));
		if (metadata == null)
			throw new ArgumentNullException(nameof(metadata));

		string usingAction = DetermineEligibilityAction(codeEdit, metadata);
		if (!string.Equals(usingAction, UsingActionEligible, StringComparison.Ordinal))
			return ConfirmNativeOnly(codeEdit, replace, usingAction);

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
			return ConfirmNativeOnly(codeEdit, replace, UsingActionPlannerUnsafe);
		}

		if (plan == null || plan.Kind == CSharpUsingInsertionPlanKind.Unsafe)
		{
			LogPlannerUnsafe(metadata, plan);
			return ConfirmNativeOnly(codeEdit, replace, UsingActionPlannerUnsafe);
		}

		if (plan.Kind == CSharpUsingInsertionPlanKind.NotRequired)
			return ConfirmNativeOnly(codeEdit, replace, UsingActionNotRequired);

		return ConfirmWithUsingInsertion(codeEdit, metadata, replace, plan);
	}

	private AutocompleteProjectTypeConfirmationResult ConfirmWithUsingInsertion(
		CodeEdit codeEdit,
		AutocompleteCompletionOptionMetadata metadata,
		bool replace,
		CSharpUsingInsertionPlan plan
	)
	{
		bool complexOperationStarted = false;
		bool confirmationSucceeded = false;
		string usingAction = UsingActionInserted;

		try
		{
			codeEdit.BeginComplexOperation();
			complexOperationStarted = true;

			codeEdit.ConfirmCodeCompletion(replace);
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
					: UsingActionConfirmationFailed
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
			usingAction
		);
	}

	private static AutocompleteProjectTypeConfirmationResult ConfirmNativeOnly(
		CodeEdit codeEdit,
		bool replace,
		string usingAction
	)
	{
		codeEdit.ConfirmCodeCompletion(replace);
		return new AutocompleteProjectTypeConfirmationResult(true, usingAction);
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

	internal const string UsingActionNotRequired = "NotRequired";
	internal const string UsingActionInserted = "Inserted";
	internal const string UsingActionSkippedConflict = "SkippedConflict";
	internal const string UsingActionSkippedNestedType = "SkippedNestedType";
	internal const string UsingActionSkippedMultiCaret = "SkippedMultiCaret";
	internal const string UsingActionSkippedEmptyNamespace = "SkippedEmptyNamespace";
	internal const string UsingActionPlannerUnsafe = "PlannerUnsafe";
	internal const string UsingActionFailedAfterConfirmation = "FailedAfterConfirmation";
	internal const string UsingActionConfirmationFailed = "ConfirmationFailed";
	private const string UsingActionEligible = "Eligible";
}

internal sealed record AutocompleteProjectTypeConfirmationResult(
	bool ConfirmationSucceeded,
	string UsingAction
);
#endif
