#if TOOLS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using SystemExplorer.EditorIntegration.ScriptEditing;

namespace SystemExplorer.Autocomplete.Indexing;

internal sealed class CSharpProjectFileInventory
{
	private static readonly HashSet<string> ExcludedDirectoryNames = new(
		new[] { ".godot", ".git", ".vs", "bin", "obj" },
		StringComparer.OrdinalIgnoreCase
	);

	internal bool TryCreate(
		string globalProjectRoot,
		CancellationToken cancellationToken,
		out IReadOnlyList<CSharpProjectFileDescriptor> files,
		out string failureDetail
	)
	{
		files = Array.Empty<CSharpProjectFileDescriptor>();
		failureDetail = "";

		if (!TryNormalizeRoot(globalProjectRoot, out string projectRoot, out failureDetail))
			return false;

		try
		{
			cancellationToken.ThrowIfCancellationRequested();

			var rootInfo = new DirectoryInfo(projectRoot);
			if (!rootInfo.Exists)
			{
				failureDetail = $"Project root does not exist: '{projectRoot}'.";
				return false;
			}

			if ((rootInfo.Attributes & FileAttributes.ReparsePoint) != 0)
			{
				failureDetail = "Project root is a reparse point or symbolic directory link.";
				return false;
			}

			string excludedPluginDirectory = Path.GetFullPath(
				Path.Combine(projectRoot, "addons", "system_explorer")
			);
			var discoveredFiles = new List<CSharpProjectFileDescriptor>();
			var pendingDirectories = new Stack<string>();
			pendingDirectories.Push(projectRoot);

			while (pendingDirectories.Count > 0)
			{
				cancellationToken.ThrowIfCancellationRequested();
				string currentDirectory = pendingDirectories.Pop();

				foreach (string filePath in Directory.GetFiles(currentDirectory))
				{
					cancellationToken.ThrowIfCancellationRequested();

					if (!filePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
						continue;

					discoveredFiles.Add(CreateDescriptor(projectRoot, filePath));
				}

				foreach (string directoryPath in Directory.GetDirectories(currentDirectory))
				{
					cancellationToken.ThrowIfCancellationRequested();

					string directoryName = Path.GetFileName(directoryPath);
					if (ExcludedDirectoryNames.Contains(directoryName))
						continue;

					string normalizedDirectoryPath = Path.GetFullPath(directoryPath);
					if (
						string.Equals(
							normalizedDirectoryPath,
							excludedPluginDirectory,
							StringComparison.OrdinalIgnoreCase
						)
					)
					{
						continue;
					}

					var directoryInfo = new DirectoryInfo(normalizedDirectoryPath);
					if ((directoryInfo.Attributes & FileAttributes.ReparsePoint) != 0)
						continue;

					pendingDirectories.Push(normalizedDirectoryPath);
				}
			}

			CSharpProjectFileDescriptor[] stableFiles = discoveredFiles
				.OrderBy(file => file.ResourcePath, StringComparer.OrdinalIgnoreCase)
				.ThenBy(file => file.ResourcePath, StringComparer.Ordinal)
				.GroupBy(file => file.ResourcePath, StringComparer.OrdinalIgnoreCase)
				.Select(group => group.First())
				.ToArray();

			files = stableFiles;
			return true;
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception exception) when (IsExpectedInventoryException(exception))
		{
			failureDetail = CreateFailureDetail("Project inventory failed", exception);
			return false;
		}
		catch (Exception exception)
		{
			failureDetail = CreateFailureDetail(
				"Project inventory failed unexpectedly",
				exception
			);
			return false;
		}
	}

	private static bool IsExpectedInventoryException(Exception exception)
	{
		return exception is UnauthorizedAccessException
			|| exception is DirectoryNotFoundException
			|| exception is DriveNotFoundException
			|| exception is IOException
			|| exception is NotSupportedException
			|| exception is ArgumentException;
	}

	private static bool TryNormalizeRoot(
		string globalProjectRoot,
		out string projectRoot,
		out string failureDetail
	)
	{
		projectRoot = "";
		failureDetail = "";

		if (string.IsNullOrWhiteSpace(globalProjectRoot))
		{
			failureDetail = "Global project root is empty.";
			return false;
		}

		try
		{
			projectRoot = Path.GetFullPath(globalProjectRoot.Trim());

			string pathRoot = Path.GetPathRoot(projectRoot) ?? "";
			if (!string.Equals(projectRoot, pathRoot, StringComparison.OrdinalIgnoreCase))
			{
				projectRoot = projectRoot.TrimEnd(
					Path.DirectorySeparatorChar,
					Path.AltDirectorySeparatorChar
				);
			}

			return !string.IsNullOrWhiteSpace(projectRoot);
		}
		catch (Exception exception) when (
			exception is ArgumentException
			or NotSupportedException
			or PathTooLongException
		)
		{
			failureDetail = CreateFailureDetail("Global project root is invalid", exception);
			return false;
		}
	}

	private static CSharpProjectFileDescriptor CreateDescriptor(
		string projectRoot,
		string filePath
	)
	{
		string globalPath = Path.GetFullPath(filePath);
		string relativePath = Path.GetRelativePath(projectRoot, globalPath);

		if (
			relativePath.Equals("..", StringComparison.Ordinal)
			|| relativePath.StartsWith(
				".." + Path.DirectorySeparatorChar,
				StringComparison.Ordinal
			)
			|| relativePath.StartsWith(
				".." + Path.AltDirectorySeparatorChar,
				StringComparison.Ordinal
			)
		)
		{
			throw new IOException("A discovered C# file resolved outside the project root.");
		}

		string resourcePath = ScriptPathUtility.Normalize(
			"res://" + relativePath.Replace('\\', '/')
		);
		var fileInfo = new FileInfo(globalPath);

		return new CSharpProjectFileDescriptor(
			resourcePath,
			globalPath,
			fileInfo.Length,
			fileInfo.LastWriteTimeUtc.Ticks
		);
	}

	private static string CreateFailureDetail(string prefix, Exception exception)
	{
		string message = exception?.Message ?? "Unknown error.";
		message = message.Replace('\r', ' ').Replace('\n', ' ').Trim();
		if (message.Length > 400)
			message = message.Substring(0, 400);

		return $"{prefix}: {exception?.GetType().Name ?? "Exception"}: {message}";
	}
}
#endif
