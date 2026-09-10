#if TOOLS
using Godot;
using System;
using System.IO;
using SystemExplorer.Notes;

public partial class SystemExplorerPlugin
{
	private const string NotesFolderPath = ResourcesFolderPath + "/Notes";
	private NoteStore _noteStore;

	private bool TryResolveNoteTargetFromMetadata(
		string metadata,
		out NoteTarget target,
		out string failureDetail
	)
	{
		target = null;
		failureDetail = "";

		if (string.IsNullOrEmpty(metadata))
		{
			failureDetail = "Note metadata was empty.";
			return false;
		}

		if (metadata.StartsWith("system::", StringComparison.Ordinal))
		{
			string systemName = GetSystemNameFromMetadata(metadata);
			if (string.IsNullOrWhiteSpace(systemName))
			{
				failureDetail = "System note metadata did not contain a system name.";
				return false;
			}

			target = new NoteTarget(systemName);
			return true;
		}

		if (metadata.StartsWith("folder::", StringComparison.Ordinal))
		{
			string systemName = GetSystemNameFromMetadata(metadata);
			string folderPath = GetFolderPathFromMetadata(metadata);

			if (string.IsNullOrWhiteSpace(systemName) || string.IsNullOrWhiteSpace(folderPath))
			{
				failureDetail = "Folder note metadata did not contain both system name and folder path.";
				return false;
			}

			target = new NoteTarget(systemName, folderPath);
			return true;
		}

		failureDetail = "Metadata is not a System Explorer system or folder entry.";
		return false;
	}

	private bool TryGetAbsoluteNotesDirectoryPath(
		out string absoluteNotesPath,
		out string failureDetail
	)
	{
		absoluteNotesPath = "";
		failureDetail = "";

		try
		{
			absoluteNotesPath = Path.GetFullPath(
				ProjectSettings.GlobalizePath(NotesFolderPath)
			);
			return true;
		}
		catch (Exception exception)
		{
			failureDetail =
				$"Could not compose the Notes persistence path from '{NotesFolderPath}'. Exception='{exception}'";
			return false;
		}
	}

	private bool TryGetNoteStore(out NoteStore noteStore, out string failureDetail)
	{
		noteStore = _noteStore;
		failureDetail = "";

		if (noteStore != null)
			return true;

		if (!TryGetAbsoluteNotesDirectoryPath(out string absoluteNotesPath, out failureDetail))
			return false;

		try
		{
			_noteStore = new NoteStore(absoluteNotesPath);
			noteStore = _noteStore;
			return true;
		}
		catch (Exception exception)
		{
			failureDetail =
				$"Could not compose the Notes persistence path from '{NotesFolderPath}'. Exception='{exception}'";
			return false;
		}
	}

	private bool TryReadNoteForMetadata(
		string metadata,
		out bool exists,
		out string text,
		out string failureDetail
	)
	{
		exists = false;
		text = "";
		failureDetail = "";

		if (!TryResolveNoteTargetFromMetadata(metadata, out NoteTarget target, out failureDetail))
			return false;

		if (!TryGetNoteStore(out NoteStore noteStore, out failureDetail))
			return false;

		return noteStore.TryReadNote(target, out exists, out text, out failureDetail);
	}

	private bool TrySaveNoteForMetadata(
		string metadata,
		string text,
		out string failureDetail
	)
	{
		failureDetail = "";

		if (!TryResolveNoteTargetFromMetadata(metadata, out NoteTarget target, out failureDetail))
			return false;

		if (!TryGetNoteStore(out NoteStore noteStore, out failureDetail))
			return false;

		return noteStore.TrySaveNote(target, text, out failureDetail);
	}

	private bool TryDeleteNoteForMetadata(
		string metadata,
		out bool deleted,
		out string failureDetail
	)
	{
		deleted = false;
		failureDetail = "";

		if (!TryResolveNoteTargetFromMetadata(metadata, out NoteTarget target, out failureDetail))
			return false;

		if (!TryGetNoteStore(out NoteStore noteStore, out failureDetail))
			return false;

		return noteStore.TryDeleteNote(target, out deleted, out failureDetail);
	}
}
#endif
