#if TOOLS
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace SystemExplorer.Autocomplete.Indexing.Persistence;

internal sealed class CSharpProjectIndexPersistentCacheStore
{
	private const int IoBufferSizeBytes = 64 * 1024;
	// Bounds cache input and prepared in-memory JSON output; JsonDocument parsing remains bounded by this limit.
	private const long MaximumCacheFileSizeBytes = 64L * 1024 * 1024;

	private readonly CSharpProjectIndexCacheJsonCodec _jsonCodec;

	internal CSharpProjectIndexPersistentCacheStore(
		CSharpProjectIndexCacheJsonCodec jsonCodec
	)
	{
		_jsonCodec = jsonCodec ?? throw new ArgumentNullException(nameof(jsonCodec));
	}

	internal CSharpProjectIndexCacheLoadResult TryLoad(
		string cachePath,
		CancellationToken cancellationToken
	)
	{
		try
		{
			cancellationToken.ThrowIfCancellationRequested();

			if (!TryNormalizeCachePath(cachePath, out string normalizedCachePath))
			{
				return CSharpProjectIndexCacheLoadResult.Ignored(
					"Cache path is empty, invalid, or not fully qualified."
				);
			}

			if (!File.Exists(normalizedCachePath))
				return CSharpProjectIndexCacheLoadResult.Missing();

			byte[] serializedCache;
			using (
				var stream = new FileStream(
					normalizedCachePath,
					FileMode.Open,
					FileAccess.Read,
					FileShare.ReadWrite | FileShare.Delete,
					bufferSize: IoBufferSizeBytes,
					options: FileOptions.SequentialScan
				)
			)
			{
				long cacheLength = stream.Length;
				if (cacheLength <= 0)
					return CSharpProjectIndexCacheLoadResult.Ignored("Cache file is empty.");

				if (cacheLength > MaximumCacheFileSizeBytes)
				{
					return CSharpProjectIndexCacheLoadResult.Ignored(
						$"Cache file exceeds the {MaximumCacheFileSizeBytes / (1024 * 1024)} MiB safety limit."
					);
				}

				serializedCache = ReadCacheBytes(
					stream,
					checked((int)cacheLength),
					cancellationToken
				);
			}

			cancellationToken.ThrowIfCancellationRequested();
			if (
				!_jsonCodec.TryRead(
					serializedCache,
					cancellationToken,
					out CSharpProjectIndexCacheDocument document,
					out string parseFailure
				)
			)
			{
				return CSharpProjectIndexCacheLoadResult.Ignored(parseFailure);
			}
			cancellationToken.ThrowIfCancellationRequested();

			if (document.CacheFormatVersion != CSharpProjectIndexCacheFormat.CurrentVersion)
			{
				return CSharpProjectIndexCacheLoadResult.Ignored(
					$"Cache format version {document.CacheFormatVersion} is incompatible."
				);
			}

			if (
				!string.Equals(
					document.ParseProfile,
					CSharpProjectIndexCacheFormat.CurrentParseProfile,
					StringComparison.Ordinal
				)
			)
			{
				return CSharpProjectIndexCacheLoadResult.Ignored(
					"Cache parse profile is incompatible."
				);
			}

			if (
				!CSharpProjectIndexCacheConverter.TryCreateSeedEntries(
					document,
					cancellationToken,
					out IReadOnlyDictionary<string, CSharpFileIndexEntry> seedEntries,
					out string validationFailure
				)
			)
			{
				return CSharpProjectIndexCacheLoadResult.Ignored(validationFailure);
			}

			cancellationToken.ThrowIfCancellationRequested();
			return CSharpProjectIndexCacheLoadResult.Loaded(seedEntries);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception exception)
		{
			return CSharpProjectIndexCacheLoadResult.Ignored(
				CreateExceptionDetail("Cache load failed", exception)
			);
		}
	}

	internal CSharpProjectIndexCachePreparedWrite PrepareWrite(
		CSharpProjectIndexCacheWriteRequest request,
		CancellationToken cancellationToken
	)
	{
		var stopwatch = Stopwatch.StartNew();
		string temporaryPath = "";
		bool prepared = false;

		try
		{
			ArgumentNullException.ThrowIfNull(request);
			cancellationToken.ThrowIfCancellationRequested();

			if (!TryNormalizeCachePath(request.CachePath, out string cachePath))
				throw new InvalidOperationException("Cache path is invalid.");

			CSharpProjectIndexSnapshot snapshot = request.Snapshot;
			if (
				snapshot == null
				|| !snapshot.HasBuiltAtLeastOnce
				|| request.Generation <= 0
				|| snapshot.Generation != request.Generation
			)
			{
				throw new InvalidOperationException(
					"Cache write request does not contain a matching published snapshot."
				);
			}

			CSharpProjectIndexCacheDocument document =
				CSharpProjectIndexCacheConverter.CreateDocument(snapshot, cancellationToken);
			cancellationToken.ThrowIfCancellationRequested();

			byte[] serializedCache = EncodeToBoundedBuffer(
				document,
				cancellationToken
			);
			cancellationToken.ThrowIfCancellationRequested();

			string cacheDirectory = Path.GetDirectoryName(cachePath) ?? "";
			if (string.IsNullOrWhiteSpace(cacheDirectory))
				throw new InvalidOperationException("Cache directory is invalid.");

			Directory.CreateDirectory(cacheDirectory);
			cancellationToken.ThrowIfCancellationRequested();

			temporaryPath = CreateTemporaryPath(cachePath, request.Generation);
			using (
				var stream = new FileStream(
					temporaryPath,
					FileMode.CreateNew,
					FileAccess.Write,
					FileShare.None,
					bufferSize: IoBufferSizeBytes,
					options: FileOptions.SequentialScan
				)
			)
			{
				WriteCacheBytes(stream, serializedCache, cancellationToken);
				cancellationToken.ThrowIfCancellationRequested();
			}

			cancellationToken.ThrowIfCancellationRequested();
			stopwatch.Stop();
			prepared = true;
			return new CSharpProjectIndexCachePreparedWrite(
				request.Generation,
				cachePath,
				temporaryPath,
				snapshot.FileCount,
				snapshot.TypeCount,
				stopwatch.Elapsed
			);
		}
		finally
		{
			if (!prepared)
				TryDeleteTemporaryFile(temporaryPath);
		}
	}

	internal void CommitPreparedWrite(CSharpProjectIndexCachePreparedWrite preparedWrite)
	{
		ArgumentNullException.ThrowIfNull(preparedWrite);

		if (
			preparedWrite.Generation <= 0
			|| !TryNormalizeCachePath(preparedWrite.CachePath, out string cachePath)
			|| !TryNormalizeTemporaryPath(
				preparedWrite.TemporaryPath,
				cachePath,
				out string temporaryPath
			)
		)
		{
			throw new InvalidOperationException("Prepared cache write is invalid.");
		}

		CommitTemporaryFile(temporaryPath, cachePath);
	}

	internal void DiscardPreparedWrite(CSharpProjectIndexCachePreparedWrite preparedWrite)
	{
		if (preparedWrite == null)
			return;

		TryDeleteTemporaryFile(preparedWrite.TemporaryPath);
	}

	private byte[] EncodeToBoundedBuffer(
		CSharpProjectIndexCacheDocument document,
		CancellationToken cancellationToken
	)
	{
		using var buffer = new CancellationAwareBoundedWriteStream(
			MaximumCacheFileSizeBytes,
			cancellationToken
		);
		_jsonCodec.Write(buffer, document, cancellationToken);
		cancellationToken.ThrowIfCancellationRequested();
		return buffer.ToArray();
	}

	private static byte[] ReadCacheBytes(
		FileStream stream,
		int length,
		CancellationToken cancellationToken
	)
	{
		var buffer = new byte[length];
		int offset = 0;

		while (offset < buffer.Length)
		{
			cancellationToken.ThrowIfCancellationRequested();
			int read = stream.Read(
				buffer,
				offset,
				Math.Min(IoBufferSizeBytes, buffer.Length - offset)
			);

			if (read <= 0)
				throw new EndOfStreamException("Cache file ended before the expected length.");

			offset += read;
		}

		cancellationToken.ThrowIfCancellationRequested();
		return buffer;
	}

	private static void WriteCacheBytes(
		FileStream stream,
		byte[] buffer,
		CancellationToken cancellationToken
	)
	{
		int offset = 0;

		while (offset < buffer.Length)
		{
			cancellationToken.ThrowIfCancellationRequested();
			int count = Math.Min(IoBufferSizeBytes, buffer.Length - offset);
			stream.Write(buffer, offset, count);
			offset += count;
		}

		cancellationToken.ThrowIfCancellationRequested();
	}

	private static bool TryNormalizeCachePath(
		string cachePath,
		out string normalizedCachePath
	)
	{
		normalizedCachePath = "";

		if (string.IsNullOrWhiteSpace(cachePath))
			return false;

		try
		{
			string trimmedPath = cachePath.Trim();
			if (!Path.IsPathFullyQualified(trimmedPath))
				return false;

			normalizedCachePath = Path.GetFullPath(trimmedPath);
			if (
				!string.Equals(
					Path.GetFileName(normalizedCachePath),
					CSharpProjectIndexCacheFormat.CacheFileName,
					StringComparison.OrdinalIgnoreCase
				)
			)
			{
				return false;
			}

			string autocompleteDirectory = Path.GetDirectoryName(normalizedCachePath) ?? "";
			string systemExplorerDirectory =
				Path.GetDirectoryName(autocompleteDirectory) ?? "";
			string godotDirectory = Path.GetDirectoryName(systemExplorerDirectory) ?? "";

			return HasDirectoryName(autocompleteDirectory, "autocomplete")
				&& HasDirectoryName(systemExplorerDirectory, "system_explorer")
				&& HasDirectoryName(godotDirectory, ".godot");
		}
		catch (Exception exception) when (
			exception is ArgumentException
			or NotSupportedException
			or PathTooLongException
		)
		{
			return false;
		}
	}

	private static bool TryNormalizeTemporaryPath(
		string temporaryPath,
		string cachePath,
		out string normalizedTemporaryPath
	)
	{
		normalizedTemporaryPath = "";

		if (string.IsNullOrWhiteSpace(temporaryPath))
			return false;

		try
		{
			string trimmedPath = temporaryPath.Trim();
			if (!Path.IsPathFullyQualified(trimmedPath))
				return false;

			normalizedTemporaryPath = Path.GetFullPath(trimmedPath);
			string temporaryDirectory = Path.GetDirectoryName(normalizedTemporaryPath) ?? "";
			string cacheDirectory = Path.GetDirectoryName(cachePath) ?? "";

			return !string.IsNullOrWhiteSpace(temporaryDirectory)
				&& string.Equals(
					temporaryDirectory,
					cacheDirectory,
					StringComparison.OrdinalIgnoreCase
				)
				&& string.Equals(
					Path.GetExtension(normalizedTemporaryPath),
					".tmp",
					StringComparison.OrdinalIgnoreCase
				);
		}
		catch (Exception exception) when (
			exception is ArgumentException
			or NotSupportedException
			or PathTooLongException
		)
		{
			return false;
		}
	}

	private static bool HasDirectoryName(string directoryPath, string expectedName)
	{
		return !string.IsNullOrWhiteSpace(directoryPath)
			&& string.Equals(
				Path.GetFileName(directoryPath),
				expectedName,
				StringComparison.OrdinalIgnoreCase
			);
	}

	private static string CreateTemporaryPath(string cachePath, long generation)
	{
		string directory = Path.GetDirectoryName(cachePath) ?? "";
		string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(cachePath);
		return Path.Combine(
			directory,
			$"{fileNameWithoutExtension}.{generation}.{Guid.NewGuid():N}.tmp"
		);
	}

	private static void CommitTemporaryFile(string temporaryPath, string cachePath)
	{
		if (!File.Exists(cachePath))
		{
			File.Move(temporaryPath, cachePath);
			return;
		}

		try
		{
			File.Replace(temporaryPath, cachePath, destinationBackupFileName: null);
		}
		catch (PlatformNotSupportedException)
		{
			File.Move(temporaryPath, cachePath, overwrite: true);
		}
		catch (FileNotFoundException)
		{
			File.Move(temporaryPath, cachePath, overwrite: true);
		}
	}

	private static void TryDeleteTemporaryFile(string temporaryPath)
	{
		if (string.IsNullOrWhiteSpace(temporaryPath))
			return;

		try
		{
			if (File.Exists(temporaryPath))
				File.Delete(temporaryPath);
		}
		catch
		{
			// Best-effort cleanup only.
		}
	}

	private sealed class CancellationAwareBoundedWriteStream : Stream
	{
		private readonly MemoryStream _inner = new();
		private readonly long _maximumLength;
		private readonly CancellationToken _cancellationToken;

		internal CancellationAwareBoundedWriteStream(
			long maximumLength,
			CancellationToken cancellationToken
		)
		{
			_maximumLength = maximumLength;
			_cancellationToken = cancellationToken;
		}

		public override bool CanRead => false;
		public override bool CanSeek => false;
		public override bool CanWrite => true;
		public override long Length => _inner.Length;

		public override long Position
		{
			get => _inner.Position;
			set => throw new NotSupportedException();
		}

		public override void Flush()
		{
			_cancellationToken.ThrowIfCancellationRequested();
		}

		public override int Read(byte[] buffer, int offset, int count)
		{
			throw new NotSupportedException();
		}

		public override long Seek(long offset, SeekOrigin origin)
		{
			throw new NotSupportedException();
		}

		public override void SetLength(long value)
		{
			throw new NotSupportedException();
		}

		public override void Write(byte[] buffer, int offset, int count)
		{
			_cancellationToken.ThrowIfCancellationRequested();
			EnsureCanAppend(count);
			_inner.Write(buffer, offset, count);
			_cancellationToken.ThrowIfCancellationRequested();
		}

		public override void Write(ReadOnlySpan<byte> buffer)
		{
			_cancellationToken.ThrowIfCancellationRequested();
			EnsureCanAppend(buffer.Length);
			_inner.Write(buffer);
			_cancellationToken.ThrowIfCancellationRequested();
		}

		public override void WriteByte(byte value)
		{
			_cancellationToken.ThrowIfCancellationRequested();
			EnsureCanAppend(1);
			_inner.WriteByte(value);
		}

		internal byte[] ToArray()
		{
			_cancellationToken.ThrowIfCancellationRequested();
			return _inner.ToArray();
		}

		protected override void Dispose(bool disposing)
		{
			if (disposing)
				_inner.Dispose();

			base.Dispose(disposing);
		}

		private void EnsureCanAppend(int count)
		{
			if (count < 0 || _inner.Length > _maximumLength - count)
			{
				throw new InvalidDataException(
					$"Cache write exceeds the {_maximumLength / (1024 * 1024)} MiB safety limit."
				);
			}
		}
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
