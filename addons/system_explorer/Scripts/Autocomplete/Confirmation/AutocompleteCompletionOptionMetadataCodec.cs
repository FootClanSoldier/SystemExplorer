#if TOOLS
using Godot;
using Godot.Collections;
using System;

namespace SystemExplorer.Autocomplete.Confirmation;

internal sealed class AutocompleteCompletionOptionMetadataCodec
{
	private const string MetadataVersionKey = "metadata_version";
	private const string OwnerKey = "owner";
	private const string SourceKey = "source";
	private const string IdentityKey = "identity";
	private const string NameKey = "name";
	private const string NamespaceKey = "namespace";
	private const string QualifierKey = "qualifier";
	private const string GenericArityKey = "generic_arity";
	private const string AvailabilityPriorityKey = "availability_priority";
	private const string HasSimpleNameConflictKey = "has_simple_name_conflict";
	private const string IsNestedTypeKey = "is_nested_type";

	internal Dictionary Encode(AutocompleteCompletionOptionMetadata metadata)
	{
		if (metadata == null)
			throw new ArgumentNullException(nameof(metadata));

		return new Dictionary
		{
			{ MetadataVersionKey, metadata.Version },
			{ OwnerKey, metadata.Owner ?? "" },
			{ SourceKey, metadata.Source ?? "" },
			{ IdentityKey, metadata.Identity ?? "" },
			{ NameKey, metadata.Name ?? "" },
			{ NamespaceKey, metadata.NamespaceName ?? "" },
			{ QualifierKey, metadata.Qualifier ?? "" },
			{ GenericArityKey, metadata.GenericArity },
			{ AvailabilityPriorityKey, metadata.AvailabilityPriority },
			{ HasSimpleNameConflictKey, metadata.HasSimpleNameConflict },
			{ IsNestedTypeKey, metadata.IsNestedType },
		};
	}

	internal bool TryDecode(
		Variant value,
		out AutocompleteCompletionOptionMetadata metadata
	)
	{
		metadata = null;

		try
		{
			if (value.VariantType != Variant.Type.Dictionary)
				return false;

			Dictionary dictionary = value.AsGodotDictionary();
			if (dictionary == null)
				return false;

			if (
				!TryGetInt(dictionary, MetadataVersionKey, out int version)
				|| version != AutocompleteCompletionOptionMetadata.CurrentVersion
				|| !TryGetString(dictionary, OwnerKey, out string owner)
				|| !string.Equals(
					owner,
					AutocompleteCompletionOptionMetadata.SystemExplorerOwner,
					StringComparison.Ordinal
				)
				|| !TryGetString(dictionary, SourceKey, out string source)
				|| string.IsNullOrWhiteSpace(source)
				|| !TryGetString(dictionary, IdentityKey, out string identity)
				|| string.IsNullOrWhiteSpace(identity)
				|| !TryGetString(dictionary, NameKey, out string name)
				|| string.IsNullOrWhiteSpace(name)
				|| !TryGetString(dictionary, NamespaceKey, out string namespaceName)
				|| !TryGetString(dictionary, QualifierKey, out string qualifier)
				|| !TryGetInt(dictionary, GenericArityKey, out int genericArity)
				|| !TryGetInt(
					dictionary,
					AvailabilityPriorityKey,
					out int availabilityPriority
				)
				|| !TryGetBool(
					dictionary,
					HasSimpleNameConflictKey,
					out bool hasSimpleNameConflict
				)
				|| !TryGetBool(dictionary, IsNestedTypeKey, out bool isNestedType)
			)
			{
				return false;
			}

			if (
				genericArity < 0
				|| availabilityPriority < 0
				|| availabilityPriority > 4
			)
			{
				return false;
			}

			metadata = new AutocompleteCompletionOptionMetadata(
				version,
				owner,
				source,
				identity,
				name,
				namespaceName,
				qualifier,
				genericArity,
				availabilityPriority,
				hasSimpleNameConflict,
				isNestedType
			);
			return true;
		}
		catch
		{
			metadata = null;
			return false;
		}
	}

	private static bool TryGetString(
		Dictionary dictionary,
		string key,
		out string value
	)
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

	private static bool TryGetInt(Dictionary dictionary, string key, out int value)
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

	private static bool TryGetBool(Dictionary dictionary, string key, out bool value)
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
}
#endif
