#if TOOLS
namespace SystemExplorer.Autocomplete.Confirmation;

internal sealed record AutocompleteDeferredUsingInsertionCandidate(
	string CompletionName,
	string NamespaceName,
	CSharpUsingInsertionPlan Plan
);

internal sealed record AutocompleteDeferredUsingInsertionRequest(
	string CompletionName,
	string NamespaceName,
	string ScriptPath,
	ulong CodeEditNativeInstanceId,
	CSharpUsingInsertionPlan Plan
);

internal sealed record AutocompleteDeferredUsingInsertionExecutionContext(
	ulong CurrentCodeEditNativeInstanceId,
	string CurrentScriptPath,
	long HostInstanceToken,
	string ManagedAssemblyGeneration,
	int GuiInputCallbackDepth
);

internal sealed record AutocompleteDeferredUsingInsertionApplyResult(
	bool Succeeded,
	string FailureReason,
	ulong CurrentCodeEditNativeInstanceId,
	string CurrentScriptPath
)
{
	internal static AutocompleteDeferredUsingInsertionApplyResult Success(
		ulong currentCodeEditNativeInstanceId,
		string currentScriptPath
	)
	{
		return new AutocompleteDeferredUsingInsertionApplyResult(
			true,
			"",
			currentCodeEditNativeInstanceId,
			currentScriptPath ?? ""
		);
	}

	internal static AutocompleteDeferredUsingInsertionApplyResult Rejected(
		string reason,
		ulong currentCodeEditNativeInstanceId = 0,
		string currentScriptPath = ""
	)
	{
		return new AutocompleteDeferredUsingInsertionApplyResult(
			false,
			reason ?? "",
			currentCodeEditNativeInstanceId,
			currentScriptPath ?? ""
		);
	}
}
#endif
