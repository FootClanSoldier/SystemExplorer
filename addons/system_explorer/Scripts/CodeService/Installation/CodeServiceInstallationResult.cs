#if TOOLS
namespace SystemExplorer.CodeService.Installation;

internal readonly struct CodeServiceInstallationResult
{
	internal CodeServiceInstallationResult(
		bool success,
		string installedVersion,
		string message
	)
	{
		Success = success;
		InstalledVersion = installedVersion ?? "";
		Message = message ?? "";
	}

	internal bool Success { get; }
	internal string InstalledVersion { get; }
	internal string Message { get; }
}
#endif
