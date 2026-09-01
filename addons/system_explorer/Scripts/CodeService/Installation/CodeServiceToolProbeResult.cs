#if TOOLS
namespace SystemExplorer.CodeService.Installation;

internal enum CodeServiceToolProbeStatus
{
	NotFound,
	RequiredVersionVerified,
	VersionMismatch,
	Failed,
	TimedOut,
}

internal readonly struct CodeServiceToolProbeResult
{
	internal CodeServiceToolProbeResult(
		CodeServiceToolProbeStatus status,
		string installedVersion,
		string detail = ""
	)
	{
		Status = status;
		InstalledVersion = installedVersion ?? "";
		Detail = detail ?? "";
	}

	internal CodeServiceToolProbeStatus Status { get; }
	internal string InstalledVersion { get; }
	internal string Detail { get; }
	internal bool RequiredVersionVerified =>
		Status == CodeServiceToolProbeStatus.RequiredVersionVerified;
}
#endif
