#if TOOLS
using System;
using System.Threading;

namespace SystemExplorer.Autocomplete.Indexing.ActiveDocument;

internal sealed class CSharpActiveDocumentIndex
{
	private CSharpActiveDocumentIndexSnapshot _snapshot =
		CSharpActiveDocumentIndexSnapshot.Empty;

	internal CSharpActiveDocumentIndexSnapshot CurrentSnapshot =>
		Volatile.Read(ref _snapshot);

	internal void Publish(CSharpActiveDocumentIndexSnapshot snapshot)
	{
		ArgumentNullException.ThrowIfNull(snapshot);
		Interlocked.Exchange(ref _snapshot, snapshot);
	}

	internal void Clear()
	{
		Interlocked.Exchange(ref _snapshot, CSharpActiveDocumentIndexSnapshot.Empty);
	}
}
#endif
