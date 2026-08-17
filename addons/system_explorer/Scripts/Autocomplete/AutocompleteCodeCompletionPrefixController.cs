#if TOOLS
using Godot;
using System;
using System.Collections.Generic;

namespace SystemExplorer.Autocomplete;

internal sealed class AutocompleteCodeCompletionPrefixController
{
	private const string MemberAccessPrefix = ".";

	private PrefixSnapshot _snapshot;

	internal bool Apply(CodeEdit codeEdit)
	{
		if (!IsValidGodotObject(codeEdit))
			return false;

		PrefixSnapshot activeSnapshot = _snapshot;
		if (activeSnapshot != null)
		{
			if (activeSnapshot.CodeEditInstanceId != codeEdit.GetInstanceId())
				return false;

			try
			{
				string[] currentPrefixes = CopyPrefixes(codeEdit.CodeCompletionPrefixes);
				if (PrefixesEqual(currentPrefixes, activeSnapshot.AppliedPrefixes))
					return true;

				ForgetOwnedState(codeEdit);
				return false;
			}
			catch
			{
				return false;
			}
		}

		string[] previousPrefixes;
		try
		{
			previousPrefixes = CopyPrefixes(codeEdit.CodeCompletionPrefixes);
		}
		catch
		{
			return false;
		}

		if (HasMemberAccessPrefix(previousPrefixes))
			return true;

		string[] intendedAppliedPrefixes = CreatePrefixesWithMemberAccessArray(previousPrefixes);
		var snapshot = new PrefixSnapshot(
			codeEdit,
			previousPrefixes,
			intendedAppliedPrefixes
		);
		_snapshot = snapshot;

		try
		{
			codeEdit.CodeCompletionPrefixes = CreatePrefixes(intendedAppliedPrefixes);
			string[] verifiedAppliedPrefixes = CopyPrefixes(codeEdit.CodeCompletionPrefixes);
			if (!PrefixesEqual(verifiedAppliedPrefixes, intendedAppliedPrefixes))
			{
				Restore(codeEdit);
				return false;
			}

			snapshot.SetAppliedPrefixes(verifiedAppliedPrefixes);
			return true;
		}
		catch
		{
			try
			{
				Restore(codeEdit);
			}
			catch
			{
			}

			return false;
		}
	}

	internal bool TryCaptureNativeOwnershipState(
		CodeEdit codeEdit,
		out ulong codeEditInstanceId,
		out bool prefixOwned,
		out string[] previousPrefixes,
		out string[] appliedPrefixes
	)
	{
		codeEditInstanceId = 0;
		prefixOwned = false;
		previousPrefixes = Array.Empty<string>();
		appliedPrefixes = Array.Empty<string>();

		if (codeEdit == null)
			return false;

		PrefixSnapshot snapshot = _snapshot;
		if (snapshot == null)
			return true;

		if (!ReferenceEquals(snapshot.CodeEdit, codeEdit))
			return false;

		codeEditInstanceId = snapshot.CodeEditInstanceId;
		prefixOwned = true;
		previousPrefixes = CopyPrefixes(snapshot.PreviousPrefixes);
		appliedPrefixes = CopyPrefixes(snapshot.AppliedPrefixes);
		return true;
	}

	internal AutocompletePresentationRestoreResult TryRestoreOwnedPrefixesFromNativeBridge(
		CodeEdit codeEdit,
		IReadOnlyList<string> expectedAppliedPrefixes,
		IReadOnlyList<string> previousPrefixes
	)
	{
		if (
			!IsValidGodotObject(codeEdit)
			|| expectedAppliedPrefixes == null
			|| previousPrefixes == null
		)
		{
			return AutocompletePresentationRestoreResult.Failure();
		}

		try
		{
			string[] currentPrefixes = CopyPrefixes(codeEdit.CodeCompletionPrefixes);
			if (!PrefixesEqual(currentPrefixes, expectedAppliedPrefixes))
				return AutocompletePresentationRestoreResult.Success(currentStateChanged: true);

			codeEdit.CodeCompletionPrefixes = CreatePrefixes(previousPrefixes);
			return AutocompletePresentationRestoreResult.Success();
		}
		catch
		{
			return AutocompletePresentationRestoreResult.Failure();
		}
	}

	internal AutocompletePresentationRestoreResult Restore(CodeEdit codeEdit)
	{
		PrefixSnapshot snapshot = _snapshot;
		if (snapshot == null)
			return AutocompletePresentationRestoreResult.Success();

		if (!ReferenceEquals(snapshot.CodeEdit, codeEdit))
			return AutocompletePresentationRestoreResult.Success();

		try
		{
			if (!IsValidGodotObject(codeEdit))
				return AutocompletePresentationRestoreResult.Failure();
			if (snapshot.CodeEditInstanceId != codeEdit.GetInstanceId())
				return AutocompletePresentationRestoreResult.Success(currentStateChanged: true);

			string[] currentPrefixes = CopyPrefixes(codeEdit.CodeCompletionPrefixes);
			if (!PrefixesEqual(currentPrefixes, snapshot.AppliedPrefixes))
				return AutocompletePresentationRestoreResult.Success(currentStateChanged: true);

			codeEdit.CodeCompletionPrefixes = CreatePrefixes(snapshot.PreviousPrefixes);
			return AutocompletePresentationRestoreResult.Success();
		}
		catch
		{
			return AutocompletePresentationRestoreResult.Failure();
		}
		finally
		{
			if (ReferenceEquals(_snapshot, snapshot))
				_snapshot = null;
		}
	}

	internal void ForgetOwnedState(CodeEdit codeEdit)
	{
		PrefixSnapshot snapshot = _snapshot;
		if (snapshot == null)
			return;

		if (
			ReferenceEquals(snapshot.CodeEdit, codeEdit)
			|| (
				IsValidGodotObject(codeEdit)
				&& snapshot.CodeEditInstanceId == codeEdit.GetInstanceId()
			)
		)
		{
			_snapshot = null;
		}
	}

	internal void Reset()
	{
		PrefixSnapshot snapshot = _snapshot;
		if (snapshot == null)
			return;

		Restore(snapshot.CodeEdit);
		if (ReferenceEquals(_snapshot, snapshot))
			_snapshot = null;
	}

	private static string[] CreatePrefixesWithMemberAccessArray(
		IReadOnlyList<string> previousPrefixes
	)
	{
		int previousCount = previousPrefixes?.Count ?? 0;
		var prefixes = new string[previousCount + 1];
		for (int index = 0; index < previousCount; index++)
			prefixes[index] = previousPrefixes[index];
		prefixes[previousCount] = MemberAccessPrefix;
		return prefixes;
	}

	private static Godot.Collections.Array<string> CreatePrefixes(
		IReadOnlyList<string> prefixes
	)
	{
		var copy = new Godot.Collections.Array<string>();

		if (prefixes == null)
			return copy;

		for (int index = 0; index < prefixes.Count; index++)
			copy.Add(prefixes[index]);

		return copy;
	}

	private static string[] CopyPrefixes(IReadOnlyList<string> prefixes)
	{
		if (prefixes == null || prefixes.Count == 0)
			return Array.Empty<string>();

		var copy = new string[prefixes.Count];
		for (int index = 0; index < prefixes.Count; index++)
			copy[index] = prefixes[index];

		return copy;
	}

	private static string[] CopyPrefixes(Godot.Collections.Array<string> prefixes)
	{
		if (prefixes == null || prefixes.Count == 0)
			return Array.Empty<string>();

		var copy = new string[prefixes.Count];
		for (int index = 0; index < prefixes.Count; index++)
			copy[index] = prefixes[index];

		return copy;
	}

	private static bool HasMemberAccessPrefix(IReadOnlyList<string> prefixes)
	{
		if (prefixes == null)
			return false;

		for (int index = 0; index < prefixes.Count; index++)
		{
			if (
				string.Equals(
					prefixes[index],
					MemberAccessPrefix,
					StringComparison.Ordinal
				)
			)
			{
				return true;
			}
		}

		return false;
	}

	private static bool PrefixesEqual(
		IReadOnlyList<string> left,
		IReadOnlyList<string> right
	)
	{
		if (ReferenceEquals(left, right))
			return true;
		if (left == null || right == null || left.Count != right.Count)
			return false;

		for (int index = 0; index < left.Count; index++)
		{
			if (!string.Equals(left[index], right[index], StringComparison.Ordinal))
				return false;
		}

		return true;
	}

	private static bool IsValidGodotObject(GodotObject source)
	{
		return source != null && GodotObject.IsInstanceValid(source);
	}

	private sealed class PrefixSnapshot
	{
		internal PrefixSnapshot(
			CodeEdit codeEdit,
			string[] previousPrefixes,
			string[] appliedPrefixes
		)
		{
			CodeEdit = codeEdit ?? throw new ArgumentNullException(nameof(codeEdit));
			CodeEditInstanceId = codeEdit.GetInstanceId();
			PreviousPrefixes = previousPrefixes ?? Array.Empty<string>();
			AppliedPrefixes = appliedPrefixes ?? Array.Empty<string>();
		}

		internal CodeEdit CodeEdit { get; }
		internal ulong CodeEditInstanceId { get; }
		internal IReadOnlyList<string> PreviousPrefixes { get; }
		internal IReadOnlyList<string> AppliedPrefixes { get; private set; }

		internal void SetAppliedPrefixes(string[] appliedPrefixes)
		{
			AppliedPrefixes = appliedPrefixes ?? Array.Empty<string>();
		}
	}
}
#endif
