#if TOOLS
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace SystemExplorer.Notes;

internal sealed class NoteFilePersistence
{
	private static readonly UTF8Encoding Utf8NoBom = new(false, true);

	private readonly string _notesDirectoryPath;

	internal NoteFilePersistence(string notesDirectoryPath)
	{
		if (string.IsNullOrWhiteSpace(notesDirectoryPath))
			throw new ArgumentException("Notes directory path must not be blank.", nameof(notesDirectoryPath));

		if (!Path.IsPathFullyQualified(notesDirectoryPath))
			throw new ArgumentException("Notes directory path must be absolute.", nameof(notesDirectoryPath));

		_notesDirectoryPath = notesDirectoryPath;
	}

	internal bool TryReadSystemFile(
		string systemName,
		out bool exists,
		out byte[] bytes,
		out string failureDetail
	)
	{
		exists = false;
		bytes = null;
		failureDetail = "";

		if (!TryGetSystemFilePath(systemName, out string targetPath, out failureDetail))
			return false;

		bool fileExists;

		try
		{
			fileExists = File.Exists(targetPath);
		}
		catch (Exception exception)
		{
			failureDetail = NotePersistenceFailure.BuildFailureDetail(
				systemName,
				"existence-check",
				$"Path='{targetPath}', Exception='{exception}'"
			);
			return false;
		}

		if (!fileExists)
			return true;

		try
		{
			bytes = File.ReadAllBytes(targetPath);
		}
		catch (Exception exception)
		{
			failureDetail = NotePersistenceFailure.BuildFailureDetail(
				systemName,
				"read",
				$"Path='{targetPath}', Exception='{exception}'"
			);
			return false;
		}

		exists = true;
		return true;
	}

	internal bool TryWriteSystemFile(
		string systemName,
		byte[] expectedContent,
		out string failureDetail
	)
	{
		failureDetail = "";

		try
		{
			Directory.CreateDirectory(_notesDirectoryPath);
		}
		catch (Exception exception)
		{
			failureDetail = NotePersistenceFailure.BuildFailureDetail(
				systemName,
				"directory-create",
				$"Directory='{_notesDirectoryPath}', Exception='{exception}'"
			);
			return false;
		}

		if (!TryGetSystemFilePath(systemName, out string targetPath, out failureDetail))
			return false;

		return TryWriteVerifiedFile(
			systemName,
			targetPath,
			expectedContent,
			out failureDetail
		);
	}

	internal bool TryRewriteExistingSystemFile(
		string systemName,
		byte[] expectedContent,
		out string failureDetail
	)
	{
		failureDetail = "";

		if (!TryGetSystemFilePath(systemName, out string targetPath, out failureDetail))
			return false;

		return TryWriteVerifiedFile(
			systemName,
			targetPath,
			expectedContent,
			out failureDetail
		);
	}

	internal bool TryDeleteSystemFile(
		string systemName,
		out string failureDetail
	)
	{
		failureDetail = "";

		if (!TryGetSystemFilePath(systemName, out string targetPath, out failureDetail))
			return false;

		try
		{
			File.Delete(targetPath);
		}
		catch (Exception exception)
		{
			failureDetail = NotePersistenceFailure.BuildFailureDetail(
				systemName,
				"delete-file",
				$"Path='{targetPath}', Exception='{exception}'"
			);
			return false;
		}

		try
		{
			if (!File.Exists(targetPath))
				return true;

			failureDetail = NotePersistenceFailure.BuildFailureDetail(
				systemName,
				"delete-verification",
				$"Path='{targetPath}', Detail='File still exists after deletion.'"
			);
			return false;
		}
		catch (Exception exception)
		{
			failureDetail = NotePersistenceFailure.BuildFailureDetail(
				systemName,
				"delete-verification",
				$"Path='{targetPath}', Exception='{exception}'"
			);
			return false;
		}
	}

	private bool TryWriteVerifiedFile(
		string systemName,
		string targetPath,
		byte[] expectedContent,
		out string failureDetail
	)
	{
		failureDetail = "";
		string stagingPath = "";
		string backupPath = "";
		bool previousTargetExisted;
		byte[] previousContent = null;

		try
		{
			previousTargetExisted = File.Exists(targetPath);
			if (previousTargetExisted)
				previousContent = File.ReadAllBytes(targetPath);
		}
		catch (Exception exception)
		{
			failureDetail = NotePersistenceFailure.BuildFailureDetail(
				systemName,
				"pre-commit-read",
				$"Path='{targetPath}', Exception='{exception}'"
			);
			return false;
		}

		try
		{
			for (int attempt = 0; attempt < 16; attempt++)
			{
				string uniqueId = Guid.NewGuid().ToString("N");
				string candidateStagingPath = targetPath + "." + uniqueId + ".tmp";
				string candidateBackupPath = targetPath + "." + uniqueId + ".bak";

				if (File.Exists(candidateStagingPath) || File.Exists(candidateBackupPath))
					continue;

				stagingPath = candidateStagingPath;
				backupPath = candidateBackupPath;
				break;
			}

			if (stagingPath.Length == 0 || backupPath.Length == 0)
			{
				failureDetail = NotePersistenceFailure.BuildFailureDetail(
					systemName,
					"staging-path-allocation",
					$"Path='{targetPath}', Detail='Could not allocate unique sibling staging paths.'"
				);
				return false;
			}

			using (
				FileStream stream = new(
					stagingPath,
					FileMode.CreateNew,
					FileAccess.Write,
					FileShare.None
				)
			)
			{
				stream.Write(expectedContent, 0, expectedContent.Length);
				stream.Flush(true);
			}

			if (!TryVerifyExactContent(stagingPath, expectedContent, out string stagingVerificationDetail))
			{
				failureDetail = NotePersistenceFailure.BuildFailureDetail(
					systemName,
					"staging-verification",
					stagingVerificationDetail
				);
				return false;
			}

			try
			{
				if (previousTargetExisted)
					File.Replace(stagingPath, targetPath, backupPath);
				else
					File.Move(stagingPath, targetPath);
			}
			catch (Exception commitException)
			{
				if (TryVerifyExactContent(targetPath, expectedContent, out _))
				{
					CleanupFileBestEffort(stagingPath);
					CleanupFileBestEffort(backupPath);
					return true;
				}

				if (
					previousTargetExisted
					&& previousContent != null
					&& TryVerifyExactContent(targetPath, previousContent, out _)
				)
				{
					failureDetail = NotePersistenceFailure.BuildFailureDetail(
						systemName,
						"commit",
						$"Path='{targetPath}', Exception='{commitException}', Detail='Previous target content remained intact.'"
					);
					return false;
				}

				if (!previousTargetExisted && !File.Exists(targetPath))
				{
					failureDetail = NotePersistenceFailure.BuildFailureDetail(
						systemName,
						"commit",
						$"Path='{targetPath}', Exception='{commitException}', Detail='Target remained absent.'"
					);
					return false;
				}

				failureDetail = NotePersistenceFailure.BuildFailureDetail(
					systemName,
					"commit",
					$"Path='{targetPath}', BackupPath='{backupPath}', Exception='{commitException}', Detail='Final target state could not be verified as the new or previous content.'"
				);
				return false;
			}

			if (!TryVerifyExactContent(targetPath, expectedContent, out string finalVerificationDetail))
			{
				failureDetail = NotePersistenceFailure.BuildFailureDetail(
					systemName,
					"final-verification",
					finalVerificationDetail
				);
				return false;
			}

			CleanupFileBestEffort(stagingPath);
			CleanupFileBestEffort(backupPath);
			return true;
		}
		catch (Exception exception)
		{
			failureDetail = NotePersistenceFailure.BuildFailureDetail(
				systemName,
				"verified-write",
				$"Path='{targetPath}', StagingPath='{stagingPath}', BackupPath='{backupPath}', Exception='{exception}'"
			);
			return false;
		}
		finally
		{
			CleanupFileBestEffort(stagingPath);

			if (
				backupPath.Length != 0
				&& TryVerifyExactContent(targetPath, expectedContent, out _)
			)
			{
				CleanupFileBestEffort(backupPath);
			}
		}
	}

	private static bool TryVerifyExactContent(
		string path,
		byte[] expectedContent,
		out string detail
	)
	{
		detail = "";

		if (string.IsNullOrEmpty(path))
		{
			detail = "Detail='Path was empty.'";
			return false;
		}

		try
		{
			if (!File.Exists(path))
			{
				detail = $"Path='{path}', Detail='File does not exist.'";
				return false;
			}

			byte[] actualContent = File.ReadAllBytes(path);
			if (!ByteArraysMatch(actualContent, expectedContent))
			{
				detail = $"Path='{path}', Detail='File content did not match the expected serialized bytes.'";
				return false;
			}

			return true;
		}
		catch (Exception exception)
		{
			detail = $"Path='{path}', Exception='{exception}'";
			return false;
		}
	}

	private static bool ByteArraysMatch(byte[] left, byte[] right)
	{
		if (ReferenceEquals(left, right))
			return true;

		if (left == null || right == null || left.Length != right.Length)
			return false;

		for (int index = 0; index < left.Length; index++)
		{
			if (left[index] != right[index])
				return false;
		}

		return true;
	}

	private static void CleanupFileBestEffort(string path)
	{
		if (string.IsNullOrEmpty(path))
			return;

		try
		{
			if (File.Exists(path))
				File.Delete(path);
		}
		catch
		{
			// Best-effort cleanup only. The caller's operation result is already decided.
		}
	}

	private bool TryGetSystemFilePath(
		string systemName,
		out string targetPath,
		out string failureDetail
	)
	{
		targetPath = "";
		failureDetail = "";

		try
		{
			targetPath = GetSystemFilePath(systemName);
			return true;
		}
		catch (Exception exception)
		{
			failureDetail = NotePersistenceFailure.BuildFailureDetail(
				systemName,
				"path-composition",
				$"Exception='{exception}'"
			);
			return false;
		}
	}

	private string GetSystemFilePath(string systemName)
	{
		byte[] nameBytes = Utf8NoBom.GetBytes(systemName);
		byte[] hash;

		using (SHA256 sha256 = SHA256.Create())
			hash = sha256.ComputeHash(nameBytes);

		StringBuilder fileName = new(hash.Length * 2 + ".json".Length);
		foreach (byte value in hash)
			fileName.Append(value.ToString("x2"));

		fileName.Append(".json");
		return Path.Combine(_notesDirectoryPath, fileName.ToString());
	}
}
#endif
