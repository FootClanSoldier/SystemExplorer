#if TOOLS
using System;
using System.IO;

namespace SystemExplorer.Diagnostics;

internal static class SystemExplorerDiagnosticLogPathResolver
{
	private const string ApplicationDirectoryName = "SystemExplorer";
	private const string DiagnosticsDirectoryName = "Diagnostics";

	internal static string ResolveDiagnosticDirectory(string projectUserDataDirectory)
	{
		if (
			string.IsNullOrWhiteSpace(projectUserDataDirectory)
			|| !Path.IsPathFullyQualified(projectUserDataDirectory)
		)
		{
			throw new InvalidOperationException(
				"could not resolve the project-local Godot user-data directory."
			);
		}

		return Path.Combine(
			projectUserDataDirectory,
			ApplicationDirectoryName,
			DiagnosticsDirectoryName
		);
	}
}
#endif
