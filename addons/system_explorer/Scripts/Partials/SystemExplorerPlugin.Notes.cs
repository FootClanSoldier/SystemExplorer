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

	private bool IsNoteTargetRemovedByStructure(
		string noteMetadata,
		string removedMetadata
	)
	{
		if (
			!TryResolveNoteTargetFromMetadata(noteMetadata, out NoteTarget noteTarget, out _)
			|| !TryResolveNoteTargetFromMetadata(
				removedMetadata,
				out NoteTarget removedTarget,
				out _
			)
			|| !IsCanonicalNoteTargetMetadata(noteMetadata, noteTarget)
			|| !IsCanonicalNoteTargetMetadata(removedMetadata, removedTarget)
		)
		{
			return false;
		}

		if (!removedTarget.IsFolder)
		{
			return string.Equals(
				noteTarget.SystemName,
				removedTarget.SystemName,
				StringComparison.Ordinal
			);
		}

		if (
			!noteTarget.IsFolder
			|| !string.Equals(
				noteTarget.SystemName,
				removedTarget.SystemName,
				StringComparison.Ordinal
			)
		)
		{
			return false;
		}

		return string.Equals(
				noteTarget.FolderPath,
				removedTarget.FolderPath,
				StringComparison.Ordinal
			)
			|| noteTarget.FolderPath.StartsWith(
				removedTarget.FolderPath + "/",
				StringComparison.Ordinal
			);
	}

	private bool TryGetRenamedNoteMetadata(
		string noteMetadata,
		string oldStructureMetadata,
		string newStructureMetadata,
		out string renamedNoteMetadata
	)
	{
		renamedNoteMetadata = "";

		if (
			!TryResolveNoteTargetFromMetadata(noteMetadata, out NoteTarget noteTarget, out _)
			|| !TryResolveNoteTargetFromMetadata(
				oldStructureMetadata,
				out NoteTarget oldStructureTarget,
				out _
			)
			|| !TryResolveNoteTargetFromMetadata(
				newStructureMetadata,
				out NoteTarget newStructureTarget,
				out _
			)
			|| !IsCanonicalNoteTargetMetadata(noteMetadata, noteTarget)
			|| !IsCanonicalNoteTargetMetadata(oldStructureMetadata, oldStructureTarget)
			|| !IsCanonicalNoteTargetMetadata(newStructureMetadata, newStructureTarget)
			|| oldStructureTarget.IsFolder != newStructureTarget.IsFolder
			|| string.Equals(
				oldStructureMetadata,
				newStructureMetadata,
				StringComparison.Ordinal
			)
		)
		{
			return false;
		}

		NoteTarget renamedTarget;

		if (!oldStructureTarget.IsFolder)
		{
			if (
				!string.Equals(
					noteTarget.SystemName,
					oldStructureTarget.SystemName,
					StringComparison.Ordinal
				)
			)
			{
				return false;
			}

			renamedTarget = noteTarget.IsFolder
				? new NoteTarget(newStructureTarget.SystemName, noteTarget.FolderPath)
				: new NoteTarget(newStructureTarget.SystemName);
		}
		else
		{
			if (
				!noteTarget.IsFolder
				|| !string.Equals(
					oldStructureTarget.SystemName,
					newStructureTarget.SystemName,
					StringComparison.Ordinal
				)
				|| !string.Equals(
					noteTarget.SystemName,
					oldStructureTarget.SystemName,
					StringComparison.Ordinal
				)
			)
			{
				return false;
			}

			string oldFolderPath = oldStructureTarget.FolderPath;
			string noteFolderPath = noteTarget.FolderPath;
			bool isExactFolder = string.Equals(
				noteFolderPath,
				oldFolderPath,
				StringComparison.Ordinal
			);

			if (
				!isExactFolder
				&& !noteFolderPath.StartsWith(
					oldFolderPath + "/",
					StringComparison.Ordinal
				)
			)
			{
				return false;
			}

			string renamedFolderPath = isExactFolder
				? newStructureTarget.FolderPath
				: newStructureTarget.FolderPath + noteFolderPath.Substring(oldFolderPath.Length);

			renamedTarget = new NoteTarget(
				newStructureTarget.SystemName,
				renamedFolderPath
			);
		}

		string candidateMetadata = renamedTarget.IsFolder
			? $"folder::{renamedTarget.SystemName}::{renamedTarget.FolderPath}"
			: $"system::{renamedTarget.SystemName}";

		if (
			!IsCanonicalNoteTargetMetadata(candidateMetadata, renamedTarget)
			|| string.Equals(noteMetadata, candidateMetadata, StringComparison.Ordinal)
		)
		{
			return false;
		}

		renamedNoteMetadata = candidateMetadata;
		return true;
	}

	private static bool IsCanonicalNoteTargetMetadata(string metadata, NoteTarget target)
	{
		if (
			target == null
			|| ContainsReservedSystemNameSeparator(target.SystemName)
			|| (target.IsFolder && ContainsReservedVirtualFolderSeparator(target.FolderPath))
		)
		{
			return false;
		}

		string canonicalMetadata = target.IsFolder
			? $"folder::{target.SystemName}::{target.FolderPath}"
			: $"system::{target.SystemName}";

		return string.Equals(metadata, canonicalMetadata, StringComparison.Ordinal);
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

	private bool TryReadNoteForMetadataWithViewState(
		string metadata,
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

		if (!TryResolveNoteTargetFromMetadata(metadata, out NoteTarget target, out failureDetail))
			return false;

		if (!TryGetNoteStore(out NoteStore noteStore, out failureDetail))
			return false;

		return noteStore.TryReadNoteWithViewState(
			target,
			out exists,
			out text,
			out viewState,
			out failureDetail
		);
	}

	private bool TrySaveNoteForMetadata(
		string metadata,
		string text,
		NoteViewState viewState,
		out string failureDetail
	)
	{
		failureDetail = "";

		if (!TryResolveNoteTargetFromMetadata(metadata, out NoteTarget target, out failureDetail))
			return false;

		if (!TryGetNoteStore(out NoteStore noteStore, out failureDetail))
			return false;

		return noteStore.TrySaveNote(target, text, viewState, out failureDetail);
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
