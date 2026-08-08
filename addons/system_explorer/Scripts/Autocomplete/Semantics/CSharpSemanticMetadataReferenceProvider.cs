#if TOOLS
using Microsoft.CodeAnalysis;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SystemExplorer.Autocomplete.Semantics;

internal sealed class CSharpSemanticMetadataReferenceProvider
{
	private MetadataReferenceSet _cachedReferenceSet;

	internal MetadataReferenceSet GetReferences()
	{
		if (_cachedReferenceSet != null)
			return _cachedReferenceSet;

		var references = new List<MetadataReference>();
		var failures = new List<string>();
		var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		string trustedPlatformAssemblies =
			AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;

		if (string.IsNullOrWhiteSpace(trustedPlatformAssemblies))
		{
			failures.Add("TRUSTED_PLATFORM_ASSEMBLIES was unavailable.");
		}
		else
		{
			foreach (
				string candidatePath in trustedPlatformAssemblies.Split(
					Path.PathSeparator,
					StringSplitOptions.RemoveEmptyEntries
				)
			)
			{
				string path = candidatePath?.Trim() ?? "";
				if (string.IsNullOrWhiteSpace(path) || !seenPaths.Add(path))
					continue;

				try
				{
					if (!File.Exists(path))
					{
						AddFailure(failures, $"Metadata reference file was missing: '{path}'.");
						continue;
					}

					references.Add(MetadataReference.CreateFromFile(path));
				}
				catch (Exception exception)
				{
					AddFailure(
						failures,
						$"Metadata reference failed for '{path}': "
							+ $"{exception.GetType().Name}: {NormalizeMessage(exception.Message)}"
					);
				}
			}
		}

		_cachedReferenceSet = new MetadataReferenceSet(
			references.ToArray(),
			failures.ToArray()
		);
		return _cachedReferenceSet;
	}

	private static void AddFailure(List<string> failures, string detail)
	{
		if (failures.Count < 8 && !string.IsNullOrWhiteSpace(detail))
			failures.Add(detail);
	}

	private static string NormalizeMessage(string message)
	{
		if (string.IsNullOrWhiteSpace(message))
			return "Unknown error.";

		string normalized = message.Replace('\r', ' ').Replace('\n', ' ').Trim();
		return normalized.Length <= 300 ? normalized : normalized.Substring(0, 300);
	}

	internal sealed class MetadataReferenceSet
	{
		internal MetadataReferenceSet(
			IReadOnlyList<MetadataReference> references,
			IReadOnlyList<string> failures
		)
		{
			References = (references ?? Array.Empty<MetadataReference>()).ToArray();
			Failures = (failures ?? Array.Empty<string>())
				.Where(failure => !string.IsNullOrWhiteSpace(failure))
				.ToArray();
		}

		internal IReadOnlyList<MetadataReference> References { get; }
		internal IReadOnlyList<string> Failures { get; }
	}
}
#endif
