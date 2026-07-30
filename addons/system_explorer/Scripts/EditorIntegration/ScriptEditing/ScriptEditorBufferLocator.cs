#if TOOLS
using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

namespace SystemExplorer.EditorIntegration.ScriptEditing;

internal sealed class ScriptEditorBufferLocator
{
	private sealed class OpenScriptPathInventoryEntry
	{
		internal OpenScriptPathInventoryEntry(string path, int firstOpenOrder)
		{
			Path = path;
			FirstOpenOrder = firstOpenOrder;
		}

		internal string Path { get; }
		internal int FirstOpenOrder { get; }
		internal int OpenOccurrenceCount { get; set; }
	}

	private readonly Func<string, string> _normalizePath;
	private readonly Func<string, ScriptTextFileReadResult> _readTextFile;
	private readonly Func<string, string, bool> _scriptTextsMatchForDiskVerification;

	internal ScriptEditorBufferLocator(
		Func<string, string> normalizePath,
		Func<string, ScriptTextFileReadResult> readTextFile,
		Func<string, string, bool> scriptTextsMatchForDiskVerification
	)
	{
		_normalizePath = normalizePath ?? throw new ArgumentNullException(nameof(normalizePath));
		_readTextFile = readTextFile ?? throw new ArgumentNullException(nameof(readTextFile));
		_scriptTextsMatchForDiskVerification =
			scriptTextsMatchForDiskVerification
			?? throw new ArgumentNullException(nameof(scriptTextsMatchForDiskVerification));
	}

	internal bool TryLocateCapturedEditorWithoutActivation(
		ScriptEditor scriptEditor,
		string scriptPath,
		string capturedScriptPath,
		Script capturedScript,
		ScriptEditorBase capturedScriptEditorBase,
		TextEdit capturedTextEditor,
		out ScriptEditorBufferLookupResult result
	)
	{
		result = new ScriptEditorBufferLookupResult(
			new Dictionary<string, OpenScriptEditorBuffer>(StringComparer.OrdinalIgnoreCase)
		);

		try
		{
			string normalizedScriptPath = _normalizePath(scriptPath);
			string normalizedCapturedPath = _normalizePath(capturedScriptPath);

			if (
				string.IsNullOrWhiteSpace(normalizedScriptPath)
				|| !normalizedScriptPath.Equals(
					normalizedCapturedPath,
					StringComparison.OrdinalIgnoreCase
				)
				|| scriptEditor == null
				|| !GodotObject.IsInstanceValid(scriptEditor)
				|| capturedScript == null
				|| !GodotObject.IsInstanceValid(capturedScript)
				|| capturedScriptEditorBase == null
				|| !GodotObject.IsInstanceValid(capturedScriptEditorBase)
				|| capturedTextEditor == null
				|| !GodotObject.IsInstanceValid(capturedTextEditor)
				|| capturedTextEditor.IsQueuedForDeletion()
			)
			{
				return false;
			}

			Control currentBaseEditor = capturedScriptEditorBase.GetBaseEditor();
			if (
				currentBaseEditor is not TextEdit currentTextEditor
				|| !GodotObject.IsInstanceValid(currentTextEditor)
				|| currentTextEditor.IsQueuedForDeletion()
				|| currentTextEditor.GetInstanceId() != capturedTextEditor.GetInstanceId()
			)
			{
				return false;
			}

			string currentCapturedScriptPath = _normalizePath(capturedScript.ResourcePath);
			if (
				!normalizedScriptPath.Equals(
					currentCapturedScriptPath,
					StringComparison.OrdinalIgnoreCase
				)
			)
			{
				return false;
			}

			HashSet<Script> matchingScriptInstances = new();
			foreach (Script openScript in scriptEditor.GetOpenScripts())
			{
				if (
					openScript == null
					|| !GodotObject.IsInstanceValid(openScript)
					|| !normalizedScriptPath.Equals(
						_normalizePath(openScript.ResourcePath),
						StringComparison.OrdinalIgnoreCase
					)
				)
				{
					continue;
				}

				matchingScriptInstances.Add(openScript);
			}

			if (
				matchingScriptInstances.Count != 1
				|| !matchingScriptInstances.Contains(capturedScript)
			)
			{
				return false;
			}

			Dictionary<string, OpenScriptEditorBuffer> openEditorsByPath = new(
				StringComparer.OrdinalIgnoreCase
			)
			{
				[normalizedScriptPath] = new OpenScriptEditorBuffer(
					normalizedScriptPath,
					capturedTextEditor
				),
			};
			result = new ScriptEditorBufferLookupResult(openEditorsByPath);
			return true;
		}
		catch
		{
			return false;
		}
	}

	// Compatibility surface for Beautify. Refactor Namespace uses the group-oriented methods.
	internal ScriptEditorBufferLookupResult LocateByScriptTextsWithoutActivation(
		ScriptEditor scriptEditor,
		Dictionary<string, string> originalTextsByPath,
		Dictionary<string, string> updatedTextsByPath
	)
	{
		ScriptEditorBufferGroupLookupResult groupLookupResult =
			LocateOpenScriptEditorGroupsByScriptTextsWithoutActivation(
				scriptEditor,
				originalTextsByPath,
				updatedTextsByPath
			);
		Dictionary<string, OpenScriptEditorBuffer> singleEditorsByPath = new(
			StringComparer.OrdinalIgnoreCase
		);
		List<string> unsafeOpenScriptPaths = new(groupLookupResult.UnsafeOpenScriptPaths);
		HashSet<string> unsafePathSet = new(
			unsafeOpenScriptPaths,
			StringComparer.OrdinalIgnoreCase
		);

		foreach (
			KeyValuePair<string, OpenScriptEditorBufferGroup> groupPair
			in groupLookupResult.OpenEditorGroupsByPath
		)
		{
			if (groupPair.Value.Buffers.Count == 1)
			{
				singleEditorsByPath[groupPair.Key] = groupPair.Value.Buffers[0];
				continue;
			}

			if (unsafePathSet.Add(groupPair.Key))
				unsafeOpenScriptPaths.Add(groupPair.Key);
		}

		return new ScriptEditorBufferLookupResult(
			singleEditorsByPath,
			unsafeOpenScriptPaths: unsafeOpenScriptPaths
		);
	}

	internal ScriptEditorBufferGroupLookupResult LocateOpenScriptEditorGroupsWithoutActivation(
		ScriptEditor scriptEditor,
		IEnumerable<string> targetPaths,
		IEnumerable<string> requiredPaths = null,
		ScriptEditorBufferDiagnosticSink diagnostics = null
	)
	{
		List<string> canonicalTargetPaths = NormalizeScriptPathsInStableOrder(targetPaths);
		Dictionary<string, OpenScriptEditorBufferGroup> emptyResult = new(
			StringComparer.OrdinalIgnoreCase
		);
		diagnostics?.Log(
			"BufferLookup",
			() =>
			{
				List<string> diagnosticRequiredPaths = NormalizeScriptPathsInStableOrder(requiredPaths);
				return $"Lookup started; VerificationSource=Disk; CandidateCount={canonicalTargetPaths.Count}; RequiredCount={diagnosticRequiredPaths.Count}; Candidates={FormatPaths(canonicalTargetPaths)}; Required={FormatPaths(diagnosticRequiredPaths)}";
			}
		);

		if (scriptEditor == null || canonicalTargetPaths.Count == 0)
		{
			diagnostics?.Log(
				"BufferLookup",
				() => $"Lookup completed without scanning; ScriptEditorNull={scriptEditor == null}; CandidateCount={canonicalTargetPaths.Count}"
			);
			return new ScriptEditorBufferGroupLookupResult(emptyResult);
		}

		Dictionary<string, string> canonicalPathByLookup = canonicalTargetPaths.ToDictionary(
			path => path,
			path => path,
			StringComparer.OrdinalIgnoreCase
		);
		List<OpenScriptPathInventoryEntry> openInventory = BuildOpenTargetScriptInventory(
			scriptEditor,
			canonicalPathByLookup,
			out int openScriptCount
		);

		diagnostics?.Log(
			"BufferLookup",
			() =>
				$"Open script inventory built; GetOpenScriptsCount={openScriptCount}; RelevantPathCount={openInventory.Count}; Occurrences={FormatInventory(openInventory)}"
		);

		if (openInventory.Count == 0)
		{
			diagnostics?.Log("BufferLookup", () => "Lookup completed; no relevant open script occurrences were found.");
			return new ScriptEditorBufferGroupLookupResult(emptyResult);
		}

		HashSet<string> canonicalRequiredPaths = NormalizeScriptPathSet(requiredPaths);
		Dictionary<string, string> diskTextsByPath = new(StringComparer.OrdinalIgnoreCase);

		foreach (OpenScriptPathInventoryEntry inventoryEntry in openInventory)
		{
			string path = inventoryEntry.Path;

			if (!path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
				continue;

			ScriptTextFileReadResult readResult;

			try
			{
				readResult = _readTextFile(path);
			}
			catch (Exception exception)
			{
				readResult = ScriptTextFileReadResult.Failed(
					ScriptTextFileReadStatus.ReadFailed,
					$"Read delegate threw {exception.GetType().Name}: {NormalizeDiagnosticDetail(exception.Message)}"
				);
			}

			if (!readResult.IsSuccess)
			{
				diagnostics?.Log(
					"BufferLookup",
					() =>
						$"Disk verification read failed; Path='{path}'; Status={readResult.Status}; FailureDetail='{NormalizeDiagnosticDetail(readResult.FailureDetail)}'"
				);
				continue;
			}

			diskTextsByPath[path] = readResult.Text;
		}

		HashSet<string> ambiguousVerificationPaths = GetPathsWithAmbiguousVerificationTexts(
			openInventory.Select(entry => entry.Path),
			path => diskTextsByPath.TryGetValue(path, out string diskText)
				? new[] { diskText }
				: Array.Empty<string>()
		);

		return BuildCompleteOpenEditorGroups(
			scriptEditor,
			openInventory,
			canonicalPathByLookup,
			canonicalRequiredPaths,
			ambiguousVerificationPaths,
			(textEditor, path) =>
				diskTextsByPath.TryGetValue(path, out string diskText)
				&& _scriptTextsMatchForDiskVerification(textEditor.Text ?? "", diskText),
			diagnostics
		);
	}

	internal ScriptEditorBufferGroupLookupResult LocateOpenScriptEditorGroupsByScriptTextsWithoutActivation(
		ScriptEditor scriptEditor,
		IReadOnlyDictionary<string, string> originalTextsByPath,
		IReadOnlyDictionary<string, string> updatedTextsByPath,
		IEnumerable<string> requiredPaths = null,
		ScriptEditorBufferDiagnosticSink diagnostics = null
	)
	{
		List<string> canonicalTargetPaths = NormalizeScriptPathsInStableOrder(
			updatedTextsByPath?.Keys
		);
		Dictionary<string, OpenScriptEditorBufferGroup> emptyResult = new(
			StringComparer.OrdinalIgnoreCase
		);
		diagnostics?.Log(
			"BufferLookup",
			() =>
			{
				List<string> diagnosticRequiredPaths = NormalizeScriptPathsInStableOrder(requiredPaths);
				return $"Lookup started; VerificationSource=OriginalOrUpdatedText; CandidateCount={canonicalTargetPaths.Count}; RequiredCount={diagnosticRequiredPaths.Count}; OriginalTextCount={originalTextsByPath?.Count ?? 0}; UpdatedTextCount={updatedTextsByPath?.Count ?? 0}; Candidates={FormatPaths(canonicalTargetPaths)}; Required={FormatPaths(diagnosticRequiredPaths)}";
			}
		);

		if (
			scriptEditor == null
			|| originalTextsByPath == null
			|| updatedTextsByPath == null
			|| canonicalTargetPaths.Count == 0
		)
		{
			diagnostics?.Log(
				"BufferLookup",
				() => $"Lookup completed without scanning; ScriptEditorNull={scriptEditor == null}; OriginalTextsNull={originalTextsByPath == null}; UpdatedTextsNull={updatedTextsByPath == null}; CandidateCount={canonicalTargetPaths.Count}"
			);
			return new ScriptEditorBufferGroupLookupResult(emptyResult);
		}

		Dictionary<string, string> canonicalPathByLookup = canonicalTargetPaths.ToDictionary(
			path => path,
			path => path,
			StringComparer.OrdinalIgnoreCase
		);
		List<OpenScriptPathInventoryEntry> openInventory = BuildOpenTargetScriptInventory(
			scriptEditor,
			canonicalPathByLookup,
			out int openScriptCount
		);

		diagnostics?.Log(
			"BufferLookup",
			() =>
				$"Open script inventory built; GetOpenScriptsCount={openScriptCount}; RelevantPathCount={openInventory.Count}; Occurrences={FormatInventory(openInventory)}"
		);

		if (openInventory.Count == 0)
		{
			diagnostics?.Log("BufferLookup", () => "Lookup completed; no relevant open script occurrences were found.");
			return new ScriptEditorBufferGroupLookupResult(emptyResult);
		}

		HashSet<string> canonicalRequiredPaths = NormalizeScriptPathSet(requiredPaths);
		Dictionary<string, string> canonicalOriginalTexts = CreateCanonicalTextDictionary(
			originalTextsByPath,
			canonicalPathByLookup
		);
		Dictionary<string, string> canonicalUpdatedTexts = CreateCanonicalTextDictionary(
			updatedTextsByPath,
			canonicalPathByLookup
		);
		HashSet<string> ambiguousVerificationPaths = GetPathsWithAmbiguousVerificationTexts(
			openInventory.Select(entry => entry.Path),
			path => GetVerificationTexts(
				path,
				canonicalOriginalTexts,
				canonicalUpdatedTexts
			)
		);

		return BuildCompleteOpenEditorGroups(
			scriptEditor,
			openInventory,
			canonicalPathByLookup,
			canonicalRequiredPaths,
			ambiguousVerificationPaths,
			(textEditor, path) => TextEditorMatchesScriptTexts(
				textEditor,
				path,
				canonicalOriginalTexts,
				canonicalUpdatedTexts
			),
			diagnostics
		);
	}

	private ScriptEditorBufferGroupLookupResult BuildCompleteOpenEditorGroups(
		ScriptEditor scriptEditor,
		IReadOnlyList<OpenScriptPathInventoryEntry> openInventory,
		IReadOnlyDictionary<string, string> canonicalPathByLookup,
		HashSet<string> requiredPaths,
		HashSet<string> ambiguousVerificationPaths,
		Func<TextEdit, string, bool> textEditorMatchesPath,
		ScriptEditorBufferDiagnosticSink diagnostics
	)
	{
		Dictionary<string, List<OpenScriptEditorBuffer>> provisionalMembersByPath = new(
			StringComparer.OrdinalIgnoreCase
		);
		Dictionary<string, TextEdit> currentTextEditorByPath = new(
			StringComparer.OrdinalIgnoreCase
		);
		Dictionary<string, OpenScriptEditorBufferGroup> completedGroupsByPath = new(
			StringComparer.OrdinalIgnoreCase
		);
		HashSet<TextEdit> usedTextEditors = new();

		foreach (OpenScriptPathInventoryEntry inventoryEntry in openInventory)
			provisionalMembersByPath[inventoryEntry.Path] = new List<OpenScriptEditorBuffer>();

		TryRegisterCurrentEditorMember(
			scriptEditor,
			canonicalPathByLookup,
			provisionalMembersByPath,
			currentTextEditorByPath,
			usedTextEditors,
			diagnostics?.IsEnabled == true,
			out string currentScriptPath,
			out bool currentEditorIdentified,
			out bool currentEditorMatched
		);

		List<TextEdit> openTextEditors = GetOpenScriptTextEditors(
			scriptEditor,
			out int openEditorControlCount
		);
		diagnostics?.Log(
			"BufferLookup",
			() =>
				$"Editor controls inspected; GetOpenScriptEditorsCount={openEditorControlCount}; ValidUniqueTextEditors={openTextEditors.Count}; CurrentScriptPath='{currentScriptPath}'; CurrentEditorIdentified={currentEditorIdentified}; CurrentEditorMatchedDirectly={currentEditorMatched}"
		);
		Dictionary<string, OpenScriptPathInventoryEntry> unresolvedInventoryByPath =
			openInventory.ToDictionary(
				entry => entry.Path,
				entry => entry,
				StringComparer.OrdinalIgnoreCase
			);
		bool madeProgress;

		do
		{
			madeProgress = false;

			foreach (OpenScriptPathInventoryEntry inventoryEntry in openInventory)
			{
				if (!unresolvedInventoryByPath.ContainsKey(inventoryEntry.Path))
					continue;

				List<OpenScriptEditorBuffer> currentMembers =
					provisionalMembersByPath[inventoryEntry.Path];

				if (currentMembers.Count != inventoryEntry.OpenOccurrenceCount)
					continue;

				RegisterCompleteGroup(
					inventoryEntry.Path,
					currentMembers,
					currentTextEditorByPath,
					completedGroupsByPath
				);
				unresolvedInventoryByPath.Remove(inventoryEntry.Path);
				madeProgress = true;
			}

			List<TextEdit> remainingSavedTextEditors = openTextEditors
				.Where(textEditor =>
					textEditor != null
					&& !usedTextEditors.Contains(textEditor)
					&& !ScriptEditorBufferStateService.IsUnsaved(textEditor)
				)
				.ToList();

			if (unresolvedInventoryByPath.Count == 0 || remainingSavedTextEditors.Count == 0)
				continue;

			Dictionary<TextEdit, List<string>> matchingPathsByEditor = new();
			Dictionary<string, List<TextEdit>> matchingEditorsByPath = new(
				StringComparer.OrdinalIgnoreCase
			);

			foreach (TextEdit textEditor in remainingSavedTextEditors)
				matchingPathsByEditor[textEditor] = new List<string>();

			foreach (OpenScriptPathInventoryEntry inventoryEntry in openInventory)
			{
				if (
					!unresolvedInventoryByPath.ContainsKey(inventoryEntry.Path)
					|| ambiguousVerificationPaths.Contains(inventoryEntry.Path)
				)
				{
					continue;
				}

				int remainingOccurrenceCount =
					inventoryEntry.OpenOccurrenceCount
					- provisionalMembersByPath[inventoryEntry.Path].Count;

				if (remainingOccurrenceCount <= 0)
					continue;

				List<TextEdit> matchingEditors = remainingSavedTextEditors
					.Where(textEditor => textEditorMatchesPath(textEditor, inventoryEntry.Path))
					.ToList();
				matchingEditorsByPath[inventoryEntry.Path] = matchingEditors;

				foreach (TextEdit matchingEditor in matchingEditors)
					matchingPathsByEditor[matchingEditor].Add(inventoryEntry.Path);
			}

			List<string> pathsToComplete = new();

			foreach (OpenScriptPathInventoryEntry inventoryEntry in openInventory)
			{
				if (
					!unresolvedInventoryByPath.ContainsKey(inventoryEntry.Path)
					|| !matchingEditorsByPath.TryGetValue(
						inventoryEntry.Path,
						out List<TextEdit> matchingEditors
					)
				)
				{
					continue;
				}

				int remainingOccurrenceCount =
					inventoryEntry.OpenOccurrenceCount
					- provisionalMembersByPath[inventoryEntry.Path].Count;

				if (
					remainingOccurrenceCount <= 0
					|| matchingEditors.Count != remainingOccurrenceCount
					|| matchingEditors.Any(editor => matchingPathsByEditor[editor].Count != 1)
				)
				{
					continue;
				}

				pathsToComplete.Add(inventoryEntry.Path);
			}

			foreach (string path in pathsToComplete)
			{
				diagnostics?.Log(
					"BufferLookup",
					() => $"Group matched through verification text; Path='{path}'; MatchingEditorCount={matchingEditorsByPath[path].Count}"
				);
				List<OpenScriptEditorBuffer> members = provisionalMembersByPath[path];

				foreach (TextEdit textEditor in matchingEditorsByPath[path])
				{
					if (!usedTextEditors.Add(textEditor))
						continue;

					members.Add(new OpenScriptEditorBuffer(path, textEditor));
				}

				OpenScriptPathInventoryEntry inventoryEntry = unresolvedInventoryByPath[path];

				if (members.Count != inventoryEntry.OpenOccurrenceCount)
					continue;

				RegisterCompleteGroup(
					path,
					members,
					currentTextEditorByPath,
					completedGroupsByPath
				);
				unresolvedInventoryByPath.Remove(path);
				madeProgress = true;
			}
		}
		while (madeProgress);

		Dictionary<string, OpenScriptEditorBufferGroup> orderedCompletedGroups = new(
			StringComparer.OrdinalIgnoreCase
		);

		foreach (OpenScriptPathInventoryEntry inventoryEntry in openInventory)
		{
			if (completedGroupsByPath.TryGetValue(inventoryEntry.Path, out OpenScriptEditorBufferGroup group))
				orderedCompletedGroups.Add(inventoryEntry.Path, group);
		}

		List<string> unsafeOpenScriptPaths = openInventory
			.Where(entry => !orderedCompletedGroups.ContainsKey(entry.Path))
			.Select(entry => entry.Path)
			.ToList();
		List<string> ambiguousOpenScriptPaths = openInventory
			.Where(entry =>
				entry.OpenOccurrenceCount > 1
				&& !orderedCompletedGroups.ContainsKey(entry.Path)
			)
			.Select(entry => entry.Path)
			.ToList();
		string ambiguousRequiredPath = ambiguousOpenScriptPaths.FirstOrDefault(
			requiredPaths.Contains
		);

		if (!string.IsNullOrWhiteSpace(ambiguousRequiredPath))
		{
			return LogLookupResult(
				new ScriptEditorBufferGroupLookupResult(
					orderedCompletedGroups,
					ScriptEditorBufferLookupFailure.AmbiguousRequiredOpenBufferGroup,
					ambiguousRequiredPath,
					unsafeOpenScriptPaths,
					ambiguousOpenScriptPaths
				),
				diagnostics
			);
		}

		List<string> unmatchedRequiredPaths = openInventory
			.Where(entry =>
				entry.OpenOccurrenceCount == 1
				&& requiredPaths.Contains(entry.Path)
				&& !orderedCompletedGroups.ContainsKey(entry.Path)
			)
			.Select(entry => entry.Path)
			.ToList();

		if (unmatchedRequiredPaths.Count > 0)
		{
			return LogLookupResult(
				new ScriptEditorBufferGroupLookupResult(
					orderedCompletedGroups,
					ScriptEditorBufferLookupFailure.UnmatchedRequiredOpenScripts,
					unsafeOpenScriptPaths: unsafeOpenScriptPaths,
					ambiguousOpenScriptPaths: ambiguousOpenScriptPaths,
					unmatchedRequiredPaths: unmatchedRequiredPaths
				),
				diagnostics
			);
		}

		return LogLookupResult(
			new ScriptEditorBufferGroupLookupResult(
				orderedCompletedGroups,
				unsafeOpenScriptPaths: unsafeOpenScriptPaths,
				ambiguousOpenScriptPaths: ambiguousOpenScriptPaths
			),
			diagnostics
		);
	}

	private void TryRegisterCurrentEditorMember(
		ScriptEditor scriptEditor,
		IReadOnlyDictionary<string, string> canonicalPathByLookup,
		Dictionary<string, List<OpenScriptEditorBuffer>> provisionalMembersByPath,
		Dictionary<string, TextEdit> currentTextEditorByPath,
		HashSet<TextEdit> usedTextEditors,
		bool collectDiagnostics,
		out string currentScriptPath,
		out bool currentEditorIdentified,
		out bool currentEditorMatched
	)
	{
		currentScriptPath = "";
		currentEditorIdentified = false;
		currentEditorMatched = false;
		Script currentScript = scriptEditor?.GetCurrentScript();
		if (collectDiagnostics)
			currentScriptPath = _normalizePath(currentScript?.ResourcePath);

		if (
			!TryGetCanonicalTargetPath(
				currentScript?.ResourcePath,
				canonicalPathByLookup,
				out string canonicalCurrentPath
			)
			|| !provisionalMembersByPath.ContainsKey(canonicalCurrentPath)
		)
		{
			return;
		}

		ScriptEditorBase currentEditor = scriptEditor.GetCurrentEditor();
		Control baseEditor = currentEditor?.GetBaseEditor();
		if (collectDiagnostics)
			currentEditorIdentified = baseEditor is TextEdit;

		if (
			baseEditor is not TextEdit currentTextEditor
			|| !GodotObject.IsInstanceValid(currentTextEditor)
			|| !usedTextEditors.Add(currentTextEditor)
		)
		{
			return;
		}

		provisionalMembersByPath[canonicalCurrentPath].Add(
			new OpenScriptEditorBuffer(canonicalCurrentPath, currentTextEditor)
		);
		currentTextEditorByPath[canonicalCurrentPath] = currentTextEditor;
		if (collectDiagnostics)
			currentEditorMatched = true;
	}

	private static void RegisterCompleteGroup(
		string path,
		IEnumerable<OpenScriptEditorBuffer> members,
		IReadOnlyDictionary<string, TextEdit> currentTextEditorByPath,
		Dictionary<string, OpenScriptEditorBufferGroup> completedGroupsByPath
	)
	{
		currentTextEditorByPath.TryGetValue(path, out TextEdit currentTextEditor);
		completedGroupsByPath.Add(
			path,
			new OpenScriptEditorBufferGroup(path, members, currentTextEditor)
		);
	}

	private List<OpenScriptPathInventoryEntry> BuildOpenTargetScriptInventory(
		ScriptEditor scriptEditor,
		IReadOnlyDictionary<string, string> canonicalPathByLookup,
		out int openScriptCount
	)
	{
		Dictionary<string, OpenScriptPathInventoryEntry> inventoryByPath = new(
			StringComparer.OrdinalIgnoreCase
		);
		Dictionary<string, HashSet<Script>> scriptInstancesByPath = new(
			StringComparer.OrdinalIgnoreCase
		);
		int openOrder = 0;
		openScriptCount = 0;

		foreach (Script openScript in scriptEditor.GetOpenScripts())
		{
			openScriptCount++;
			int currentOpenOrder = openOrder++;

			if (
				openScript == null
				|| !GodotObject.IsInstanceValid(openScript)
				|| !TryGetCanonicalTargetPath(
					openScript.ResourcePath,
					canonicalPathByLookup,
					out string canonicalPath
				)
			)
			{
				continue;
			}

			if (
				!scriptInstancesByPath.TryGetValue(
					canonicalPath,
					out HashSet<Script> scriptInstances
				)
			)
			{
				scriptInstances = new HashSet<Script>();
				scriptInstancesByPath[canonicalPath] = scriptInstances;
			}

			if (!scriptInstances.Add(openScript))
				continue;

			if (!inventoryByPath.TryGetValue(canonicalPath, out OpenScriptPathInventoryEntry entry))
			{
				entry = new OpenScriptPathInventoryEntry(canonicalPath, currentOpenOrder);
				inventoryByPath.Add(canonicalPath, entry);
			}

			entry.OpenOccurrenceCount++;
		}

		return inventoryByPath.Values.OrderBy(entry => entry.FirstOpenOrder).ToList();
	}

	private bool TryGetCanonicalTargetPath(
		string rawPath,
		IReadOnlyDictionary<string, string> canonicalPathByLookup,
		out string canonicalPath
	)
	{
		canonicalPath = "";
		string normalizedPath = _normalizePath(rawPath);

		return !string.IsNullOrWhiteSpace(normalizedPath)
			&& canonicalPathByLookup.TryGetValue(normalizedPath, out canonicalPath);
	}

	private bool TextEditorMatchesScriptTexts(
		TextEdit textEditor,
		string scriptPath,
		IReadOnlyDictionary<string, string> originalTextsByPath,
		IReadOnlyDictionary<string, string> updatedTextsByPath
	)
	{
		if (textEditor == null || string.IsNullOrWhiteSpace(scriptPath))
			return false;

		if (
			!originalTextsByPath.TryGetValue(scriptPath, out string originalText)
			|| !updatedTextsByPath.TryGetValue(scriptPath, out string updatedText)
		)
		{
			return false;
		}

		string editorText = textEditor.Text ?? "";
		return _scriptTextsMatchForDiskVerification(editorText, originalText)
			|| _scriptTextsMatchForDiskVerification(editorText, updatedText);
	}

	private HashSet<string> GetPathsWithAmbiguousVerificationTexts(
		IEnumerable<string> paths,
		Func<string, IReadOnlyList<string>> verificationTextsForPath
	)
	{
		List<string> pathList = paths
			?.Where(path => !string.IsNullOrWhiteSpace(path))
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToList()
			?? new List<string>();
		HashSet<string> result = new(StringComparer.OrdinalIgnoreCase);

		for (int firstIndex = 0; firstIndex < pathList.Count; firstIndex++)
		{
			string firstPath = pathList[firstIndex];
			IReadOnlyList<string> firstTexts = verificationTextsForPath(firstPath);

			if (firstTexts == null || firstTexts.Count == 0)
				continue;

			for (int secondIndex = firstIndex + 1; secondIndex < pathList.Count; secondIndex++)
			{
				string secondPath = pathList[secondIndex];
				IReadOnlyList<string> secondTexts = verificationTextsForPath(secondPath);

				if (secondTexts == null || secondTexts.Count == 0)
					continue;

				bool textsOverlap = firstTexts.Any(firstText =>
					secondTexts.Any(secondText =>
						_scriptTextsMatchForDiskVerification(firstText, secondText)
					)
				);

				if (!textsOverlap)
					continue;

				result.Add(firstPath);
				result.Add(secondPath);
			}
		}

		return result;
	}

	private static IReadOnlyList<string> GetVerificationTexts(
		string path,
		IReadOnlyDictionary<string, string> originalTextsByPath,
		IReadOnlyDictionary<string, string> updatedTextsByPath
	)
	{
		List<string> result = new();

		if (originalTextsByPath.TryGetValue(path, out string originalText))
			result.Add(originalText ?? "");

		if (
			updatedTextsByPath.TryGetValue(path, out string updatedText)
			&& !result.Contains(updatedText ?? "", StringComparer.Ordinal)
		)
		{
			result.Add(updatedText ?? "");
		}

		return result;
	}

	private Dictionary<string, string> CreateCanonicalTextDictionary(
		IReadOnlyDictionary<string, string> source,
		IReadOnlyDictionary<string, string> canonicalPathByLookup
	)
	{
		Dictionary<string, string> result = new(StringComparer.OrdinalIgnoreCase);

		if (source == null)
			return result;

		foreach (KeyValuePair<string, string> pair in source)
		{
			if (
				TryGetCanonicalTargetPath(
					pair.Key,
					canonicalPathByLookup,
					out string canonicalPath
				)
			)
			{
				result[canonicalPath] = pair.Value ?? "";
			}
		}

		return result;
	}

	private List<string> NormalizeScriptPathsInStableOrder(IEnumerable<string> paths)
	{
		List<string> result = new();
		HashSet<string> seenPaths = new(StringComparer.OrdinalIgnoreCase);

		foreach (string path in paths ?? Array.Empty<string>())
		{
			string normalizedPath = _normalizePath(path);

			if (
				string.IsNullOrWhiteSpace(normalizedPath)
				|| !normalizedPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
				|| !seenPaths.Add(normalizedPath)
			)
			{
				continue;
			}

			result.Add(normalizedPath);
		}

		return result;
	}

	private HashSet<string> NormalizeScriptPathSet(IEnumerable<string> paths)
	{
		return NormalizeScriptPathsInStableOrder(paths).ToHashSet(
			StringComparer.OrdinalIgnoreCase
		);
	}

	private static List<TextEdit> GetOpenScriptTextEditors(
		ScriptEditor scriptEditor,
		out int openEditorControlCount
	)
	{
		List<TextEdit> result = new();
		openEditorControlCount = 0;
		HashSet<TextEdit> seenTextEditors = new();

		if (scriptEditor == null)
			return result;

		foreach (ScriptEditorBase scriptEditorBase in scriptEditor.GetOpenScriptEditors())
		{
			openEditorControlCount++;
			if (scriptEditorBase == null || !GodotObject.IsInstanceValid(scriptEditorBase))
				continue;

			Control baseEditor = scriptEditorBase.GetBaseEditor();

			if (
				baseEditor is TextEdit textEditor
				&& GodotObject.IsInstanceValid(textEditor)
				&& seenTextEditors.Add(textEditor)
			)
			{
				result.Add(textEditor);
			}
		}

		return result;
	}
	private static ScriptEditorBufferGroupLookupResult LogLookupResult(
		ScriptEditorBufferGroupLookupResult result,
		ScriptEditorBufferDiagnosticSink diagnostics
	)
	{
		diagnostics?.Log(
			"BufferLookup",
			() =>
			{
				string groups = result?.OpenEditorGroupsByPath == null
					? "[]"
					: $"[{string.Join(", ", result.OpenEditorGroupsByPath.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase).Select(pair => $"'{pair.Key}'({pair.Value?.Buffers.Count ?? 0})"))}]";
				return
					$"Lookup completed; Success={result?.Success ?? false}; Failure={result?.Failure}; FailurePath='{result?.FailurePath ?? ""}'; Groups={groups}; Unsafe={FormatPaths(result?.UnsafeOpenScriptPaths)}; Ambiguous={FormatPaths(result?.AmbiguousOpenScriptPaths)}; UnmatchedRequired={FormatPaths(result?.UnmatchedRequiredPaths)}";
			}
		);
		return result;
	}

	private static string FormatInventory(IEnumerable<OpenScriptPathInventoryEntry> inventory)
	{
		return $"[{string.Join(", ", (inventory ?? Array.Empty<OpenScriptPathInventoryEntry>()).Select(entry => $"'{entry.Path}'={entry.OpenOccurrenceCount}"))}]";
	}

	private static string NormalizeDiagnosticDetail(string detail)
	{
		return string.IsNullOrWhiteSpace(detail)
			? ""
			: detail.Replace('\r', ' ').Replace('\n', ' ').Trim();
	}

	private static string FormatPaths(IEnumerable<string> paths)
	{
		return
			$"[{string.Join(", ", (paths ?? Array.Empty<string>()).Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(path => path, StringComparer.OrdinalIgnoreCase).Select(path => $"'{path}'"))}]";
	}

}
#endif
