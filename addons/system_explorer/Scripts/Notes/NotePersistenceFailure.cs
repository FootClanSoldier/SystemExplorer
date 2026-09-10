#if TOOLS
namespace SystemExplorer.Notes;

internal static class NotePersistenceFailure
{
	internal static string BuildFailureDetail(
		string systemName,
		string phase,
		string detail
	)
	{
		return $"SystemName={FormatForDetail(systemName)}, Phase='{phase}', {detail}";
	}

	internal static string FormatForDetail(string value)
	{
		if (value == null)
			return "<null>";

		return "'" + value.Replace("'", "''") + "'";
	}
}
#endif
