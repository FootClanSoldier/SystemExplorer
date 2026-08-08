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
				return HasMemberAccessPrefix(
					CopyPrefixes(codeEdit.CodeCompletionPrefixes)
				);
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

		var snapshot = new PrefixSnapshot(codeEdit, previousPrefixes);
		_snapshot = snapshot;

		try
		{
			codeEdit.CodeCompletionPrefixes = CreatePrefixesWithMemberAccess(
				previousPrefixes
			);

			if (
				!HasMemberAccessPrefix(
					CopyPrefixes(codeEdit.CodeCompletionPrefixes)
				)
			)
			{
				Restore(codeEdit);
				return false;
			}

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

	internal void Restore(CodeEdit codeEdit)
	{
		PrefixSnapshot snapshot = _snapshot;
		if (snapshot == null)
			return;

		if (!IsValidGodotObject(codeEdit))
		{
			if (ReferenceEquals(snapshot.CodeEdit, codeEdit))
				_snapshot = null;
			return;
		}

		if (snapshot.CodeEditInstanceId != codeEdit.GetInstanceId())
			return;

		try
		{
			codeEdit.CodeCompletionPrefixes = CreatePrefixes(snapshot.PreviousPrefixes);
		}
		finally
		{
			if (ReferenceEquals(_snapshot, snapshot))
				_snapshot = null;
		}
	}

	internal void Reset()
	{
		PrefixSnapshot snapshot = _snapshot;
		if (snapshot == null)
			return;

		Restore(snapshot.CodeEdit);

		if (ReferenceEquals(_snapshot, snapshot) && !IsValidGodotObject(snapshot.CodeEdit))
			_snapshot = null;
	}

	private static Godot.Collections.Array<string> CreatePrefixesWithMemberAccess(
		IReadOnlyList<string> previousPrefixes
	)
	{
		var prefixes = CreatePrefixes(previousPrefixes);
		prefixes.Add(MemberAccessPrefix);
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

	private static bool IsValidGodotObject(GodotObject source)
	{
		return source != null && GodotObject.IsInstanceValid(source);
	}

	private sealed class PrefixSnapshot
	{
		internal PrefixSnapshot(CodeEdit codeEdit, string[] previousPrefixes)
		{
			CodeEdit = codeEdit ?? throw new ArgumentNullException(nameof(codeEdit));
			CodeEditInstanceId = codeEdit.GetInstanceId();
			PreviousPrefixes = previousPrefixes ?? Array.Empty<string>();
		}

		internal CodeEdit CodeEdit { get; }
		internal ulong CodeEditInstanceId { get; }
		internal IReadOnlyList<string> PreviousPrefixes { get; }
	}
}
#endif
