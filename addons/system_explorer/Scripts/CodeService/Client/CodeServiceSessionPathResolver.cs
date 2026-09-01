#if TOOLS
using System;
using System.IO;
using SystemExplorer.CodeService.Runtime;

namespace SystemExplorer.CodeService.Client;

internal static class CodeServiceSessionPathResolver
{
	private const string ApplicationDirectoryName = "SystemExplorer";
	private const string ServiceDirectoryName = "CodeService";
	private const string SessionsDirectoryName = "Sessions";
	private const string DescriptorDirectoryName = "Descriptors";

	internal static string ResolveDescriptorPath(CodeServiceProcessIdentity ownerIdentity)
	{
		return Path.Combine(
			ResolveDescriptorDirectory(),
			$"owner_{ownerIdentity.ProcessId}_{ownerIdentity.StartTimeUtcTicks}.json"
		);
	}

	internal static string ResolveDescriptorDirectory()
	{
		return Path.Combine(ResolveSessionsDirectory(), DescriptorDirectoryName);
	}

	private static string ResolveSessionsDirectory()
	{
		if (OperatingSystem.IsWindows())
		{
			return Path.Combine(
				ResolveWindowsBaseDirectory(),
				ApplicationDirectoryName,
				ServiceDirectoryName,
				SessionsDirectoryName
			);
		}

		if (OperatingSystem.IsMacOS())
		{
			string userProfile = TryGetSpecialFolder(Environment.SpecialFolder.UserProfile);
			if (userProfile != null)
			{
				return Path.Combine(
					userProfile,
					"Library",
					"Application Support",
					ApplicationDirectoryName,
					ServiceDirectoryName,
					SessionsDirectoryName
				);
			}

			return Path.Combine(
				ResolveApplicationDataFallback(),
				ApplicationDirectoryName,
				ServiceDirectoryName,
				SessionsDirectoryName
			);
		}

		string xdgRuntimeDirectory = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
		if (IsAbsolutePath(xdgRuntimeDirectory))
		{
			return Path.Combine(
				xdgRuntimeDirectory,
				ApplicationDirectoryName,
				ServiceDirectoryName,
				SessionsDirectoryName
			);
		}

		string xdgStateHome = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
		if (IsAbsolutePath(xdgStateHome))
		{
			return Path.Combine(
				xdgStateHome,
				ApplicationDirectoryName,
				ServiceDirectoryName,
				SessionsDirectoryName
			);
		}

		string unixUserProfile = TryGetSpecialFolder(Environment.SpecialFolder.UserProfile);
		if (unixUserProfile != null)
		{
			return Path.Combine(
				unixUserProfile,
				".local",
				"state",
				ApplicationDirectoryName,
				ServiceDirectoryName,
				SessionsDirectoryName
			);
		}

		return Path.Combine(
			ResolveApplicationDataFallback(),
			ApplicationDirectoryName,
			ServiceDirectoryName,
			SessionsDirectoryName
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
			"could not resolve a per-user Windows application-data directory."
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
			"could not resolve a per-user application-data directory."
		);
	}

	private static string TryGetSpecialFolder(Environment.SpecialFolder folder)
	{
		string path = Environment.GetFolderPath(folder);
		return string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)
			? null
			: path;
	}
}
#endif
