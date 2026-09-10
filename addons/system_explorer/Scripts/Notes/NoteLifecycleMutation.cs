#if TOOLS
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace SystemExplorer.Notes;

internal sealed class NoteLifecycleMutation
{
	internal sealed class SystemFileSnapshot
	{
		private readonly byte[] _exactOriginalBytes;

		internal string SystemName { get; }
		internal bool FileExisted { get; }

		internal SystemFileSnapshot(
			string systemName,
			bool fileExisted,
			byte[] exactOriginalBytes
		)
		{
			SystemName = systemName ?? throw new ArgumentNullException(nameof(systemName));
			FileExisted = fileExisted;

			if (fileExisted)
			{
				if (exactOriginalBytes == null)
					throw new ArgumentNullException(nameof(exactOriginalBytes));

				_exactOriginalBytes = (byte[])exactOriginalBytes.Clone();
			}
			else
			{
				_exactOriginalBytes = Array.Empty<byte>();
			}
		}

		internal byte[] GetExactOriginalBytesCopy()
		{
			return (byte[])_exactOriginalBytes.Clone();
		}
	}

	private static readonly IReadOnlyList<SystemFileSnapshot> EmptySnapshots =
		Array.Empty<SystemFileSnapshot>();

	internal static NoteLifecycleMutation NoOp { get; } =
		new(EmptySnapshots);

	internal IReadOnlyList<SystemFileSnapshot> Snapshots { get; }
	internal bool IsNoOp => Snapshots.Count == 0;

	internal NoteLifecycleMutation(IEnumerable<SystemFileSnapshot> snapshots)
	{
		if (snapshots == null)
			throw new ArgumentNullException(nameof(snapshots));

		List<SystemFileSnapshot> copiedSnapshots = new();

		foreach (SystemFileSnapshot snapshot in snapshots)
		{
			if (snapshot == null)
				throw new ArgumentException("Lifecycle mutation snapshots must not contain null values.", nameof(snapshots));

			copiedSnapshots.Add(
				new SystemFileSnapshot(
					snapshot.SystemName,
					snapshot.FileExisted,
					snapshot.GetExactOriginalBytesCopy()
				)
			);
		}

		Snapshots = copiedSnapshots.Count == 0
			? EmptySnapshots
			: new ReadOnlyCollection<SystemFileSnapshot>(copiedSnapshots);
	}
}
#endif
