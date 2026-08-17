#if TOOLS
using SystemExplorer.EditorIntegration.ScriptEditing;

namespace SystemExplorer.Autocomplete.Confirmation;

internal sealed record AutocompleteDeferredUsingInsertionCandidate(
	string CompletionName,
	string NamespaceName,
	long OriginatingCompletionPublicationId,
	EditorBindingLease BindingLease,
	CSharpUsingInsertionPlan Plan
);

internal sealed record AutocompleteDeferredUsingInsertionRequest(
	string CompletionName,
	string NamespaceName,
	long OriginatingCompletionPublicationId,
	EditorBindingLease BindingLease,
	CSharpUsingInsertionPlan Plan
)
{
	internal string ScriptPath => BindingLease.ScriptResourcePath ?? "";
	internal ulong CodeEditNativeInstanceId => BindingLease.CodeEditInstanceId;
}

internal sealed record AutocompleteDeferredUsingInsertionExecutionContext(
	long MutationTransactionId,
	ulong CurrentCodeEditNativeInstanceId,
	string CurrentScriptPath,
	EditorBindingLease CurrentBindingLease,
	long HostInstanceToken,
	string ManagedAssemblyGeneration,
	int GuiInputCallbackDepth
);

internal sealed record AutocompleteDeferredUsingInsertionApplyResult(
	bool Succeeded,
	string FailureReason,
	ulong CurrentCodeEditNativeInstanceId,
	string CurrentScriptPath,
	EditorBindingLease? CurrentBindingLease
)
{
	internal static AutocompleteDeferredUsingInsertionApplyResult Success(
		ulong currentCodeEditNativeInstanceId,
		string currentScriptPath,
		EditorBindingLease currentBindingLease
	)
	{
		return new AutocompleteDeferredUsingInsertionApplyResult(
			true,
			"",
			currentCodeEditNativeInstanceId,
			currentScriptPath ?? "",
			currentBindingLease
		);
	}

	internal static AutocompleteDeferredUsingInsertionApplyResult Rejected(
		string reason,
		ulong currentCodeEditNativeInstanceId = 0,
		string currentScriptPath = "",
		EditorBindingLease? currentBindingLease = null
	)
	{
		return new AutocompleteDeferredUsingInsertionApplyResult(
			false,
			reason ?? "",
			currentCodeEditNativeInstanceId,
			currentScriptPath ?? "",
			currentBindingLease
		);
	}
}
#endif
