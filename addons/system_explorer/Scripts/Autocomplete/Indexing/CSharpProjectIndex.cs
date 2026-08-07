#if TOOLS
using System;
using System.Threading;

namespace SystemExplorer.Autocomplete.Indexing;

internal sealed class CSharpProjectIndex
{
	private CSharpProjectIndexSnapshot _snapshot = CSharpProjectIndexSnapshot.Empty;

	internal CSharpProjectIndexSnapshot CurrentSnapshot => Volatile.Read(ref _snapshot);

	internal void Publish(CSharpProjectIndexSnapshot snapshot)
	{
		ArgumentNullException.ThrowIfNull(snapshot);
		Interlocked.Exchange(ref _snapshot, snapshot);
	}
}
#endif
