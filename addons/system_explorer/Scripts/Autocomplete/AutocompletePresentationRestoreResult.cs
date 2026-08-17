#if TOOLS
namespace SystemExplorer.Autocomplete;

internal readonly record struct AutocompletePresentationRestoreResult(
	bool Succeeded,
	bool CurrentStateChangedBeforeRestore
)
{
	internal static AutocompletePresentationRestoreResult Success(bool currentStateChanged = false)
	{
		return new AutocompletePresentationRestoreResult(
			Succeeded: true,
			CurrentStateChangedBeforeRestore: currentStateChanged
		);
	}

	internal static AutocompletePresentationRestoreResult Failure()
	{
		return new AutocompletePresentationRestoreResult(
			Succeeded: false,
			CurrentStateChangedBeforeRestore: false
		);
	}
}
#endif
