#if TOOLS
using System;
using System.Threading;

namespace SystemExplorer.Autocomplete.Semantics;

internal sealed class CSharpSemanticMemberIndex
{
	private CSharpSemanticMemberIndexSnapshot _snapshot =
		CSharpSemanticMemberIndexSnapshot.Empty;

	internal CSharpSemanticMemberIndexSnapshot CurrentSnapshot =>
		Volatile.Read(ref _snapshot);

	internal void Publish(CSharpSemanticMemberIndexSnapshot snapshot)
	{
		ArgumentNullException.ThrowIfNull(snapshot);
		Interlocked.Exchange(ref _snapshot, snapshot);
	}

	internal void Clear()
	{
		Interlocked.Exchange(ref _snapshot, CSharpSemanticMemberIndexSnapshot.Empty);
	}
}
#endif
