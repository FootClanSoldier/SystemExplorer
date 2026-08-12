#if TOOLS
using Godot;
using System;
using System.Runtime.Loader;

public partial class SystemExplorerPlugin
{
	#region ScriptEditor Crash-Tail Diagnostics

	private readonly record struct ScriptEditorEditBoundaryContext(
		long BoundaryToken,
		long PreviousBoundaryToken,
		int PreviousCallDepth,
		string Origin,
		string ScriptPath
	);

	private readonly record struct BeautifyBatchDiagnosticSnapshot(
		bool BatchActive,
		long BatchToken,
		string CurrentTargetPath,
		string CurrentPhase,
		long LastRefreshToken,
		string LastRefreshedPath
	);

	private readonly record struct AutocompleteFilesystemDiagnosticBoundaryContext(
		long BoundaryToken
	);

	private long _scriptEditorDiagnosticBoundarySequence;
	private long _beautifyBatchDiagnosticSequence;
	private long _beautifyRefreshDiagnosticSequence;
	private long _autocompleteFilesystemDiagnosticBoundarySequence;
	private bool _beautifyBatchDiagnosticActive;
	private long _beautifyBatchDiagnosticToken;
	private string _beautifyBatchDiagnosticCurrentTargetPath = "";
	private string _beautifyBatchDiagnosticCurrentPhase = "";
	private long _beautifyBatchDiagnosticLastRefreshToken;
	private string _beautifyBatchDiagnosticLastRefreshedPath = "";
	private long _activeEditScriptBoundaryToken;
	private int _editScriptCallDepth;

	private ScriptEditorEditBoundaryContext BeginEditScriptDiagnosticBoundary(
		string origin,
		string scriptPath
	)
	{
		if (!DebugLogger.IsEnabled)
			return default;

		long previousBoundaryToken = _activeEditScriptBoundaryToken;
		int previousCallDepth = _editScriptCallDepth;
		long boundaryToken = NextScriptEditorDiagnosticBoundaryToken();

		_activeEditScriptBoundaryToken = boundaryToken;
		_editScriptCallDepth = previousCallDepth + 1;

		var context = new ScriptEditorEditBoundaryContext(
			boundaryToken,
			previousBoundaryToken,
			previousCallDepth,
			NormalizeScriptEditorDiagnosticText(origin),
			NormalizeScriptEditorDiagnosticText(scriptPath)
		);

		LogScriptEditorDiagnosticPhase(
			"ScriptEditor EditScript boundary",
			context.Origin,
			"Begin",
			context.ScriptPath,
			_scriptEditorSyncScriptEditor,
			textEditor: null,
			boundaryToken: context.BoundaryToken
		);

		return context;
	}

	private void CompleteEditScriptDiagnosticBoundary(
		ScriptEditorEditBoundaryContext context
	)
	{
		if (context.BoundaryToken == 0)
			return;

		_activeEditScriptBoundaryToken = context.PreviousBoundaryToken;
		_editScriptCallDepth = context.PreviousCallDepth;

		LogScriptEditorDiagnosticPhase(
			"ScriptEditor EditScript boundary",
			context.Origin,
			"Returned",
			context.ScriptPath,
			_scriptEditorSyncScriptEditor,
			textEditor: null,
			boundaryToken: context.BoundaryToken
		);
	}

	private void LogScriptEditorCallbackEntry(string callbackName, Script script = null)
	{
		try
		{
			if (!DebugLogger.IsEnabled)
				return;

			string callbackScriptContext = DescribeDiagnosticGodotObject(
				"CallbackScript",
				script
			);

			DebugLogger.LogPersistentFileOnlyOperation(
				"ScriptEditor managed callback entry",
				$"Callback='{NormalizeScriptEditorDiagnosticText(callbackName)}', "
					+ $"{CreatePluginDiagnosticContext()}, "
					+ $"{callbackScriptContext}, "
					+ $"{DescribeDiagnosticGodotObject("ScriptEditor", _scriptEditorSyncScriptEditor)}, "
					+ $"ActiveEditScriptBoundaryToken='{_activeEditScriptBoundaryToken}', "
					+ $"EditScriptCallActive='{_editScriptCallDepth > 0}', "
					+ $"EditScriptCallDepth='{_editScriptCallDepth}'"
			);
		}
		catch
		{
			// Callback-generation evidence must never affect callback control flow.
		}
	}

	private Action<string, ScriptEditor, CodeEdit> CreateAutocompleteScriptChangeDiagnosticPhase(
		string origin,
		string targetScriptPath = ""
	)
	{
		if (!DebugLogger.IsEnabled)
			return null;

		string normalizedOrigin = NormalizeScriptEditorDiagnosticText(origin);
		string normalizedTargetScriptPath = NormalizeScriptEditorDiagnosticText(targetScriptPath);

		return (phase, scriptEditor, codeEdit) =>
			LogScriptEditorDiagnosticPhase(
				"C# autocomplete ScriptEditor boundary",
				normalizedOrigin,
				phase,
				normalizedTargetScriptPath,
				scriptEditor ?? _scriptEditorSyncScriptEditor,
				codeEdit
			);
	}

	private long BeginBeautifyBatchDiagnosticContext()
	{
		if (!DebugLogger.IsEnabled)
			return 0;

		long batchToken = NextBeautifyBatchDiagnosticToken();
		_beautifyBatchDiagnosticActive = true;
		_beautifyBatchDiagnosticToken = batchToken;
		_beautifyBatchDiagnosticCurrentTargetPath = "";
		_beautifyBatchDiagnosticCurrentPhase = "Beautify.Batch.Begin";
		_beautifyBatchDiagnosticLastRefreshToken = 0;
		_beautifyBatchDiagnosticLastRefreshedPath = "";
		return batchToken;
	}

	private void CompleteBeautifyBatchDiagnosticContext(long batchToken)
	{
		if (batchToken == 0 || !_beautifyBatchDiagnosticActive)
			return;
		if (_beautifyBatchDiagnosticToken != batchToken)
			return;

		_beautifyBatchDiagnosticActive = false;
		_beautifyBatchDiagnosticToken = 0;
		_beautifyBatchDiagnosticCurrentTargetPath = "";
		_beautifyBatchDiagnosticCurrentPhase = "";
		_beautifyBatchDiagnosticLastRefreshToken = 0;
		_beautifyBatchDiagnosticLastRefreshedPath = "";
	}

	private void SetBeautifyBatchDiagnosticTarget(string targetScriptPath)
	{
		if (!_beautifyBatchDiagnosticActive)
			return;

		_beautifyBatchDiagnosticCurrentTargetPath =
			NormalizeScriptEditorDiagnosticText(targetScriptPath);
		_beautifyBatchDiagnosticCurrentPhase = "Beautify.Item.Begin";
	}

	private long BeginBeautifyBatchRefreshDiagnostic(
		string targetScriptPath,
		ScriptEditor scriptEditor,
		TextEdit textEditor,
		string extraDetails = ""
	)
	{
		if (!_beautifyBatchDiagnosticActive)
			return 0;

		long refreshToken = NextBeautifyRefreshDiagnosticToken();
		_beautifyBatchDiagnosticLastRefreshToken = refreshToken;
		_beautifyBatchDiagnosticLastRefreshedPath =
			NormalizeScriptEditorDiagnosticText(targetScriptPath);

		LogBeautifyDiagnosticPhase(
			"Beautify.RefreshScripts",
			targetScriptPath,
			scriptEditor,
			textEditor,
			AppendDiagnosticDetails(
				$"BeautifyRefreshToken='{refreshToken}'",
				extraDetails
			)
		);
		return refreshToken;
	}

	private void CompleteBeautifyBatchRefreshDiagnostic(
		long refreshToken,
		string targetScriptPath,
		ScriptEditor scriptEditor,
		TextEdit textEditor,
		string extraDetails = ""
	)
	{
		if (refreshToken == 0)
			return;

		LogBeautifyDiagnosticPhase(
			"Beautify.RefreshScripts.Completed",
			targetScriptPath,
			scriptEditor,
			textEditor,
			AppendDiagnosticDetails(
				$"BeautifyRefreshToken='{refreshToken}'",
				extraDetails
			)
		);
	}

	private AutocompleteFilesystemDiagnosticBoundaryContext
		BeginAutocompleteFilesystemChangedDiagnosticBoundary()
	{
		if (!DebugLogger.IsEnabled || !_beautifyBatchDiagnosticActive)
			return default;

		var context = new AutocompleteFilesystemDiagnosticBoundaryContext(
			NextAutocompleteFilesystemDiagnosticBoundaryToken()
		);
		LogAutocompleteFilesystemChangedDiagnosticPhase(context, "Begin");
		return context;
	}

	private void CompleteAutocompleteFilesystemChangedDiagnosticBoundary(
		AutocompleteFilesystemDiagnosticBoundaryContext context
	)
	{
		if (context.BoundaryToken == 0)
			return;

		LogAutocompleteFilesystemChangedDiagnosticPhase(context, "Returned");
	}

	private Action<string, string> CreateAutocompleteFilesystemChangedDiagnosticPhase(
		AutocompleteFilesystemDiagnosticBoundaryContext context
	)
	{
		if (context.BoundaryToken == 0)
			return null;

		return (phase, details) =>
			LogAutocompleteFilesystemChangedDiagnosticPhase(
				context,
				phase,
				details
			);
	}

	private void LogAutocompleteFilesystemChangedDiagnosticPhase(
		AutocompleteFilesystemDiagnosticBoundaryContext context,
		string phase,
		string extraDetails = ""
	)
	{
		try
		{
			if (!DebugLogger.IsEnabled || context.BoundaryToken == 0)
				return;

			BeautifyBatchDiagnosticSnapshot beautify = CaptureBeautifyBatchDiagnosticSnapshot();
			string details =
				$"Phase='{NormalizeScriptEditorDiagnosticText(phase)}', "
				+ $"FilesystemBoundaryToken='{context.BoundaryToken}', "
				+ $"{CreatePluginDiagnosticContext()}, "
				+ $"BeautifyBatchActive='{beautify.BatchActive}', "
				+ $"BeautifyBatchToken='{beautify.BatchToken}', "
				+ $"BeautifyCurrentTargetPath='{beautify.CurrentTargetPath}', "
				+ $"BeautifyCurrentPhase='{beautify.CurrentPhase}', "
				+ $"BeautifyLastRefreshToken='{beautify.LastRefreshToken}', "
				+ $"BeautifyLastRefreshedPath='{beautify.LastRefreshedPath}'";

			if (!string.IsNullOrWhiteSpace(extraDetails))
				details += $", {extraDetails}";

			DebugLogger.LogPersistentFileOnlyOperation(
				"C# autocomplete FilesystemChanged boundary",
				details
			);
		}
		catch
		{
			// Filesystem correlation must never affect callback control flow.
		}
	}

	private BeautifyBatchDiagnosticSnapshot CaptureBeautifyBatchDiagnosticSnapshot()
	{
		return new BeautifyBatchDiagnosticSnapshot(
			_beautifyBatchDiagnosticActive,
			_beautifyBatchDiagnosticToken,
			NormalizeScriptEditorDiagnosticText(_beautifyBatchDiagnosticCurrentTargetPath),
			NormalizeScriptEditorDiagnosticText(_beautifyBatchDiagnosticCurrentPhase),
			_beautifyBatchDiagnosticLastRefreshToken,
			NormalizeScriptEditorDiagnosticText(_beautifyBatchDiagnosticLastRefreshedPath)
		);
	}

	private void LogBeautifyDiagnosticPhase(
		string phase,
		string targetScriptPath,
		ScriptEditor scriptEditor = null,
		TextEdit textEditor = null,
		string extraDetails = ""
	)
	{
		UpdateBeautifyBatchDiagnosticPhase(phase, targetScriptPath);

		LogScriptEditorDiagnosticPhase(
			"Beautify ScriptEditor boundary",
			"Beautify Scripts",
			phase,
			targetScriptPath,
			scriptEditor,
			textEditor,
			extraDetails: extraDetails
		);
	}

	private void LogScriptEditorDiagnosticPhase(
		string operation,
		string origin,
		string phase,
		string targetScriptPath,
		ScriptEditor scriptEditor,
		TextEdit textEditor,
		long boundaryToken = 0,
		string extraDetails = ""
	)
	{
		try
		{
			if (!DebugLogger.IsEnabled)
				return;

			long effectiveBoundaryToken = boundaryToken != 0
				? boundaryToken
				: _activeEditScriptBoundaryToken;
			string textEditorDiagnosticName = textEditor is CodeEdit ? "CodeEdit" : "TextEdit";
			string details =
				$"Origin='{NormalizeScriptEditorDiagnosticText(origin)}', "
				+ $"Phase='{NormalizeScriptEditorDiagnosticText(phase)}', "
				+ $"TargetScriptPath='{NormalizeScriptEditorDiagnosticText(targetScriptPath)}', "
				+ $"EditorBoundaryToken='{effectiveBoundaryToken}', "
				+ $"EditScriptCallActive='{_editScriptCallDepth > 0}', "
				+ $"EditScriptCallDepth='{_editScriptCallDepth}', "
				+ $"{CreatePluginDiagnosticContext()}, "
				+ $"{DescribeDiagnosticGodotObject("ScriptEditor", scriptEditor)}, "
				+ $"{DescribeDiagnosticGodotObject(textEditorDiagnosticName, textEditor)}";

			if (!string.IsNullOrWhiteSpace(extraDetails))
				details += $", {extraDetails}";

			DebugLogger.LogPersistentFileOnlyOperation(operation, details);
		}
		catch
		{
			// Crash-tail diagnostics must be observation-only and fail closed.
		}
	}

	private string CreatePluginDiagnosticContext()
	{
		string loadContextObjectToken = "0";
		string loadContextCollectible = "<unknown>";

		try
		{
			AssemblyLoadContext loadContext = AssemblyLoadContext.GetLoadContext(
				typeof(SystemExplorerPlugin).Assembly
			);
			loadContextObjectToken = GetManagedDiagnosticObjectToken(loadContext).ToString();
			loadContextCollectible = loadContext == null
				? "<unknown>"
				: loadContext.IsCollectible.ToString();
		}
		catch
		{
		}

		return
			$"ManagedAssemblyGeneration='{ManagedAssemblyGeneration}', "
			+ $"PluginNativeInstanceId='{DescribeDiagnosticGodotInstanceId(this)}', "
			+ $"PluginManagedObjectToken='{GetManagedDiagnosticObjectToken(this)}', "
			+ $"PluginAssemblyLoadContextObjectToken='{loadContextObjectToken}', "
			+ $"PluginAssemblyLoadContextCollectible='{loadContextCollectible}', "
			+ $"HostInstanceToken='{_autocompleteHostInstanceToken}', "
			+ $"HostManagedAssemblyGeneration='{_autocompleteHostManagedAssemblyGeneration}', "
			+ $"TreeKeyboardNavigationBurstActive='{IsTreeKeyboardNavigationBurstActive}', "
			+ $"AutocompleteScriptEditorChangedCallbackDepth='{_autocompleteScriptEditorChangedCallbackDepth}', "
			+ $"AutocompleteDeferredScriptChangeRebindPending='{_autocompleteDeferredScriptChangeRebindPending}'";
	}

	private void UpdateBeautifyBatchDiagnosticPhase(string phase, string targetScriptPath)
	{
		if (!_beautifyBatchDiagnosticActive)
			return;

		_beautifyBatchDiagnosticCurrentPhase = NormalizeScriptEditorDiagnosticText(phase);
		if (!string.IsNullOrWhiteSpace(targetScriptPath))
		{
			_beautifyBatchDiagnosticCurrentTargetPath =
				NormalizeScriptEditorDiagnosticText(targetScriptPath);
		}
	}

	private long NextBeautifyBatchDiagnosticToken()
	{
		return NextPositiveDiagnosticToken(ref _beautifyBatchDiagnosticSequence);
	}

	private long NextBeautifyRefreshDiagnosticToken()
	{
		return NextPositiveDiagnosticToken(ref _beautifyRefreshDiagnosticSequence);
	}

	private long NextAutocompleteFilesystemDiagnosticBoundaryToken()
	{
		return NextPositiveDiagnosticToken(ref _autocompleteFilesystemDiagnosticBoundarySequence);
	}

	private static long NextPositiveDiagnosticToken(ref long sequence)
	{
		unchecked
		{
			sequence++;
			if (sequence <= 0)
				sequence = 1;
		}

		return sequence;
	}

	private static string AppendDiagnosticDetails(string first, string second)
	{
		if (string.IsNullOrWhiteSpace(first))
			return second ?? "";
		if (string.IsNullOrWhiteSpace(second))
			return first;
		return $"{first}, {second}";
	}

	private long NextScriptEditorDiagnosticBoundaryToken()
	{
		unchecked
		{
			_scriptEditorDiagnosticBoundarySequence++;
			if (_scriptEditorDiagnosticBoundarySequence <= 0)
				_scriptEditorDiagnosticBoundarySequence = 1;
		}

		return _scriptEditorDiagnosticBoundarySequence;
	}

	private static int GetManagedDiagnosticObjectToken(object source)
	{
		return source == null
			? 0
			: System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(source);
	}

	private static string DescribeDiagnosticGodotObject(string name, GodotObject source)
	{
		string safeName = string.IsNullOrWhiteSpace(name) ? "GodotObject" : name;
		return
			$"{safeName}NativeInstanceId='{DescribeDiagnosticGodotInstanceId(source)}', "
			+ $"{safeName}ManagedObjectToken='{GetManagedDiagnosticObjectToken(source)}'";
	}

	private static string DescribeDiagnosticGodotInstanceId(GodotObject source)
	{
		try
		{
			if (source == null)
				return "<null>";
			if (!GodotObject.IsInstanceValid(source))
				return "<invalid>";

			return source.GetInstanceId().ToString();
		}
		catch (Exception exception)
		{
			return $"<read-failed:{exception.GetType().Name}>";
		}
	}

	private static string NormalizeScriptEditorDiagnosticText(string value)
	{
		if (string.IsNullOrWhiteSpace(value))
			return "";

		string normalized = value
			.Replace('\r', ' ')
			.Replace('\n', ' ')
			.Replace('\t', ' ')
			.Trim();
		return normalized.Length <= 220 ? normalized : normalized.Substring(0, 220);
	}

	#endregion
}
#endif
