#if TOOLS
using Godot;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using SystemExplorer.CodeService.Autocomplete;
using SystemExplorer.CodeService.Client;
using SystemExplorer.CodeService.Completion;
using SystemExplorer.CodeService.Documents;

public partial class SystemExplorerPlugin
{
	#region C# Autocomplete Integration
	private enum AutocompleteNativeCoalescingPhase
	{
		PreAdmission,
		AdmittedPending,
		ActiveFlight,
		PublishedSession
	}

	private sealed record AutocompletePendingPreAdmissionIntent(
		AutocompleteRequestContext Request,
		bool EmitIngressDiagnostics);

	private sealed record AutocompletePendingCompletionIntent(
		AutocompleteRequestContext Request,
		CodeServiceDocumentCompletionAdmissionSnapshot Admission);

	private sealed record AutocompleteCompletedCompletionFlight(
		string ManagedGeneration,
		AutocompleteRequestContext Request,
		CodeServiceDocumentCompletionAdmissionSnapshot Admission,
		CodeServiceCompletionResult Result,
		bool WasCanceled,
		long DurationMilliseconds);

	private sealed record AutocompleteImportCommitIntent(
		string ManagedGeneration,
		ulong CodeEditInstanceId,
		string ScriptPath,
		long RequestGeneration,
		AutocompleteCompletionItem Item,
		AutocompleteCompletionAuthority Authority);

	private sealed record AutocompleteCompletedImportResolveFlight(
		AutocompleteImportCommitIntent Intent,
		CodeServiceCompletionResolveResult Result,
		bool WasCanceled,
		long DurationMilliseconds);

	private readonly object _autocompleteCompletionStateGate = new();
	private AutocompletePluginHost _autocompleteHost;
	private long _autocompleteLastCompletionRequestedValidationGeneration = long.MinValue;
	private AutocompleteRequestContext _autocompleteAutomaticNativeCoalescingRequest;
	private bool _autocompleteCompletionRequestAdmissionOpen;
	private Task<CodeServiceCompletionResult> _autocompleteCompletionFlight;
	private CancellationTokenSource _autocompleteCompletionFlightCancellation;
	private AutocompleteRequestContext _autocompleteCompletionFlightRequest;
	private CodeServiceDocumentCompletionAdmissionSnapshot _autocompleteCompletionFlightAdmission;
	private Task _autocompleteCompletionFlightObservationTask;
	private AutocompletePendingPreAdmissionIntent _autocompletePendingPreAdmissionIntent;
	private AutocompletePendingCompletionIntent _autocompletePendingCompletionIntent;
	private AutocompleteCompletedCompletionFlight _autocompleteCompletedCompletionFlight;

	private Task<CodeServiceCompletionResolveResult> _autocompleteImportResolveFlight;
	private CancellationTokenSource _autocompleteImportResolveCancellation;
	private AutocompleteImportCommitIntent _autocompleteImportResolveIntent;
	private Task _autocompleteImportResolveObservationTask;
	private AutocompleteCompletedImportResolveFlight _autocompleteCompletedImportResolveFlight;

	private string _autocompleteCompletionUnsupportedSessionId = "";
	private int _autocompleteCompletionUnsupportedServicePid;
	private long _autocompleteCompletionUnsupportedServiceStartTicks;
	private string _autocompleteCompletionSuspendedManagedGeneration = "";
	private string _autocompleteCompletionSuspendedSessionId = "";
	private int _autocompleteCompletionSuspendedServicePid;
	private long _autocompleteCompletionSuspendedServiceStartTicks;

	private AutocompletePluginHost CreateAutocompleteHost()
	{
		return new AutocompletePluginHost(
			() => EditorInterface.Singleton?.GetScriptEditor(),
			TryConnectPluginSignal,
			DisconnectPluginSignal,
			nameof(OnAutocompleteScriptChanged),
			nameof(OnAutocompleteTextChanged),
			nameof(OnAutocompleteCodeCompletionRequested),
			nameof(OnAutocompleteGuiInput),
			CancelAutocompleteImportResolveForEditorRebind
		);
	}

	private bool TryEnsureAutocompleteHost(out AutocompletePluginHost host)
	{
		if (_autocompleteHost != null)
		{
			host = _autocompleteHost;
			return true;
		}

		try
		{
			ResetAutocompleteCompletionIngressState();
			_autocompleteHost = CreateAutocompleteHost();
			host = _autocompleteHost;
			DebugLogger.LogOperation(
				"C# autocomplete host restored",
				"Rebuilt the managed autocomplete feature graph."
			);
			return true;
		}
		catch (Exception exception)
		{
			_autocompleteHost = null;
			host = null;
			DebugLogger.LogOperation(
				"C# autocomplete host recovery failed: composition",
				exception.ToString()
			);
			return false;
		}
	}

	private bool EnsureAutocompleteLifecycleCurrent()
	{
		if (!TryEnsureAutocompleteHost(out AutocompletePluginHost host)
			|| !host.EnsureLifecycleCurrent())
		{
			return false;
		}

		ResetAutocompleteCompletionIngressState();
		OpenAutocompleteCompletionRequestAdmission();
		return true;
	}

	private void OpenAutocompleteCompletionRequestAdmission()
	{
		lock (_autocompleteCompletionStateGate)
		{
			if (!_editorOperationShutdownStarted)
				_autocompleteCompletionRequestAdmissionOpen = true;
		}
	}

	private void ResetAutocompleteTransientStateAfterManagedAssemblyReload()
	{
		ResetAutocompleteCompletionIngressState();
		ShutdownAutocompleteCompletionTransport("Managed Assembly Reload");
		_autocompleteHost?.ResetTransientState();
	}

	private void ShutdownAutocomplete()
	{
		ResetAutocompleteCompletionIngressState();
		ShutdownAutocompleteCompletionTransport("Autocomplete Shutdown");
		_autocompleteHost?.Shutdown();
		_autocompleteHost = null;
	}

	private void ShutdownAutocompleteCompletionTransport(string reason)
	{
		ResetAutocompleteCompletionIngressState();
		CancellationTokenSource cancellation = null;
		CancellationTokenSource resolveCancellation = null;
		lock (_autocompleteCompletionStateGate)
		{
			_autocompleteCompletionRequestAdmissionOpen = false;
			_autocompletePendingPreAdmissionIntent = null;
			_autocompletePendingCompletionIntent = null;
			cancellation = _autocompleteCompletionFlightCancellation;
			resolveCancellation = _autocompleteImportResolveCancellation;
			_autocompleteCompletedImportResolveFlight = null;
		}

		try { cancellation?.Cancel(); } catch { }
		try { resolveCancellation?.Cancel(); } catch { }
		TryLogEditorOperation(
			"CodeService Completion Discarded",
			$"Reason='{BoundAutocompleteCompletionDetail(reason)}', ManagedGeneration='{ManagedAssemblyGeneration}'"
		);
	}

	private void OnAutocompleteScriptChanged(Script script)
	{
		ResetAutocompleteCompletionIngressState();
		InvalidateAutocompleteCompletionForScriptChange();
		_autocompleteHost?.InvalidatePendingValidations();

		if (!EnsureManagedAssemblyStateCurrent("C# Autocomplete Script Changed"))
			return;

		if (TryEnsureAutocompleteHost(out AutocompletePluginHost host))
			host.HandleScriptChanged();
	}

	private void InvalidateAutocompleteCompletionForScriptChange()
	{
		CancellationTokenSource resolveCancellation = null;
		lock (_autocompleteCompletionStateGate)
		{
			_autocompletePendingPreAdmissionIntent = null;
			_autocompletePendingCompletionIntent = null;
			resolveCancellation = _autocompleteImportResolveCancellation;
			_autocompleteCompletedImportResolveFlight = null;
		}
		try { resolveCancellation?.Cancel(); } catch { }
	}

	private void OnAutocompleteCodeCompletionRequested()
	{
		if (!EnsureManagedAssemblyStateCurrent("C# Autocomplete Completion Requested"))
			return;
		if (!TryEnsureAutocompleteHost(out AutocompletePluginHost host))
			return;
		if (TryCoalesceAutocompleteNativeRequestWithAutomaticIntent(
			host,
			out AutocompleteRequestContext coalescedRequest,
			out AutocompleteNativeCoalescingPhase coalescingPhase
		))
		{
			_autocompleteLastCompletionRequestedValidationGeneration =
				coalescedRequest.ValidationGeneration;
			TryLogEditorOperation(
				"CodeService Completion Native Request Coalesced",
				BuildAutocompleteNativeCoalescingMetadata(coalescedRequest, coalescingPhase)
			);
			return;
		}
		if (!host.TryCaptureCompletionRequest(out AutocompleteRequestContext request))
		{
			ResetAutocompleteCompletionIngressState();
			return;
		}

		_autocompleteLastCompletionRequestedValidationGeneration = request.ValidationGeneration;
		HandleCapturedAutocompleteCompletionRequest(request, emitIngressDiagnostics: true);
	}

	private bool TryCoalesceAutocompleteNativeRequestWithAutomaticIntent(
		AutocompletePluginHost host,
		out AutocompleteRequestContext request,
		out AutocompleteNativeCoalescingPhase phase
	)
	{
		request = null;
		phase = default;
		AutocompleteRequestContext candidate;
		lock (_autocompleteCompletionStateGate)
		{
			candidate = _autocompleteAutomaticNativeCoalescingRequest;
		}

		if (candidate == null)
			return false;

		if (host == null || !host.IsCompletionRequestCurrent(candidate))
		{
			ClearAutocompleteAutomaticNativeCoalescingRequest(candidate);
			return false;
		}

		lock (_autocompleteCompletionStateGate)
		{
			if (!_autocompleteCompletionRequestAdmissionOpen
				|| !ReferenceEquals(_autocompleteAutomaticNativeCoalescingRequest, candidate))
			{
				if (ReferenceEquals(_autocompleteAutomaticNativeCoalescingRequest, candidate))
					_autocompleteAutomaticNativeCoalescingRequest = null;
				return false;
			}

			if (_autocompletePendingPreAdmissionIntent?.Request.RequestGeneration
				== candidate.RequestGeneration)
			{
				if (!_autocompletePendingPreAdmissionIntent.EmitIngressDiagnostics)
				{
					_autocompletePendingPreAdmissionIntent = new AutocompletePendingPreAdmissionIntent(
						candidate,
						true
					);
				}
				_autocompleteAutomaticNativeCoalescingRequest = null;
				request = candidate;
				phase = AutocompleteNativeCoalescingPhase.PreAdmission;
				return true;
			}

			if (_autocompletePendingCompletionIntent?.Request.RequestGeneration
				== candidate.RequestGeneration)
			{
				_autocompleteAutomaticNativeCoalescingRequest = null;
				request = candidate;
				phase = AutocompleteNativeCoalescingPhase.AdmittedPending;
				return true;
			}

			if (_autocompleteCompletionFlight != null
				&& _autocompleteCompletionFlightRequest?.RequestGeneration
					== candidate.RequestGeneration)
			{
				_autocompleteAutomaticNativeCoalescingRequest = null;
				request = candidate;
				phase = AutocompleteNativeCoalescingPhase.ActiveFlight;
				return true;
			}
		}

		// Deliberately do not coalesce the completed-but-not-yet-applied transport
		// state. Only an already published session for this exact request generation
		// is allowed to satisfy the paired native ingress here.
		if (host.TryRestorePublishedCompletionForRequest(candidate))
		{
			lock (_autocompleteCompletionStateGate)
			{
				if (!ReferenceEquals(_autocompleteAutomaticNativeCoalescingRequest, candidate))
					return false;
				if (!_autocompleteCompletionRequestAdmissionOpen)
				{
					_autocompleteAutomaticNativeCoalescingRequest = null;
					return false;
				}
				_autocompleteAutomaticNativeCoalescingRequest = null;
			}
			request = candidate;
			phase = AutocompleteNativeCoalescingPhase.PublishedSession;
			return true;
		}

		ClearAutocompleteAutomaticNativeCoalescingRequest(candidate);
		return false;
	}


	private void OnAutocompleteGuiInput(InputEvent inputEvent)
	{
		AutocompletePluginHost host = _autocompleteHost;
		host?.RestoreTypedOpeningParenthesisAutoCloseSuppression();

		if (host != null && IsAutocompleteTypedOpeningParenthesisInput(inputEvent))
			host.TrySuppressTypedOpeningParenthesisAutoClose();

		if (!IsAutocompleteImportConfirmationEvent(inputEvent))
			return;

		if (host == null)
			return;

		bool intercepted = host.TryInterceptSelectedImportCommit(
			out AutocompleteImportCommitSelection selection,
			out string interceptionDetail
		);
		if (!intercepted)
			return;

		// The exact managed import option has already been accepted at the Control
		// boundary and its native popup canceled. From this point onward every
		// failure is fail-closed and can never fall through to placeholder insertion.
		if (selection == null)
		{
			TryLogEditorOperation(
				"CodeService Completion Resolve Discarded",
				$"Reason='{BoundAutocompleteCompletionDetail(interceptionDetail)}'"
			);
			return;
		}

		var intent = new AutocompleteImportCommitIntent(
			ManagedAssemblyGeneration,
			selection.CodeEditInstanceId,
			selection.ScriptPath,
			selection.RequestGeneration,
			selection.Item,
			selection.Authority
		);
		TryStartAutocompleteImportResolveFlight(intent);
	}

	private static bool IsAutocompleteTypedOpeningParenthesisInput(InputEvent inputEvent)
	{
		return inputEvent is InputEventKey keyEvent
			&& keyEvent.Pressed
			&& keyEvent.Unicode == (uint)'(';
	}

	private static bool IsAutocompleteImportConfirmationEvent(InputEvent inputEvent)
	{
		if (inputEvent is InputEventKey keyEvent)
		{
			return keyEvent.Pressed
				&& (keyEvent.IsAction("ui_text_completion_accept", true)
					|| keyEvent.IsAction("ui_text_completion_replace", true));
		}

		return inputEvent is InputEventMouseButton mouseButton
			&& mouseButton.ButtonIndex == MouseButton.Left
			&& mouseButton.Pressed
			&& mouseButton.DoubleClick;
	}

	private void TryStartAutocompleteImportResolveFlight(AutocompleteImportCommitIntent intent)
	{
		if (intent == null
			|| intent.Item == null
			|| !intent.Item.HasValidCommitContract
			|| !intent.Item.RequiresImport
			|| !intent.Item.CompletionHandle.HasValue
			|| intent.Item.CompletionHandle.Value == Guid.Empty)
		{
			return;
		}

		if (!TryValidateAutocompleteImportCommitState(intent, out string preResolveDetail))
		{
			TryLogEditorOperation(
				"CodeService Completion Resolve Discarded",
				BuildAutocompleteImportResolveMetadata(intent)
				+ $", Reason='{BoundAutocompleteCompletionDetail(preResolveDetail)}'"
			);
			return;
		}

		CodeServiceClientCoordinator clientCoordinator = _codeServiceClientCoordinator;
		if (clientCoordinator == null)
			return;

		var request = new CodeServiceCompletionResolveRequest(
			intent.Authority.ClientGeneration,
			intent.Authority.EpochId,
			intent.Authority.DocumentPath,
			intent.Authority.ClientVersion,
			intent.Item.CompletionHandle.Value
		);
		var cancellation = new CancellationTokenSource();
		Task<CodeServiceCompletionResolveResult> flight;
		lock (_autocompleteCompletionStateGate)
		{
			if (!_autocompleteCompletionRequestAdmissionOpen
				|| _autocompleteImportResolveFlight != null
				|| _autocompleteCompletedImportResolveFlight != null)
			{
				cancellation.Dispose();
				return;
			}

			_autocompleteImportResolveCancellation = cancellation;
			_autocompleteImportResolveIntent = intent;
			flight = ExecuteAutocompleteImportResolveFlightAsync(
				clientCoordinator,
				intent.Authority.Session,
				request,
				cancellation.Token
			);
			_autocompleteImportResolveFlight = flight;
		}

		TryLogEditorOperation(
			"CodeService Completion Resolve Started",
			BuildAutocompleteImportResolveMetadata(intent)
		);
		_autocompleteImportResolveObservationTask = ObserveAutocompleteImportResolveFlightAsync(
			flight,
			cancellation,
			intent,
			Stopwatch.GetTimestamp()
		);
	}

	private async Task<CodeServiceCompletionResolveResult> ExecuteAutocompleteImportResolveFlightAsync(
		CodeServiceClientCoordinator clientCoordinator,
		CodeServiceClientSessionInfo expectedSession,
		CodeServiceCompletionResolveRequest request,
		CancellationToken cancellationToken)
	{
		CodeServiceCompletionResolveResult result;
		try
		{
			result = await clientCoordinator.ResolveCompletionAsync(
				expectedSession,
				request,
				cancellationToken
			).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception exception)
		{
			result = CodeServiceCompletionResolveResult.Failure(
				CodeServiceCompletionResolveOutcome.TransportUnavailable,
				"Unexpected completion resolve transport failure: " + exception.Message
			);
		}

		if (result.Outcome is CodeServiceCompletionResolveOutcome.AuthenticationFailed
			or CodeServiceCompletionResolveOutcome.TransportUnavailable)
		{
			try
			{
				await clientCoordinator.ReportSessionFailureAndEnsureReadyAsync(
					expectedSession,
					"Completion Resolve Transport Failure",
					cancellationToken
				).ConfigureAwait(false);
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				throw;
			}
			catch { }
		}
		return result;
	}

	private async Task ObserveAutocompleteImportResolveFlightAsync(
		Task<CodeServiceCompletionResolveResult> flight,
		CancellationTokenSource flightCancellation,
		AutocompleteImportCommitIntent intent,
		long startedTimestamp)
	{
		CodeServiceCompletionResolveResult result = default;
		bool canceled = false;
		try
		{
			result = await flight.ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			canceled = true;
		}
		catch (Exception exception)
		{
			result = CodeServiceCompletionResolveResult.Failure(
				CodeServiceCompletionResolveOutcome.TransportUnavailable,
				"Completion resolve observer failed: " + exception.Message
			);
		}

		long durationMilliseconds = GetElapsedMilliseconds(startedTimestamp);
		bool scheduleDeferred;
		lock (_autocompleteCompletionStateGate)
		{
			if (!ReferenceEquals(_autocompleteImportResolveFlight, flight)
				|| !ReferenceEquals(_autocompleteImportResolveCancellation, flightCancellation)
				|| !ReferenceEquals(_autocompleteImportResolveIntent, intent))
			{
				try { flightCancellation.Dispose(); } catch { }
				return;
			}

			_autocompleteImportResolveFlight = null;
			_autocompleteImportResolveCancellation = null;
			_autocompleteImportResolveIntent = null;

			// Closed admission is a terminal publication boundary for this managed
			// generation. A late observer must not recreate completed resolve state
			// after shutdown/reload has already cleared it.
			scheduleDeferred = _autocompleteCompletionRequestAdmissionOpen;
			_autocompleteCompletedImportResolveFlight = scheduleDeferred
				? new AutocompleteCompletedImportResolveFlight(
					intent,
					result,
					canceled,
					durationMilliseconds
				)
				: null;

			if (scheduleDeferred && !canceled
				&& result.Outcome == CodeServiceCompletionResolveOutcome.CompletionResolveUnavailableForSession)
			{
				SetAutocompleteCompletionUnsupportedSessionLocked(intent.Authority.Session);
			}
			else if (scheduleDeferred && !canceled
				&& result.Outcome is CodeServiceCompletionResolveOutcome.VersionMismatch
					or CodeServiceCompletionResolveOutcome.MalformedResponse)
			{
				SetAutocompleteCompletionSuspendedSessionLocked(intent.Authority.Session);
			}
		}
		try { flightCancellation.Dispose(); } catch { }
		if (!scheduleDeferred)
			return;

		try
		{
			CallDeferred(
				nameof(ApplyAutocompleteImportResolveFlightDeferred),
				intent.ManagedGeneration,
				intent.RequestGeneration,
				intent.Authority.ClientGeneration,
				intent.Authority.ClientVersion,
				intent.Authority.Session.ServiceProcessIdentity.ProcessId,
				intent.Authority.Session.ServiceProcessIdentity.StartTimeUtcTicks
			);
		}
		catch { }
	}

	private void ApplyAutocompleteImportResolveFlightDeferred(
		string managedGeneration,
		long requestGeneration,
		long clientGeneration,
		long clientVersion,
		int serviceProcessId,
		long serviceStartTimeUtcTicks)
	{
		if (!string.Equals(managedGeneration, ManagedAssemblyGeneration, StringComparison.Ordinal))
			return;

		AutocompleteCompletedImportResolveFlight completed;
		lock (_autocompleteCompletionStateGate)
		{
			completed = _autocompleteCompletedImportResolveFlight;
			if (completed == null
				|| !string.Equals(completed.Intent.ManagedGeneration, managedGeneration, StringComparison.Ordinal)
				|| completed.Intent.RequestGeneration != requestGeneration
				|| completed.Intent.Authority.ClientGeneration != clientGeneration
				|| completed.Intent.Authority.ClientVersion != clientVersion
				|| completed.Intent.Authority.Session.ServiceProcessIdentity.ProcessId != serviceProcessId
				|| completed.Intent.Authority.Session.ServiceProcessIdentity.StartTimeUtcTicks != serviceStartTimeUtcTicks)
			{
				return;
			}
			_autocompleteCompletedImportResolveFlight = null;
		}

		if (completed.WasCanceled)
		{
			TryLogEditorOperation(
				"CodeService Completion Resolve Discarded",
				BuildAutocompleteImportResolveMetadata(completed.Intent)
				+ $", DurationMs='{completed.DurationMilliseconds}', Reason='Canceled'"
			);
			return;
		}

		CodeServiceCompletionResolveResult result = completed.Result;
		TryLogEditorOperation(
			"CodeService Completion Resolve Completed",
			BuildAutocompleteImportResolveMetadata(completed.Intent)
			+ $", Outcome='{result.Outcome}', EditCount='{result.Edits?.Count ?? 0}', DurationMs='{completed.DurationMilliseconds}'"
		);
		HandleAutocompleteImportResolveResult(completed);
	}

	private void HandleAutocompleteImportResolveResult(AutocompleteCompletedImportResolveFlight completed)
	{
		AutocompleteImportCommitIntent intent = completed.Intent;
		CodeServiceCompletionResolveResult result = completed.Result;
		switch (result.Outcome)
		{
			case CodeServiceCompletionResolveOutcome.Success:
				break;
			case CodeServiceCompletionResolveOutcome.CompletionResolveUnavailableForSession:
				RememberAutocompleteCompletionUnsupportedSession(intent.Authority.Session);
				return;
			case CodeServiceCompletionResolveOutcome.VersionMismatch:
			case CodeServiceCompletionResolveOutcome.MalformedResponse:
				RememberAutocompleteCompletionSuspendedSession(intent.Authority.Session);
				return;
			case CodeServiceCompletionResolveOutcome.DocumentNotOpen:
				TryRefreshCodeServiceOpenDocumentInventory("Completion Resolve DocumentNotOpen");
				RequestCodeServiceDocumentQuietBoundary();
				return;
			case CodeServiceCompletionResolveOutcome.CompletionExpired:
			case CodeServiceCompletionResolveOutcome.AuthenticationFailed:
			case CodeServiceCompletionResolveOutcome.TransportUnavailable:
			case CodeServiceCompletionResolveOutcome.StaleSession:
			case CodeServiceCompletionResolveOutcome.Disposed:
			case CodeServiceCompletionResolveOutcome.Busy:
			case CodeServiceCompletionResolveOutcome.WorkspaceUnavailable:
			case CodeServiceCompletionResolveOutcome.RoslynUnavailable:
			case CodeServiceCompletionResolveOutcome.CompletionUnavailable:
			case CodeServiceCompletionResolveOutcome.StaleVersion:
			case CodeServiceCompletionResolveOutcome.DocumentNotSynchronized:
			case CodeServiceCompletionResolveOutcome.DocumentNotInWorkspace:
			case CodeServiceCompletionResolveOutcome.StaleEpoch:
			case CodeServiceCompletionResolveOutcome.EpochConflict:
			case CodeServiceCompletionResolveOutcome.InvalidRequest:
			case CodeServiceCompletionResolveOutcome.Unavailable:
			case CodeServiceCompletionResolveOutcome.LocalInvalidRequest:
			default:
				return;
		}

		if (!TryValidateAutocompleteImportResolveAuthority(intent.Authority, result, out string authorityDetail))
		{
			TryLogEditorOperation(
				"CodeService Completion Resolve Discarded",
				BuildAutocompleteImportResolveMetadata(intent)
				+ $", Outcome='Success', Reason='{BoundAutocompleteCompletionDetail(authorityDetail)}'"
			);
			return;
		}

		if (!TryValidateAutocompleteImportCommitState(intent, out string postResolveDetail))
		{
			TryLogEditorOperation(
				"CodeService Completion Resolve Discarded",
				BuildAutocompleteImportResolveMetadata(intent)
				+ $", Outcome='Success', Reason='{BoundAutocompleteCompletionDetail(postResolveDetail)}'"
			);
			return;
		}

		if (result.Edits == null || result.Edits.Count != 1)
			return;

		AutocompletePluginHost host = _autocompleteHost;
		if (host == null)
			return;

		AutocompleteResolvedEditApplyResult applyResult = host.ApplyResolvedImportEdit(
			intent.CodeEditInstanceId,
			intent.ScriptPath,
			result.Edits[0]
		);
		if (!applyResult.SourceApplied)
		{
			TryLogEditorOperation(
				"CodeService Completion Resolve Discarded",
				BuildAutocompleteImportResolveMetadata(intent)
				+ $", Outcome='Success', Reason='{BoundAutocompleteCompletionDetail(applyResult.Detail)}'"
			);
			return;
		}

		TryLogEditorOperation(
			"CodeService Completion Resolve Applied",
			BuildAutocompleteImportResolveMetadata(intent)
			+ $", CaretRestored='{applyResult.CaretRestored}'"
		);
		if (!applyResult.CaretRestored)
		{
			TryLogEditorOperation(
				"CodeService Completion Resolve Caret Restore Failed",
				BuildAutocompleteImportResolveMetadata(intent)
				+ $", Reason='{BoundAutocompleteCompletionDetail(applyResult.Detail)}'"
			);
		}
	}

	private bool TryValidateAutocompleteImportCommitState(
		AutocompleteImportCommitIntent intent,
		out string detail)
	{
		detail = "";
		if (intent == null || intent.Authority == null)
		{
			detail = "Import commit authority is unavailable.";
			return false;
		}
		if (!string.Equals(intent.ManagedGeneration, ManagedAssemblyGeneration, StringComparison.Ordinal))
		{
			detail = "Managed assembly generation changed.";
			return false;
		}

		AutocompletePluginHost host = _autocompleteHost;
		if (host == null
			|| !host.TryValidateImportCommitEditor(intent.CodeEditInstanceId, intent.ScriptPath, out _, out detail))
			return false;

		AutocompleteCompletionAuthority authority = intent.Authority;
		CodeServiceDocumentSynchronizationCoordinator documentCoordinator = _codeServiceDocumentSynchronizationCoordinator;
		if (documentCoordinator == null || documentCoordinator.IsFailedClosed
			|| !documentCoordinator.TryGetCompletionAdmissionSnapshot(
				authority.DocumentPath,
				out CodeServiceDocumentCompletionAdmissionSnapshot currentAdmission,
				out detail))
			return false;

		if (currentAdmission.ClientGeneration != authority.ClientGeneration
			|| !string.Equals(currentAdmission.EpochId, authority.EpochId, StringComparison.Ordinal)
			|| !string.Equals(currentAdmission.DocumentPath, authority.DocumentPath, StringComparison.Ordinal)
			|| currentAdmission.ClientVersion != authority.ClientVersion
			|| !currentAdmission.IsCurrentVersionSynchronized
			|| !IsExactAutocompleteCompletionSession(currentAdmission.Session, authority.Session))
		{
			detail = "Document admission no longer exactly matches the completion authority.";
			return false;
		}

		CodeServiceClientCoordinator clientCoordinator = _codeServiceClientCoordinator;
		if (clientCoordinator == null
			|| !clientCoordinator.TryGetReadySessionInfo(out CodeServiceClientSessionInfo currentSession)
			|| !IsExactAutocompleteCompletionSession(currentSession, authority.Session))
		{
			detail = "Logical CodeService session no longer matches the completion authority.";
			return false;
		}
		return true;
	}

	private static bool TryValidateAutocompleteImportResolveAuthority(
		AutocompleteCompletionAuthority authority,
		CodeServiceCompletionResolveResult result,
		out string detail)
	{
		detail = "";
		if (authority == null || result.Outcome != CodeServiceCompletionResolveOutcome.Success)
		{
			detail = "Successful resolve authority is unavailable.";
			return false;
		}
		if (result.ClientGeneration != authority.ClientGeneration
			|| !string.Equals(result.EpochId, authority.EpochId, StringComparison.Ordinal)
			|| !string.Equals(result.DocumentPath, authority.DocumentPath, StringComparison.Ordinal)
			|| result.AcceptedClientVersion != authority.ClientVersion
			|| result.WorkspaceGeneration != authority.WorkspaceGeneration
			|| result.WorkspacePublicationVersion != authority.WorkspacePublicationVersion
			|| result.RoslynGeneration != authority.RoslynGeneration
			|| result.RoslynDocumentVersion != authority.RoslynDocumentVersion
			|| result.RoslynOverlayRevision != authority.RoslynOverlayRevision
			|| result.Edits == null
			|| result.Edits.Count != 1)
		{
			detail = "Resolve result does not exactly match the original completion publication authority.";
			return false;
		}
		return true;
	}

	private void CancelAutocompleteImportResolveForEditorRebind()
	{
		CancellationTokenSource cancellation = null;
		lock (_autocompleteCompletionStateGate)
		{
			cancellation = _autocompleteImportResolveCancellation;
			_autocompleteCompletedImportResolveFlight = null;
		}
		try { cancellation?.Cancel(); } catch { }
	}

	private void CancelAutocompleteImportResolveForLogicalSessionChange(
		CodeServiceClientSessionInfo currentSession)
	{
		CancellationTokenSource cancellation = null;
		lock (_autocompleteCompletionStateGate)
		{
			if (_autocompleteImportResolveIntent != null
				&& !IsExactAutocompleteCompletionSession(_autocompleteImportResolveIntent.Authority.Session, currentSession))
			{
				cancellation = _autocompleteImportResolveCancellation;
			}
			if (_autocompleteCompletedImportResolveFlight != null
				&& !IsExactAutocompleteCompletionSession(_autocompleteCompletedImportResolveFlight.Intent.Authority.Session, currentSession))
			{
				_autocompleteCompletedImportResolveFlight = null;
			}
		}
		try { cancellation?.Cancel(); } catch { }
	}

	private static string BuildAutocompleteImportResolveMetadata(AutocompleteImportCommitIntent intent)
	{
		if (intent == null || intent.Authority == null)
			return "";
		return $"DocumentPath='{intent.Authority.DocumentPath}', ClientGeneration='{intent.Authority.ClientGeneration}', ClientVersion='{intent.Authority.ClientVersion}', RequestGeneration='{intent.RequestGeneration}', ServicePid='{intent.Authority.Session.ServiceProcessIdentity.ProcessId}'";
	}

	private void HandleCapturedAutocompleteCompletionRequest(
		AutocompleteRequestContext request,
		bool emitIngressDiagnostics,
		bool isReadinessReplay = false
	)
	{
		if (request == null || !TryAdmitLatestAutocompleteCompletionIntent(request.RequestGeneration))
			return;

		if (!CodeServiceDocumentPath.TryFromResourcePath(request.ScriptPath, out _, out string pathDetail))
		{
			ClearAutocompleteCompletionIntentsForRequest(request.RequestGeneration);
			if (emitIngressDiagnostics)
			{
				TryLogEditorOperation(
					"CodeService Completion Unavailable",
					$"RequestGeneration='{request.RequestGeneration}', Line='{request.Line}', Character='{request.LspCharacter}', Detail='{BoundAutocompleteCompletionDetail(pathDetail)}'"
				);
			}
			return;
		}

		CodeServiceClientCoordinator clientCoordinator = _codeServiceClientCoordinator;
		if (clientCoordinator == null
			|| !clientCoordinator.TryGetReadySessionInfo(out CodeServiceClientSessionInfo currentSession))
		{
			RememberLatestPreAdmissionAutocompleteCompletionIntent(request, emitIngressDiagnostics);
			if (emitIngressDiagnostics && !isReadinessReplay)
			{
				TryLogEditorOperation(
					"CodeService Completion Waiting For Service Ready",
					$"RequestGeneration='{request.RequestGeneration}', Line='{request.Line}', Character='{request.LspCharacter}'"
				);
			}
			return;
		}

		if (IsAutocompleteCompletionSessionBlocked(currentSession, out string blockedDetail))
		{
			ClearAutocompleteCompletionIntentsForRequest(request.RequestGeneration);
			if (emitIngressDiagnostics)
			{
				TryLogEditorOperation(
					"CodeService Completion Unavailable",
					$"RequestGeneration='{request.RequestGeneration}', Line='{request.Line}', Character='{request.LspCharacter}', Detail='{BoundAutocompleteCompletionDetail(blockedDetail)}'"
				);
			}
			return;
		}

		CodeServiceDocumentSynchronizationCoordinator documentCoordinator =
			_codeServiceDocumentSynchronizationCoordinator;
		if (documentCoordinator == null)
		{
			RememberLatestPreAdmissionAutocompleteCompletionIntent(request, emitIngressDiagnostics);
			return;
		}
		if (documentCoordinator.IsFailedClosed)
		{
			ClearAutocompleteCompletionIntentsForRequest(request.RequestGeneration);
			if (emitIngressDiagnostics)
			{
				TryLogEditorOperation(
					"CodeService Completion Unavailable",
					$"RequestGeneration='{request.RequestGeneration}', Line='{request.Line}', Character='{request.LspCharacter}', Detail='Document synchronization composition is failed closed.'"
				);
			}
			return;
		}

		if (!TryPrepareCodeServiceCompletionDocumentAdmission(
			request.ScriptPath,
			out CodeServiceDocumentCompletionAdmissionSnapshot admission,
			out string detail
		))
		{
			// At this point a current non-failed-closed composition existed. Path,
			// tracking/capacity, or snapshot validation failure is therefore not a
			// startup-readiness signal and must remain fail-closed.
			ClearAutocompleteCompletionIntentsForRequest(request.RequestGeneration);
			if (emitIngressDiagnostics)
			{
				TryLogEditorOperation(
					"CodeService Completion Unavailable",
					$"RequestGeneration='{request.RequestGeneration}', Line='{request.Line}', Character='{request.LspCharacter}', Detail='{BoundAutocompleteCompletionDetail(detail)}'"
				);
			}
			return;
		}

		if (!IsExactAutocompleteCompletionSession(currentSession, admission.Session))
		{
			// The client has a Ready logical session, but the document layer has not
			// yet adopted that exact authority (or has just retired an older one).
			RememberLatestPreAdmissionAutocompleteCompletionIntent(request, emitIngressDiagnostics);
			return;
		}

		ClearPreAdmissionAutocompleteCompletionIntent(request.RequestGeneration);

		if (emitIngressDiagnostics)
		{
			TryLogEditorOperation(
				"CodeService Completion Requested",
				BuildAutocompleteCompletionMetadata(request, admission)
			);
		}

		bool hasActiveFlight;
		lock (_autocompleteCompletionStateGate)
		{
			if (!_autocompleteCompletionRequestAdmissionOpen)
				return;
			hasActiveFlight = _autocompleteCompletionFlight != null;
		}

		if (hasActiveFlight)
		{
			RememberLatestAdmittedAutocompleteCompletionIntent(request, admission);
			if (emitIngressDiagnostics && !admission.IsCurrentVersionSynchronized)
			{
				TryLogEditorOperation(
					"CodeService Completion Waiting For Document Sync",
					BuildAutocompleteCompletionMetadata(request, admission)
				);
			}
			return;
		}

		if (!admission.IsCurrentVersionSynchronized)
		{
			RememberLatestAdmittedAutocompleteCompletionIntent(request, admission);
			if (emitIngressDiagnostics)
			{
				TryLogEditorOperation(
					"CodeService Completion Waiting For Document Sync",
					BuildAutocompleteCompletionMetadata(request, admission)
				);
			}
			return;
		}

		TryStartAutocompleteCompletionFlight(request, admission);
	}

	private void TryStartAutocompleteCompletionFlight(
		AutocompleteRequestContext request,
		CodeServiceDocumentCompletionAdmissionSnapshot admission
	)
	{
		if (request == null
			|| !admission.IsCurrentVersionSynchronized
			|| IsAutocompleteCompletionSessionBlocked(admission.Session, out _))
		{
			return;
		}

		CodeServiceClientCoordinator clientCoordinator = _codeServiceClientCoordinator;
		if (clientCoordinator == null
			|| !clientCoordinator.TryGetReadySessionInfo(out CodeServiceClientSessionInfo currentSession))
		{
			TryDemoteAutocompleteCompletionIntentToPreAdmissionIfCurrent(request);
			return;
		}
		if (IsAutocompleteCompletionSessionBlocked(currentSession, out _))
		{
			ClearAutocompleteCompletionIntentsForRequest(request.RequestGeneration);
			return;
		}
		if (!IsExactAutocompleteCompletionSession(currentSession, admission.Session))
		{
			TryDemoteAutocompleteCompletionIntentToPreAdmissionIfCurrent(request);
			return;
		}

		var completionRequest = new CodeServiceCompletionRequest(
			admission.ClientGeneration,
			admission.EpochId,
			admission.DocumentPath,
			admission.ClientVersion,
			request.Line,
			request.LspCharacter,
			request.Prefix
		);
		var cancellation = new CancellationTokenSource();
		Task<CodeServiceCompletionResult> flight = null;
		bool activeFlightAppeared = false;
		bool lostSessionAuthority = false;
		bool blockedSession = false;
		lock (_autocompleteCompletionStateGate)
		{
			if (!_autocompleteCompletionRequestAdmissionOpen)
			{
				cancellation.Dispose();
				return;
			}
			if (_autocompleteCompletionFlight != null)
			{
				activeFlightAppeared = true;
			}
			else if (!clientCoordinator.TryGetReadySessionInfo(out currentSession))
			{
				lostSessionAuthority = true;
			}
			else if (IsAutocompleteCompletionSessionBlocked(currentSession, out _))
			{
				blockedSession = true;
			}
			else if (!IsExactAutocompleteCompletionSession(currentSession, admission.Session))
			{
				lostSessionAuthority = true;
			}
			else
			{
				ClearAutocompleteCompletionIntentsUpToLocked(request.RequestGeneration);
				_autocompleteCompletionFlightCancellation = cancellation;
				_autocompleteCompletionFlightRequest = request;
				_autocompleteCompletionFlightAdmission = admission;
				flight = ExecuteAutocompleteCompletionFlightAsync(
					clientCoordinator,
					admission.Session,
					completionRequest,
					cancellation.Token
				);
				_autocompleteCompletionFlight = flight;
			}
		}

		if (activeFlightAppeared)
		{
			cancellation.Dispose();
			RememberLatestAdmittedAutocompleteCompletionIntent(request, admission);
			return;
		}
		if (blockedSession)
		{
			cancellation.Dispose();
			ClearAutocompleteCompletionIntentsForRequest(request.RequestGeneration);
			return;
		}
		if (lostSessionAuthority || flight == null)
		{
			cancellation.Dispose();
			TryDemoteAutocompleteCompletionIntentToPreAdmissionIfCurrent(request);
			return;
		}

		TryLogEditorOperation(
			"CodeService Completion Started",
			BuildAutocompleteCompletionMetadata(request, admission)
		);
		_autocompleteCompletionFlightObservationTask = ObserveAutocompleteCompletionFlightAsync(
			flight,
			cancellation,
			request,
			admission,
			ManagedAssemblyGeneration,
			Stopwatch.GetTimestamp()
		);
	}

	private async Task<CodeServiceCompletionResult> ExecuteAutocompleteCompletionFlightAsync(
		CodeServiceClientCoordinator clientCoordinator,
		CodeServiceClientSessionInfo expectedSession,
		CodeServiceCompletionRequest request,
		CancellationToken cancellationToken
	)
	{
		CodeServiceCompletionResult result;
		try
		{
			result = await clientCoordinator.CompleteDocumentAsync(
				expectedSession,
				request,
				cancellationToken
			).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception exception)
		{
			result = CodeServiceCompletionResult.Failure(
				CodeServiceCompletionOutcome.TransportUnavailable,
				"Unexpected completion transport failure: " + exception.Message
			);
		}

		if (result.Outcome is CodeServiceCompletionOutcome.AuthenticationFailed
			or CodeServiceCompletionOutcome.TransportUnavailable)
		{
			try
			{
				await clientCoordinator.ReportSessionFailureAndEnsureReadyAsync(
					expectedSession,
					"Completion Transport Failure",
					cancellationToken
				).ConfigureAwait(false);
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				throw;
			}
			catch
			{
			}
		}

		return result;
	}

	private async Task ObserveAutocompleteCompletionFlightAsync(
		Task<CodeServiceCompletionResult> flight,
		CancellationTokenSource flightCancellation,
		AutocompleteRequestContext request,
		CodeServiceDocumentCompletionAdmissionSnapshot admission,
		string managedGeneration,
		long startedTimestamp
	)
	{
		CodeServiceCompletionResult result = default;
		bool canceled = false;
		try
		{
			result = await flight.ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			canceled = true;
		}
		catch (Exception exception)
		{
			result = CodeServiceCompletionResult.Failure(
				CodeServiceCompletionOutcome.TransportUnavailable,
				"Completion flight observer failed: " + exception.Message
			);
		}

		long durationMilliseconds = GetElapsedMilliseconds(startedTimestamp);
		bool scheduleDeferred;
		lock (_autocompleteCompletionStateGate)
		{
			if (!ReferenceEquals(_autocompleteCompletionFlight, flight)
				|| !ReferenceEquals(_autocompleteCompletionFlightCancellation, flightCancellation)
				|| _autocompleteCompletionFlightRequest?.RequestGeneration != request.RequestGeneration)
			{
				try { flightCancellation.Dispose(); } catch { }
				return;
			}

			_autocompleteCompletionFlight = null;
			_autocompleteCompletionFlightCancellation = null;
			_autocompleteCompletionFlightRequest = null;
			_autocompleteCompletionFlightAdmission = default;
			_autocompleteCompletedCompletionFlight = new AutocompleteCompletedCompletionFlight(
				managedGeneration,
				request,
				admission,
				result,
				canceled,
				durationMilliseconds
			);
			if (!canceled && result.Outcome == CodeServiceCompletionOutcome.CompletionUnavailableForSession)
			{
				SetAutocompleteCompletionUnsupportedSessionLocked(admission.Session);
			}
			else if (!canceled && result.Outcome is CodeServiceCompletionOutcome.VersionMismatch
				or CodeServiceCompletionOutcome.MalformedResponse)
			{
				SetAutocompleteCompletionSuspendedSessionLocked(admission.Session);
			}
			scheduleDeferred = _autocompleteCompletionRequestAdmissionOpen;
		}
		try { flightCancellation.Dispose(); } catch { }

		if (!scheduleDeferred)
			return;

		try
		{
			CallDeferred(
				nameof(ApplyAutocompleteCompletionFlightDeferred),
				managedGeneration,
				request.RequestGeneration,
				admission.ClientGeneration,
				admission.EpochId ?? "",
				admission.Session.SessionId ?? "",
				admission.Session.ServiceProcessIdentity.ProcessId,
				admission.Session.ServiceProcessIdentity.StartTimeUtcTicks
			);
		}
		catch
		{
		}
	}

	private void ApplyAutocompleteCompletionFlightDeferred(
		string managedGeneration,
		long requestGeneration,
		long clientGeneration,
		string epochId,
		string sessionId,
		int serviceProcessId,
		long serviceStartTimeUtcTicks
	)
	{
		if (!string.Equals(managedGeneration, ManagedAssemblyGeneration, StringComparison.Ordinal))
			return;

		AutocompleteCompletedCompletionFlight completed;
		lock (_autocompleteCompletionStateGate)
		{
			completed = _autocompleteCompletedCompletionFlight;
			if (completed == null
				|| !string.Equals(completed.ManagedGeneration, managedGeneration, StringComparison.Ordinal)
				|| completed.Request.RequestGeneration != requestGeneration
				|| completed.Admission.ClientGeneration != clientGeneration
				|| !string.Equals(completed.Admission.EpochId, epochId, StringComparison.Ordinal)
				|| !string.Equals(completed.Admission.Session.SessionId, sessionId, StringComparison.Ordinal)
				|| completed.Admission.Session.ServiceProcessIdentity.ProcessId != serviceProcessId
				|| completed.Admission.Session.ServiceProcessIdentity.StartTimeUtcTicks != serviceStartTimeUtcTicks)
			{
				return;
			}
			_autocompleteCompletedCompletionFlight = null;
		}

		if (completed.WasCanceled)
		{
			TryLogEditorOperation(
				"CodeService Completion Discarded",
				BuildAutocompleteCompletionMetadata(completed.Request, completed.Admission)
				+ $", DurationMs='{completed.DurationMilliseconds}', Reason='Canceled'"
			);
			TryResumeLatestAutocompleteCompletionAfterReadinessBoundary();
			return;
		}

		CodeServiceCompletionResult result = completed.Result;
		TryLogEditorOperation(
			"CodeService Completion Completed",
			BuildAutocompleteCompletionMetadata(completed.Request, completed.Admission)
			+ $", Outcome='{result.Outcome}', ItemCount='{result.Items?.Count ?? 0}', IsIncomplete='{result.IsIncomplete}', DurationMs='{completed.DurationMilliseconds}'"
		);

		HandleAutocompleteCompletionResult(completed);
		TryResumeLatestAutocompleteCompletionAfterReadinessBoundary();
	}

	private void HandleAutocompleteCompletionResult(AutocompleteCompletedCompletionFlight completed)
	{
		CodeServiceCompletionResult result = completed.Result;
		AutocompleteRequestContext request = completed.Request;
		CodeServiceDocumentCompletionAdmissionSnapshot originalAdmission = completed.Admission;

		switch (result.Outcome)
		{
			case CodeServiceCompletionOutcome.Success:
				break;
			case CodeServiceCompletionOutcome.CompletionUnavailableForSession:
				RememberAutocompleteCompletionUnsupportedSession(originalAdmission.Session);
				TryLogEditorOperation(
					"CodeService Completion Unavailable",
					BuildAutocompleteCompletionMetadata(request, originalAdmission)
					+ ", Outcome='CompletionUnavailableForSession'"
				);
				return;
			case CodeServiceCompletionOutcome.VersionMismatch:
			case CodeServiceCompletionOutcome.MalformedResponse:
				RememberAutocompleteCompletionSuspendedSession(originalAdmission.Session);
				TryLogEditorOperation(
					"CodeService Completion Unavailable",
					BuildAutocompleteCompletionMetadata(request, originalAdmission)
					+ $", Outcome='{result.Outcome}', Detail='{BoundAutocompleteCompletionDetail(result.Detail)}'"
				);
				return;
			case CodeServiceCompletionOutcome.DocumentNotOpen:
				TryRefreshCodeServiceOpenDocumentInventory("Completion DocumentNotOpen");
				RequestCodeServiceDocumentQuietBoundary();
				return;
			case CodeServiceCompletionOutcome.AuthenticationFailed:
			case CodeServiceCompletionOutcome.TransportUnavailable:
			case CodeServiceCompletionOutcome.StaleSession:
			case CodeServiceCompletionOutcome.Disposed:
			case CodeServiceCompletionOutcome.Busy:
			case CodeServiceCompletionOutcome.WorkspaceUnavailable:
			case CodeServiceCompletionOutcome.RoslynUnavailable:
			case CodeServiceCompletionOutcome.SemanticUnavailable:
			case CodeServiceCompletionOutcome.CompletionUnavailable:
			case CodeServiceCompletionOutcome.StaleVersion:
			case CodeServiceCompletionOutcome.DocumentNotSynchronized:
			case CodeServiceCompletionOutcome.DocumentNotInWorkspace:
			case CodeServiceCompletionOutcome.StaleEpoch:
			case CodeServiceCompletionOutcome.EpochConflict:
			case CodeServiceCompletionOutcome.InvalidRequest:
			case CodeServiceCompletionOutcome.Unavailable:
			case CodeServiceCompletionOutcome.LocalInvalidRequest:
				return;
			default:
				return;
		}

		if (!TryValidateAutocompleteCompletionPublication(
			request,
			originalAdmission,
			result,
			out string staleDetail
		))
		{
			TryLogEditorOperation(
				"CodeService Completion Discarded",
				BuildAutocompleteCompletionMetadata(request, originalAdmission)
				+ $", Outcome='Success', Reason='{BoundAutocompleteCompletionDetail(staleDetail)}'"
			);
			return;
		}

		if (!AutocompleteCompletionAuthority.TryCreate(
			originalAdmission,
			result,
			out AutocompleteCompletionAuthority authority,
			out string authorityDetail))
		{
			TryLogEditorOperation(
				"CodeService Completion Discarded",
				BuildAutocompleteCompletionMetadata(request, originalAdmission)
				+ $", Outcome='Success', Reason='{BoundAutocompleteCompletionDetail(authorityDetail)}'"
			);
			return;
		}

		var mappedItems = new List<AutocompleteCompletionItem>(result.Items?.Count ?? 0);
		if (result.Items != null)
		{
			foreach (CodeServiceCompletionItem item in result.Items)
			{
				if (item == null)
					continue;
				if (!item.HasValidCommitContract)
					return;
				mappedItems.Add(new AutocompleteCompletionItem(
					AutocompleteCompletionKindMapper.Map(item.Kind),
					item.DisplayText,
					item.InsertText,
					item.FilterText,
					item.SortText,
					item.Preselect,
					item.SemanticOrigin,
					item.InheritanceDepth,
					item.RequiresImport,
					item.CompletionHandle
				));
			}
		}

		AutocompletePluginHost host = _autocompleteHost;
		string publicationDetail = "Autocomplete host is unavailable before publication.";
		if (host == null || !host.TryPublishCompletionResult(request, mappedItems, authority, out publicationDetail))
		{
			TryLogEditorOperation(
				"CodeService Completion Discarded",
				BuildAutocompleteCompletionMetadata(request, originalAdmission)
				+ $", Outcome='Success', Reason='{BoundAutocompleteCompletionDetail(publicationDetail)}'"
			);
			return;
		}

		TryLogEditorOperation(
			"CodeService Completion Published",
			BuildAutocompleteCompletionMetadata(request, originalAdmission)
			+ $", ItemCount='{mappedItems.Count}', IsIncomplete='{result.IsIncomplete}'"
		);
	}

	private bool TryValidateAutocompleteCompletionPublication(
		AutocompleteRequestContext request,
		CodeServiceDocumentCompletionAdmissionSnapshot originalAdmission,
		CodeServiceCompletionResult result,
		out string detail
	)
	{
		detail = "";
		lock (_autocompleteCompletionStateGate)
		{
			if (!_autocompleteCompletionRequestAdmissionOpen)
			{
				detail = "Completion request admission is closed.";
				return false;
			}
		}
		AutocompletePluginHost host = _autocompleteHost;
		if (host == null || !host.IsCompletionRequestCurrent(request))
		{
			detail = "Autocomplete editor/request context is stale.";
			return false;
		}

		CodeServiceDocumentSynchronizationCoordinator documentCoordinator =
			_codeServiceDocumentSynchronizationCoordinator;
		if (documentCoordinator == null || documentCoordinator.IsFailedClosed)
		{
			detail = "Document synchronization composition is unavailable.";
			return false;
		}
		if (!documentCoordinator.TryGetCompletionAdmissionSnapshot(
			originalAdmission.DocumentPath,
			out CodeServiceDocumentCompletionAdmissionSnapshot currentAdmission,
			out detail
		))
		{
			return false;
		}

		if (currentAdmission.ClientGeneration != originalAdmission.ClientGeneration
			|| !string.Equals(currentAdmission.EpochId, originalAdmission.EpochId, StringComparison.Ordinal)
			|| !string.Equals(currentAdmission.DocumentPath, originalAdmission.DocumentPath, StringComparison.Ordinal)
			|| currentAdmission.ClientVersion != originalAdmission.ClientVersion
			|| !currentAdmission.IsCurrentVersionSynchronized
			|| !IsExactAutocompleteCompletionSession(currentAdmission.Session, originalAdmission.Session))
		{
			detail = "Document completion admission changed before publication.";
			return false;
		}

		CodeServiceClientCoordinator clientCoordinator = _codeServiceClientCoordinator;
		if (clientCoordinator == null
			|| !clientCoordinator.TryGetReadySessionInfo(out CodeServiceClientSessionInfo currentSession)
			|| !IsExactAutocompleteCompletionSession(currentSession, originalAdmission.Session))
		{
			detail = "Logical CodeService session changed before publication.";
			return false;
		}

		if (result.AcceptedClientVersion != originalAdmission.ClientVersion)
		{
			detail = "Service completion acceptedClientVersion no longer matches the requested document version.";
			return false;
		}
		return true;
	}

	private void TryResumePendingAutocompleteCompletionAfterWorkspaceReady()
	{
		TryResumeLatestAutocompleteCompletionAfterReadinessBoundary();
	}

	private void TryResumePendingAutocompleteCompletionAfterDocumentSynchronization()
	{
		TryResumeLatestAutocompleteCompletionAfterReadinessBoundary();
	}

	private void TryResumeLatestAutocompleteCompletionAfterReadinessBoundary()
	{
		TryResumePendingPreAdmissionAutocompleteCompletion();
		TryStartLatestPendingAutocompleteCompletion();
	}

	private void TryResumePendingPreAdmissionAutocompleteCompletion()
	{
		AutocompletePendingPreAdmissionIntent pending;
		lock (_autocompleteCompletionStateGate)
		{
			if (!_autocompleteCompletionRequestAdmissionOpen
				|| _autocompletePendingPreAdmissionIntent == null)
			{
				return;
			}
			pending = _autocompletePendingPreAdmissionIntent;
		}

		AutocompletePluginHost host = _autocompleteHost;
		if (host == null || !host.IsCompletionRequestCurrent(pending.Request))
		{
			ClearPreAdmissionAutocompleteCompletionIntent(pending.Request.RequestGeneration);
			return;
		}

		ClearPreAdmissionAutocompleteCompletionIntent(pending.Request.RequestGeneration);
		HandleCapturedAutocompleteCompletionRequest(
			pending.Request,
			pending.EmitIngressDiagnostics,
			isReadinessReplay: true
		);
	}

	private void TryStartLatestPendingAutocompleteCompletion()
	{
		AutocompletePendingCompletionIntent pending;
		lock (_autocompleteCompletionStateGate)
		{
			if (!_autocompleteCompletionRequestAdmissionOpen
				|| _autocompleteCompletionFlight != null
				|| _autocompletePendingCompletionIntent == null)
			{
				return;
			}
			pending = _autocompletePendingCompletionIntent;
		}

		AutocompletePluginHost host = _autocompleteHost;
		if (host == null || !host.IsCompletionRequestCurrent(pending.Request))
		{
			ClearPendingAutocompleteCompletionIntent(pending.Request.RequestGeneration);
			return;
		}

		CodeServiceDocumentSynchronizationCoordinator documentCoordinator =
			_codeServiceDocumentSynchronizationCoordinator;
		if (documentCoordinator == null)
		{
			ClearPendingAutocompleteCompletionIntent(pending.Request.RequestGeneration);
			HandleCapturedAutocompleteCompletionRequest(
				pending.Request,
				emitIngressDiagnostics: false,
				isReadinessReplay: true
			);
			return;
		}
		if (documentCoordinator.IsFailedClosed)
		{
			ClearPendingAutocompleteCompletionIntent(pending.Request.RequestGeneration);
			return;
		}

		if (!documentCoordinator.TryGetCompletionAdmissionSnapshot(
			pending.Admission.DocumentPath,
			out CodeServiceDocumentCompletionAdmissionSnapshot currentAdmission,
			out _
		))
		{
			ClearPendingAutocompleteCompletionIntent(pending.Request.RequestGeneration);
			HandleCapturedAutocompleteCompletionRequest(
				pending.Request,
				emitIngressDiagnostics: false,
				isReadinessReplay: true
			);
			return;
		}

		if (currentAdmission.ClientGeneration != pending.Admission.ClientGeneration
			|| !string.Equals(currentAdmission.EpochId, pending.Admission.EpochId, StringComparison.Ordinal)
			|| !string.Equals(currentAdmission.DocumentPath, pending.Admission.DocumentPath, StringComparison.Ordinal)
			|| currentAdmission.ClientVersion != pending.Admission.ClientVersion
			|| !IsExactAutocompleteCompletionSession(currentAdmission.Session, pending.Admission.Session))
		{
			// The editor intent is still exact-current, but its old document/session
			// authority is stale. Never reuse or mutate that admission; reacquire it.
			ClearPendingAutocompleteCompletionIntent(pending.Request.RequestGeneration);
			HandleCapturedAutocompleteCompletionRequest(
				pending.Request,
				emitIngressDiagnostics: false,
				isReadinessReplay: true
			);
			return;
		}

		if (!currentAdmission.IsCurrentVersionSynchronized)
		{
			RequestCodeServiceDocumentCatchUp(currentAdmission.DocumentPath);
			return;
		}
		if (IsAutocompleteCompletionSessionBlocked(currentAdmission.Session, out _))
		{
			ClearPendingAutocompleteCompletionIntent(pending.Request.RequestGeneration);
			return;
		}

		ClearPendingAutocompleteCompletionIntent(pending.Request.RequestGeneration);
		TryStartAutocompleteCompletionFlight(pending.Request, currentAdmission);
	}

	private bool TryAdmitLatestAutocompleteCompletionIntent(long requestGeneration)
	{
		lock (_autocompleteCompletionStateGate)
		{
			if (!_autocompleteCompletionRequestAdmissionOpen)
				return false;

			if (_autocompletePendingPreAdmissionIntent != null
				&& _autocompletePendingPreAdmissionIntent.Request.RequestGeneration < requestGeneration)
			{
				_autocompletePendingPreAdmissionIntent = null;
			}
			if (_autocompletePendingCompletionIntent != null
				&& _autocompletePendingCompletionIntent.Request.RequestGeneration < requestGeneration)
			{
				_autocompletePendingCompletionIntent = null;
			}

			long latestPendingGeneration = Math.Max(
				_autocompletePendingPreAdmissionIntent?.Request.RequestGeneration ?? long.MinValue,
				_autocompletePendingCompletionIntent?.Request.RequestGeneration ?? long.MinValue
			);
			return latestPendingGeneration <= requestGeneration;
		}
	}

	private void RememberLatestPreAdmissionAutocompleteCompletionIntent(
		AutocompleteRequestContext request,
		bool emitIngressDiagnostics
	)
	{
		if (request == null)
			return;

		lock (_autocompleteCompletionStateGate)
		{
			if (!_autocompleteCompletionRequestAdmissionOpen)
				return;

			long latestPendingGeneration = Math.Max(
				_autocompletePendingPreAdmissionIntent?.Request.RequestGeneration ?? long.MinValue,
				_autocompletePendingCompletionIntent?.Request.RequestGeneration ?? long.MinValue
			);
			if (latestPendingGeneration > request.RequestGeneration)
				return;

			_autocompletePendingPreAdmissionIntent = new AutocompletePendingPreAdmissionIntent(
				request,
				emitIngressDiagnostics
			);
			if (_autocompletePendingCompletionIntent != null
				&& _autocompletePendingCompletionIntent.Request.RequestGeneration <= request.RequestGeneration)
			{
				_autocompletePendingCompletionIntent = null;
			}
		}
	}

	private void RememberLatestAdmittedAutocompleteCompletionIntent(
		AutocompleteRequestContext request,
		CodeServiceDocumentCompletionAdmissionSnapshot admission
	)
	{
		if (request == null)
			return;

		lock (_autocompleteCompletionStateGate)
		{
			if (!_autocompleteCompletionRequestAdmissionOpen)
				return;

			long latestPendingGeneration = Math.Max(
				_autocompletePendingPreAdmissionIntent?.Request.RequestGeneration ?? long.MinValue,
				_autocompletePendingCompletionIntent?.Request.RequestGeneration ?? long.MinValue
			);
			if (latestPendingGeneration > request.RequestGeneration)
				return;

			if (_autocompletePendingPreAdmissionIntent != null
				&& _autocompletePendingPreAdmissionIntent.Request.RequestGeneration <= request.RequestGeneration)
			{
				_autocompletePendingPreAdmissionIntent = null;
			}
			_autocompletePendingCompletionIntent = new AutocompletePendingCompletionIntent(
				request,
				admission
			);
		}
	}

	private void TryDemoteAutocompleteCompletionIntentToPreAdmissionIfCurrent(
		AutocompleteRequestContext request
	)
	{
		if (request == null)
			return;
		AutocompletePluginHost host = _autocompleteHost;
		if (host == null || !host.IsCompletionRequestCurrent(request))
		{
			ClearAutocompleteCompletionIntentsForRequest(request.RequestGeneration);
			return;
		}
		if (_codeServiceDocumentSynchronizationCoordinator?.IsFailedClosed == true)
		{
			ClearAutocompleteCompletionIntentsForRequest(request.RequestGeneration);
			return;
		}

		CodeServiceClientCoordinator clientCoordinator = _codeServiceClientCoordinator;
		if (clientCoordinator != null
			&& clientCoordinator.TryGetReadySessionInfo(out CodeServiceClientSessionInfo currentSession)
			&& IsAutocompleteCompletionSessionBlocked(currentSession, out _))
		{
			ClearAutocompleteCompletionIntentsForRequest(request.RequestGeneration);
			return;
		}

		RememberLatestPreAdmissionAutocompleteCompletionIntent(
			request,
			emitIngressDiagnostics: false
		);
	}

	private void ClearPreAdmissionAutocompleteCompletionIntent(long requestGeneration)
	{
		lock (_autocompleteCompletionStateGate)
		{
			if (_autocompletePendingPreAdmissionIntent?.Request.RequestGeneration == requestGeneration)
				_autocompletePendingPreAdmissionIntent = null;
		}
	}

	private void ClearPendingAutocompleteCompletionIntent(long requestGeneration)
	{
		lock (_autocompleteCompletionStateGate)
		{
			if (_autocompletePendingCompletionIntent?.Request.RequestGeneration == requestGeneration)
				_autocompletePendingCompletionIntent = null;
		}
	}

	private void ClearAutocompleteCompletionIntentsForRequest(long requestGeneration)
	{
		lock (_autocompleteCompletionStateGate)
		{
			if (_autocompletePendingPreAdmissionIntent?.Request.RequestGeneration == requestGeneration)
				_autocompletePendingPreAdmissionIntent = null;
			if (_autocompletePendingCompletionIntent?.Request.RequestGeneration == requestGeneration)
				_autocompletePendingCompletionIntent = null;
		}
	}

	private void ClearAutocompleteCompletionIntentsUpToLocked(long requestGeneration)
	{
		if (_autocompletePendingPreAdmissionIntent != null
			&& _autocompletePendingPreAdmissionIntent.Request.RequestGeneration <= requestGeneration)
		{
			_autocompletePendingPreAdmissionIntent = null;
		}
		if (_autocompletePendingCompletionIntent != null
			&& _autocompletePendingCompletionIntent.Request.RequestGeneration <= requestGeneration)
		{
			_autocompletePendingCompletionIntent = null;
		}
	}

	private void DiscardStalePendingAutocompleteCompletionIntentAfterTextChanged(
		long validationGeneration
	)
	{
		lock (_autocompleteCompletionStateGate)
		{
			if (_autocompletePendingPreAdmissionIntent != null
				&& _autocompletePendingPreAdmissionIntent.Request.ValidationGeneration < validationGeneration)
			{
				_autocompletePendingPreAdmissionIntent = null;
			}
			if (_autocompletePendingCompletionIntent != null
				&& _autocompletePendingCompletionIntent.Request.ValidationGeneration < validationGeneration)
			{
				_autocompletePendingCompletionIntent = null;
			}
		}
	}

	private bool IsAutocompleteCompletionSessionBlocked(
		CodeServiceClientSessionInfo session,
		out string detail
	)
	{
		detail = "";
		if (string.IsNullOrEmpty(session.SessionId))
		{
			detail = "No current logical CodeService session is available.";
			return true;
		}

		lock (_autocompleteCompletionStateGate)
		{
			if (MatchesAutocompleteCompletionUnsupportedSessionLocked(session))
			{
				detail = "Completion is unavailable for the current logical CodeService session.";
				return true;
			}
			if (string.Equals(
				_autocompleteCompletionSuspendedManagedGeneration,
				ManagedAssemblyGeneration,
				StringComparison.Ordinal
				)
				&& string.Equals(_autocompleteCompletionSuspendedSessionId, session.SessionId, StringComparison.Ordinal)
				&& _autocompleteCompletionSuspendedServicePid == session.ServiceProcessIdentity.ProcessId
				&& _autocompleteCompletionSuspendedServiceStartTicks == session.ServiceProcessIdentity.StartTimeUtcTicks)
			{
				detail = "Completion capability is suspended for the current managed/session boundary.";
				return true;
			}
		}
		return false;
	}

	private bool MatchesAutocompleteCompletionUnsupportedSessionLocked(CodeServiceClientSessionInfo session)
	{
		return string.Equals(_autocompleteCompletionUnsupportedSessionId, session.SessionId, StringComparison.Ordinal)
			&& _autocompleteCompletionUnsupportedServicePid == session.ServiceProcessIdentity.ProcessId
			&& _autocompleteCompletionUnsupportedServiceStartTicks == session.ServiceProcessIdentity.StartTimeUtcTicks;
	}

	private void SetAutocompleteCompletionUnsupportedSessionLocked(CodeServiceClientSessionInfo session)
	{
		_autocompleteCompletionUnsupportedSessionId = session.SessionId ?? "";
		_autocompleteCompletionUnsupportedServicePid = session.ServiceProcessIdentity.ProcessId;
		_autocompleteCompletionUnsupportedServiceStartTicks = session.ServiceProcessIdentity.StartTimeUtcTicks;
	}

	private void RememberAutocompleteCompletionUnsupportedSession(CodeServiceClientSessionInfo session)
	{
		lock (_autocompleteCompletionStateGate)
			SetAutocompleteCompletionUnsupportedSessionLocked(session);
	}

	private void SetAutocompleteCompletionSuspendedSessionLocked(CodeServiceClientSessionInfo session)
	{
		_autocompleteCompletionSuspendedManagedGeneration = ManagedAssemblyGeneration;
		_autocompleteCompletionSuspendedSessionId = session.SessionId ?? "";
		_autocompleteCompletionSuspendedServicePid = session.ServiceProcessIdentity.ProcessId;
		_autocompleteCompletionSuspendedServiceStartTicks = session.ServiceProcessIdentity.StartTimeUtcTicks;
	}

	private void RememberAutocompleteCompletionSuspendedSession(CodeServiceClientSessionInfo session)
	{
		lock (_autocompleteCompletionStateGate)
			SetAutocompleteCompletionSuspendedSessionLocked(session);
	}

	private void ResetAutocompleteCompletionIngressState()
	{
		lock (_autocompleteCompletionStateGate)
		{
			_autocompleteLastCompletionRequestedValidationGeneration = long.MinValue;
			_autocompleteAutomaticNativeCoalescingRequest = null;
		}
	}

	private void ClearAutocompleteAutomaticNativeCoalescingRequest(
		AutocompleteRequestContext expectedRequest = null
	)
	{
		lock (_autocompleteCompletionStateGate)
		{
			if (expectedRequest == null
				|| ReferenceEquals(_autocompleteAutomaticNativeCoalescingRequest, expectedRequest))
			{
				_autocompleteAutomaticNativeCoalescingRequest = null;
			}
		}
	}

	private void RememberAutocompleteAutomaticNativeCoalescingRequest(
		AutocompleteRequestContext request
	)
	{
		if (request == null)
			return;

		lock (_autocompleteCompletionStateGate)
		{
			if (_autocompleteCompletionRequestAdmissionOpen)
				_autocompleteAutomaticNativeCoalescingRequest = request;
		}
	}

	private bool CanAutomaticallyCaptureAutocompleteCompletionIntent()
	{
		lock (_autocompleteCompletionStateGate)
		{
			if (!_autocompleteCompletionRequestAdmissionOpen)
				return false;
		}

		CodeServiceDocumentSynchronizationCoordinator documentCoordinator =
			_codeServiceDocumentSynchronizationCoordinator;
		if (documentCoordinator?.IsFailedClosed == true)
			return false;

		CodeServiceClientCoordinator clientCoordinator = _codeServiceClientCoordinator;
		if (clientCoordinator == null
			|| !clientCoordinator.TryGetReadySessionInfo(out CodeServiceClientSessionInfo currentSession))
		{
			// Missing Ready Service authority is a temporary pre-admission state. The
			// automatic producer may still capture the exact current editor intent.
			return true;
		}

		return !IsAutocompleteCompletionSessionBlocked(currentSession, out _);
	}

	private static bool IsExactAutocompleteCompletionSession(
		CodeServiceClientSessionInfo left,
		CodeServiceClientSessionInfo right
	)
	{
		return left.IsSameLogicalSession(right)
			&& left.GodotOwnerIdentity.ProcessId == right.GodotOwnerIdentity.ProcessId
			&& left.GodotOwnerIdentity.StartTimeUtcTicks == right.GodotOwnerIdentity.StartTimeUtcTicks;
	}

	private static string BuildAutocompleteCompletionMetadata(
		AutocompleteRequestContext request,
		CodeServiceDocumentCompletionAdmissionSnapshot admission
	)
	{
		return $"DocumentPath='{admission.DocumentPath}', ClientGeneration='{admission.ClientGeneration}', ClientVersion='{admission.ClientVersion}', Line='{request.Line}', Character='{request.LspCharacter}', RequestGeneration='{request.RequestGeneration}', SessionId='{admission.Session.SessionId}', ServicePid='{admission.Session.ServiceProcessIdentity.ProcessId}'";
	}

	private static string BuildAutocompleteNativeCoalescingMetadata(
		AutocompleteRequestContext request,
		AutocompleteNativeCoalescingPhase phase
	)
	{
		return $"RequestGeneration='{request.RequestGeneration}', ValidationGeneration='{request.ValidationGeneration}', Line='{request.Line}', Character='{request.LspCharacter}', Phase='{phase}'";
	}

	private static string BoundAutocompleteCompletionDetail(string detail)
	{
		string value = (detail ?? "").Replace('\r', ' ').Replace('\n', ' ');
		return value.Length <= 512 ? value : value.Substring(0, 512);
	}

	private static long GetElapsedMilliseconds(long startedTimestamp)
	{
		long elapsed = Stopwatch.GetTimestamp() - startedTimestamp;
		if (elapsed <= 0)
			return 0;
		return (long)Math.Round(elapsed * 1000d / Stopwatch.Frequency);
	}

	private void OnAutocompleteTextChanged()
	{
		AutocompletePluginHost host = _autocompleteHost;
		host?.RestoreTypedOpeningParenthesisAutoCloseSuppression();

		ClearAutocompleteAutomaticNativeCoalescingRequest();
		if (host == null)
			return;

		long generation = host.BeginTextChangedValidation();
		CallDeferred(
			nameof(ValidateAutocompleteAfterTextChangedDeferred),
			generation
		);
	}

	private bool IsAutocompleteAutomaticTextChangedLifecycleStable()
	{
		if (_editorOperationShutdownStarted
			|| !IsValidGodotObject(this)
			|| !IsInsideTree()
			|| !HasVerifiedPersistentTreeStateForCurrentAssembly
			|| _isRecoveringManagedAssemblyState)
		{
			return false;
		}

		return _managedAssemblyRecoveryState != ManagedAssemblyRecoveryState.Queued
			&& _managedAssemblyRecoveryState != ManagedAssemblyRecoveryState.Recovering
			&& _managedAssemblyRecoveryState != ManagedAssemblyRecoveryState.PermanentlyFailed;
	}

	private void ValidateAutocompleteAfterTextChangedDeferred(long generation)
	{
		AutocompletePluginHost scheduledHost = _autocompleteHost;
		if (scheduledHost == null || !scheduledHost.IsValidationCurrent(generation))
			return;

		if (!IsAutocompleteAutomaticTextChangedLifecycleStable()
			|| !ReferenceEquals(scheduledHost, _autocompleteHost))
		{
			return;
		}

		bool currentBindingValidated =
			scheduledHost.TryValidateAfterTextChangedCurrentBinding(
				generation,
				out bool suppressAutomaticRequest
			);

		DiscardStalePendingAutocompleteCompletionIntentAfterTextChanged(generation);

		if (!scheduledHost.IsValidationCurrent(generation)
			|| !IsAutocompleteAutomaticTextChangedLifecycleStable()
			|| !ReferenceEquals(scheduledHost, _autocompleteHost)
			|| !currentBindingValidated)
		{
			return;
		}

		if (suppressAutomaticRequest)
			return;

		if (_autocompleteLastCompletionRequestedValidationGeneration == generation
			|| !CanAutomaticallyCaptureAutocompleteCompletionIntent())
		{
			return;
		}

		if (!scheduledHost.TryCaptureAutomaticCompletionRequest(
			out AutocompleteRequestContext request
		)
			|| request.ValidationGeneration != generation)
		{
			return;
		}

		RememberAutocompleteAutomaticNativeCoalescingRequest(request);
		HandleCapturedAutocompleteCompletionRequest(
			request,
			emitIngressDiagnostics: false
		);
	}

	#endregion
}
#endif
