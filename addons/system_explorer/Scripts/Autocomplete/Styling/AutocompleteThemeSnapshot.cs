#if TOOLS
using Godot;
using System.Collections.Generic;

namespace SystemExplorer.Autocomplete.Styling;

internal sealed class AutocompleteThemeSnapshot
{
	internal readonly struct ColorOverrideSnapshot
	{
		internal ColorOverrideSnapshot(bool hadOverride, Color previousValue)
		{
			HadOverride = hadOverride;
			PreviousValue = previousValue;
		}

		internal bool HadOverride { get; }
		internal Color PreviousValue { get; }
	}

	internal readonly struct ConstantOverrideSnapshot
	{
		internal ConstantOverrideSnapshot(bool hadOverride, int previousValue)
		{
			HadOverride = hadOverride;
			PreviousValue = previousValue;
		}

		internal bool HadOverride { get; }
		internal int PreviousValue { get; }
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
}
#endif
