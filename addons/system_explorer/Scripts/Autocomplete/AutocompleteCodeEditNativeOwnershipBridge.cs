#if TOOLS
using Godot;
using System;
using System.Globalization;

namespace SystemExplorer.Autocomplete;

internal sealed class AutocompleteCodeEditNativeOwnershipBridge
{
	private const int SchemaVersion = 1;
	private const string MetadataKey =
		"_system_explorer_autocomplete_code_edit_state_v1";
	private const string SchemaVersionKey = "schema_version";
	private const string OwnerManagedAssemblyGenerationKey =
		"owner_managed_assembly_generation";
	private const string CodeEditNativeInstanceIdKey = "code_edit_native_instance_id";
	private const string PrefixOwnedKey = "prefix_owned";
	private const string PreviousCodeCompletionPrefixesKey =
		"previous_code_completion_prefixes";
	private const string CompletionExistingColorOwnedKey =
		"completion_existing_color_owned";
	private const string HadPreviousCompletionExistingColorOverrideKey =
		"had_previous_completion_existing_color_override";
	private const string PreviousCompletionExistingColorKey =
		"previous_completion_existing_color";

	internal enum MarkerReadStatus
	{
		Missing,
		Valid,
		Malformed,
	}

	internal sealed class OwnershipState
	{
		internal OwnershipState(
			string ownerManagedAssemblyGeneration,
			ulong codeEditNativeInstanceId,
			bool prefixOwned,
			string[] previousCodeCompletionPrefixes,
			bool completionExistingColorOwned,
			bool hadPreviousCompletionExistingColorOverride,
			Color previousCompletionExistingColor
		)
		{
			OwnerManagedAssemblyGeneration = ownerManagedAssemblyGeneration ?? "";
			CodeEditNativeInstanceId = codeEditNativeInstanceId;
			PrefixOwned = prefixOwned;
			PreviousCodeCompletionPrefixes =
				previousCodeCompletionPrefixes == null
					? Array.Empty<string>()
					: (string[])previousCodeCompletionPrefixes.Clone();
			CompletionExistingColorOwned = completionExistingColorOwned;
			HadPreviousCompletionExistingColorOverride =
				hadPreviousCompletionExistingColorOverride;
			PreviousCompletionExistingColor = previousCompletionExistingColor;
		}

		internal string OwnerManagedAssemblyGeneration { get; }
		internal ulong CodeEditNativeInstanceId { get; }
		internal bool PrefixOwned { get; }
		internal string[] PreviousCodeCompletionPrefixes { get; }
		internal bool CompletionExistingColorOwned { get; }
		internal bool HadPreviousCompletionExistingColorOverride { get; }
		internal Color PreviousCompletionExistingColor { get; }
	}

	internal MarkerReadStatus Inspect(
		CodeEdit codeEdit,
		out OwnershipState state,
		out string failureDetail
	)
	{
		state = null;
		failureDetail = "";

		if (!IsValidGodotObject(codeEdit))
		{
			failureDetail = "CodeEdit is null or invalid.";
			return MarkerReadStatus.Malformed;
		}

		try
		{
			if (!codeEdit.HasMeta(MetadataKey))
				return MarkerReadStatus.Missing;

			Variant rawMarker = codeEdit.GetMeta(MetadataKey);
			if (rawMarker.VariantType != Variant.Type.Dictionary)
			{
				failureDetail = "Metadata value is not a Dictionary Variant.";
				return MarkerReadStatus.Malformed;
			}

			Godot.Collections.Dictionary dictionary = rawMarker.AsGodotDictionary();
			if (dictionary == null)
			{
				failureDetail = "Metadata dictionary is null.";
				return MarkerReadStatus.Malformed;
			}

			if (
				!TryGetInt(dictionary, SchemaVersionKey, out int schemaVersion)
				|| schemaVersion != SchemaVersion
			)
			{
				failureDetail = "Schema version is missing or unsupported.";
				return MarkerReadStatus.Malformed;
			}

			if (
				!TryGetString(
					dictionary,
					OwnerManagedAssemblyGenerationKey,
					out string ownerManagedAssemblyGeneration
				)
				|| string.IsNullOrWhiteSpace(ownerManagedAssemblyGeneration)
			)
			{
				failureDetail = "Owner managed assembly generation is missing.";
				return MarkerReadStatus.Malformed;
			}

			if (
				!TryGetString(
					dictionary,
					CodeEditNativeInstanceIdKey,
					out string codeEditNativeInstanceIdText
				)
				|| !ulong.TryParse(
					codeEditNativeInstanceIdText,
					NumberStyles.None,
					CultureInfo.InvariantCulture,
					out ulong codeEditNativeInstanceId
				)
				|| codeEditNativeInstanceId == 0
			)
			{
				failureDetail = "CodeEdit native instance id is missing or invalid.";
				return MarkerReadStatus.Malformed;
			}

			ulong currentCodeEditNativeInstanceId = codeEdit.GetInstanceId();
			if (currentCodeEditNativeInstanceId != codeEditNativeInstanceId)
			{
				failureDetail =
					$"CodeEdit identity mismatch. Marker='{codeEditNativeInstanceId}', Current='{currentCodeEditNativeInstanceId}'.";
				return MarkerReadStatus.Malformed;
			}

			if (
				!TryGetBool(dictionary, PrefixOwnedKey, out bool prefixOwned)
				|| !TryGetStringArray(
					dictionary,
					PreviousCodeCompletionPrefixesKey,
					out string[] previousCodeCompletionPrefixes
				)
			)
			{
				failureDetail = "Prefix ownership state is missing or invalid.";
				return MarkerReadStatus.Malformed;
			}

			if (!prefixOwned && previousCodeCompletionPrefixes.Length != 0)
			{
				failureDetail = "Unowned prefix state contains a previous-prefix snapshot.";
				return MarkerReadStatus.Malformed;
			}

			if (
				!TryGetBool(
					dictionary,
					CompletionExistingColorOwnedKey,
					out bool completionExistingColorOwned
				)
				|| !TryGetBool(
					dictionary,
					HadPreviousCompletionExistingColorOverrideKey,
					out bool hadPreviousCompletionExistingColorOverride
				)
				|| !TryGetColor(
					dictionary,
					PreviousCompletionExistingColorKey,
					out Color previousCompletionExistingColor
				)
				|| !IsFinite(previousCompletionExistingColor)
			)
			{
				failureDetail = "completion_existing_color ownership state is invalid.";
				return MarkerReadStatus.Malformed;
			}

			if (
				!completionExistingColorOwned
				&& hadPreviousCompletionExistingColorOverride
			)
			{
				failureDetail =
					"Unowned completion_existing_color state claims a previous override.";
				return MarkerReadStatus.Malformed;
			}

			if (!prefixOwned && !completionExistingColorOwned)
			{
				failureDetail = "Marker does not describe any owned reversible state.";
				return MarkerReadStatus.Malformed;
			}

			state = new OwnershipState(
				ownerManagedAssemblyGeneration,
				codeEditNativeInstanceId,
				prefixOwned,
				previousCodeCompletionPrefixes,
				completionExistingColorOwned,
				hadPreviousCompletionExistingColorOverride,
				previousCompletionExistingColor
			);
			return MarkerReadStatus.Valid;
		}
		catch (Exception exception)
		{
			failureDetail =
				$"Marker read failed: {exception.GetType().Name}: {exception.Message}";
			state = null;
			return MarkerReadStatus.Malformed;
		}
	}

	internal bool TryWrite(
		CodeEdit codeEdit,
		OwnershipState state,
		out string failureDetail
	)
	{
		failureDetail = "";

		if (codeEdit == null)
		{
			failureDetail = "CodeEdit is null.";
			return false;
		}

		if (!IsStateStructurallyValid(state))
		{
			failureDetail = "Ownership state is structurally invalid.";
			return false;
		}

		try
		{
			var previousPrefixes = new Godot.Collections.Array();
			foreach (string prefix in state.PreviousCodeCompletionPrefixes)
				previousPrefixes.Add(prefix);

			var dictionary = new Godot.Collections.Dictionary
			{
				{ SchemaVersionKey, SchemaVersion },
				{
					OwnerManagedAssemblyGenerationKey,
					state.OwnerManagedAssemblyGeneration
				},
				{
					CodeEditNativeInstanceIdKey,
					state.CodeEditNativeInstanceId.ToString(CultureInfo.InvariantCulture)
				},
				{ PrefixOwnedKey, state.PrefixOwned },
				{ PreviousCodeCompletionPrefixesKey, previousPrefixes },
				{
					CompletionExistingColorOwnedKey,
					state.CompletionExistingColorOwned
				},
				{
					HadPreviousCompletionExistingColorOverrideKey,
					state.HadPreviousCompletionExistingColorOverride
				},
				{
					PreviousCompletionExistingColorKey,
					state.PreviousCompletionExistingColor
				},
			};

			codeEdit.SetMeta(MetadataKey, dictionary);
			return true;
		}
		catch (Exception exception)
		{
			failureDetail =
				$"Marker write failed: {exception.GetType().Name}: {exception.Message}";
			return false;
		}
	}

	internal bool TryClearVerifiedMarker(
		CodeEdit codeEdit,
		OwnershipState expectedState,
		out string failureDetail
	)
	{
		failureDetail = "";

		MarkerReadStatus status = Inspect(codeEdit, out OwnershipState currentState, out string readFailure);
		if (status == MarkerReadStatus.Missing)
		{
			failureDetail = "Verified marker disappeared before it could be cleared.";
			return false;
		}
		if (status != MarkerReadStatus.Valid)
		{
			failureDetail = readFailure;
			return false;
		}
		if (!MatchesState(currentState, expectedState))
		{
			failureDetail = "Marker state changed before clear.";
			return false;
		}

		try
		{
			codeEdit.RemoveMeta(MetadataKey);
			return true;
		}
		catch (Exception exception)
		{
			failureDetail =
				$"Marker clear failed: {exception.GetType().Name}: {exception.Message}";
			return false;
		}
	}

	internal bool TryClearOwnedMarkerForGeneration(
		CodeEdit codeEdit,
		string managedAssemblyGeneration,
		out string failureDetail
	)
	{
		failureDetail = "";

		if (!IsValidGodotObject(codeEdit) || string.IsNullOrWhiteSpace(managedAssemblyGeneration))
			return true;

		MarkerReadStatus status = Inspect(codeEdit, out OwnershipState state, out string readFailure);
		if (status == MarkerReadStatus.Missing)
			return true;
		if (status != MarkerReadStatus.Valid)
		{
			failureDetail = readFailure;
			return false;
		}
		if (
			!string.Equals(
				state.OwnerManagedAssemblyGeneration,
				managedAssemblyGeneration,
				StringComparison.Ordinal
			)
		)
		{
			return true;
		}

		try
		{
			codeEdit.RemoveMeta(MetadataKey);
			return true;
		}
		catch (Exception exception)
		{
			failureDetail =
				$"Marker clear failed: {exception.GetType().Name}: {exception.Message}";
			return false;
		}
	}

	private static bool IsStateStructurallyValid(OwnershipState state)
	{
		return state != null
			&& !string.IsNullOrWhiteSpace(state.OwnerManagedAssemblyGeneration)
			&& state.CodeEditNativeInstanceId != 0
			&& (state.PrefixOwned || state.CompletionExistingColorOwned)
			&& (state.PrefixOwned || state.PreviousCodeCompletionPrefixes.Length == 0)
			&& HasOnlyNonNullPrefixes(state.PreviousCodeCompletionPrefixes)
			&& (
				state.CompletionExistingColorOwned
				|| !state.HadPreviousCompletionExistingColorOverride
			)
			&& IsFinite(state.PreviousCompletionExistingColor);
	}

	private static bool HasOnlyNonNullPrefixes(string[] prefixes)
	{
		if (prefixes == null)
			return false;

		for (int index = 0; index < prefixes.Length; index++)
		{
			if (prefixes[index] == null)
				return false;
		}

		return true;
	}

	private static bool MatchesState(OwnershipState left, OwnershipState right)
	{
		if (
			left == null
			|| right == null
			|| left.CodeEditNativeInstanceId != right.CodeEditNativeInstanceId
			|| !string.Equals(
				left.OwnerManagedAssemblyGeneration,
				right.OwnerManagedAssemblyGeneration,
				StringComparison.Ordinal
			)
			|| left.PrefixOwned != right.PrefixOwned
			|| left.CompletionExistingColorOwned != right.CompletionExistingColorOwned
			|| left.HadPreviousCompletionExistingColorOverride
				!= right.HadPreviousCompletionExistingColorOverride
			|| !left.PreviousCompletionExistingColor.Equals(
				right.PreviousCompletionExistingColor
			)
			|| left.PreviousCodeCompletionPrefixes.Length
				!= right.PreviousCodeCompletionPrefixes.Length
		)
		{
			return false;
		}

		for (int index = 0; index < left.PreviousCodeCompletionPrefixes.Length; index++)
		{
			if (
				!string.Equals(
					left.PreviousCodeCompletionPrefixes[index],
					right.PreviousCodeCompletionPrefixes[index],
					StringComparison.Ordinal
				)
			)
			{
				return false;
			}
		}

		return true;
	}

	private static bool TryGetString(Godot.Collections.Dictionary dictionary, string key, out string value)
	{
		value = "";
		if (
			dictionary == null
			|| !dictionary.TryGetValue(key, out Variant rawValue)
			|| rawValue.VariantType != Variant.Type.String
		)
		{
			return false;
		}

		value = rawValue.AsString();
		return true;
	}

	private static bool TryGetInt(Godot.Collections.Dictionary dictionary, string key, out int value)
	{
		value = 0;
		if (
			dictionary == null
			|| !dictionary.TryGetValue(key, out Variant rawValue)
			|| rawValue.VariantType != Variant.Type.Int
		)
		{
			return false;
		}

		long longValue = rawValue.AsInt64();
		if (longValue < int.MinValue || longValue > int.MaxValue)
			return false;

		value = (int)longValue;
		return true;
	}

	private static bool TryGetBool(Godot.Collections.Dictionary dictionary, string key, out bool value)
	{
		value = false;
		if (
			dictionary == null
			|| !dictionary.TryGetValue(key, out Variant rawValue)
			|| rawValue.VariantType != Variant.Type.Bool
		)
		{
			return false;
		}

		value = rawValue.AsBool();
		return true;
	}

	private static bool TryGetColor(Godot.Collections.Dictionary dictionary, string key, out Color value)
	{
		value = default;
		if (
			dictionary == null
			|| !dictionary.TryGetValue(key, out Variant rawValue)
			|| rawValue.VariantType != Variant.Type.Color
		)
		{
			return false;
		}

		value = rawValue.AsColor();
		return true;
	}

	private static bool TryGetStringArray(
		Godot.Collections.Dictionary dictionary,
		string key,
		out string[] values
	)
	{
		values = Array.Empty<string>();
		if (
			dictionary == null
			|| !dictionary.TryGetValue(key, out Variant rawValue)
			|| rawValue.VariantType != Variant.Type.Array
		)
		{
			return false;
		}

		Godot.Collections.Array array = rawValue.AsGodotArray();
		if (array == null || array.Count == 0)
			return array != null;

		var copy = new string[array.Count];
		for (int index = 0; index < array.Count; index++)
		{
			Variant item = array[index];
			if (item.VariantType != Variant.Type.String)
				return false;

			copy[index] = item.AsString();
		}

		values = copy;
		return true;
	}

	private static bool IsFinite(Color color)
	{
		return IsFinite(color.R)
			&& IsFinite(color.G)
			&& IsFinite(color.B)
			&& IsFinite(color.A);
	}

	private static bool IsFinite(float value)
	{
		return !float.IsNaN(value) && !float.IsInfinity(value);
	}

	private static bool IsValidGodotObject(GodotObject source)
	{
		return source != null && GodotObject.IsInstanceValid(source);
	}
}
#endif
