#if TOOLS
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using SystemExplorer.Autocomplete.Indexing.Persistence;

namespace SystemExplorer.Autocomplete.Indexing;

internal sealed class CSharpProjectIndexWorker
{
	private readonly CSharpProjectFileInventory _inventory;
	private readonly RoslynProjectTypeScanner _typeScanner;
	private readonly CSharpProjectIndexPersistentCacheStore _cacheStore;

	internal CSharpProjectIndexWorker(
		CSharpProjectFileInventory inventory,
		RoslynProjectTypeScanner typeScanner,
		CSharpProjectIndexPersistentCacheStore cacheStore
	)
	{
		_inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
		_typeScanner = typeScanner ?? throw new ArgumentNullException(nameof(typeScanner));
		_cacheStore = cacheStore ?? throw new ArgumentNullException(nameof(cacheStore));
	}

	internal CSharpProjectIndexBuildResult Build(
		CSharpProjectIndexBuildRequest request,
		CancellationToken cancellationToken
	)
	{
		ArgumentNullException.ThrowIfNull(request);
		var stopwatch = Stopwatch.StartNew();
		CSharpProjectIndexCacheLoadResult cacheLoadResult =
			CSharpProjectIndexCacheLoadResult.NotAttempted();

		try
		{
			cancellationToken.ThrowIfCancellationRequested();

			if (string.IsNullOrWhiteSpace(request.GlobalProjectRoot))
			{
				return CreateFailedResult(
					request,
					stopwatch,
					"Global project root is empty.",
					cacheLoadResult
				);
			}

			CSharpProjectIndexSnapshot previousSnapshot =
				request.PreviousSnapshot ?? CSharpProjectIndexSnapshot.Empty;

			if (
				!previousSnapshot.HasBuiltAtLeastOnce
				&& !string.IsNullOrWhiteSpace(request.CachePath)
			)
			{
				cacheLoadResult = _cacheStore.TryLoad(
					request.CachePath,
					cancellationToken
				);
			}

			cancellationToken.ThrowIfCancellationRequested();

			if (
				!_inventory.TryCreate(
					request.GlobalProjectRoot,
					cancellationToken,
					out IReadOnlyList<CSharpProjectFileDescriptor> inventoryFiles,
					out string inventoryFailure
				)
			)
			{
				return CreateFailedResult(
					request,
					stopwatch,
					inventoryFailure,
					cacheLoadResult
				);
			}

			IReadOnlyDictionary<string, CSharpFileIndexEntry> cacheSeedEntries =
				cacheLoadResult.SeedEntriesByResourcePath;
			var nextEntries = new Dictionary<string, CSharpFileIndexEntry>(
				StringComparer.OrdinalIgnoreCase
			);
			var failureDetails = new List<string>();
			int reusedFileCount = 0;
			int cacheEntriesReused = 0;
			int reparsedFileCount = 0;
			int retainedAfterReadFailureCount = 0;
			int skippedFileCount = 0;

			foreach (CSharpProjectFileDescriptor file in inventoryFiles)
			{
				cancellationToken.ThrowIfCancellationRequested();

				previousSnapshot.FilesByResourcePath.TryGetValue(
					file.ResourcePath,
					out CSharpFileIndexEntry previousEntry
				);

				if (IsUnchanged(file, previousEntry))
				{
					nextEntries[file.ResourcePath] = previousEntry;
					reusedFileCount++;
					continue;
				}

				cacheSeedEntries.TryGetValue(
					file.ResourcePath,
					out CSharpFileIndexEntry cachedSeedEntry
				);

				if (IsUnchanged(file, cachedSeedEntry))
				{
					nextEntries[file.ResourcePath] = CreateVerifiedCacheEntry(
						file,
						cachedSeedEntry
					);
					reusedFileCount++;
					cacheEntriesReused++;
					continue;
				}

				if (
					TryReadAndScan(
						file,
						cancellationToken,
						out CSharpFileIndexEntry indexedEntry,
						out string fileFailure
					)
				)
				{
					nextEntries[file.ResourcePath] = indexedEntry;
					reparsedFileCount++;
					continue;
				}

				if (previousEntry != null)
				{
					nextEntries[file.ResourcePath] = previousEntry;
					retainedAfterReadFailureCount++;
				}
				else
				{
					skippedFileCount++;
				}

				if (failureDetails.Count < 8 && !string.IsNullOrWhiteSpace(fileFailure))
					failureDetails.Add(fileFailure);
			}

			cancellationToken.ThrowIfCancellationRequested();

			CSharpProjectTypeSymbol[] flattenedTypes = inventoryFiles
				.Where(file => nextEntries.ContainsKey(file.ResourcePath))
				.SelectMany(file => nextEntries[file.ResourcePath].Types)
				.ToArray();
			var snapshot = new CSharpProjectIndexSnapshot(
				request.Generation,
				nextEntries,
				flattenedTypes,
				hasBuiltAtLeastOnce: true
			);

			stopwatch.Stop();
			return new CSharpProjectIndexBuildResult(
				request.Generation,
				request.Reason,
				CSharpProjectIndexBuildStatus.Succeeded,
				stopwatch.Elapsed,
				inventoryFiles.Count,
				reusedFileCount,
				reparsedFileCount,
				retainedAfterReadFailureCount,
				skippedFileCount,
				snapshot.TypeCount,
				snapshot.SyntaxDiagnosticCount,
				cacheLoadResult.Status,
				cacheLoadResult.EntriesRead,
				cacheEntriesReused,
				cacheLoadResult.Detail,
				string.Join(" | ", failureDetails),
				CreateSampleTypeNames(flattenedTypes),
				snapshot
			);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			stopwatch.Stop();
			return new CSharpProjectIndexBuildResult(
				request.Generation,
				request.Reason,
				CSharpProjectIndexBuildStatus.Cancelled,
				stopwatch.Elapsed,
				0,
				0,
				0,
				0,
				0,
				0,
				0,
				cacheLoadResult.Status,
				cacheLoadResult.EntriesRead,
				0,
				cacheLoadResult.Detail,
				"Build cancellation was requested.",
				Array.Empty<string>(),
				snapshot: null
			);
		}
		catch (Exception exception)
		{
			stopwatch.Stop();
			return CreateFailedResult(
				request,
				stopwatch,
				CreateExceptionDetail("Unexpected index build failure", exception),
				cacheLoadResult
			);
		}
	}

	private bool TryReadAndScan(
		CSharpProjectFileDescriptor file,
		CancellationToken cancellationToken,
		out CSharpFileIndexEntry entry,
		out string failureDetail
	)
	{
		entry = null;
		failureDetail = "";

		try
		{
			cancellationToken.ThrowIfCancellationRequested();
			string sourceText = File.ReadAllText(file.GlobalPath, Encoding.UTF8);
			cancellationToken.ThrowIfCancellationRequested();
			entry = _typeScanner.ScanFile(file, sourceText, cancellationToken);
			return true;
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception exception) when (IsExpectedFileReadException(exception))
		{
			failureDetail = CreateFileFailureDetail(file.ResourcePath, exception);
			return false;
		}
		catch (Exception exception)
		{
			failureDetail = CreateFileFailureDetail(file.ResourcePath, exception);
			return false;
		}
	}

	private static bool IsExpectedFileReadException(Exception exception)
	{
		return exception is FileNotFoundException
			|| exception is DirectoryNotFoundException
			|| exception is DriveNotFoundException
			|| exception is UnauthorizedAccessException
			|| exception is IOException;
	}

	private static bool IsUnchanged(
		CSharpProjectFileDescriptor file,
		CSharpFileIndexEntry previousEntry
	)
	{
		return previousEntry != null
			&& string.Equals(
				previousEntry.ResourcePath,
				file.ResourcePath,
				StringComparison.OrdinalIgnoreCase
			)
			&& previousEntry.Length == file.Length
			&& previousEntry.LastWriteTimeUtcTicks == file.LastWriteTimeUtcTicks;
	}

	private static CSharpFileIndexEntry CreateVerifiedCacheEntry(
		CSharpProjectFileDescriptor file,
		CSharpFileIndexEntry cachedSeedEntry
	)
	{
		CSharpProjectTypeSymbol[] types = cachedSeedEntry.Types
			.Select(
				type =>
					new CSharpProjectTypeSymbol(
						type.Name,
						type.NamespaceName,
						type.ContainingTypeNames,
						file.ResourcePath,
						type.Kind,
						type.GenericArity,
						type.IsPartial,
						type.IsStatic,
						type.IsAbstract
					)
			)
			.ToArray();

		return new CSharpFileIndexEntry(
			file.ResourcePath,
			file.GlobalPath,
			file.Length,
			file.LastWriteTimeUtcTicks,
			types,
			cachedSeedEntry.SyntaxDiagnosticCount,
			cachedSeedEntry.GlobalUsings
		);
	}

	private static IReadOnlyList<string> CreateSampleTypeNames(
		IReadOnlyList<CSharpProjectTypeSymbol> types
	)
	{
		var seen = new HashSet<string>(StringComparer.Ordinal);
		var samples = new List<string>(20);

		foreach (CSharpProjectTypeSymbol type in types)
		{
			if (type == null || !seen.Add(type.Name))
				continue;

			samples.Add(type.Name);
			if (samples.Count == 20)
				break;
		}

		return samples;
	}

	private static CSharpProjectIndexBuildResult CreateFailedResult(
		CSharpProjectIndexBuildRequest request,
		Stopwatch stopwatch,
		string failureDetail,
		CSharpProjectIndexCacheLoadResult cacheLoadResult
	)
	{
		if (stopwatch.IsRunning)
			stopwatch.Stop();

		cacheLoadResult ??= CSharpProjectIndexCacheLoadResult.NotAttempted();
		return new CSharpProjectIndexBuildResult(
			request.Generation,
			request.Reason,
			CSharpProjectIndexBuildStatus.Failed,
			stopwatch.Elapsed,
			0,
			0,
			0,
			0,
			0,
			0,
			0,
			cacheLoadResult.Status,
			cacheLoadResult.EntriesRead,
			0,
			cacheLoadResult.Detail,
			failureDetail,
			Array.Empty<string>(),
			snapshot: null
		);
	}

	private static string CreateFileFailureDetail(
		string resourcePath,
		Exception exception
	)
	{
		return $"{resourcePath}: {CreateExceptionDetail("File indexing failed", exception)}";
	}

	private static string CreateExceptionDetail(string prefix, Exception exception)
	{
		string message = exception?.Message ?? "Unknown error.";
		message = message.Replace('\r', ' ').Replace('\n', ' ').Trim();
		if (message.Length > 400)
			message = message.Substring(0, 400);

		return $"{prefix}: {exception?.GetType().Name ?? "Exception"}: {message}";
	}
}
#endif
