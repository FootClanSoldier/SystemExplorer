#if TOOLS
using System;
using System.IO;

namespace SystemExplorer.Diagnostics;

internal static class SystemExplorerDiagnosticLogPathResolver
{
	private const string ApplicationDirectoryName = "SystemExplorer";
	private const string DiagnosticsDirectoryName = "Diagnostics";
	private const string ProducerDirectoryName = "GodotPlugin";

	internal static string ResolveDiagnosticDirectory()
	{
		if (OperatingSystem.IsWindows())
		{
			return BuildProductDiagnosticDirectory(ResolveWindowsBaseDirectory());
		}

		if (OperatingSystem.IsMacOS())
		{
			string userProfile = TryGetSpecialFolder(Environment.SpecialFolder.UserProfile);
			if (userProfile != null)
			{
				return Path.Combine(
					userProfile,
					"Library",
					"Logs",
					ApplicationDirectoryName,
					ProducerDirectoryName
				);
			}

			return BuildProductDiagnosticDirectory(ResolveApplicationDataFallback());
		}

		string xdgStateHome = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
		if (IsAbsolutePath(xdgStateHome))
			return BuildProductDiagnosticDirectory(xdgStateHome);

		string userProfileFallback = TryGetSpecialFolder(Environment.SpecialFolder.UserProfile);
		if (userProfileFallback != null)
		{
			return BuildProductDiagnosticDirectory(
				Path.Combine(userProfileFallback, ".local", "state")
			);
		}

		return BuildProductDiagnosticDirectory(ResolveApplicationDataFallback());
	}

	private static string BuildProductDiagnosticDirectory(string baseDirectory)
	{
		return Path.Combine(
			baseDirectory,
			ApplicationDirectoryName,
			DiagnosticsDirectoryName,
			ProducerDirectoryName
		);
	}

	private static bool IsAbsolutePath(string path)
	{
		return !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path);
	}

	private static string ResolveWindowsBaseDirectory()
	{
		string localApplicationData = TryGetSpecialFolder(
			Environment.SpecialFolder.LocalApplicationData
		);
		if (localApplicationData != null)
			return localApplicationData;

		string applicationData = TryGetSpecialFolder(Environment.SpecialFolder.ApplicationData);
		if (applicationData != null)
			return applicationData;

		string userProfile = TryGetSpecialFolder(Environment.SpecialFolder.UserProfile);
		if (userProfile != null)
			return Path.Combine(userProfile, "AppData", "Local");

		throw new InvalidOperationException(
			"could not resolve a per-user Windows diagnostics base directory."
		);
	}

	private static string ResolveApplicationDataFallback()
	{
		string localApplicationData = TryGetSpecialFolder(
			Environment.SpecialFolder.LocalApplicationData
		);
		if (localApplicationData != null)
			return localApplicationData;

		string applicationData = TryGetSpecialFolder(Environment.SpecialFolder.ApplicationData);
		if (applicationData != null)
			return applicationData;

		throw new InvalidOperationException(
			"could not resolve a per-user application-data diagnostics base directory."
		);
	}

	private static string TryGetSpecialFolder(Environment.SpecialFolder folder)
	{
		string path = Environment.GetFolderPath(folder);
		return IsAbsolutePath(path) ? path : null;
	}
}
#endif
