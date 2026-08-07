#if TOOLS
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;

namespace SystemExplorer.Autocomplete.Indexing.Persistence;

internal sealed class CSharpProjectIndexCacheJsonCodec
{
	private const int MaximumJsonDepth = 64;
	private const int MaximumFiles = 1_000_000;
	private const int MaximumTypesPerFile = 1_000_000;
	private const int MaximumContainingTypeNames = 1_024;
	private const int MaximumGlobalUsingsPerFile = 1_000_000;

	internal void Write(
		Stream destination,
		CSharpProjectIndexCacheDocument document,
		CancellationToken cancellationToken
	)
	{
		ArgumentNullException.ThrowIfNull(destination);
		ArgumentNullException.ThrowIfNull(document);
		cancellationToken.ThrowIfCancellationRequested();

		using var writer = new Utf8JsonWriter(
			destination,
			new JsonWriterOptions
			{
				Indented = false,
				SkipValidation = false,
			}
		);

		writer.WriteStartObject();
		writer.WriteNumber("CacheFormatVersion", document.CacheFormatVersion);
		writer.WriteString("ParseProfile", document.ParseProfile);
		writer.WriteString("CreatedUtc", document.CreatedUtc);
		writer.WritePropertyName("Files");
		writer.WriteStartArray();

		foreach (CSharpProjectIndexCacheFileEntry cachedFile in document.Files)
		{
			cancellationToken.ThrowIfCancellationRequested();
			WriteFile(writer, cachedFile, cancellationToken);
		}

		writer.WriteEndArray();
		writer.WriteEndObject();

		cancellationToken.ThrowIfCancellationRequested();
		writer.Flush();
		cancellationToken.ThrowIfCancellationRequested();
	}

	internal bool TryRead(
		byte[] serializedCache,
		CancellationToken cancellationToken,
		out CSharpProjectIndexCacheDocument document,
		out string failureDetail
	)
	{
		document = null;
		failureDetail = "";

		if (serializedCache == null || serializedCache.Length == 0)
		{
			failureDetail = "Cache JSON is empty.";
			return false;
		}

		cancellationToken.ThrowIfCancellationRequested();

		try
		{
			using JsonDocument jsonDocument = JsonDocument.Parse(
				serializedCache.AsMemory(),
				new JsonDocumentOptions
				{
					MaxDepth = MaximumJsonDepth,
					AllowTrailingCommas = false,
					CommentHandling = JsonCommentHandling.Disallow,
				}
			);
			cancellationToken.ThrowIfCancellationRequested();

			JsonElement root = jsonDocument.RootElement;
			if (root.ValueKind != JsonValueKind.Object)
			{
				failureDetail = "Cache JSON root must be an object.";
				return false;
			}

			if (
				!TryReadRequiredInt32(
					root,
					"CacheFormatVersion",
					out int cacheFormatVersion,
					out failureDetail
				)
				|| !TryReadRequiredString(
					root,
					"ParseProfile",
					out string parseProfile,
					out failureDetail
				)
				|| !TryReadRequiredDateTime(
					root,
					"CreatedUtc",
					out DateTime createdUtc,
					out failureDetail
				)
				|| !TryGetRequiredProperty(
					root,
					"Files",
					JsonValueKind.Array,
					out JsonElement filesElement,
					out failureDetail
				)
			)
			{
				return false;
			}

			if (!TryValidateCollectionCount(filesElement, MaximumFiles, "Files", out failureDetail))
				return false;

			var files = new List<CSharpProjectIndexCacheFileEntry>(filesElement.GetArrayLength());
			int fileIndex = 0;
			foreach (JsonElement fileElement in filesElement.EnumerateArray())
			{
				cancellationToken.ThrowIfCancellationRequested();

				if (
					!TryReadFile(
						fileElement,
						fileIndex,
						cancellationToken,
						out CSharpProjectIndexCacheFileEntry cachedFile,
						out failureDetail
					)
				)
				{
					return false;
				}

				files.Add(cachedFile);
				fileIndex++;
			}

			cancellationToken.ThrowIfCancellationRequested();
			document = new CSharpProjectIndexCacheDocument
			{
				CacheFormatVersion = cacheFormatVersion,
				ParseProfile = parseProfile,
				CreatedUtc = createdUtc,
				Files = files,
			};
			return true;
		}
		catch (JsonException exception)
		{
			failureDetail = CreateJsonFailureDetail(exception);
			return false;
		}
	}

	private static void WriteFile(
		Utf8JsonWriter writer,
		CSharpProjectIndexCacheFileEntry cachedFile,
		CancellationToken cancellationToken
	)
	{
		writer.WriteStartObject();
		writer.WriteString("ResourcePath", cachedFile.ResourcePath);
		writer.WriteNumber("Length", cachedFile.Length);
		writer.WriteNumber("LastWriteTimeUtcTicks", cachedFile.LastWriteTimeUtcTicks);
		writer.WriteNumber("SyntaxDiagnosticCount", cachedFile.SyntaxDiagnosticCount);
		writer.WritePropertyName("Types");
		writer.WriteStartArray();

		foreach (CSharpProjectIndexCacheTypeEntry cachedType in cachedFile.Types)
		{
			cancellationToken.ThrowIfCancellationRequested();
			WriteType(writer, cachedType, cancellationToken);
		}

		writer.WriteEndArray();
		writer.WritePropertyName("GlobalUsings");
		writer.WriteStartArray();

		foreach (CSharpProjectIndexCacheGlobalUsingEntry cachedGlobalUsing in cachedFile.GlobalUsings)
		{
			cancellationToken.ThrowIfCancellationRequested();
			WriteGlobalUsing(writer, cachedGlobalUsing);
		}

		writer.WriteEndArray();
		writer.WriteEndObject();
	}

	private static void WriteType(
		Utf8JsonWriter writer,
		CSharpProjectIndexCacheTypeEntry cachedType,
		CancellationToken cancellationToken
	)
	{
		writer.WriteStartObject();
		writer.WriteString("Name", cachedType.Name);
		writer.WriteString("NamespaceName", cachedType.NamespaceName);
		writer.WritePropertyName("ContainingTypeNames");
		writer.WriteStartArray();

		foreach (string containingTypeName in cachedType.ContainingTypeNames)
		{
			cancellationToken.ThrowIfCancellationRequested();
			writer.WriteStringValue(containingTypeName);
		}

		writer.WriteEndArray();
		writer.WriteString("ScriptPath", cachedType.ScriptPath);
		writer.WriteNumber("Kind", cachedType.Kind);
		writer.WriteNumber("GenericArity", cachedType.GenericArity);
		writer.WriteBoolean("IsPartial", cachedType.IsPartial);
		writer.WriteBoolean("IsStatic", cachedType.IsStatic);
		writer.WriteBoolean("IsAbstract", cachedType.IsAbstract);
		writer.WriteEndObject();
	}

	private static void WriteGlobalUsing(
		Utf8JsonWriter writer,
		CSharpProjectIndexCacheGlobalUsingEntry cachedGlobalUsing
	)
	{
		writer.WriteStartObject();
		writer.WriteNumber("Kind", cachedGlobalUsing.Kind);
		writer.WriteString("Name", cachedGlobalUsing.Name);
		writer.WriteString("Alias", cachedGlobalUsing.Alias);
		writer.WriteString("ScriptPath", cachedGlobalUsing.ScriptPath);
		writer.WriteEndObject();
	}

	private static bool TryReadFile(
		JsonElement fileElement,
		int fileIndex,
		CancellationToken cancellationToken,
		out CSharpProjectIndexCacheFileEntry cachedFile,
		out string failureDetail
	)
	{
		cachedFile = null;
		failureDetail = "";
		string context = $"Files[{fileIndex}]";

		if (fileElement.ValueKind != JsonValueKind.Object)
		{
			failureDetail = $"Cache JSON {context} must be an object.";
			return false;
		}

		if (
			!TryReadRequiredString(
				fileElement,
				"ResourcePath",
				out string resourcePath,
				out failureDetail,
				context
			)
			|| !TryReadRequiredInt64(
				fileElement,
				"Length",
				out long length,
				out failureDetail,
				context
			)
			|| !TryReadRequiredInt64(
				fileElement,
				"LastWriteTimeUtcTicks",
				out long lastWriteTimeUtcTicks,
				out failureDetail,
				context
			)
			|| !TryReadRequiredInt32(
				fileElement,
				"SyntaxDiagnosticCount",
				out int syntaxDiagnosticCount,
				out failureDetail,
				context
			)
			|| !TryGetRequiredProperty(
				fileElement,
				"Types",
				JsonValueKind.Array,
				out JsonElement typesElement,
				out failureDetail,
				context
			)
		)
		{
			return false;
		}

		if (
			!TryValidateCollectionCount(
				typesElement,
				MaximumTypesPerFile,
				$"{context}.Types",
				out failureDetail
			)
		)
		{
			return false;
		}

		var types = new List<CSharpProjectIndexCacheTypeEntry>(typesElement.GetArrayLength());
		int typeIndex = 0;
		foreach (JsonElement typeElement in typesElement.EnumerateArray())
		{
			cancellationToken.ThrowIfCancellationRequested();

			if (
				!TryReadType(
					typeElement,
					context,
					typeIndex,
					cancellationToken,
					out CSharpProjectIndexCacheTypeEntry cachedType,
					out failureDetail
				)
			)
			{
				return false;
			}

			types.Add(cachedType);
			typeIndex++;
		}

		if (
			!TryGetRequiredProperty(
				fileElement,
				"GlobalUsings",
				JsonValueKind.Array,
				out JsonElement globalUsingsElement,
				out failureDetail,
				context
			)
		)
		{
			return false;
		}

		if (
			!TryValidateCollectionCount(
				globalUsingsElement,
				MaximumGlobalUsingsPerFile,
				$"{context}.GlobalUsings",
				out failureDetail
			)
		)
		{
			return false;
		}

		var globalUsings = new List<CSharpProjectIndexCacheGlobalUsingEntry>(
			globalUsingsElement.GetArrayLength()
		);
		int globalUsingIndex = 0;
		foreach (JsonElement globalUsingElement in globalUsingsElement.EnumerateArray())
		{
			cancellationToken.ThrowIfCancellationRequested();

			if (
				!TryReadGlobalUsing(
					globalUsingElement,
					context,
					globalUsingIndex,
					out CSharpProjectIndexCacheGlobalUsingEntry cachedGlobalUsing,
					out failureDetail
				)
			)
			{
				return false;
			}

			globalUsings.Add(cachedGlobalUsing);
			globalUsingIndex++;
		}

		cancellationToken.ThrowIfCancellationRequested();
		cachedFile = new CSharpProjectIndexCacheFileEntry
		{
			ResourcePath = resourcePath,
			Length = length,
			LastWriteTimeUtcTicks = lastWriteTimeUtcTicks,
			SyntaxDiagnosticCount = syntaxDiagnosticCount,
			Types = types,
		};

		// Assign only after the property was found and structurally validated so
		// HasGlobalUsingsProperty continues to distinguish incomplete v2 data.
		cachedFile.GlobalUsings = globalUsings;
		return true;
	}

	private static bool TryReadType(
		JsonElement typeElement,
		string fileContext,
		int typeIndex,
		CancellationToken cancellationToken,
		out CSharpProjectIndexCacheTypeEntry cachedType,
		out string failureDetail
	)
	{
		cachedType = null;
		failureDetail = "";
		string context = $"{fileContext}.Types[{typeIndex}]";

		if (typeElement.ValueKind != JsonValueKind.Object)
		{
			failureDetail = $"Cache JSON {context} must be an object.";
			return false;
		}

		if (
			!TryReadRequiredString(
				typeElement,
				"Name",
				out string name,
				out failureDetail,
				context
			)
			|| !TryReadRequiredString(
				typeElement,
				"NamespaceName",
				out string namespaceName,
				out failureDetail,
				context
			)
			|| !TryGetRequiredProperty(
				typeElement,
				"ContainingTypeNames",
				JsonValueKind.Array,
				out JsonElement containingTypeNamesElement,
				out failureDetail,
				context
			)
			|| !TryReadRequiredString(
				typeElement,
				"ScriptPath",
				out string scriptPath,
				out failureDetail,
				context
			)
			|| !TryReadRequiredInt32(
				typeElement,
				"Kind",
				out int kind,
				out failureDetail,
				context
			)
			|| !TryReadRequiredInt32(
				typeElement,
				"GenericArity",
				out int genericArity,
				out failureDetail,
				context
			)
			|| !TryReadRequiredBoolean(
				typeElement,
				"IsPartial",
				out bool isPartial,
				out failureDetail,
				context
			)
			|| !TryReadRequiredBoolean(
				typeElement,
				"IsStatic",
				out bool isStatic,
				out failureDetail,
				context
			)
			|| !TryReadRequiredBoolean(
				typeElement,
				"IsAbstract",
				out bool isAbstract,
				out failureDetail,
				context
			)
		)
		{
			return false;
		}

		if (
			!TryValidateCollectionCount(
				containingTypeNamesElement,
				MaximumContainingTypeNames,
				$"{context}.ContainingTypeNames",
				out failureDetail
			)
		)
		{
			return false;
		}

		var containingTypeNames = new List<string>(containingTypeNamesElement.GetArrayLength());
		int containingTypeIndex = 0;
		foreach (JsonElement containingTypeNameElement in containingTypeNamesElement.EnumerateArray())
		{
			cancellationToken.ThrowIfCancellationRequested();

			if (containingTypeNameElement.ValueKind != JsonValueKind.String)
			{
				failureDetail =
					$"Cache JSON {context}.ContainingTypeNames[{containingTypeIndex}] must be a string.";
				return false;
			}

			containingTypeNames.Add(containingTypeNameElement.GetString() ?? "");
			containingTypeIndex++;
		}

		cachedType = new CSharpProjectIndexCacheTypeEntry
		{
			Name = name,
			NamespaceName = namespaceName,
			ContainingTypeNames = containingTypeNames,
			ScriptPath = scriptPath,
			Kind = kind,
			GenericArity = genericArity,
			IsPartial = isPartial,
			IsStatic = isStatic,
			IsAbstract = isAbstract,
		};
		return true;
	}

	private static bool TryReadGlobalUsing(
		JsonElement globalUsingElement,
		string fileContext,
		int globalUsingIndex,
		out CSharpProjectIndexCacheGlobalUsingEntry cachedGlobalUsing,
		out string failureDetail
	)
	{
		cachedGlobalUsing = null;
		failureDetail = "";
		string context = $"{fileContext}.GlobalUsings[{globalUsingIndex}]";

		if (globalUsingElement.ValueKind != JsonValueKind.Object)
		{
			failureDetail = $"Cache JSON {context} must be an object.";
			return false;
		}

		if (
			!TryReadRequiredInt32(
				globalUsingElement,
				"Kind",
				out int kind,
				out failureDetail,
				context
			)
			|| !TryReadRequiredString(
				globalUsingElement,
				"Name",
				out string name,
				out failureDetail,
				context
			)
			|| !TryReadRequiredString(
				globalUsingElement,
				"Alias",
				out string alias,
				out failureDetail,
				context
			)
			|| !TryReadRequiredString(
				globalUsingElement,
				"ScriptPath",
				out string scriptPath,
				out failureDetail,
				context
			)
		)
		{
			return false;
		}

		cachedGlobalUsing = new CSharpProjectIndexCacheGlobalUsingEntry
		{
			Kind = kind,
			Name = name,
			Alias = alias,
			ScriptPath = scriptPath,
		};
		return true;
	}

	private static bool TryReadRequiredString(
		JsonElement parent,
		string propertyName,
		out string value,
		out string failureDetail,
		string context = "document"
	)
	{
		value = "";
		if (
			!TryGetRequiredProperty(
				parent,
				propertyName,
				JsonValueKind.String,
				out JsonElement element,
				out failureDetail,
				context
			)
		)
		{
			return false;
		}

		value = element.GetString() ?? "";
		return true;
	}

	private static bool TryReadRequiredDateTime(
		JsonElement parent,
		string propertyName,
		out DateTime value,
		out string failureDetail,
		string context = "document"
	)
	{
		value = default;
		if (
			!TryGetRequiredProperty(
				parent,
				propertyName,
				JsonValueKind.String,
				out JsonElement element,
				out failureDetail,
				context
			)
		)
		{
			return false;
		}

		if (!element.TryGetDateTime(out value))
		{
			failureDetail = $"Cache JSON {context}.{propertyName} is not a valid DateTime.";
			return false;
		}

		return true;
	}

	private static bool TryReadRequiredInt32(
		JsonElement parent,
		string propertyName,
		out int value,
		out string failureDetail,
		string context = "document"
	)
	{
		value = default;
		if (
			!TryGetRequiredProperty(
				parent,
				propertyName,
				JsonValueKind.Number,
				out JsonElement element,
				out failureDetail,
				context
			)
		)
		{
			return false;
		}

		if (!IsIntegerToken(element) || !element.TryGetInt32(out value))
		{
			failureDetail = $"Cache JSON {context}.{propertyName} must be a 32-bit integer.";
			return false;
		}

		return true;
	}

	private static bool TryReadRequiredInt64(
		JsonElement parent,
		string propertyName,
		out long value,
		out string failureDetail,
		string context = "document"
	)
	{
		value = default;
		if (
			!TryGetRequiredProperty(
				parent,
				propertyName,
				JsonValueKind.Number,
				out JsonElement element,
				out failureDetail,
				context
			)
		)
		{
			return false;
		}

		if (!IsIntegerToken(element) || !element.TryGetInt64(out value))
		{
			failureDetail = $"Cache JSON {context}.{propertyName} must be a 64-bit integer.";
			return false;
		}

		return true;
	}

	private static bool TryReadRequiredBoolean(
		JsonElement parent,
		string propertyName,
		out bool value,
		out string failureDetail,
		string context = "document"
	)
	{
		value = default;

		if (!parent.TryGetProperty(propertyName, out JsonElement element))
		{
			failureDetail = $"Cache JSON {context}.{propertyName} is missing.";
			return false;
		}

		if (element.ValueKind == JsonValueKind.True)
		{
			value = true;
			failureDetail = "";
			return true;
		}

		if (element.ValueKind == JsonValueKind.False)
		{
			value = false;
			failureDetail = "";
			return true;
		}

		failureDetail = $"Cache JSON {context}.{propertyName} must be a boolean.";
		return false;
	}

	private static bool TryGetRequiredProperty(
		JsonElement parent,
		string propertyName,
		JsonValueKind expectedKind,
		out JsonElement element,
		out string failureDetail,
		string context = "document"
	)
	{
		if (!parent.TryGetProperty(propertyName, out element))
		{
			failureDetail = $"Cache JSON {context}.{propertyName} is missing.";
			return false;
		}

		if (element.ValueKind != expectedKind)
		{
			failureDetail =
				$"Cache JSON {context}.{propertyName} must be {GetKindDescription(expectedKind)}.";
			return false;
		}

		failureDetail = "";
		return true;
	}

	private static bool TryValidateCollectionCount(
		JsonElement arrayElement,
		int maximumCount,
		string context,
		out string failureDetail
	)
	{
		int count = arrayElement.GetArrayLength();
		if (count > maximumCount)
		{
			failureDetail = $"Cache JSON {context} exceeds the supported collection limit.";
			return false;
		}

		failureDetail = "";
		return true;
	}

	private static bool IsIntegerToken(JsonElement element)
	{
		string rawText = element.GetRawText();
		if (rawText.Length == 0)
			return false;

		int index = rawText[0] == '-' ? 1 : 0;
		if (index >= rawText.Length)
			return false;

		for (; index < rawText.Length; index++)
		{
			char character = rawText[index];
			if (character < '0' || character > '9')
				return false;
		}

		return true;
	}

	private static string GetKindDescription(JsonValueKind kind)
	{
		return kind switch
		{
			JsonValueKind.Object => "an object",
			JsonValueKind.Array => "an array",
			JsonValueKind.String => "a string",
			JsonValueKind.Number => "a number",
			_ => kind.ToString(),
		};
	}

	private static string CreateJsonFailureDetail(JsonException exception)
	{
		string message = exception?.Message ?? "Invalid JSON.";
		message = message.Replace('\r', ' ').Replace('\n', ' ').Trim();
		if (message.Length > 300)
			message = message.Substring(0, 300);

		return $"Cache JSON parse failed: {message}";
	}
}
#endif
