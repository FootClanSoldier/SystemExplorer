#if TOOLS
using Godot;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SystemExplorer.CodeService.Client;
using SystemExplorer.CodeService.Documents;

public partial class SystemExplorerPlugin
{
	#region CodeService Document Synchronization
	private const string CodeServiceDocumentClientGenerationMetadataKey =
		"_system_explorer_code_service_document_client_generation_v1";
	private const double CodeServiceDocumentQuietWindowSeconds = 0.2;
	private const int CodeServiceDocumentMaximumBindingRetryAttempts = 3;
	private const int CodeServiceDocumentMaximumQuietRetryAttempts = 3;

	private CodeServiceDocumentSynchronizationCoordinator _codeServiceDocumentSynchronizationCoordinator;
	private CodeServiceDocumentEditorBinding _codeServiceDocumentEditorBinding;
	private Timer _codeServiceDocumentQuietTimer;
	private Task _codeServiceDocumentFlightObservationTask;
	private bool _codeServiceDocumentCompositionEstablishmentAttempted;
	private bool _codeServiceDocumentBindingRetryQueued;
	private int _codeServiceDocumentBindingRetryAttempts;
	private int _codeServiceDocumentQuietRetryAttempts;
	private bool _codeServiceDocumentActiveRecaptureRequired;
	private string _codeServiceDocumentActivationCandidatePath = "";
	private bool _codeServiceDocumentActivationCandidateEdited;
	private string _codeServiceDocumentEstablishedActivePath = "";
	private bool _codeServiceDocumentActivationBoundaryPending;
	private bool _codeServiceDocumentCatchUpDeferredQueued;
	private bool _codeServiceDocumentQuietBoundaryPending;
	private bool _codeServiceDocumentFlightRetirementPending;

	private bool _codeServiceDocumentHasPendingWorkspaceReadyIntent;
	private string _codeServiceDocumentPendingWorkspaceSessionId = "";
	private int _codeServiceDocumentPendingWorkspaceOwnerPid;
	private long _codeServiceDocumentPendingWorkspaceOwnerStartTicks;
	private int _codeServiceDocumentPendingWorkspaceServicePid;
	private long _codeServiceDocumentPendingWorkspaceServiceStartTicks;
	private string _codeServiceDocumentPendingWorkspaceProjectRoot = "";
	private int _codeServiceDocumentPendingWorkspaceReusedState = -1;
	private string _codeServiceDocumentPendingWorkspaceReason = "";

	private string _codeServiceDocumentUnavailableLoggedSessionId = "";
	private int _codeServiceDocumentUnavailableLoggedServicePid;
	private long _codeServiceDocumentUnavailableLoggedServiceStartTicks;

	private bool EnsureCodeServiceDocumentSynchronizationLifecycleCurrent()
	{
		if (!TryEnsureCodeServiceDocumentSynchronizationComposition(out string detail))
		{
			TryLogEditorOperation(
				"CodeService Document Synchronization Unavailable",
				$"Reason='Composition', Detail='{detail}'"
			);
			return false;
		}

		bool bindingCurrent = false;
		try
		{
			CaptureOutgoingCodeServiceDocumentBeforeImplicitRebindIfRequired(
				_codeServiceDocumentSynchronizationCoordinator,
				_codeServiceDocumentEditorBinding
			);
			bindingCurrent = _codeServiceDocumentEditorBinding.EnsureLifecycleCurrent();
		}
		catch (Exception exception)
		{
			TryLogEditorOperation(
				"CodeService Document Binding Failed",
				$"Detail='{exception.Message}'"
			);
		}

		if (bindingCurrent)
		{
			_codeServiceDocumentBindingRetryAttempts = 0;
			RefreshCodeServiceDocumentActivationCandidateFromCurrentBinding(_codeServiceDocumentEditorBinding);
			TryConsumePendingCodeServiceDocumentWorkspaceReadyIntent();
		}
		else
		{
			QueueCodeServiceDocumentBindingRetry();
		}

		return true;
	}

	private bool TryEnsureCodeServiceDocumentSynchronizationComposition(out string detail)
	{
		detail = "";
		if (_codeServiceDocumentSynchronizationCoordinator != null
			&& _codeServiceDocumentEditorBinding != null
			&& IsValidGodotObject(_codeServiceDocumentQuietTimer))
		{
			return true;
		}

		if (_codeServiceDocumentCompositionEstablishmentAttempted)
		{
			detail = "Document synchronization composition establishment already failed in this managed generation.";
			return false;
		}
		_codeServiceDocumentCompositionEstablishmentAttempted = true;

		if (!TryPrepareCodeServiceDocumentClientGeneration(
			out Control generationMetadataHost,
			out long clientGeneration,
			out detail
		))
		{
			return false;
		}

		string epochId = Guid.NewGuid().ToString("D");
		try
		{
			_codeServiceDocumentSynchronizationCoordinator =
				new CodeServiceDocumentSynchronizationCoordinator(clientGeneration, epochId);
			_codeServiceDocumentEditorBinding = new CodeServiceDocumentEditorBinding(
				() => EditorInterface.Singleton?.GetScriptEditor(),
				TryConnectPluginSignal,
				DisconnectPluginSignal,
				nameof(OnCodeServiceDocumentScriptChanged),
				nameof(OnCodeServiceDocumentScriptClose),
				nameof(OnCodeServiceDocumentTextChanged)
			);

			_codeServiceDocumentQuietTimer = new Timer
			{
				WaitTime = CodeServiceDocumentQuietWindowSeconds,
				OneShot = true,
				Autostart = false,
			};
			AddChild(_codeServiceDocumentQuietTimer);
			if (!TryConnectPluginSignal(
				_codeServiceDocumentQuietTimer,
				Timer.SignalName.Timeout,
				nameof(OnCodeServiceDocumentQuietTimerTimeout),
				nameof(_codeServiceDocumentQuietTimer)
			))
			{
				detail = "The document synchronization quiet Timer signal could not be connected.";
				ShutdownCodeServiceDocumentSynchronization("Quiet Timer Connection Failure");
				return false;
			}

			try
			{
				generationMetadataHost.SetMeta(
					CodeServiceDocumentClientGenerationMetadataKey,
					clientGeneration
				);
			}
			catch (Exception exception)
			{
				detail = "Native document client generation metadata could not be committed: " + exception.Message;
				ShutdownCodeServiceDocumentSynchronization("Client Generation Metadata Failure");
				return false;
			}

			TryLogEditorOperation(
				"CodeService Document Composition Established",
				$"ClientGeneration='{clientGeneration}', EpochId='{epochId}'"
			);
			return true;
		}
		catch (Exception exception)
		{
			detail = exception.Message;
			ShutdownCodeServiceDocumentSynchronization("Composition Failure");
			return false;
		}
	}

	private static bool TryGetPositiveInt64Variant(Variant value, out long result)
	{
		result = 0;
		if (value.VariantType != Variant.Type.Int)
			return false;
		try
		{
			result = value.AsInt64();
			return result > 0;
		}
		catch
		{
			return false;
		}
	}

	private bool TryPrepareCodeServiceDocumentClientGeneration(
		out Control metadataHost,
		out long clientGeneration,
		out string detail
	)
	{
		metadataHost = null;
		clientGeneration = 0;
		detail = "";
		Control host;
		try
		{
			host = EditorInterface.Singleton.GetBaseControl();
		}
		catch (Exception exception)
		{
			detail = "EditorInterface base control is unavailable: " + exception.Message;
			return false;
		}

		if (!IsValidGodotObject(host))
		{
			detail = "EditorInterface base control is unavailable.";
			return false;
		}

		long nextGeneration = 1;
		try
		{
			if (host.HasMeta(CodeServiceDocumentClientGenerationMetadataKey))
			{
				Variant raw = host.GetMeta(CodeServiceDocumentClientGenerationMetadataKey);
				if (!TryGetPositiveInt64Variant(raw, out long previous))
				{
					detail = "Stored native document client generation metadata is malformed or non-positive.";
					return false;
				}
				if (previous == long.MaxValue)
				{
					detail = "Stored native document client generation reached Int64.MaxValue; restart Godot to re-establish document synchronization.";
					return false;
				}
				nextGeneration = checked(previous + 1);
			}

			metadataHost = host;
			clientGeneration = nextGeneration;
			return true;
		}
		catch (Exception exception)
		{
			detail = "Native document client generation metadata could not be read: " + exception.Message;
			return false;
		}
	}

	private void OnCodeServiceDocumentTextChanged()
	{
		CodeServiceDocumentSynchronizationCoordinator coordinator =
			_codeServiceDocumentSynchronizationCoordinator;
		CodeServiceDocumentEditorBinding binding = _codeServiceDocumentEditorBinding;
		if (coordinator == null || binding == null)
			return;

		string documentPath = binding.BoundWirePath;
		if (string.IsNullOrEmpty(documentPath))
			return;

		if (!coordinator.TryRecordTextChanged(documentPath, out _, out _))
			return;

		if (_codeServiceDocumentActivationBoundaryPending
			&& CodeServiceDocumentPath.Equals(documentPath, _codeServiceDocumentActivationCandidatePath))
		{
			_codeServiceDocumentActivationCandidateEdited = true;
			EnsureCodeServiceDocumentQuietTimerArmed();
			return;
		}

		if (CodeServiceDocumentPath.Equals(documentPath, _codeServiceDocumentEstablishedActivePath))
		{
			RequestCodeServiceDocumentCatchUp(documentPath);
			return;
		}

		// A TextChanged from a bound C# editor whose activation signal was missed must not
		// bypass pre-admission stability. Record the real edit above, then establish that
		// exact bound path as the single activation candidate without capturing source text.
		BeginCodeServiceDocumentActivationCandidate(documentPath, candidateEdited: true);
	}

	private void OnCodeServiceDocumentScriptClose(Script script)
	{
		CodeServiceDocumentSynchronizationCoordinator coordinator =
			_codeServiceDocumentSynchronizationCoordinator;
		CodeServiceDocumentEditorBinding binding = _codeServiceDocumentEditorBinding;
		if (coordinator == null || binding == null)
			return;

		// script_close is emitted before Godot removes the script from its open inventory.
		// Preserve a bound buffer only when that exact script is the one being closed; closing
		// an unrelated inactive tab must not force an early full capture of the active editor.
		if (IsValidGodotObject(script)
			&& binding.TryGetBoundDocument(out string boundResourcePath, out _, out _)
			&& string.Equals(script.ResourcePath, boundResourcePath, StringComparison.Ordinal))
		{
			CaptureOutgoingCodeServiceDocumentIfRequired(coordinator, binding);
		}

		try
		{
			CallDeferred(
				nameof(ApplyCodeServiceDocumentScriptCloseDeferred),
				coordinator.ClientGeneration,
				coordinator.EpochId
			);
		}
		catch
		{
		}
	}

	private void ApplyCodeServiceDocumentScriptCloseDeferred(
		long clientGeneration,
		string epochId
	)
	{
		if (!IsCodeServiceDocumentCompositionCurrent(clientGeneration, epochId))
			return;

		CodeServiceDocumentEditorBinding binding = _codeServiceDocumentEditorBinding;
		if (binding == null)
			return;

		try
		{
			binding.EnsureLifecycleCurrent();
		}
		catch (Exception exception)
		{
			TryLogEditorOperation(
				"CodeService Document Binding Failed",
				$"Reason='Script Close', Detail='{exception.Message}'"
			);
		}

		TryRefreshCodeServiceOpenDocumentInventory("Script Close");
		RefreshCodeServiceDocumentActivationCandidateFromCurrentBinding(binding);
		// Closing any C# tab changes the Service open inventory. Let a newly selected script's
		// activation deadline remain authoritative instead of resetting it from close traffic.
		RequestCodeServiceDocumentQuietBoundary();
	}

	private void OnCodeServiceDocumentScriptChanged(Script script)
	{
		CodeServiceDocumentSynchronizationCoordinator coordinator =
			_codeServiceDocumentSynchronizationCoordinator;
		CodeServiceDocumentEditorBinding binding = _codeServiceDocumentEditorBinding;
		if (coordinator == null || binding == null)
			return;

		CaptureOutgoingCodeServiceDocumentBeforeImplicitRebindIfRequired(coordinator, binding);

		try
		{
			binding.RefreshCurrentBinding();
		}
		catch (Exception exception)
		{
			TryLogEditorOperation(
				"CodeService Document Binding Failed",
				$"Reason='Script Changed', Detail='{exception.Message}'"
			);
		}

		TryRefreshCodeServiceOpenDocumentInventory("Script Changed");
		// Replace the single cheap activation candidate. Only an actual identity change
		// restarts the 200 ms activation deadline; no full snapshot is created here.
		RefreshCodeServiceDocumentActivationCandidateFromCurrentBinding(binding);
	}

	private void CaptureOutgoingCodeServiceDocumentBeforeImplicitRebindIfRequired(
		CodeServiceDocumentSynchronizationCoordinator coordinator,
		CodeServiceDocumentEditorBinding binding
	)
	{
		if (coordinator == null || binding == null)
			return;
		if (!binding.TryGetBoundDocument(out _, out _, out _))
			return;
		if (binding.TryGetCurrentDocument(out _, out _, out _))
			return;

		CaptureOutgoingCodeServiceDocumentIfRequired(coordinator, binding);
	}

	private void CaptureOutgoingCodeServiceDocumentIfRequired(
		CodeServiceDocumentSynchronizationCoordinator coordinator,
		CodeServiceDocumentEditorBinding binding
	)
	{
		if (!binding.TryGetBoundDocument(out _, out string documentPath, out CodeEdit codeEdit))
			return;

		bool establishedOutgoing =
			CodeServiceDocumentPath.Equals(documentPath, _codeServiceDocumentEstablishedActivePath);
		bool editedCandidateOutgoing =
			_codeServiceDocumentActivationBoundaryPending
			&& _codeServiceDocumentActivationCandidateEdited
			&& CodeServiceDocumentPath.Equals(documentPath, _codeServiceDocumentActivationCandidatePath);
		if ((!establishedOutgoing && !editedCandidateOutgoing)
			|| !coordinator.ShouldCaptureOnBoundary(documentPath))
		{
			return;
		}

		string text;
		try
		{
			text = codeEdit.Text;
		}
		catch (Exception exception)
		{
			TryLogEditorOperation(
				"CodeService Document Snapshot Capture Failed",
				$"DocumentPath='{documentPath}', Boundary='Outgoing', Detail='{exception.Message}'"
			);
			return;
		}

		if (coordinator.TryCaptureSnapshot(documentPath, text, out CodeServiceDocumentSnapshot snapshot, out string detail))
		{
			if (editedCandidateOutgoing)
				_codeServiceDocumentActivationCandidateEdited = false;
			TryLogEditorOperation(
				"CodeService Document Snapshot Captured",
				$"DocumentPath='{snapshot.DocumentPath}', ClientGeneration='{coordinator.ClientGeneration}', EpochId='{coordinator.EpochId}', ClientVersion='{snapshot.ClientVersion}', Utf8Bytes='{snapshot.Utf8Bytes}', Boundary='Outgoing'"
			);
		}
		else
		{
			TryLogEditorOperation(
				"CodeService Document Snapshot Capture Failed",
				$"DocumentPath='{documentPath}', Boundary='Outgoing', Detail='{detail}'"
			);
		}
	}

	private void OnCodeServiceDocumentQuietTimerTimeout()
	{
		_codeServiceDocumentBindingRetryQueued = false;
		_codeServiceDocumentQuietBoundaryPending = false;
		CodeServiceDocumentSynchronizationCoordinator coordinator =
			_codeServiceDocumentSynchronizationCoordinator;
		CodeServiceDocumentEditorBinding binding = _codeServiceDocumentEditorBinding;
		if (coordinator == null || binding == null || coordinator.IsFailedClosed)
			return;

		coordinator.MarkExplicitCatchUpBoundary();

		bool bindingCurrent = false;
		try
		{
			CaptureOutgoingCodeServiceDocumentBeforeImplicitRebindIfRequired(coordinator, binding);
			bindingCurrent = binding.EnsureLifecycleCurrent();
		}
		catch (Exception exception)
		{
			TryLogEditorOperation(
				"CodeService Document Binding Failed",
				$"Reason='Quiet Boundary', Detail='{exception.Message}'"
			);
		}

		if (!bindingCurrent)
		{
			QueueCodeServiceDocumentBindingRetry();
			return;
		}

		_codeServiceDocumentBindingRetryAttempts = 0;
		TryConsumePendingCodeServiceDocumentWorkspaceReadyIntent();
		if (_codeServiceDocumentQuietBoundaryPending)
		{
			EnsureCodeServiceDocumentQuietTimerArmed();
			return;
		}
		if (!TryRefreshCodeServiceOpenDocumentInventory("Quiet Boundary"))
			return;

		bool hasCurrentDocument =
			binding.TryGetCurrentDocument(out _, out string currentPath, out CodeEdit codeEdit);
		string currentIdentity = hasCurrentDocument ? currentPath : "";
		bool admittedActivation = false;

		if (_codeServiceDocumentActivationBoundaryPending)
		{
			if (!CodeServiceDocumentActivationIdentityEquals(
				currentIdentity,
				_codeServiceDocumentActivationCandidatePath
			))
			{
				BeginCodeServiceDocumentActivationCandidate(
					currentIdentity,
					candidateEdited: false
				);
				return;
			}

			_codeServiceDocumentActivationBoundaryPending = false;
			_codeServiceDocumentActivationCandidateEdited = false;
			_codeServiceDocumentEstablishedActivePath = currentIdentity;
			admittedActivation = true;
		}
		else if (!CodeServiceDocumentActivationIdentityEquals(
			currentIdentity,
			_codeServiceDocumentEstablishedActivePath
		))
		{
			// A missed/recovery rebind is still navigation and cannot bypass the 200 ms gate.
			BeginCodeServiceDocumentActivationCandidate(currentIdentity, candidateEdited: false);
			return;
		}

		string activeDocumentPath = hasCurrentDocument ? currentPath : "";
		if (hasCurrentDocument)
			coordinator.TryEnsureTracked(currentPath, out _, out _);

		// Do not manufacture a second full-text state while an actual document flight is
		// still executing or its terminal result has not yet retired on the Godot thread.
		// The retirement boundary will re-arm exactly one newest-state catch-up if needed.
		if (coordinator.IsFlightActive || _codeServiceDocumentFlightRetirementPending)
			return;

		if (hasCurrentDocument
			&& (_codeServiceDocumentActiveRecaptureRequired
				|| coordinator.ShouldCaptureOnBoundary(currentPath)))
		{
			string boundary = admittedActivation
				? "Activation"
				: (_codeServiceDocumentActiveRecaptureRequired ? "Recovery" : "QuietRetry");
			string text;
			try
			{
				text = codeEdit.Text;
			}
			catch (Exception exception)
			{
				TryLogEditorOperation(
					"CodeService Document Snapshot Capture Failed",
					$"DocumentPath='{currentPath}', Boundary='{boundary}', Detail='{exception.Message}'"
				);
				return;
			}

			if (!coordinator.TryCaptureSnapshot(currentPath, text, out CodeServiceDocumentSnapshot snapshot, out string captureDetail))
			{
				TryLogEditorOperation(
					"CodeService Document Snapshot Capture Failed",
					$"DocumentPath='{currentPath}', Boundary='{boundary}', Detail='{captureDetail}'"
				);
				// Do not let one locally un-capturable active buffer prevent epoch reconciliation
				// or replay of other authoritative cached documents. A new flight will never use
				// an older capture for this path because planning requires captured==live version.
			}
			else
			{
				_codeServiceDocumentActiveRecaptureRequired = false;
				TryLogEditorOperation(
					"CodeService Document Snapshot Captured",
					$"DocumentPath='{snapshot.DocumentPath}', ClientGeneration='{coordinator.ClientGeneration}', EpochId='{coordinator.EpochId}', ClientVersion='{snapshot.ClientVersion}', Utf8Bytes='{snapshot.Utf8Bytes}', Boundary='{boundary}'"
				);
			}
		}

		StartCodeServiceDocumentSynchronizationFlight(activeDocumentPath);
	}

	private bool TryRefreshCodeServiceOpenDocumentInventory(string reason)
	{
		CodeServiceDocumentSynchronizationCoordinator coordinator =
			_codeServiceDocumentSynchronizationCoordinator;
		CodeServiceDocumentEditorBinding binding = _codeServiceDocumentEditorBinding;
		if (coordinator == null || binding == null)
			return false;

		ScriptEditor scriptEditor = binding.ScriptEditor;
		if (!IsValidGodotObject(scriptEditor))
		{
			TryLogEditorOperation(
				"CodeService Document Open Set Refresh Failed",
				$"Reason='{reason}', Detail='ScriptEditor is unavailable.'"
			);
			return false;
		}

		List<string> paths = new();
		HashSet<string> unique = new(CodeServiceDocumentPath.PlatformComparer);
		try
		{
			foreach (Script openScript in scriptEditor.GetOpenScripts())
			{
				if (!IsValidGodotObject(openScript))
				{
					TryLogEditorOperation(
						"CodeService Document Open Set Refresh Failed",
						$"Reason='{reason}', Detail='GetOpenScripts contained an invalid Script object.'"
					);
					return false;
				}
				string resourcePath = openScript.ResourcePath;
				if (string.IsNullOrWhiteSpace(resourcePath)
					|| !resourcePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}

				if (!CodeServiceDocumentPath.TryFromResourcePath(resourcePath, out string documentPath, out string pathDetail))
				{
					TryLogEditorOperation(
						"CodeService Document Open Set Refresh Failed",
						$"Reason='{reason}', ResourcePath='{resourcePath}', Detail='{pathDetail}'"
					);
					return false;
				}
				if (unique.Add(documentPath))
					paths.Add(documentPath);
			}
		}
		catch (Exception exception)
		{
			TryLogEditorOperation(
				"CodeService Document Open Set Refresh Failed",
				$"Reason='{reason}', Detail='{exception.Message}'"
			);
			return false;
		}

		if (paths.Count > CodeServiceDocumentSynchronizationLimits.MaxTrackedOpenDocuments)
		{
			TryLogEditorOperation(
				"CodeService Document Open Set Refresh Failed",
				$"Reason='{reason}', OpenDocumentCount='{paths.Count}', Limit='{CodeServiceDocumentSynchronizationLimits.MaxTrackedOpenDocuments}'"
			);
			return false;
		}

		if (!coordinator.TryUpdateOpenInventory(paths, out string detail))
		{
			TryLogEditorOperation(
				"CodeService Document Open Set Refresh Failed",
				$"Reason='{reason}', Detail='{detail}'"
			);
			return false;
		}
		return true;
	}

	private static bool CodeServiceDocumentActivationIdentityEquals(string left, string right)
	{
		if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right))
			return string.IsNullOrEmpty(left) && string.IsNullOrEmpty(right);
		return CodeServiceDocumentPath.Equals(left, right);
	}

	private void RefreshCodeServiceDocumentActivationCandidateFromCurrentBinding(
		CodeServiceDocumentEditorBinding binding
	)
	{
		if (binding == null)
			return;

		string currentPath = binding.TryGetCurrentDocument(out _, out string documentPath, out _)
			? documentPath
			: "";

		if (!_codeServiceDocumentActivationBoundaryPending
			&& CodeServiceDocumentActivationIdentityEquals(
				currentPath,
				_codeServiceDocumentEstablishedActivePath
			))
		{
			return;
		}

		if (_codeServiceDocumentActivationBoundaryPending
			&& CodeServiceDocumentActivationIdentityEquals(
				currentPath,
				_codeServiceDocumentActivationCandidatePath
			))
		{
			EnsureCodeServiceDocumentQuietTimerArmed();
			return;
		}

		BeginCodeServiceDocumentActivationCandidate(currentPath, candidateEdited: false);
	}

	private void BeginCodeServiceDocumentActivationCandidate(
		string documentPath,
		bool candidateEdited
	)
	{
		string nextPath = documentPath ?? "";
		if (_codeServiceDocumentActivationBoundaryPending
			&& CodeServiceDocumentActivationIdentityEquals(
				nextPath,
				_codeServiceDocumentActivationCandidatePath
			))
		{
			_codeServiceDocumentActivationCandidateEdited |= candidateEdited;
			EnsureCodeServiceDocumentQuietTimerArmed();
			return;
		}

		if (!_codeServiceDocumentActivationBoundaryPending
			&& CodeServiceDocumentActivationIdentityEquals(
				nextPath,
				_codeServiceDocumentEstablishedActivePath
			))
		{
			return;
		}

		_codeServiceDocumentActivationCandidatePath = nextPath;
		_codeServiceDocumentActivationCandidateEdited = candidateEdited;
		_codeServiceDocumentEstablishedActivePath = "";
		_codeServiceDocumentActivationBoundaryPending = true;
		RestartCodeServiceDocumentQuietTimer();
	}

	private void RequestCodeServiceDocumentCatchUp(string documentPath)
	{
		CodeServiceDocumentSynchronizationCoordinator coordinator =
			_codeServiceDocumentSynchronizationCoordinator;
		if (coordinator == null || coordinator.IsFailedClosed || string.IsNullOrEmpty(documentPath))
			return;

		if (_codeServiceDocumentActivationBoundaryPending)
		{
			EnsureCodeServiceDocumentQuietTimerArmed();
			return;
		}
		if (_codeServiceDocumentQuietBoundaryPending || _codeServiceDocumentBindingRetryQueued)
		{
			EnsureCodeServiceDocumentQuietTimerArmed();
			return;
		}
		if (coordinator.IsFlightActive || _codeServiceDocumentFlightRetirementPending)
			return;
		if (!CodeServiceDocumentPath.Equals(documentPath, _codeServiceDocumentEstablishedActivePath))
			return;

		QueueCodeServiceDocumentCatchUpDeferred();
	}

	private void QueueCodeServiceDocumentCatchUpDeferred()
	{
		if (_codeServiceDocumentCatchUpDeferredQueued)
			return;

		CodeServiceDocumentSynchronizationCoordinator coordinator =
			_codeServiceDocumentSynchronizationCoordinator;
		if (coordinator == null || coordinator.IsFailedClosed)
			return;

		_codeServiceDocumentCatchUpDeferredQueued = true;
		try
		{
			CallDeferred(
				nameof(ApplyCodeServiceDocumentCatchUpDeferred),
				coordinator.ClientGeneration,
				coordinator.EpochId
			);
		}
		catch
		{
			_codeServiceDocumentCatchUpDeferredQueued = false;
		}
	}

	private void ApplyCodeServiceDocumentCatchUpDeferred(long clientGeneration, string epochId)
	{
		if (!IsCodeServiceDocumentCompositionCurrent(clientGeneration, epochId))
			return;

		_codeServiceDocumentCatchUpDeferredQueued = false;
		CodeServiceDocumentSynchronizationCoordinator coordinator =
			_codeServiceDocumentSynchronizationCoordinator;
		CodeServiceDocumentEditorBinding binding = _codeServiceDocumentEditorBinding;
		if (coordinator == null || binding == null || coordinator.IsFailedClosed)
			return;

		if (_codeServiceDocumentActivationBoundaryPending
			|| _codeServiceDocumentQuietBoundaryPending
			|| _codeServiceDocumentBindingRetryQueued
			|| coordinator.IsFlightActive
			|| _codeServiceDocumentFlightRetirementPending)
		{
			return;
		}

		bool hasCurrentDocument =
			binding.TryGetCurrentDocument(out _, out string currentPath, out CodeEdit codeEdit);
		string currentIdentity = hasCurrentDocument ? currentPath : "";
		if (!CodeServiceDocumentActivationIdentityEquals(
			currentIdentity,
			_codeServiceDocumentEstablishedActivePath
		))
		{
			RefreshCodeServiceDocumentActivationCandidateFromCurrentBinding(binding);
			return;
		}

		if (!hasCurrentDocument)
		{
			StartCodeServiceDocumentSynchronizationFlight("");
			return;
		}

		bool recoveryRecapture = _codeServiceDocumentActiveRecaptureRequired;
		if (recoveryRecapture || coordinator.ShouldCaptureOnBoundary(currentPath))
		{
			string boundary = recoveryRecapture ? "Recovery" : "Typing";
			string text;
			try
			{
				text = codeEdit.Text;
			}
			catch (Exception exception)
			{
				TryLogEditorOperation(
					"CodeService Document Snapshot Capture Failed",
					$"DocumentPath='{currentPath}', Boundary='{boundary}', Detail='{exception.Message}'"
				);
				return;
			}

			if (!coordinator.TryCaptureSnapshot(currentPath, text, out CodeServiceDocumentSnapshot snapshot, out string detail))
			{
				TryLogEditorOperation(
					"CodeService Document Snapshot Capture Failed",
					$"DocumentPath='{currentPath}', Boundary='{boundary}', Detail='{detail}'"
				);
				return;
			}

			if (recoveryRecapture)
				_codeServiceDocumentActiveRecaptureRequired = false;

			TryLogEditorOperation(
				"CodeService Document Snapshot Captured",
				$"DocumentPath='{snapshot.DocumentPath}', ClientGeneration='{coordinator.ClientGeneration}', EpochId='{coordinator.EpochId}', ClientVersion='{snapshot.ClientVersion}', Utf8Bytes='{snapshot.Utf8Bytes}', Boundary='{boundary}'"
			);
		}

		StartCodeServiceDocumentSynchronizationFlight(currentPath);
	}

	private bool HasCodeServiceDocumentCatchUpWorkForCurrentEstablishedDocument(
		CodeServiceDocumentSynchronizationCoordinator coordinator
	)
	{
		if (coordinator == null)
			return false;
		if (coordinator.HasPendingWork)
			return true;

		CodeServiceDocumentEditorBinding binding = _codeServiceDocumentEditorBinding;
		return binding != null
			&& binding.TryGetCurrentDocument(out _, out string currentPath, out _)
			&& CodeServiceDocumentPath.Equals(currentPath, _codeServiceDocumentEstablishedActivePath)
			&& (_codeServiceDocumentActiveRecaptureRequired
				|| coordinator.ShouldCaptureOnBoundary(currentPath));
	}

	private void TryTrackCurrentCodeServiceDocument()
	{
		CodeServiceDocumentSynchronizationCoordinator coordinator =
			_codeServiceDocumentSynchronizationCoordinator;
		CodeServiceDocumentEditorBinding binding = _codeServiceDocumentEditorBinding;
		if (coordinator == null || binding == null)
			return;
		if (binding.TryGetCurrentDocument(out _, out string documentPath, out _))
			coordinator.TryEnsureTracked(documentPath, out _, out _);
	}

	private bool TryPrepareCodeServiceCompletionDocumentAdmission(
		string resourcePath,
		out CodeServiceDocumentCompletionAdmissionSnapshot snapshot,
		out string detail
	)
	{
		snapshot = default;
		detail = "";
		CodeServiceDocumentSynchronizationCoordinator coordinator =
			_codeServiceDocumentSynchronizationCoordinator;
		if (coordinator == null || coordinator.IsFailedClosed)
		{
			detail = "Document synchronization composition is unavailable.";
			return false;
		}
		if (!CodeServiceDocumentPath.TryFromResourcePath(
			resourcePath,
			out string documentPath,
			out detail
		))
		{
			return false;
		}
		if (!coordinator.TryEnsureTracked(documentPath, out _, out detail)
			|| !coordinator.TryGetCompletionAdmissionSnapshot(documentPath, out snapshot, out detail))
		{
			return false;
		}

		if (!snapshot.IsCurrentVersionSynchronized)
			RequestCodeServiceDocumentCatchUp(documentPath);
		return true;
	}

	private void StartCodeServiceDocumentSynchronizationFlight(string activeDocumentPath)
	{
		CodeServiceDocumentSynchronizationCoordinator coordinator =
			_codeServiceDocumentSynchronizationCoordinator;
		CodeServiceClientCoordinator clientCoordinator = _codeServiceClientCoordinator;
		if (coordinator == null
			|| clientCoordinator == null
			|| coordinator.IsFlightActive
			|| _codeServiceDocumentFlightRetirementPending)
		{
			return;
		}

		if (!coordinator.TryStartFlight(
			clientCoordinator,
			activeDocumentPath,
			out Task<CodeServiceDocumentFlightResult> flight,
			out _
		))
		{
			return;
		}

		long clientGeneration = coordinator.ClientGeneration;
		string epochId = coordinator.EpochId;
		_codeServiceDocumentFlightRetirementPending = true;
		_codeServiceDocumentFlightObservationTask = ObserveCodeServiceDocumentFlightAsync(
			flight,
			clientGeneration,
			epochId
		);
	}

	private async Task ObserveCodeServiceDocumentFlightAsync(
		Task<CodeServiceDocumentFlightResult> flight,
		long clientGeneration,
		string epochId
	)
	{
		CodeServiceDocumentFlightResult result;
		try
		{
			result = await flight.ConfigureAwait(false);
		}
		catch
		{
			return;
		}

		if (!IsCodeServiceDocumentCompositionCurrent(clientGeneration, epochId))
			return;

		try
		{
			CallDeferred(
				nameof(ApplyCodeServiceDocumentFlightCompletionDeferred),
				clientGeneration,
				epochId,
				result.SessionId,
				result.ServiceProcessId,
				result.ServiceStartTimeUtcTicks,
				(int)result.TerminalOutcome,
				result.HasPendingWork,
				result.RetryAfterQuietWindow,
				result.CompositionFailedClosed,
				result.RequestedSessionRecovery,
				result.SentSnapshotCount,
				result.Detail ?? ""
			);
		}
		catch
		{
		}
	}

	private void ApplyCodeServiceDocumentFlightCompletionDeferred(
		long clientGeneration,
		string epochId,
		string sessionId,
		int serviceProcessId,
		long serviceStartTimeUtcTicks,
		int outcomeValue,
		bool hasPendingWork,
		bool retryAfterQuietWindow,
		bool compositionFailedClosed,
		bool requestedSessionRecovery,
		int sentSnapshotCount,
		string detail
	)
	{
		if (!IsCodeServiceDocumentCompositionCurrent(clientGeneration, epochId))
			return;

		CodeServiceDocumentSynchronizationCoordinator coordinator =
			_codeServiceDocumentSynchronizationCoordinator;
		CodeServiceClientCoordinator clientCoordinator = _codeServiceClientCoordinator;
		if (coordinator == null || clientCoordinator == null)
			return;

		// The coordinator releases its pure-managed _activeFlight before this Godot-thread
		// result boundary. Keep plugin admission closed until retry/backoff/session policy has
		// actually been applied here, otherwise a TextChanged could race through the gap.
		_codeServiceDocumentFlightRetirementPending = false;

		if (!clientCoordinator.TryGetReadySessionInfo(out CodeServiceClientSessionInfo currentSession)
			|| !string.Equals(currentSession.SessionId, sessionId, StringComparison.Ordinal)
			|| currentSession.ServiceProcessIdentity.ProcessId != serviceProcessId
			|| currentSession.ServiceProcessIdentity.StartTimeUtcTicks != serviceStartTimeUtcTicks)
		{
			if (HasCodeServiceDocumentCatchUpWorkForCurrentEstablishedDocument(coordinator))
			{
				if (_codeServiceDocumentActivationBoundaryPending
					|| _codeServiceDocumentQuietBoundaryPending
					|| _codeServiceDocumentBindingRetryQueued)
				{
					EnsureCodeServiceDocumentQuietTimerArmed();
				}
				else
				{
					QueueCodeServiceDocumentCatchUpDeferred();
				}
			}
			return;
		}

		CodeServiceDocumentOutcome outcome = Enum.IsDefined(typeof(CodeServiceDocumentOutcome), outcomeValue)
			? (CodeServiceDocumentOutcome)outcomeValue
			: CodeServiceDocumentOutcome.MalformedResponse;

		if (outcome == CodeServiceDocumentOutcome.DocumentSynchronizationUnavailableForSession)
		{
			if (!IsCodeServiceDocumentUnavailableLoggedForSession(currentSession))
			{
				RememberCodeServiceDocumentUnavailableSession(currentSession);
				TryLogEditorOperation(
					"CodeService Document Synchronization Unavailable",
					$"SessionId='{currentSession.SessionId}', ServicePid='{currentSession.ServiceProcessIdentity.ProcessId}', ServiceStartTimeUtcTicks='{currentSession.ServiceProcessIdentity.StartTimeUtcTicks}', Detail='{detail}'"
				);
			}
			return;
		}

		TryLogEditorOperation(
			"CodeService Document Flight Completed",
			$"ClientGeneration='{clientGeneration}', EpochId='{epochId}', Outcome='{outcome}', SessionId='{sessionId}', ServicePid='{serviceProcessId}', SentSnapshotCount='{sentSnapshotCount}', Pending='{hasPendingWork}', RetryAfterQuiet='{retryAfterQuietWindow}', RecoveryRequested='{requestedSessionRecovery}', FailedClosed='{compositionFailedClosed}', Detail='{detail}'"
		);

		if (compositionFailedClosed)
			return;

		if (outcome is CodeServiceDocumentOutcome.Success or CodeServiceDocumentOutcome.AlreadyCurrent)
			TryResumePendingAutocompleteCompletionAfterDocumentSynchronization();

		if (outcome is CodeServiceDocumentOutcome.RoslynUnavailable
			or CodeServiceDocumentOutcome.Unavailable)
		{
			// Suspend until a later user/editor lifecycle boundary explicitly re-arms the
			// capability; do not turn a terminal unavailable result into a timer loop.
			return;
		}

		if (retryAfterQuietWindow)
		{
			if (_codeServiceDocumentQuietRetryAttempts >= CodeServiceDocumentMaximumQuietRetryAttempts)
			{
				TryLogEditorOperation(
					"CodeService Document Quiet Retry Suspended",
					$"Outcome='{outcome}', Attempts='{_codeServiceDocumentQuietRetryAttempts}', SessionId='{sessionId}'"
				);
				return;
			}
			_codeServiceDocumentQuietRetryAttempts++;
			RequestCodeServiceDocumentQuietBoundary();
			return;
		}

		_codeServiceDocumentQuietRetryAttempts = 0;
		if (hasPendingWork || HasCodeServiceDocumentCatchUpWorkForCurrentEstablishedDocument(coordinator))
		{
			if (_codeServiceDocumentActivationBoundaryPending
				|| _codeServiceDocumentQuietBoundaryPending
				|| _codeServiceDocumentBindingRetryQueued)
			{
				EnsureCodeServiceDocumentQuietTimerArmed();
			}
			else
			{
				QueueCodeServiceDocumentCatchUpDeferred();
			}
		}
	}

	private bool IsCodeServiceDocumentCompositionCurrent(long clientGeneration, string epochId)
	{
		CodeServiceDocumentSynchronizationCoordinator coordinator =
			_codeServiceDocumentSynchronizationCoordinator;
		return coordinator != null
			&& !coordinator.IsFailedClosed
			&& coordinator.ClientGeneration == clientGeneration
			&& string.Equals(coordinator.EpochId, epochId, StringComparison.Ordinal);
	}

	private void RestartCodeServiceDocumentQuietTimer()
	{
		Timer timer = _codeServiceDocumentQuietTimer;
		if (!IsValidGodotObject(timer))
			return;
		try
		{
			timer.Stop();
			timer.Start(CodeServiceDocumentQuietWindowSeconds);
		}
		catch
		{
		}
	}

	private void EnsureCodeServiceDocumentQuietTimerArmed()
	{
		Timer timer = _codeServiceDocumentQuietTimer;
		if (!IsValidGodotObject(timer))
			return;
		try
		{
			if (timer.IsStopped())
				timer.Start(CodeServiceDocumentQuietWindowSeconds);
		}
		catch
		{
		}
	}

	private void RequestCodeServiceDocumentQuietBoundary()
	{
		_codeServiceDocumentQuietBoundaryPending = true;
		if (_codeServiceDocumentActivationBoundaryPending)
		{
			// Activation keeps its original deadline. Repair/retry/completion traffic may
			// piggyback that boundary but must not debounce it forward.
			EnsureCodeServiceDocumentQuietTimerArmed();
			return;
		}
		RestartCodeServiceDocumentQuietTimer();
	}

	private void QueueCodeServiceDocumentBindingRetry()
	{
		if (_codeServiceDocumentBindingRetryQueued)
			return;
		if (_codeServiceDocumentBindingRetryAttempts >= CodeServiceDocumentMaximumBindingRetryAttempts)
		{
			TryLogEditorOperation(
				"CodeService Document Binding Suspended",
				$"Attempts='{_codeServiceDocumentBindingRetryAttempts}'"
			);
			return;
		}

		_codeServiceDocumentBindingRetryAttempts++;
		_codeServiceDocumentBindingRetryQueued = true;
		RestartCodeServiceDocumentQuietTimer();
	}

	private void ApplyCodeServiceDocumentWorkspaceReadyIntent(
		CodeServiceClientSessionInfo sessionInfo,
		string normalizedProjectRoot,
		int reusedState,
		string reason
	)
	{
		_codeServiceDocumentHasPendingWorkspaceReadyIntent = true;
		_codeServiceDocumentPendingWorkspaceSessionId = sessionInfo.SessionId;
		_codeServiceDocumentPendingWorkspaceOwnerPid = sessionInfo.GodotOwnerIdentity.ProcessId;
		_codeServiceDocumentPendingWorkspaceOwnerStartTicks = sessionInfo.GodotOwnerIdentity.StartTimeUtcTicks;
		_codeServiceDocumentPendingWorkspaceServicePid = sessionInfo.ServiceProcessIdentity.ProcessId;
		_codeServiceDocumentPendingWorkspaceServiceStartTicks = sessionInfo.ServiceProcessIdentity.StartTimeUtcTicks;
		_codeServiceDocumentPendingWorkspaceProjectRoot = normalizedProjectRoot ?? "";
		_codeServiceDocumentPendingWorkspaceReusedState = reusedState;
		_codeServiceDocumentPendingWorkspaceReason = reason ?? "CodeService Workspace Ready";

		TryConsumePendingCodeServiceDocumentWorkspaceReadyIntent();
	}

	private void TryConsumePendingCodeServiceDocumentWorkspaceReadyIntent()
	{
		if (!_codeServiceDocumentHasPendingWorkspaceReadyIntent)
			return;
		CodeServiceDocumentSynchronizationCoordinator coordinator =
			_codeServiceDocumentSynchronizationCoordinator;
		CodeServiceDocumentEditorBinding binding = _codeServiceDocumentEditorBinding;
		CodeServiceClientCoordinator clientCoordinator = _codeServiceClientCoordinator;
		if (coordinator == null || binding == null || clientCoordinator == null)
			return;

		// Workspace Ready can race ahead of ScriptEditor composition. Keep the scalar intent
		// pending until editor binding is genuinely current; preserve any outgoing bound
		// buffer before a catch-up rebind so this startup path cannot discard unsaved text.
		try
		{
			CaptureOutgoingCodeServiceDocumentBeforeImplicitRebindIfRequired(coordinator, binding);
			if (!binding.EnsureLifecycleCurrent())
			{
				QueueCodeServiceDocumentBindingRetry();
				return;
			}
		}
		catch (Exception exception)
		{
			TryLogEditorOperation(
				"CodeService Document Binding Failed",
				$"Reason='Workspace Ready Catch-up', Detail='{exception.Message}'"
			);
			QueueCodeServiceDocumentBindingRetry();
			return;
		}

		_codeServiceDocumentBindingRetryAttempts = 0;
		if (!clientCoordinator.TryGetReadySessionInfo(out CodeServiceClientSessionInfo currentSession))
			return;
		if (!MatchesPendingCodeServiceDocumentWorkspaceSession(currentSession))
			return;

		if (!coordinator.TrySetWorkspaceReady(
			currentSession,
			_codeServiceDocumentPendingWorkspaceProjectRoot,
			out bool logicalSessionChanged,
			out string detail
		))
		{
			TryLogEditorOperation(
				"CodeService Document Workspace Ready Rejected",
				$"SessionId='{currentSession.SessionId}', Detail='{detail}'"
			);
			return;
		}

		if (logicalSessionChanged)
		{
			CancelAutocompleteImportResolveForLogicalSessionChange(currentSession);
			_codeServiceDocumentUnavailableLoggedSessionId = "";
			_codeServiceDocumentUnavailableLoggedServicePid = 0;
			_codeServiceDocumentUnavailableLoggedServiceStartTicks = 0;
		}

		_codeServiceDocumentHasPendingWorkspaceReadyIntent = false;
		if (coordinator.IsDocumentSynchronizationUnavailableForCurrentSession)
			return;

		_codeServiceDocumentQuietRetryAttempts = 0;
		_codeServiceDocumentActiveRecaptureRequired = true;
		TryRefreshCodeServiceOpenDocumentInventory("Workspace Ready");
		TryTrackCurrentCodeServiceDocument();
		RefreshCodeServiceDocumentActivationCandidateFromCurrentBinding(binding);
		RequestCodeServiceDocumentQuietBoundary();
		TryResumePendingAutocompleteCompletionAfterWorkspaceReady();

		TryLogEditorOperation(
			"CodeService Document Workspace Ready",
			$"ClientGeneration='{coordinator.ClientGeneration}', EpochId='{coordinator.EpochId}', SessionId='{currentSession.SessionId}', ServicePid='{currentSession.ServiceProcessIdentity.ProcessId}', ProjectRoot='{_codeServiceDocumentPendingWorkspaceProjectRoot}', ReusedState='{_codeServiceDocumentPendingWorkspaceReusedState}', Reason='{_codeServiceDocumentPendingWorkspaceReason}'"
		);
	}

	private bool MatchesPendingCodeServiceDocumentWorkspaceSession(CodeServiceClientSessionInfo currentSession)
	{
		return string.Equals(currentSession.SessionId, _codeServiceDocumentPendingWorkspaceSessionId, StringComparison.Ordinal)
			&& currentSession.GodotOwnerIdentity.ProcessId == _codeServiceDocumentPendingWorkspaceOwnerPid
			&& currentSession.GodotOwnerIdentity.StartTimeUtcTicks == _codeServiceDocumentPendingWorkspaceOwnerStartTicks
			&& currentSession.ServiceProcessIdentity.ProcessId == _codeServiceDocumentPendingWorkspaceServicePid
			&& currentSession.ServiceProcessIdentity.StartTimeUtcTicks == _codeServiceDocumentPendingWorkspaceServiceStartTicks;
	}

	private bool IsCodeServiceDocumentUnavailableLoggedForSession(CodeServiceClientSessionInfo session) =>
		string.Equals(_codeServiceDocumentUnavailableLoggedSessionId, session.SessionId, StringComparison.Ordinal)
		&& _codeServiceDocumentUnavailableLoggedServicePid == session.ServiceProcessIdentity.ProcessId
		&& _codeServiceDocumentUnavailableLoggedServiceStartTicks == session.ServiceProcessIdentity.StartTimeUtcTicks;

	private void RememberCodeServiceDocumentUnavailableSession(CodeServiceClientSessionInfo session)
	{
		_codeServiceDocumentUnavailableLoggedSessionId = session.SessionId;
		_codeServiceDocumentUnavailableLoggedServicePid = session.ServiceProcessIdentity.ProcessId;
		_codeServiceDocumentUnavailableLoggedServiceStartTicks = session.ServiceProcessIdentity.StartTimeUtcTicks;
	}

	private void ResetCodeServiceDocumentSynchronizationAfterManagedAssemblyReload()
	{
		ShutdownCodeServiceDocumentSynchronization("Managed Assembly Reload");
		// A managed reload creates a new document authority. Allow exactly one new
		// composition establishment attempt; native metadata supplies the strictly
		// increasing clientGeneration across ALC generations.
		_codeServiceDocumentCompositionEstablishmentAttempted = false;
	}

	private void ShutdownCodeServiceDocumentSynchronization(string reason)
	{
		_codeServiceDocumentBindingRetryQueued = false;
		_codeServiceDocumentBindingRetryAttempts = 0;
		_codeServiceDocumentQuietRetryAttempts = 0;
		_codeServiceDocumentActiveRecaptureRequired = false;
		_codeServiceDocumentActivationCandidatePath = "";
		_codeServiceDocumentActivationCandidateEdited = false;
		_codeServiceDocumentEstablishedActivePath = "";
		_codeServiceDocumentActivationBoundaryPending = false;
		_codeServiceDocumentCatchUpDeferredQueued = false;
		_codeServiceDocumentQuietBoundaryPending = false;
		_codeServiceDocumentFlightRetirementPending = false;

		Timer timer = _codeServiceDocumentQuietTimer;
		if (IsValidGodotObject(timer))
		{
			try { timer.Stop(); } catch { }
			DisconnectPluginSignal(
				timer,
				Timer.SignalName.Timeout,
				nameof(OnCodeServiceDocumentQuietTimerTimeout),
				nameof(_codeServiceDocumentQuietTimer)
			);
			try { timer.QueueFree(); } catch { }
		}
		_codeServiceDocumentQuietTimer = null;

		try { _codeServiceDocumentEditorBinding?.Shutdown(); } catch { }
		_codeServiceDocumentEditorBinding = null;

		try { _codeServiceDocumentSynchronizationCoordinator?.Dispose(); } catch { }
		_codeServiceDocumentSynchronizationCoordinator = null;
		_codeServiceDocumentFlightObservationTask = null;

		_codeServiceDocumentHasPendingWorkspaceReadyIntent = false;
		_codeServiceDocumentPendingWorkspaceSessionId = "";
		_codeServiceDocumentPendingWorkspaceOwnerPid = 0;
		_codeServiceDocumentPendingWorkspaceOwnerStartTicks = 0;
		_codeServiceDocumentPendingWorkspaceServicePid = 0;
		_codeServiceDocumentPendingWorkspaceServiceStartTicks = 0;
		_codeServiceDocumentPendingWorkspaceProjectRoot = "";
		_codeServiceDocumentPendingWorkspaceReusedState = -1;
		_codeServiceDocumentPendingWorkspaceReason = "";

		TryLogEditorOperation(
			"CodeService Document Synchronization Shutdown",
			$"Reason='{reason}'"
		);
	}
	#endregion
}
#endif
