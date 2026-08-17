#if TOOLS
using Godot;
using System;
using System.Globalization;
using System.Collections.Generic;
using SystemExplorer.EditorIntegration.ScriptEditing;

namespace SystemExplorer.Autocomplete;

internal sealed class AutocompleteCodeEditNativeOwnershipBridge
{
	private const int LegacySchemaVersion = 1;
	internal const int CurrentSchemaVersion = 2;
	private const string MetadataKey =
		"_system_explorer_autocomplete_code_edit_state_v1";
	private const string SchemaVersionKey = "schema_version";
	private const string OwnerManagedAssemblyGenerationKey =
		"owner_managed_assembly_generation";
	private const string OwnerHostInstanceTokenKey = "owner_host_instance_token";
	private const string OwnerScriptTransitionIdKey = "owner_script_transition_id";
	private const string OwnerReloadReadyEpochKey = "owner_reload_ready_epoch";
	private const string OwnerBindingEpochKey = "owner_binding_epoch";
	private const string CodeEditNativeInstanceIdKey = "code_edit_native_instance_id";
	private const string ScriptResourcePathKey = "script_resource_path";
	private const string PrefixOwnedKey = "prefix_owned";
	private const string PreviousCodeCompletionPrefixesKey =
		"previous_code_completion_prefixes";
	private const string AppliedCodeCompletionPrefixesKey =
		"applied_code_completion_prefixes";
	private const string CompletionExistingColorOwnedKey =
		"completion_existing_color_owned";
	private const string HadPreviousCompletionExistingColorOverrideKey =
		"had_previous_completion_existing_color_override";
	private const string PreviousCompletionExistingColorKey =
		"previous_completion_existing_color";
	private const string AppliedCompletionExistingColorKey =
		"applied_completion_existing_color";
	private const string LegacyMemberAccessPrefix = ".";

	internal enum MarkerReadStatus
	{
		Missing,
		Valid,
		Malformed,
	}

	internal sealed class OwnershipState
	{
		internal OwnershipState(
			int schemaVersion,
			bool isLegacy,
			string ownerManagedAssemblyGeneration,
			long ownerHostInstanceToken,
			long ownerScriptTransitionId,
			long ownerReloadReadyEpoch,
			long ownerBindingEpoch,
			ulong codeEditNativeInstanceId,
			string scriptResourcePath,
			bool prefixOwned,
			string[] previousCodeCompletionPrefixes,
			string[] appliedCodeCompletionPrefixes,
			bool completionExistingColorOwned,
			bool hadPreviousCompletionExistingColorOverride,
			Color previousCompletionExistingColor,
			Color appliedCompletionExistingColor
		)
		{
			SchemaVersion = schemaVersion;
			IsLegacy = isLegacy;
			OwnerManagedAssemblyGeneration = ownerManagedAssemblyGeneration ?? "";
			OwnerHostInstanceToken = ownerHostInstanceToken;
			OwnerScriptTransitionId = ownerScriptTransitionId;
			OwnerReloadReadyEpoch = ownerReloadReadyEpoch;
			OwnerBindingEpoch = ownerBindingEpoch;
			CodeEditNativeInstanceId = codeEditNativeInstanceId;
			ScriptResourcePath = ScriptPathUtility.Normalize(scriptResourcePath);
			PrefixOwned = prefixOwned;
			PreviousCodeCompletionPrefixes = ClonePrefixes(previousCodeCompletionPrefixes);
			AppliedCodeCompletionPrefixes = ClonePrefixes(appliedCodeCompletionPrefixes);
			CompletionExistingColorOwned = completionExistingColorOwned;
			HadPreviousCompletionExistingColorOverride =
				hadPreviousCompletionExistingColorOverride;
			PreviousCompletionExistingColor = previousCompletionExistingColor;
			AppliedCompletionExistingColor = appliedCompletionExistingColor;
		}

		internal int SchemaVersion { get; }
		internal bool IsLegacy { get; }
		internal string OwnerManagedAssemblyGeneration { get; }
		internal long OwnerHostInstanceToken { get; }
		internal long OwnerScriptTransitionId { get; }
		internal long OwnerReloadReadyEpoch { get; }
		internal long OwnerBindingEpoch { get; }
		internal ulong CodeEditNativeInstanceId { get; }
		internal string ScriptResourcePath { get; }
		internal bool PrefixOwned { get; }
		internal string[] PreviousCodeCompletionPrefixes { get; }
		internal string[] AppliedCodeCompletionPrefixes { get; }
		internal bool CompletionExistingColorOwned { get; }
		internal bool HadPreviousCompletionExistingColorOverride { get; }
		internal Color PreviousCompletionExistingColor { get; }
		internal Color AppliedCompletionExistingColor { get; }
		internal bool HasOwnedReversibleState => PrefixOwned || CompletionExistingColorOwned;

		private static string[] ClonePrefixes(string[] prefixes)
		{
			return prefixes == null ? Array.Empty<string>() : (string[])prefixes.Clone();
		}
	}

	internal MarkerReadStatus Inspect(
		CodeEdit codeEdit,
		out OwnershipState state,
		out string failureDetail,
		Action<string, string> nativeBoundaryDiagnosticPhase = null
	)
	{
		state = null;
		failureDetail = "";
		InvokeNativeBoundaryDiagnosticPhase(
			nativeBoundaryDiagnosticPhase,
			"NativeOwnershipBridge.Inspect.IsInstanceValid.Begin"
		);
		bool codeEditValid = IsValidGodotObject(codeEdit);
		InvokeNativeBoundaryDiagnosticPhase(
			nativeBoundaryDiagnosticPhase,
			"NativeOwnershipBridge.Inspect.IsInstanceValid.Returned",
			$"Result='{codeEditValid}'"
		);
		if (!codeEditValid)
		{
			failureDetail = "CodeEdit is null or invalid.";
			return MarkerReadStatus.Malformed;
		}

		try
		{
			InvokeNativeBoundaryDiagnosticPhase(
				nativeBoundaryDiagnosticPhase,
				"NativeOwnershipBridge.Inspect.HasMeta.Begin"
			);
			bool hasMarker = codeEdit.HasMeta(MetadataKey);
			InvokeNativeBoundaryDiagnosticPhase(
				nativeBoundaryDiagnosticPhase,
				"NativeOwnershipBridge.Inspect.HasMeta.Returned",
				$"Result='{hasMarker}'"
			);
			if (!hasMarker)
				return MarkerReadStatus.Missing;

			InvokeNativeBoundaryDiagnosticPhase(
				nativeBoundaryDiagnosticPhase,
				"NativeOwnershipBridge.Inspect.GetMeta.Begin"
			);
			Variant rawMarker = codeEdit.GetMeta(MetadataKey);
			InvokeNativeBoundaryDiagnosticPhase(
				nativeBoundaryDiagnosticPhase,
				"NativeOwnershipBridge.Inspect.GetMeta.Returned"
			);

			InvokeNativeBoundaryDiagnosticPhase(
				nativeBoundaryDiagnosticPhase,
				"NativeOwnershipBridge.Inspect.MarkerDecode.Begin"
			);

			if (rawMarker.VariantType != Variant.Type.Dictionary)
			{
				failureDetail = "Metadata value is not a Dictionary Variant.";
				return CompleteMarkerDecode(MarkerReadStatus.Malformed, nativeBoundaryDiagnosticPhase);
			}

			Godot.Collections.Dictionary dictionary = rawMarker.AsGodotDictionary();
			if (dictionary == null)
			{
				failureDetail = "Metadata dictionary is null.";
				return CompleteMarkerDecode(MarkerReadStatus.Malformed, nativeBoundaryDiagnosticPhase);
			}

			if (!TryGetInt(dictionary, SchemaVersionKey, out int schemaVersion))
			{
				failureDetail = "Schema version is missing.";
				return CompleteMarkerDecode(MarkerReadStatus.Malformed, nativeBoundaryDiagnosticPhase);
			}
			if (schemaVersion != LegacySchemaVersion && schemaVersion != CurrentSchemaVersion)
			{
				failureDetail = $"Schema version '{schemaVersion}' is unsupported.";
				return CompleteMarkerDecode(MarkerReadStatus.Malformed, nativeBoundaryDiagnosticPhase);
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
				return CompleteMarkerDecode(MarkerReadStatus.Malformed, nativeBoundaryDiagnosticPhase);
			}

			if (
				!TryGetPositiveUlongString(
					dictionary,
					CodeEditNativeInstanceIdKey,
					out ulong codeEditNativeInstanceId
				)
			)
			{
				failureDetail = "CodeEdit native instance id is missing or invalid.";
				return CompleteMarkerDecode(MarkerReadStatus.Malformed, nativeBoundaryDiagnosticPhase);
			}

			InvokeNativeBoundaryDiagnosticPhase(
				nativeBoundaryDiagnosticPhase,
				"NativeOwnershipBridge.Inspect.GetInstanceId.Begin"
			);
			ulong currentCodeEditNativeInstanceId = codeEdit.GetInstanceId();
			InvokeNativeBoundaryDiagnosticPhase(
				nativeBoundaryDiagnosticPhase,
				"NativeOwnershipBridge.Inspect.GetInstanceId.Returned",
				$"NativeInstanceId='{currentCodeEditNativeInstanceId}'"
			);
			if (currentCodeEditNativeInstanceId != codeEditNativeInstanceId)
			{
				failureDetail =
					$"CodeEdit identity mismatch. Marker='{codeEditNativeInstanceId}', Current='{currentCodeEditNativeInstanceId}'.";
				return CompleteMarkerDecode(MarkerReadStatus.Malformed, nativeBoundaryDiagnosticPhase);
			}

			return schemaVersion == LegacySchemaVersion
				? DecodeLegacyState(
					dictionary,
					ownerManagedAssemblyGeneration,
					codeEditNativeInstanceId,
					out state,
					out failureDetail,
					nativeBoundaryDiagnosticPhase
				)
				: DecodeCurrentState(
					dictionary,
					ownerManagedAssemblyGeneration,
					codeEditNativeInstanceId,
					out state,
					out failureDetail,
					nativeBoundaryDiagnosticPhase
				);
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

		if (!IsValidGodotObject(codeEdit))
		{
			failureDetail = "CodeEdit is null or invalid.";
			return false;
		}

		if (!IsCurrentStateStructurallyValid(state))
		{
			failureDetail = "Ownership state is structurally invalid for schema v2.";
			return false;
		}

		try
		{
			var previousPrefixes = ToVariantArray(state.PreviousCodeCompletionPrefixes);
			var appliedPrefixes = ToVariantArray(state.AppliedCodeCompletionPrefixes);

			var dictionary = new Godot.Collections.Dictionary
			{
				{ SchemaVersionKey, CurrentSchemaVersion },
				{
					OwnerManagedAssemblyGenerationKey,
					state.OwnerManagedAssemblyGeneration
				},
				{
					OwnerHostInstanceTokenKey,
					state.OwnerHostInstanceToken.ToString(CultureInfo.InvariantCulture)
				},
				{
					OwnerScriptTransitionIdKey,
					state.OwnerScriptTransitionId.ToString(CultureInfo.InvariantCulture)
				},
				{
					OwnerReloadReadyEpochKey,
					state.OwnerReloadReadyEpoch.ToString(CultureInfo.InvariantCulture)
				},
				{
					OwnerBindingEpochKey,
					state.OwnerBindingEpoch.ToString(CultureInfo.InvariantCulture)
				},
				{
					CodeEditNativeInstanceIdKey,
					state.CodeEditNativeInstanceId.ToString(CultureInfo.InvariantCulture)
				},
				{ ScriptResourcePathKey, state.ScriptResourcePath },
				{ PrefixOwnedKey, state.PrefixOwned },
				{ PreviousCodeCompletionPrefixesKey, previousPrefixes },
				{ AppliedCodeCompletionPrefixesKey, appliedPrefixes },
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
				{
					AppliedCompletionExistingColorKey,
					state.AppliedCompletionExistingColor
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
		out string failureDetail,
		Action<string, string> nativeBoundaryDiagnosticPhase = null
	)
	{
		failureDetail = "";

		MarkerReadStatus status = Inspect(
			codeEdit,
			out OwnershipState currentState,
			out string readFailure,
			nativeBoundaryDiagnosticPhase
		);
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
			InvokeNativeBoundaryDiagnosticPhase(
				nativeBoundaryDiagnosticPhase,
				"NativeOwnershipBridge.TryClearVerifiedMarker.RemoveMeta.Begin"
			);
			codeEdit.RemoveMeta(MetadataKey);
			InvokeNativeBoundaryDiagnosticPhase(
				nativeBoundaryDiagnosticPhase,
				"NativeOwnershipBridge.TryClearVerifiedMarker.RemoveMeta.Returned"
			);
			return true;
		}
		catch (Exception exception)
		{
			failureDetail =
				$"Marker clear failed: {exception.GetType().Name}: {exception.Message}";
			return false;
		}
	}

	internal static bool MatchesState(OwnershipState left, OwnershipState right)
	{
		if (
			left == null
			|| right == null
			|| left.SchemaVersion != right.SchemaVersion
			|| left.IsLegacy != right.IsLegacy
			|| left.OwnerHostInstanceToken != right.OwnerHostInstanceToken
			|| left.OwnerScriptTransitionId != right.OwnerScriptTransitionId
			|| left.OwnerReloadReadyEpoch != right.OwnerReloadReadyEpoch
			|| left.OwnerBindingEpoch != right.OwnerBindingEpoch
			|| left.CodeEditNativeInstanceId != right.CodeEditNativeInstanceId
			|| !string.Equals(
				left.OwnerManagedAssemblyGeneration,
				right.OwnerManagedAssemblyGeneration,
				StringComparison.Ordinal
			)
			|| !string.Equals(
				ScriptPathUtility.Normalize(left.ScriptResourcePath),
				ScriptPathUtility.Normalize(right.ScriptResourcePath),
				StringComparison.Ordinal
			)
			|| left.PrefixOwned != right.PrefixOwned
			|| left.CompletionExistingColorOwned != right.CompletionExistingColorOwned
			|| left.HadPreviousCompletionExistingColorOverride
				!= right.HadPreviousCompletionExistingColorOverride
			|| !left.PreviousCompletionExistingColor.Equals(
				right.PreviousCompletionExistingColor
			)
			|| !left.AppliedCompletionExistingColor.Equals(
				right.AppliedCompletionExistingColor
			)
			|| !PrefixesEqual(
				left.PreviousCodeCompletionPrefixes,
				right.PreviousCodeCompletionPrefixes
			)
			|| !PrefixesEqual(
				left.AppliedCodeCompletionPrefixes,
				right.AppliedCodeCompletionPrefixes
			)
		)
		{
			return false;
		}

		return true;
	}

	private static MarkerReadStatus DecodeLegacyState(
		Godot.Collections.Dictionary dictionary,
		string ownerManagedAssemblyGeneration,
		ulong codeEditNativeInstanceId,
		out OwnershipState state,
		out string failureDetail,
		Action<string, string> nativeBoundaryDiagnosticPhase
	)
	{
		state = null;
		failureDetail = "";

		if (
			!TryGetBool(dictionary, PrefixOwnedKey, out bool prefixOwned)
			|| !TryGetStringArray(
				dictionary,
				PreviousCodeCompletionPrefixesKey,
				out string[] previousCodeCompletionPrefixes
			)
		)
		{
			failureDetail = "Legacy prefix ownership state is missing or invalid.";
			return CompleteMarkerDecode(MarkerReadStatus.Malformed, nativeBoundaryDiagnosticPhase);
		}
		if (!prefixOwned && previousCodeCompletionPrefixes.Length != 0)
		{
			failureDetail = "Legacy unowned prefix state contains a previous-prefix snapshot.";
			return CompleteMarkerDecode(MarkerReadStatus.Malformed, nativeBoundaryDiagnosticPhase);
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
			failureDetail = "Legacy completion_existing_color ownership state is invalid.";
			return CompleteMarkerDecode(MarkerReadStatus.Malformed, nativeBoundaryDiagnosticPhase);
		}
		if (!completionExistingColorOwned && hadPreviousCompletionExistingColorOverride)
		{
			failureDetail =
				"Legacy unowned completion_existing_color state claims a previous override.";
			return CompleteMarkerDecode(MarkerReadStatus.Malformed, nativeBoundaryDiagnosticPhase);
		}
		if (!prefixOwned && !completionExistingColorOwned)
		{
			failureDetail = "Legacy marker does not describe any owned reversible state.";
			return CompleteMarkerDecode(MarkerReadStatus.Malformed, nativeBoundaryDiagnosticPhase);
		}

		string[] appliedPrefixes = prefixOwned
			? CreateLegacyAppliedPrefixes(previousCodeCompletionPrefixes)
			: Array.Empty<string>();
		state = new OwnershipState(
			LegacySchemaVersion,
			isLegacy: true,
			ownerManagedAssemblyGeneration,
			ownerHostInstanceToken: 0,
			ownerScriptTransitionId: 0,
			ownerReloadReadyEpoch: 0,
			ownerBindingEpoch: 0,
			codeEditNativeInstanceId,
			scriptResourcePath: "",
			prefixOwned,
			previousCodeCompletionPrefixes,
			appliedPrefixes,
			completionExistingColorOwned,
			hadPreviousCompletionExistingColorOverride,
			previousCompletionExistingColor,
			completionExistingColorOwned ? Colors.Transparent : default
		);
		return CompleteMarkerDecode(MarkerReadStatus.Valid, nativeBoundaryDiagnosticPhase);
	}

	private static MarkerReadStatus DecodeCurrentState(
		Godot.Collections.Dictionary dictionary,
		string ownerManagedAssemblyGeneration,
		ulong codeEditNativeInstanceId,
		out OwnershipState state,
		out string failureDetail,
		Action<string, string> nativeBoundaryDiagnosticPhase
	)
	{
		state = null;
		failureDetail = "";
		if (
			!TryGetPositiveLongString(dictionary, OwnerHostInstanceTokenKey, out long ownerHostInstanceToken)
			|| !TryGetPositiveLongString(dictionary, OwnerScriptTransitionIdKey, out long ownerScriptTransitionId)
			|| !TryGetPositiveLongString(dictionary, OwnerReloadReadyEpochKey, out long ownerReloadReadyEpoch)
			|| !TryGetPositiveLongString(dictionary, OwnerBindingEpochKey, out long ownerBindingEpoch)
		)
		{
			failureDetail = "Schema-v2 owner identity is missing or invalid.";
			return CompleteMarkerDecode(MarkerReadStatus.Malformed, nativeBoundaryDiagnosticPhase);
		}

		if (
			!TryGetString(dictionary, ScriptResourcePathKey, out string scriptResourcePath)
			|| string.IsNullOrWhiteSpace(ScriptPathUtility.Normalize(scriptResourcePath))
		)
		{
			failureDetail = "Schema-v2 script resource path is missing or invalid.";
			return CompleteMarkerDecode(MarkerReadStatus.Malformed, nativeBoundaryDiagnosticPhase);
		}
		scriptResourcePath = ScriptPathUtility.Normalize(scriptResourcePath);

		if (
			!TryGetBool(dictionary, PrefixOwnedKey, out bool prefixOwned)
			|| !TryGetStringArray(
				dictionary,
				PreviousCodeCompletionPrefixesKey,
				out string[] previousCodeCompletionPrefixes
			)
			|| !TryGetStringArray(
				dictionary,
				AppliedCodeCompletionPrefixesKey,
				out string[] appliedCodeCompletionPrefixes
			)
		)
		{
			failureDetail = "Schema-v2 prefix ownership state is missing or invalid.";
			return CompleteMarkerDecode(MarkerReadStatus.Malformed, nativeBoundaryDiagnosticPhase);
		}
		if (
			!prefixOwned
			&& (
				previousCodeCompletionPrefixes.Length != 0
				|| appliedCodeCompletionPrefixes.Length != 0
			)
		)
		{
			failureDetail = "Schema-v2 unowned prefix state contains presentation snapshots.";
			return CompleteMarkerDecode(MarkerReadStatus.Malformed, nativeBoundaryDiagnosticPhase);
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
			|| !TryGetColor(
				dictionary,
				AppliedCompletionExistingColorKey,
				out Color appliedCompletionExistingColor
			)
			|| !IsFinite(previousCompletionExistingColor)
			|| !IsFinite(appliedCompletionExistingColor)
		)
		{
			failureDetail = "Schema-v2 completion_existing_color ownership state is invalid.";
			return CompleteMarkerDecode(MarkerReadStatus.Malformed, nativeBoundaryDiagnosticPhase);
		}
		if (!completionExistingColorOwned && hadPreviousCompletionExistingColorOverride)
		{
			failureDetail =
				"Schema-v2 unowned completion_existing_color state claims a previous override.";
			return CompleteMarkerDecode(MarkerReadStatus.Malformed, nativeBoundaryDiagnosticPhase);
		}
		if (!prefixOwned && !completionExistingColorOwned)
		{
			failureDetail = "Schema-v2 marker does not describe any owned reversible state.";
			return CompleteMarkerDecode(MarkerReadStatus.Malformed, nativeBoundaryDiagnosticPhase);
		}

		state = new OwnershipState(
			CurrentSchemaVersion,
			isLegacy: false,
			ownerManagedAssemblyGeneration,
			ownerHostInstanceToken,
			ownerScriptTransitionId,
			ownerReloadReadyEpoch,
			ownerBindingEpoch,
			codeEditNativeInstanceId,
			scriptResourcePath,
			prefixOwned,
			previousCodeCompletionPrefixes,
			appliedCodeCompletionPrefixes,
			completionExistingColorOwned,
			hadPreviousCompletionExistingColorOverride,
			previousCompletionExistingColor,
			appliedCompletionExistingColor
		);
		if (!IsCurrentStateStructurallyValid(state))
		{
			state = null;
			failureDetail = "Schema-v2 marker failed structural validation.";
			return CompleteMarkerDecode(MarkerReadStatus.Malformed, nativeBoundaryDiagnosticPhase);
		}

		return CompleteMarkerDecode(MarkerReadStatus.Valid, nativeBoundaryDiagnosticPhase);
	}

	private static bool IsCurrentStateStructurallyValid(OwnershipState state)
	{
		return state != null
			&& state.SchemaVersion == CurrentSchemaVersion
			&& !state.IsLegacy
			&& !string.IsNullOrWhiteSpace(state.OwnerManagedAssemblyGeneration)
			&& state.OwnerHostInstanceToken > 0
			&& state.OwnerScriptTransitionId > 0
			&& state.OwnerReloadReadyEpoch > 0
			&& state.OwnerBindingEpoch > 0
			&& state.CodeEditNativeInstanceId != 0
			&& !string.IsNullOrWhiteSpace(ScriptPathUtility.Normalize(state.ScriptResourcePath))
			&& state.HasOwnedReversibleState
			&& (
				state.PrefixOwned
				|| (
					state.PreviousCodeCompletionPrefixes.Length == 0
					&& state.AppliedCodeCompletionPrefixes.Length == 0
				)
			)
			&& HasOnlyNonNullPrefixes(state.PreviousCodeCompletionPrefixes)
			&& HasOnlyNonNullPrefixes(state.AppliedCodeCompletionPrefixes)
			&& (
				state.CompletionExistingColorOwned
				|| !state.HadPreviousCompletionExistingColorOverride
			)
			&& IsFinite(state.PreviousCompletionExistingColor)
			&& IsFinite(state.AppliedCompletionExistingColor);
	}

	private static string[] CreateLegacyAppliedPrefixes(IReadOnlyList<string> previousPrefixes)
	{
		int previousCount = previousPrefixes?.Count ?? 0;
		var applied = new string[previousCount + 1];
		for (int index = 0; index < previousCount; index++)
			applied[index] = previousPrefixes[index];
		applied[previousCount] = LegacyMemberAccessPrefix;
		return applied;
	}

	private static Godot.Collections.Array ToVariantArray(IReadOnlyList<string> values)
	{
		var array = new Godot.Collections.Array();
		if (values == null)
			return array;
		for (int index = 0; index < values.Count; index++)
			array.Add(values[index]);
		return array;
	}

	private static MarkerReadStatus CompleteMarkerDecode(
		MarkerReadStatus status,
		Action<string, string> nativeBoundaryDiagnosticPhase
	)
	{
		InvokeNativeBoundaryDiagnosticPhase(
			nativeBoundaryDiagnosticPhase,
			"NativeOwnershipBridge.Inspect.MarkerDecode.Returned",
			$"Status='{status}'"
		);
		return status;
	}

	private static void InvokeNativeBoundaryDiagnosticPhase(
		Action<string, string> nativeBoundaryDiagnosticPhase,
		string phase,
		string details = ""
	)
	{
		try
		{
			nativeBoundaryDiagnosticPhase?.Invoke(phase ?? "", details ?? "");
		}
		catch
		{
			// Operation-local diagnostics must never affect ownership behavior.
		}
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

	private static bool PrefixesEqual(IReadOnlyList<string> left, IReadOnlyList<string> right)
	{
		if (left == null || right == null || left.Count != right.Count)
			return false;
		for (int index = 0; index < left.Count; index++)
		{
			if (!string.Equals(left[index], right[index], StringComparison.Ordinal))
				return false;
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

	private static bool TryGetPositiveLongString(
		Godot.Collections.Dictionary dictionary,
		string key,
		out long value
	)
	{
		value = 0;
		return TryGetString(dictionary, key, out string text)
			&& long.TryParse(
				text,
				NumberStyles.None,
				CultureInfo.InvariantCulture,
				out value
			)
			&& value > 0;
	}

	private static bool TryGetPositiveUlongString(
		Godot.Collections.Dictionary dictionary,
		string key,
		out ulong value
	)
	{
		value = 0;
		return TryGetString(dictionary, key, out string text)
			&& ulong.TryParse(
				text,
				NumberStyles.None,
				CultureInfo.InvariantCulture,
				out value
			)
			&& value > 0;
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
