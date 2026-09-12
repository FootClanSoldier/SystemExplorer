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
		return TryReadNoteWithViewState(
			target,
			out exists,
			out text,
			out _,
			out failureDetail
		);
	}

	internal bool TryReadNoteWithViewState(
		NoteTarget target,
		out bool exists,
		out string text,
		out NoteViewState viewState,
		out string failureDetail
	)
	{
		exists = false;
		text = "";
		viewState = null;
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

			document.FolderViewStates.TryGetValue(target.FolderPath, out viewState);
			exists = true;
			text = folderText;
			return true;
		}

		if (document.Note.Length == 0)
			return true;

		exists = true;
		text = document.Note;
		viewState = document.SystemViewState;
		return true;
	}

	internal bool TrySaveNote(
		NoteTarget target,
		string text,
		NoteViewState viewState,
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

		if (viewState == null)
		{
			failureDetail = NotePersistenceFailure.BuildFailureDetail(
				target.SystemName,
				"target-validation",
				"Detail='Non-whitespace Note save requires a view-state.'"
			);
			return false;
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
		{
			document.Folders[target.FolderPath] = text;
			document.FolderViewStates[target.FolderPath] = viewState;
		}
		else
		{
			document.Note = text;
			document.SystemViewState = viewState;
		}

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

			document.FolderViewStates.Remove(target.FolderPath);
		}
		else
		{
			if (document.Note.Length == 0)
				return true;

			document.Note = "";
			document.SystemViewState = null;
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
