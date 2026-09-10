#if TOOLS
using System;

namespace SystemExplorer.Notes;

internal sealed class NoteStore
{
	private readonly NoteFilePersistence _filePersistence;

	internal NoteStore(string notesDirectoryPath)
	{
		_filePersistence = new NoteFilePersistence(notesDirectoryPath);
	}

	internal bool TryReadNote(
		NoteTarget target,
		out bool exists,
		out string text,
		out string failureDetail
	)
	{
		exists = false;
		text = "";
		failureDetail = "";

		if (!TryValidateTarget(target, out failureDetail))
			return false;

		if (
			!TryLoadSystemDocument(
				target.SystemName,
				out bool documentExists,
				out NoteSystemDocument document,
				out failureDetail
			)
		)
		{
			return false;
		}

		if (!documentExists)
			return true;

		if (target.IsFolder)
		{
			if (!document.Folders.TryGetValue(target.FolderPath, out string folderText))
				return true;

			exists = true;
			text = folderText;
			return true;
		}

		if (document.Note.Length == 0)
			return true;

		exists = true;
		text = document.Note;
		return true;
	}

	internal bool TrySaveNote(
		NoteTarget target,
		string text,
		out string failureDetail
	)
	{
		failureDetail = "";

		if (!TryValidateTarget(target, out failureDetail))
			return false;

		if (string.IsNullOrWhiteSpace(text))
		{
			return TryDeleteNote(
				target,
				out _,
				out failureDetail
			);
		}

		if (
			!TryLoadSystemDocument(
				target.SystemName,
				out bool documentExists,
				out NoteSystemDocument document,
				out failureDetail
			)
		)
		{
			return false;
		}

		if (!documentExists)
			document = new NoteSystemDocument(target.SystemName);

		if (target.IsFolder)
			document.Folders[target.FolderPath] = text;
		else
			document.Note = text;

		byte[] expectedContent;

		try
		{
			expectedContent = NoteDocumentCodec.SerializeDocument(document);
		}
		catch (Exception exception)
		{
			failureDetail = NotePersistenceFailure.BuildFailureDetail(
				target.SystemName,
				"serialize",
				$"Exception='{exception}'"
			);
			return false;
		}

		return _filePersistence.TryWriteSystemFile(
			target.SystemName,
			expectedContent,
			out failureDetail
		);
	}

	internal bool TryDeleteNote(
		NoteTarget target,
		out bool deleted,
		out string failureDetail
	)
	{
		deleted = false;
		failureDetail = "";

		if (!TryValidateTarget(target, out failureDetail))
			return false;

		if (
			!TryLoadSystemDocument(
				target.SystemName,
				out bool documentExists,
				out NoteSystemDocument document,
				out failureDetail
			)
		)
		{
			return false;
		}

		if (!documentExists)
			return true;

		if (target.IsFolder)
		{
			if (!document.Folders.Remove(target.FolderPath))
				return true;
		}
		else
		{
			if (document.Note.Length == 0)
				return true;

			document.Note = "";
		}

		deleted = true;

		if (document.Note.Length == 0 && document.Folders.Count == 0)
		{
			if (!_filePersistence.TryDeleteSystemFile(target.SystemName, out failureDetail))
			{
				deleted = false;
				return false;
			}

			return true;
		}

		byte[] expectedContent;

		try
		{
			expectedContent = NoteDocumentCodec.SerializeDocument(document);
		}
		catch (Exception exception)
		{
			deleted = false;
			failureDetail = NotePersistenceFailure.BuildFailureDetail(
				target.SystemName,
				"serialize-after-delete",
				$"Exception='{exception}'"
			);
			return false;
		}

		if (
			!_filePersistence.TryRewriteExistingSystemFile(
				target.SystemName,
				expectedContent,
				out failureDetail
			)
		)
		{
			deleted = false;
			return false;
		}

		return true;
	}

	private bool TryLoadSystemDocument(
		string systemName,
		out bool exists,
		out NoteSystemDocument document,
		out string failureDetail
	)
	{
		exists = false;
		document = null;
		failureDetail = "";

		if (
			!_filePersistence.TryReadSystemFile(
				systemName,
				out bool fileExists,
				out byte[] bytes,
				out failureDetail
			)
		)
		{
			return false;
		}

		if (!fileExists)
			return true;

		if (
			!NoteDocumentCodec.TryDeserializeAndValidateDocument(
				systemName,
				bytes,
				out document,
				out failureDetail
			)
		)
		{
			return false;
		}

		exists = true;
		return true;
	}

	private static bool TryValidateTarget(NoteTarget target, out string failureDetail)
	{
		failureDetail = "";

		if (target == null)
		{
			failureDetail = "Phase='target-validation', Detail='Note target was null.'";
			return false;
		}

		if (string.IsNullOrWhiteSpace(target.SystemName))
		{
			failureDetail = "Phase='target-validation', Detail='System name was blank.'";
			return false;
		}

		if (target.FolderPath == null)
		{
			failureDetail = NotePersistenceFailure.BuildFailureDetail(
				target.SystemName,
				"target-validation",
				"Detail='Folder path was null.'"
			);
			return false;
		}

		if (target.IsFolder && string.IsNullOrWhiteSpace(target.FolderPath))
		{
			failureDetail = NotePersistenceFailure.BuildFailureDetail(
				target.SystemName,
				"target-validation",
				"Detail='Folder target path was whitespace-only.'"
			);
			return false;
		}

		return true;
	}
}
#endif
