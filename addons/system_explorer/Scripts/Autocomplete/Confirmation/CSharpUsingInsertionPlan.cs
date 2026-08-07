#if TOOLS
namespace SystemExplorer.Autocomplete.Confirmation;

internal enum CSharpUsingInsertionPlanKind
{
	NotRequired,
	Insert,
	Unsafe,
}

internal sealed record CSharpUsingInsertionPlan(
	CSharpUsingInsertionPlanKind Kind,
	string TargetNamespace,
	int InsertLine,
	int InsertColumn,
	string InsertionText,
	int InsertedLineCount,
	string Reason
)
{
	internal static CSharpUsingInsertionPlan NotRequired(string targetNamespace)
	{
		return new CSharpUsingInsertionPlan(
			CSharpUsingInsertionPlanKind.NotRequired,
			targetNamespace ?? "",
			-1,
			-1,
			"",
			0,
			"AlreadyImported"
		);
	}

	internal static CSharpUsingInsertionPlan Insert(
		string targetNamespace,
		int insertLine,
		int insertColumn,
		string insertionText
	)
	{
		return new CSharpUsingInsertionPlan(
			CSharpUsingInsertionPlanKind.Insert,
			targetNamespace ?? "",
			insertLine,
			insertColumn,
			insertionText ?? "",
			1,
			""
		);
	}

	internal static CSharpUsingInsertionPlan Unsafe(
		string targetNamespace,
		string reason
	)
	{
		return new CSharpUsingInsertionPlan(
			CSharpUsingInsertionPlanKind.Unsafe,
			targetNamespace ?? "",
			-1,
			-1,
			"",
			0,
			reason ?? "Unsafe"
		);
	}
}
#endif
