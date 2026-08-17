#if TOOLS
using Godot;
using System.Collections.Generic;

namespace SystemExplorer.Autocomplete.Styling;

internal sealed class AutocompleteThemeSnapshot
{
	internal readonly struct ColorOverrideSnapshot
	{
		internal ColorOverrideSnapshot(
			bool hadOverride,
			Color previousValue,
			Color appliedValue
		)
		{
			HadOverride = hadOverride;
			PreviousValue = previousValue;
			AppliedValue = appliedValue;
		}

		internal bool HadOverride { get; }
		internal Color PreviousValue { get; }
		internal Color AppliedValue { get; }
	}

	internal readonly struct ConstantOverrideSnapshot
	{
		internal ConstantOverrideSnapshot(
			bool hadOverride,
			int previousValue,
			int appliedValue
		)
		{
			HadOverride = hadOverride;
			PreviousValue = previousValue;
			AppliedValue = appliedValue;
		}

		internal bool HadOverride { get; }
		internal int PreviousValue { get; }
		internal int AppliedValue { get; }
	}

	internal AutocompleteThemeSnapshot(CodeEdit codeEdit)
	{
		CodeEdit = codeEdit;
		CodeEditInstanceId = codeEdit.GetInstanceId();
	}

	internal CodeEdit CodeEdit { get; }
	internal ulong CodeEditInstanceId { get; }
	internal Dictionary<string, ColorOverrideSnapshot> ColorOverrides { get; } = new();
	internal Dictionary<string, ConstantOverrideSnapshot> ConstantOverrides { get; } = new();
	internal bool HasCompletionStyleboxSnapshot { get; set; }
	internal bool HadCompletionStyleboxOverride { get; set; }
	internal StyleBox PreviousCompletionStylebox { get; set; }
	internal StyleBox AppliedCompletionStylebox { get; set; }
	internal ulong AppliedCompletionStyleboxInstanceId { get; set; }
}
#endif
