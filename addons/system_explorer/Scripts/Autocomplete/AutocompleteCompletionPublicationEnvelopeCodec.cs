#if TOOLS
using Godot;
using Godot.Collections;
using System;
using SystemExplorer.Autocomplete.Confirmation;

namespace SystemExplorer.Autocomplete;

internal sealed record AutocompleteCompletionPublicationEnvelope(
	long PublicationId,
	AutocompleteCompletionOptionMetadata ItemMetadata
);

internal sealed class AutocompleteCompletionPublicationEnvelopeCodec
{
	internal const int CurrentVersion = 1;
	internal const string SystemExplorerOwner = "SystemExplorer";

	private const string EnvelopeVersionKey = "publication_envelope_version";
	private const string OwnerKey = "publication_owner";
	private const string PublicationIdKey = "publication_id";
	private const string ItemMetadataKey = "item_metadata";

	private readonly AutocompleteCompletionOptionMetadataCodec _metadataCodec;

	internal AutocompleteCompletionPublicationEnvelopeCodec(
		AutocompleteCompletionOptionMetadataCodec metadataCodec
	)
	{
		_metadataCodec = metadataCodec ?? throw new ArgumentNullException(nameof(metadataCodec));
	}

	internal Dictionary Encode(
		long publicationId,
		AutocompleteCompletionOptionMetadata itemMetadata
	)
	{
		if (publicationId <= 0)
			throw new ArgumentOutOfRangeException(nameof(publicationId));

		var envelope = new Dictionary
		{
			{ EnvelopeVersionKey, CurrentVersion },
			{ OwnerKey, SystemExplorerOwner },
			{ PublicationIdKey, publicationId },
		};

		if (itemMetadata != null)
		{
			Dictionary encodedMetadata = _metadataCodec.Encode(itemMetadata);
			Variant encodedMetadataValue = encodedMetadata;
			envelope.Add(ItemMetadataKey, encodedMetadataValue);
		}

		return envelope;
	}

	internal bool TryDecodePublicationId(Variant value, out long publicationId)
	{
		publicationId = 0;

		if (!TryDecodeEnvelope(value, out Dictionary dictionary, out publicationId))
			return false;

		if (
			dictionary.TryGetValue(ItemMetadataKey, out Variant itemMetadataValue)
			&& itemMetadataValue.VariantType != Variant.Type.Dictionary
		)
		{
			publicationId = 0;
			return false;
		}

		return true;
	}

	internal bool TryDecodeWithItemMetadata(
		Variant value,
		out AutocompleteCompletionPublicationEnvelope envelope
	)
	{
		envelope = null;

		if (
			!TryDecodeEnvelope(value, out Dictionary dictionary, out long publicationId)
			|| !dictionary.TryGetValue(ItemMetadataKey, out Variant itemMetadataValue)
			|| itemMetadataValue.VariantType != Variant.Type.Dictionary
			|| !_metadataCodec.TryDecode(
				itemMetadataValue,
				out AutocompleteCompletionOptionMetadata itemMetadata
			)
		)
		{
			return false;
		}

		envelope = new AutocompleteCompletionPublicationEnvelope(
			publicationId,
			itemMetadata
		);
		return true;
	}

	private static bool TryDecodeEnvelope(
		Variant value,
		out Dictionary dictionary,
		out long publicationId
	)
	{
		dictionary = null;
		publicationId = 0;

		try
		{
			if (value.VariantType != Variant.Type.Dictionary)
				return false;

			dictionary = value.AsGodotDictionary();
			if (dictionary == null)
				return false;

			if (
				!TryGetInt(dictionary, EnvelopeVersionKey, out int version)
				|| version != CurrentVersion
				|| !TryGetString(dictionary, OwnerKey, out string owner)
				|| !string.Equals(owner, SystemExplorerOwner, StringComparison.Ordinal)
				|| !TryGetLong(dictionary, PublicationIdKey, out publicationId)
				|| publicationId <= 0
			)
			{
				dictionary = null;
				publicationId = 0;
				return false;
			}

			return true;
		}
		catch
		{
			dictionary = null;
			publicationId = 0;
			return false;
		}
	}

	private static bool TryGetString(Dictionary dictionary, string key, out string value)
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
		if (!TryGetLong(dictionary, key, out long longValue))
			return false;
		if (longValue < int.MinValue || longValue > int.MaxValue)
			return false;

		value = (int)longValue;
		return true;
	}

	private static bool TryGetLong(Dictionary dictionary, string key, out long value)
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

		value = rawValue.AsInt64();
		return true;
	}
}
#endif
