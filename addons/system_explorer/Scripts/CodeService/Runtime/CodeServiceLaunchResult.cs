#if TOOLS
namespace SystemExplorer.CodeService.Runtime;

internal readonly struct CodeServiceLaunchResult
{
	private CodeServiceLaunchResult(
		bool started,
		CodeServiceLaunchedProcess launchedProcess,
		string detail
	)
	{
		Started = started;
		LaunchedProcess = launchedProcess;
		Detail = detail ?? "";
	}

	internal bool Started { get; }
	internal CodeServiceLaunchedProcess LaunchedProcess { get; }
	internal string Detail { get; }

	internal static CodeServiceLaunchResult Success(CodeServiceLaunchedProcess launchedProcess)
	{
		return new CodeServiceLaunchResult(true, launchedProcess, "");
	}

	internal static CodeServiceLaunchResult Failure(string detail)
	{
		return new CodeServiceLaunchResult(false, null, detail);
	}
}
#endif
