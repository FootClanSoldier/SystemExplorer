#if TOOLS
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using SystemExplorer.Autocomplete.Indexing.Context;

namespace SystemExplorer.Autocomplete.Indexing.Persistence;

internal static class CSharpProjectIndexCacheConverter
{
	private const int MaximumTextFieldLength = 4096;

	internal static CSharpProjectIndexCacheDocument CreateDocument(
		CSharpProjectIndexSnapshot snapshot,
		CancellationToken cancellationToken
	)
	{
		ArgumentNullException.ThrowIfNull(snapshot);
		cancellationToken.ThrowIfCancellationRequested();

		var document = new CSharpProjectIndexCacheDocument
		{
			CacheFormatVersion = CSharpProjectIndexCacheFormat.CurrentVersion,
			ParseProfile = CSharpProjectIndexCacheFormat.CurrentParseProfile,
			CreatedUtc = DateTime.UtcNow,
			Files = new List<CSharpProjectIndexCacheFileEntry>(snapshot.FileCount),
		};

		foreach (
			CSharpFileIndexEntry fileEntry in snapshot.FilesByResourcePath.Values
				.OrderBy(entry => entry.ResourcePath, StringComparer.OrdinalIgnoreCase)
				.ThenBy(entry => entry.ResourcePath, StringComparer.Ordinal)
		)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var cachedFile = new CSharpProjectIndexCacheFileEntry
			{
				ResourcePath = fileEntry.ResourcePath,
				Length = fileEntry.Length,
				LastWriteTimeUtcTicks = fileEntry.LastWriteTimeUtcTicks,
				SyntaxDiagnosticCount = fileEntry.SyntaxDiagnosticCount,
				Types = new List<CSharpProjectIndexCacheTypeEntry>(fileEntry.Types.Count),
				GlobalUsings = new List<CSharpProjectIndexCacheGlobalUsingEntry>(
					fileEntry.GlobalUsings.Count
				),
			};

			foreach (CSharpProjectTypeSymbol type in fileEntry.Types)
			{
				cancellationToken.ThrowIfCancellationRequested();
				cachedFile.Types.Add(
					new CSharpProjectIndexCacheTypeEntry
					{
						Name = type.Name,
						NamespaceName = type.NamespaceName,
						ContainingTypeNames = type.ContainingTypeNames.ToList(),
						ScriptPath = type.ScriptPath,
						Kind = (int)type.Kind,
						GenericArity = type.GenericArity,
						IsPartial = type.IsPartial,
						IsStatic = type.IsStatic,
						IsAbstract = type.IsAbstract,
					}
				);
			}

			foreach (CSharpUsingDirectiveInfo globalUsing in fileEntry.GlobalUsings)
			{
				cancellationToken.ThrowIfCancellationRequested();
				cachedFile.GlobalUsings.Add(
					new CSharpProjectIndexCacheGlobalUsingEntry
					{
						Kind = (int)globalUsing.Kind,
						Name = globalUsing.Name,
						Alias = globalUsing.Alias,
						ScriptPath = fileEntry.ResourcePath,
					}
				);
			}

			document.Files.Add(cachedFile);
		}

		return document;
	}

	internal static bool TryCreateSeedEntries(
		CSharpProjectIndexCacheDocument document,
		CancellationToken cancellationToken,
		out IReadOnlyDictionary<string, CSharpFileIndexEntry> seedEntries,
		out string failureDetail
	)
	{
		seedEntries = null;
		failureDetail = "";

		if (document == null)
		{
			failureDetail = "Cache document is missing.";
			return false;
		}

		if (document.CreatedUtc == default || document.CreatedUtc.Kind != DateTimeKind.Utc)
		{
			failureDetail = "Cache CreatedUtc is missing or is not UTC.";
			return false;
		}

		if (document.Files == null)
		{
			failureDetail = "Cache file entries are missing.";
			return false;
		}

		var entries = new Dictionary<string, CSharpFileIndexEntry>(
			StringComparer.OrdinalIgnoreCase
		);

		foreach (CSharpProjectIndexCacheFileEntry cachedFile in document.Files)
		{
			cancellationToken.ThrowIfCancellationRequested();

			if (
				!TryCreateSeedEntry(
					cachedFile,
					cancellationToken,
					out CSharpFileIndexEntry seedEntry,
					out failureDetail
				)
			)
			{
				return false;
			}

			if (!entries.TryAdd(seedEntry.ResourcePath, seedEntry))
			{
				failureDetail =
					$"Cache contains duplicate resource path '{seedEntry.ResourcePath}'.";
				return false;
			}
		}

		seedEntries = entries;
		return true;
	}

	private static bool TryCreateSeedEntry(
		CSharpProjectIndexCacheFileEntry cachedFile,
		CancellationToken cancellationToken,
		out CSharpFileIndexEntry seedEntry,
		out string failureDetail
	)
	{
		seedEntry = null;
		failureDetail = "";

		if (cachedFile == null)
		{
			failureDetail = "Cache contains a null file entry.";
			return false;
		}

		if (!IsValidResourcePath(cachedFile.ResourcePath))
		{
			failureDetail = "Cache contains an invalid C# resource path.";
			return false;
		}

		if (
			cachedFile.Length < 0
			|| cachedFile.LastWriteTimeUtcTicks <= 0
			|| cachedFile.LastWriteTimeUtcTicks > DateTime.MaxValue.Ticks
			|| cachedFile.SyntaxDiagnosticCount < 0
		)
		{
			failureDetail =
				$"Cache contains an invalid fingerprint for '{cachedFile.ResourcePath}'.";
			return false;
		}

		if (cachedFile.Types == null)
		{
			failureDetail = $"Cache type entries are missing for '{cachedFile.ResourcePath}'.";
			return false;
		}

		if (!cachedFile.HasGlobalUsingsProperty || cachedFile.GlobalUsings == null)
		{
			failureDetail =
				$"Cache global-using entries are missing for '{cachedFile.ResourcePath}'.";
			return false;
		}

		var types = new List<CSharpProjectTypeSymbol>(cachedFile.Types.Count);
		foreach (CSharpProjectIndexCacheTypeEntry cachedType in cachedFile.Types)
		{
			cancellationToken.ThrowIfCancellationRequested();

			if (
				!TryCreateType(
					cachedFile.ResourcePath,
					cachedType,
					out CSharpProjectTypeSymbol type,
					out failureDetail
				)
			)
			{
				return false;
			}

			types.Add(type);
		}

		var globalUsings = new List<CSharpUsingDirectiveInfo>(
			cachedFile.GlobalUsings.Count
		);
		var globalUsingIdentities = new HashSet<string>(StringComparer.Ordinal);

		foreach (
			CSharpProjectIndexCacheGlobalUsingEntry cachedGlobalUsing in cachedFile.GlobalUsings
		)
		{
			cancellationToken.ThrowIfCancellationRequested();

			if (
				!TryCreateGlobalUsing(
					cachedFile.ResourcePath,
					cachedGlobalUsing,
					out CSharpUsingDirectiveInfo globalUsing,
					out string identity,
					out failureDetail
				)
			)
			{
				return false;
			}

			if (!globalUsingIdentities.Add(identity))
			{
				failureDetail =
					$"Cache contains duplicate global-using data for '{cachedFile.ResourcePath}'.";
				return false;
			}

			globalUsings.Add(globalUsing);
		}

		seedEntry = new CSharpFileIndexEntry(
			cachedFile.ResourcePath,
			"",
			cachedFile.Length,
			cachedFile.LastWriteTimeUtcTicks,
			types,
			cachedFile.SyntaxDiagnosticCount,
			globalUsings
		);
		return true;
	}

	private static bool TryCreateType(
		string fileResourcePath,
		CSharpProjectIndexCacheTypeEntry cachedType,
		out CSharpProjectTypeSymbol type,
		out string failureDetail
	)
	{
		type = null;
		failureDetail = "";

		if (cachedType == null)
		{
			failureDetail = $"Cache contains a null type entry for '{fileResourcePath}'.";
			return false;
		}

		if (!IsValidRequiredText(cachedType.Name))
		{
			failureDetail = $"Cache contains an invalid type name for '{fileResourcePath}'.";
			return false;
		}

		if (!IsValidOptionalText(cachedType.NamespaceName))
		{
			failureDetail =
				$"Cache contains an invalid namespace for '{fileResourcePath}'.";
			return false;
		}

		if (
			!IsValidResourcePath(cachedType.ScriptPath)
			|| !string.Equals(
				cachedType.ScriptPath,
				fileResourcePath,
				StringComparison.OrdinalIgnoreCase
			)
		)
		{
			failureDetail =
				$"Cache contains an invalid type script path for '{fileResourcePath}'.";
			return false;
		}

		if (
			!Enum.IsDefined(typeof(CSharpProjectTypeKind), cachedType.Kind)
			|| cachedType.GenericArity < 0
		)
		{
			failureDetail = $"Cache contains invalid type metadata for '{fileResourcePath}'.";
			return false;
		}

		if (cachedType.ContainingTypeNames == null)
		{
			failureDetail =
				$"Cache containing-type data is missing for '{fileResourcePath}'.";
			return false;
		}

		foreach (string containingTypeName in cachedType.ContainingTypeNames)
		{
			if (!IsValidRequiredText(containingTypeName))
			{
				failureDetail =
					$"Cache contains an invalid containing type for '{fileResourcePath}'.";
				return false;
			}
		}

		type = new CSharpProjectTypeSymbol(
			cachedType.Name,
			cachedType.NamespaceName,
			cachedType.ContainingTypeNames,
			fileResourcePath,
			(CSharpProjectTypeKind)cachedType.Kind,
			cachedType.GenericArity,
			cachedType.IsPartial,
			cachedType.IsStatic,
			cachedType.IsAbstract
		);
		return true;
	}

	private static bool TryCreateGlobalUsing(
		string fileResourcePath,
		CSharpProjectIndexCacheGlobalUsingEntry cachedGlobalUsing,
		out CSharpUsingDirectiveInfo globalUsing,
		out string identity,
		out string failureDetail
	)
	{
		globalUsing = null;
		identity = "";
		failureDetail = "";

		if (cachedGlobalUsing == null)
		{
			failureDetail =
				$"Cache contains a null global-using entry for '{fileResourcePath}'.";
			return false;
		}

		if (
			!Enum.IsDefined(typeof(CSharpUsingDirectiveKind), cachedGlobalUsing.Kind)
			|| !IsGlobalUsingKind((CSharpUsingDirectiveKind)cachedGlobalUsing.Kind)
		)
		{
			failureDetail =
				$"Cache contains an invalid global-using kind for '{fileResourcePath}'.";
			return false;
		}

		if (!IsValidRequiredText(cachedGlobalUsing.Name))
		{
			failureDetail =
				$"Cache contains an invalid global-using name for '{fileResourcePath}'.";
			return false;
		}

		if (!IsValidOptionalText(cachedGlobalUsing.Alias))
		{
			failureDetail =
				$"Cache contains an invalid global-using alias for '{fileResourcePath}'.";
			return false;
		}

		CSharpUsingDirectiveKind kind = (CSharpUsingDirectiveKind)cachedGlobalUsing.Kind;
		if (
			(kind == CSharpUsingDirectiveKind.GlobalAlias
				&& !IsValidRequiredText(cachedGlobalUsing.Alias))
			|| (kind != CSharpUsingDirectiveKind.GlobalAlias
				&& !string.IsNullOrEmpty(cachedGlobalUsing.Alias))
		)
		{
			failureDetail =
				$"Cache contains inconsistent global-using alias data for '{fileResourcePath}'.";
			return false;
		}

		if (
			!IsValidResourcePath(cachedGlobalUsing.ScriptPath)
			|| !string.Equals(
				cachedGlobalUsing.ScriptPath,
				fileResourcePath,
				StringComparison.OrdinalIgnoreCase
			)
		)
		{
			failureDetail =
				$"Cache contains an invalid global-using script path for '{fileResourcePath}'.";
			return false;
		}

		globalUsing = new CSharpUsingDirectiveInfo(
			kind,
			cachedGlobalUsing.Name,
			cachedGlobalUsing.Alias,
			scopeStartLine: 0,
			scopeEndLine: 0
		);
		identity =
			$"{cachedGlobalUsing.Kind}\u001f{cachedGlobalUsing.Name}\u001f{cachedGlobalUsing.Alias}";
		return true;
	}

	private static bool IsGlobalUsingKind(CSharpUsingDirectiveKind kind)
	{
		return kind is CSharpUsingDirectiveKind.GlobalNamespace
			or CSharpUsingDirectiveKind.GlobalAlias
			or CSharpUsingDirectiveKind.GlobalStatic;
	}

	private static bool IsValidResourcePath(string resourcePath)
	{
		if (
			string.IsNullOrWhiteSpace(resourcePath)
			|| resourcePath.Length > MaximumTextFieldLength
			|| !string.Equals(resourcePath, resourcePath.Trim(), StringComparison.Ordinal)
			|| !resourcePath.StartsWith("res://", StringComparison.Ordinal)
			|| !resourcePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
			|| resourcePath.IndexOf('\\') >= 0
			|| resourcePath.IndexOf('\0') >= 0
		)
		{
			return false;
		}

		string relativePath = resourcePath.Substring("res://".Length);
		if (string.IsNullOrWhiteSpace(relativePath) || relativePath.StartsWith('/'))
			return false;

		string[] segments = relativePath.Split('/');
		return segments.All(
			segment =>
				!string.IsNullOrWhiteSpace(segment)
				&& !string.Equals(segment, ".", StringComparison.Ordinal)
				&& !string.Equals(segment, "..", StringComparison.Ordinal)
				&& segment.IndexOf(':') < 0
				&& !ContainsControlCharacter(segment)
		);
	}

	private static bool IsValidRequiredText(string value)
	{
		return !string.IsNullOrWhiteSpace(value)
			&& value.Length <= MaximumTextFieldLength
			&& !ContainsControlCharacter(value);
	}

	private static bool IsValidOptionalText(string value)
	{
		return value != null
			&& value.Length <= MaximumTextFieldLength
			&& !ContainsControlCharacter(value);
	}

	private static bool ContainsControlCharacter(string value)
	{
		return value.Any(char.IsControl);
	}
}
#endif
