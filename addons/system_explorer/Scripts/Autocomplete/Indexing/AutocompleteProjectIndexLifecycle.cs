#if TOOLS
using Godot;
using System;
using SystemExplorer.Autocomplete.Indexing.Persistence;

namespace SystemExplorer.Autocomplete.Indexing;

internal sealed class AutocompleteProjectIndexLifecycle
{
	private const string FilesystemDescription =
		"C# Autocomplete Project Index EditorFileSystem";

	private readonly Func<EditorFileSystem> _resourceFilesystemProvider;
	private readonly Func<string> _globalProjectRootProvider;
	private readonly Func<GodotObject, StringName, string, string, bool> _connectPluginSignal;
	private readonly Action<GodotObject, StringName, string, string> _disconnectPluginSignal;
	private readonly string _filesystemChangedMethodName;
	private readonly CSharpProjectIndexCoordinator _coordinator;
	private readonly Action<string> _requestRefreshAdmission;
	private readonly Action<string, string> _debugLog;

	private EditorFileSystem _editorFileSystem;
	private bool _initialRefreshRequested;
	private bool _shutdown;

	internal AutocompleteProjectIndexLifecycle(
		Func<EditorFileSystem> resourceFilesystemProvider,
		Func<string> globalProjectRootProvider,
		Func<GodotObject, StringName, string, string, bool> connectPluginSignal,
		Action<GodotObject, StringName, string, string> disconnectPluginSignal,
		string filesystemChangedMethodName,
		CSharpProjectIndexCoordinator coordinator,
		Action<string> requestRefreshAdmission,
		Action<string, string> debugLog
	)
	{
		_resourceFilesystemProvider =
			resourceFilesystemProvider
			?? throw new ArgumentNullException(nameof(resourceFilesystemProvider));
		_globalProjectRootProvider =
			globalProjectRootProvider
			?? throw new ArgumentNullException(nameof(globalProjectRootProvider));
		_connectPluginSignal =
			connectPluginSignal ?? throw new ArgumentNullException(nameof(connectPluginSignal));
		_disconnectPluginSignal =
			disconnectPluginSignal
			?? throw new ArgumentNullException(nameof(disconnectPluginSignal));
		_filesystemChangedMethodName =
			filesystemChangedMethodName
			?? throw new ArgumentNullException(nameof(filesystemChangedMethodName));
		_coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
		_requestRefreshAdmission =
			requestRefreshAdmission
			?? throw new ArgumentNullException(nameof(requestRefreshAdmission));
		_debugLog = debugLog ?? throw new ArgumentNullException(nameof(debugLog));
	}

	internal bool EnsureLifecycleCurrent()
	{
		if (_shutdown)
			return false;

		bool signalConnected = EnsureFilesystemSignalCurrent();

		if (!_initialRefreshRequested)
		{
			_initialRefreshRequested = true;
			RequestRefreshAdmission("Initial project index");
		}

		return signalConnected;
	}

	internal void HandleFilesystemChanged()
	{
		if (_shutdown)
			return;

		EnsureLifecycleCurrent();
		RequestRefreshAdmission("EditorFileSystem.FilesystemChanged");
	}

	internal void ResetTransientState()
	{
		if (_shutdown)
			return;

		_coordinator.ResetTransientState();
		DisconnectFilesystemSignal();
		_editorFileSystem = null;
		_initialRefreshRequested = false;
	}

	internal void Shutdown()
	{
		if (_shutdown)
			return;

		_shutdown = true;
		_coordinator.StopAcceptingRequests();
		DisconnectFilesystemSignal();
		_editorFileSystem = null;
	}

	private bool EnsureFilesystemSignalCurrent()
	{
		EditorFileSystem currentFileSystem;

		try
		{
			currentFileSystem = _resourceFilesystemProvider();
		}
		catch (Exception exception)
		{
			_debugLog(
				"C# autocomplete project index lifecycle failed: filesystem provider",
				exception.ToString()
			);
			DisconnectFilesystemSignal();
			_editorFileSystem = null;
			return false;
		}

		if (!IsValidGodotObject(currentFileSystem))
		{
			DisconnectFilesystemSignal();
			_editorFileSystem = null;
			return false;
		}

		if (
			IsValidGodotObject(_editorFileSystem)
			&& _editorFileSystem.GetInstanceId() != currentFileSystem.GetInstanceId()
		)
		{
			DisconnectFilesystemSignal();
		}

		_editorFileSystem = currentFileSystem;
		return _connectPluginSignal(
			currentFileSystem,
			EditorFileSystem.SignalName.FilesystemChanged,
			_filesystemChangedMethodName,
			FilesystemDescription
		);
	}

	private void RequestRefreshAdmission(string reason)
	{
		_requestRefreshAdmission(reason ?? "");
	}

	internal void ExecuteRefresh(string reason)
	{
		if (_shutdown)
			return;

		string globalProjectRoot = "";
		string cachePath = "";

		try
		{
			globalProjectRoot = _globalProjectRootProvider();
		}
		catch (Exception exception)
		{
			_debugLog(
				"C# autocomplete project index lifecycle failed: project root",
				exception.ToString()
			);
		}

		try
		{
			cachePath = CSharpProjectIndexCacheFormat.CreateCachePath(globalProjectRoot);
		}
		catch (Exception exception)
		{
			_debugLog(
				"C# autocomplete project index lifecycle failed: cache path",
				exception.ToString()
			);
		}

		_coordinator.RequestRefresh(reason, globalProjectRoot, cachePath);
	}

	private void DisconnectFilesystemSignal()
	{
		_disconnectPluginSignal(
			_editorFileSystem,
			EditorFileSystem.SignalName.FilesystemChanged,
			_filesystemChangedMethodName,
			FilesystemDescription
		);
	}

	private static bool IsValidGodotObject(GodotObject source)
	{
		return source != null && GodotObject.IsInstanceValid(source);
	}
}
#endif
