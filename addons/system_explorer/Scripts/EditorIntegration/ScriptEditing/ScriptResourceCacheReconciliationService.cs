#if TOOLS
using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

namespace SystemExplorer.EditorIntegration.ScriptEditing;

internal readonly record struct ScriptResourceCacheReconciliationFailure(
	string Path,
	string FailureDetail
);

internal sealed class ScriptResourceCacheReconciliationResult
{
	internal bool WasAttempted { get; }
	internal bool Success => Failures.Count == 0;
	internal int AttemptedPathCount { get; }
	internal IReadOnlyList<string> NotCachedPaths { get; }
	internal IReadOnlyList<string> AlreadyCurrentPaths { get; }
	internal IReadOnlyList<string> SourceUpdatedPaths { get; }
	internal IReadOnlyList<ScriptResourceCacheReconciliationFailure> Failures { get; }
	internal IReadOnlyList<string> FailedPaths { get; }

	private ScriptResourceCacheReconciliationResult(
		bool wasAttempted,
		int attemptedPathCount,
		IEnumerable<string> notCachedPaths,
		IEnumerable<string> alreadyCurrentPaths,
		IEnumerable<string> sourceUpdatedPaths,
		IEnumerable<ScriptResourceCacheReconciliationFailure> failures
	)
	{
		WasAttempted = wasAttempted;
		AttemptedPathCount = Math.Max(0, attemptedPathCount);
		NotCachedPaths = CreateReadOnlyList(notCachedPaths);
		AlreadyCurrentPaths = CreateReadOnlyList(alreadyCurrentPaths);
		SourceUpdatedPaths = CreateReadOnlyList(sourceUpdatedPaths);
		Failures = CreateReadOnlyList(failures);
		FailedPaths = Failures.Select(failure => failure.Path).ToList().AsReadOnly();
	}

	internal static ScriptResourceCacheReconciliationResult NotAttempted() =>
		new(false, 0, null, null, null, null);

	internal static ScriptResourceCacheReconciliationResult Completed(
		int attemptedPathCount,
		IEnumerable<string> notCachedPaths,
		IEnumerable<string> alreadyCurrentPaths,
		IEnumerable<string> sourceUpdatedPaths,
		IEnumerable<ScriptResourceCacheReconciliationFailure> failures
	) =>
		new(
			true,
			attemptedPathCount,
			notCachedPaths,
			alreadyCurrentPaths,
			sourceUpdatedPaths,
			failures
		);

	internal static ScriptResourceCacheReconciliationResult FailedForPaths(
		IEnumerable<string> paths,
		string failureDetail
	)
	{
		List<string> orderedPaths = paths?.ToList() ?? new List<string>();
		string normalizedFailureDetail = NormalizeFailureDetail(failureDetail);
		List<ScriptResourceCacheReconciliationFailure> failures = orderedPaths
			.Select(path =>
				new ScriptResourceCacheReconciliationFailure(path ?? "", normalizedFailureDetail)
			)
			.ToList();

		if (failures.Count == 0)
			failures.Add(new ScriptResourceCacheReconciliationFailure("", normalizedFailureDetail));

		return new ScriptResourceCacheReconciliationResult(
			true,
			Math.Max(1, orderedPaths.Count),
			null,
			null,
			null,
			failures
		);
	}

	private static string NormalizeFailureDetail(string detail)
	{
		if (string.IsNullOrWhiteSpace(detail))
			return "";

		string normalized = detail.Replace('\r', ' ').Replace('\n', ' ').Trim();
		const int maximumLength = 512;
		return normalized.Length <= maximumLength ? normalized : normalized[..maximumLength];
	}

	private static IReadOnlyList<T> CreateReadOnlyList<T>(IEnumerable<T> source)
	{
		List<T> copy = source == null ? new List<T>() : new List<T>(source);
		return copy.AsReadOnly();
	}
}

internal sealed class ScriptResourceCacheReconciliationService
{
	private const int MaximumFailureDetailLength = 512;

	internal ScriptResourceCacheReconciliationResult ReconcileCommittedTexts(
		IReadOnlyDictionary<string, string> committedTextsByPath
	)
	{
		if (committedTextsByPath == null)
		{
			return ScriptResourceCacheReconciliationResult.FailedForPaths(
				Array.Empty<string>(),
				"Committed text dictionary was null."
			);
		}

		List<string> notCachedPaths = new();
		List<string> alreadyCurrentPaths = new();
		List<string> sourceUpdatedPaths = new();
		List<ScriptResourceCacheReconciliationFailure> failures = new();

		foreach (KeyValuePair<string, string> committedTextPair in committedTextsByPath)
		{
			string normalizedPath = "";

			try
			{
				normalizedPath = ScriptPathUtility.Normalize(committedTextPair.Key);

				if (string.IsNullOrWhiteSpace(normalizedPath))
				{
					failures.Add(
						new ScriptResourceCacheReconciliationFailure(
							normalizedPath,
							"Path normalization produced an empty path."
						)
					);
					continue;
				}

				if (!ResourceLoader.HasCached(normalizedPath))
				{
					notCachedPaths.Add(normalizedPath);
					continue;
				}

				Resource cachedResource = ResourceLoader.GetCachedRef(normalizedPath);

				if (cachedResource == null || !GodotObject.IsInstanceValid(cachedResource))
				{
					failures.Add(
						new ScriptResourceCacheReconciliationFailure(
							normalizedPath,
							"Cached resource reference was null or invalid."
						)
					);
					continue;
				}

				if (cachedResource is not Script cachedScript)
				{
					failures.Add(
						new ScriptResourceCacheReconciliationFailure(
							normalizedPath,
							SanitizeFailureDetail(
								$"Cached resource type was '{cachedResource.GetType().Name}', expected Script."
							)
						)
					);
					continue;
				}

				if (!GodotObject.IsInstanceValid(cachedScript))
				{
					failures.Add(
						new ScriptResourceCacheReconciliationFailure(
							normalizedPath,
							"Cached Script instance was invalid."
						)
					);
					continue;
				}

				string cachedResourcePath = ScriptPathUtility.Normalize(cachedScript.ResourcePath);
				if (!PathsMatch(cachedResourcePath, normalizedPath))
				{
					failures.Add(
						new ScriptResourceCacheReconciliationFailure(
							normalizedPath,
							SanitizeFailureDetail(
								$"Cached Script ResourcePath normalized to '{cachedResourcePath}', expected '{normalizedPath}'."
							)
						)
					);
					continue;
				}

				if (
					ScriptTextFileService.TextsMatchForDiskVerification(
						cachedScript.SourceCode,
						committedTextPair.Value
					)
				)
				{
					alreadyCurrentPaths.Add(normalizedPath);
					continue;
				}

				ulong cachedInstanceId = cachedScript.GetInstanceId();
				cachedScript.SourceCode = committedTextPair.Value;

				if (!GodotObject.IsInstanceValid(cachedScript))
				{
					failures.Add(
						new ScriptResourceCacheReconciliationFailure(
							normalizedPath,
							"Cached Script instance became invalid after SourceCode assignment."
						)
					);
					continue;
				}

				ulong updatedInstanceId = cachedScript.GetInstanceId();
				if (updatedInstanceId != cachedInstanceId)
				{
					failures.Add(
						new ScriptResourceCacheReconciliationFailure(
							normalizedPath,
							$"SourceCode assignment changed Script instance identity from {cachedInstanceId} to {updatedInstanceId}."
						)
					);
					continue;
				}

				string updatedResourcePath = ScriptPathUtility.Normalize(cachedScript.ResourcePath);
				if (!PathsMatch(updatedResourcePath, normalizedPath))
				{
					failures.Add(
						new ScriptResourceCacheReconciliationFailure(
							normalizedPath,
							SanitizeFailureDetail(
								$"Cached Script ResourcePath normalized to '{updatedResourcePath}' after SourceCode assignment, expected '{normalizedPath}'."
							)
						)
					);
					continue;
				}

				if (
					!ScriptTextFileService.TextsMatchForDiskVerification(
						cachedScript.SourceCode,
						committedTextPair.Value
					)
				)
				{
					failures.Add(
						new ScriptResourceCacheReconciliationFailure(
							normalizedPath,
							"Cached Script SourceCode did not match committed text after SourceCode assignment."
						)
					);
					continue;
				}

				sourceUpdatedPaths.Add(normalizedPath);
			}
			catch (Exception exception)
			{
				failures.Add(
					new ScriptResourceCacheReconciliationFailure(
						normalizedPath,
						FormatExceptionFailure(exception)
					)
				);
			}
		}

		return ScriptResourceCacheReconciliationResult.Completed(
			committedTextsByPath.Count,
			notCachedPaths,
			alreadyCurrentPaths,
			sourceUpdatedPaths,
			failures
		);
	}

	private static bool PathsMatch(string left, string right) =>
		string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

	private static string FormatExceptionFailure(Exception exception)
	{
		string exceptionType = exception?.GetType().Name ?? "UnknownException";
		string message = SanitizeFailureDetail(exception?.Message);
		return string.IsNullOrWhiteSpace(message)
			? $"{exceptionType}."
			: SanitizeFailureDetail($"{exceptionType}: {message}");
	}

	private static string SanitizeFailureDetail(string detail)
	{
		if (string.IsNullOrWhiteSpace(detail))
			return "";

		string sanitized = detail.Replace('\r', ' ').Replace('\n', ' ').Trim();
		return sanitized.Length <= MaximumFailureDetailLength
			? sanitized
			: sanitized[..MaximumFailureDetailLength];
	}
}
#endif
