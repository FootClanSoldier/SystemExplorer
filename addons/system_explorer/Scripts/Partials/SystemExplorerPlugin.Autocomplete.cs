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

	private readonly object _autocompleteCompletionStateGate = new();
	private AutocompletePluginHost _autocompleteHost;
	private long _autocompleteLastCompletionRequestedValidationGeneration = long.MinValue;
	private bool _autocompleteCompletionRequestAdmissionOpen;
	private Task<CodeServiceCompletionResult> _autocompleteCompletionFlight;
	private CancellationTokenSource _autocompleteCompletionFlightCancellation;
	private AutocompleteRequestContext _autocompleteCompletionFlightRequest;
	private CodeServiceDocumentCompletionAdmissionSnapshot _autocompleteCompletionFlightAdmission;
	private Task _autocompleteCompletionFlightObservationTask;
	private AutocompletePendingCompletionIntent _autocompletePendingCompletionIntent;
	private AutocompleteCompletedCompletionFlight _autocompleteCompletedCompletionFlight;

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
			nameof(OnAutocompleteCodeCompletionRequested)
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
			ResetAutocompleteCompletionRequestedValidationGeneration();
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

		ResetAutocompleteCompletionRequestedValidationGeneration();
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
		ResetAutocompleteCompletionRequestedValidationGeneration();
		ShutdownAutocompleteCompletionTransport("Managed Assembly Reload");
		_autocompleteHost?.ResetTransientState();
	}

	private void ShutdownAutocomplete()
	{
		ResetAutocompleteCompletionRequestedValidationGeneration();
		ShutdownAutocompleteCompletionTransport("Autocomplete Shutdown");
		_autocompleteHost?.Shutdown();
		_autocompleteHost = null;
	}

	private void ShutdownAutocompleteCompletionTransport(string reason)
	{
		ResetAutocompleteCompletionRequestedValidationGeneration();
		CancellationTokenSource cancellation = null;
		lock (_autocompleteCompletionStateGate)
		{
			_autocompleteCompletionRequestAdmissionOpen = false;
			_autocompletePendingCompletionIntent = null;
			cancellation = _autocompleteCompletionFlightCancellation;
		}

		try { cancellation?.Cancel(); } catch { }
		TryLogEditorOperation(
			"CodeService Completion Discarded",
			$"Reason='{BoundAutocompleteCompletionDetail(reason)}', ManagedGeneration='{ManagedAssemblyGeneration}'"
		);
	}

	private void OnAutocompleteScriptChanged(Script script)
	{
		ResetAutocompleteCompletionRequestedValidationGeneration();
		InvalidateAutocompleteCompletionForScriptChange();
		_autocompleteHost?.InvalidatePendingValidations();

		if (!EnsureManagedAssemblyStateCurrent("C# Autocomplete Script Changed"))
			return;

		if (TryEnsureAutocompleteHost(out AutocompletePluginHost host))
			host.HandleScriptChanged();
	}

	private void InvalidateAutocompleteCompletionForScriptChange()
	{
		CancellationTokenSource cancellation = null;
		lock (_autocompleteCompletionStateGate)
		{
			_autocompletePendingCompletionIntent = null;
			cancellation = _autocompleteCompletionFlightCancellation;
		}
		try { cancellation?.Cancel(); } catch { }
	}

	private void OnAutocompleteCodeCompletionRequested()
	{
		if (!EnsureManagedAssemblyStateCurrent("C# Autocomplete Completion Requested"))
			return;
		if (!TryEnsureAutocompleteHost(out AutocompletePluginHost host))
			return;
		if (!host.TryCaptureCompletionRequest(out AutocompleteRequestContext request))
		{
			ResetAutocompleteCompletionRequestedValidationGeneration();
			return;
		}

		_autocompleteLastCompletionRequestedValidationGeneration = request.ValidationGeneration;
		HandleCapturedAutocompleteCompletionRequest(request, emitIngressDiagnostics: true);
	}

	private void HandleCapturedAutocompleteCompletionRequest(
		AutocompleteRequestContext request,
		bool emitIngressDiagnostics
	)
	{
		if (request == null)
			return;

		if (!TryPrepareCodeServiceCompletionDocumentAdmission(
			request.ScriptPath,
			out CodeServiceDocumentCompletionAdmissionSnapshot admission,
			out string detail
		))
		{
			if (emitIngressDiagnostics)
			{
				TryLogEditorOperation(
					"CodeService Completion Unavailable",
					$"RequestGeneration='{request.RequestGeneration}', Line='{request.Line}', Character='{request.LspCharacter}', Detail='{BoundAutocompleteCompletionDetail(detail)}'"
				);
			}
			return;
		}

		if (emitIngressDiagnostics)
		{
			TryLogEditorOperation(
				"CodeService Completion Requested",
				BuildAutocompleteCompletionMetadata(request, admission)
			);
		}

		if (IsAutocompleteCompletionSessionBlocked(admission.Session, out string blockedDetail))
		{
			if (emitIngressDiagnostics)
			{
				TryLogEditorOperation(
					"CodeService Completion Unavailable",
					BuildAutocompleteCompletionMetadata(request, admission)
					+ $", Detail='{BoundAutocompleteCompletionDetail(blockedDetail)}'"
				);
			}
			return;
		}

		CancellationTokenSource activeCancellation = null;
		bool hasActiveFlight;
		lock (_autocompleteCompletionStateGate)
		{
			if (!_autocompleteCompletionRequestAdmissionOpen)
				return;

			hasActiveFlight = _autocompleteCompletionFlight != null;
			if (hasActiveFlight)
			{
				_autocompletePendingCompletionIntent = new AutocompletePendingCompletionIntent(
					request,
					admission
				);
				activeCancellation = _autocompleteCompletionFlightCancellation;
			}
		}

		if (hasActiveFlight)
		{
			try { activeCancellation?.Cancel(); } catch { }
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
			lock (_autocompleteCompletionStateGate)
			{
				if (_autocompleteCompletionRequestAdmissionOpen)
				{
					_autocompletePendingCompletionIntent = new AutocompletePendingCompletionIntent(
						request,
						admission
					);
				}
			}
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
			|| !clientCoordinator.TryGetReadySessionInfo(out CodeServiceClientSessionInfo currentSession)
			|| !IsExactAutocompleteCompletionSession(currentSession, admission.Session))
		{
			return;
		}

		var completionRequest = new CodeServiceCompletionRequest(
			admission.ClientGeneration,
			admission.EpochId,
			admission.DocumentPath,
			admission.ClientVersion,
			request.Line,
			request.LspCharacter
		);
		var cancellation = new CancellationTokenSource();
		Task<CodeServiceCompletionResult> flight;
		lock (_autocompleteCompletionStateGate)
		{
			if (!_autocompleteCompletionRequestAdmissionOpen || _autocompleteCompletionFlight != null)
			{
				cancellation.Dispose();
				return;
			}

			_autocompletePendingCompletionIntent = null;
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
			TryStartLatestPendingAutocompleteCompletion();
			return;
		}

		CodeServiceCompletionResult result = completed.Result;
		TryLogEditorOperation(
			"CodeService Completion Completed",
			BuildAutocompleteCompletionMetadata(completed.Request, completed.Admission)
			+ $", Outcome='{result.Outcome}', ItemCount='{result.Items?.Count ?? 0}', IsIncomplete='{result.IsIncomplete}', DurationMs='{completed.DurationMilliseconds}'"
		);

		HandleAutocompleteCompletionResult(completed);
		TryStartLatestPendingAutocompleteCompletion();
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
				RestartCodeServiceDocumentQuietTimer();
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

		var mappedItems = new List<AutocompleteCompletionItem>(result.Items?.Count ?? 0);
		if (result.Items != null)
		{
			foreach (CodeServiceCompletionItem item in result.Items)
			{
				if (item == null)
					continue;
				mappedItems.Add(new AutocompleteCompletionItem(
					AutocompleteCompletionKindMapper.Map(item.Kind),
					item.DisplayText,
					item.InsertText,
					item.FilterText,
					item.SortText,
					item.Preselect
				));
			}
		}

		AutocompletePluginHost host = _autocompleteHost;
		string publicationDetail = "Autocomplete host is unavailable before publication.";
		if (host == null || !host.TryPublishCompletionResult(request, mappedItems, out publicationDetail))
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

	private void TryResumePendingAutocompleteCompletionAfterDocumentSynchronization()
	{
		TryStartLatestPendingAutocompleteCompletion();
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
		if (documentCoordinator == null
			|| !documentCoordinator.TryGetCompletionAdmissionSnapshot(
				pending.Admission.DocumentPath,
				out CodeServiceDocumentCompletionAdmissionSnapshot currentAdmission,
				out _
			))
		{
			ClearPendingAutocompleteCompletionIntent(pending.Request.RequestGeneration);
			return;
		}

		if (currentAdmission.ClientGeneration != pending.Admission.ClientGeneration
			|| !string.Equals(currentAdmission.EpochId, pending.Admission.EpochId, StringComparison.Ordinal)
			|| !string.Equals(currentAdmission.DocumentPath, pending.Admission.DocumentPath, StringComparison.Ordinal)
			|| currentAdmission.ClientVersion != pending.Admission.ClientVersion
			|| !IsExactAutocompleteCompletionSession(currentAdmission.Session, pending.Admission.Session))
		{
			ClearPendingAutocompleteCompletionIntent(pending.Request.RequestGeneration);
			return;
		}

		if (!currentAdmission.IsCurrentVersionSynchronized)
		{
			RestartCodeServiceDocumentQuietTimer();
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

	private void ClearPendingAutocompleteCompletionIntent(long requestGeneration)
	{
		lock (_autocompleteCompletionStateGate)
		{
			if (_autocompletePendingCompletionIntent?.Request.RequestGeneration == requestGeneration)
				_autocompletePendingCompletionIntent = null;
		}
	}

	private void InvalidateStaleAutocompleteCompletionRequestsAfterTextChanged(
		long validationGeneration
	)
	{
		CancellationTokenSource cancellation = null;
		lock (_autocompleteCompletionStateGate)
		{
			if (_autocompletePendingCompletionIntent != null
				&& _autocompletePendingCompletionIntent.Request.ValidationGeneration < validationGeneration)
			{
				_autocompletePendingCompletionIntent = null;
			}
			if (_autocompleteCompletionFlightRequest != null
				&& _autocompleteCompletionFlightRequest.ValidationGeneration < validationGeneration)
			{
				cancellation = _autocompleteCompletionFlightCancellation;
			}
		}
		try { cancellation?.Cancel(); } catch { }
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

	private void ResetAutocompleteCompletionRequestedValidationGeneration()
	{
		_autocompleteLastCompletionRequestedValidationGeneration = long.MinValue;
	}

	private bool CanAutomaticallyRequestAutocompleteCompletion()
	{
		lock (_autocompleteCompletionStateGate)
		{
			if (!_autocompleteCompletionRequestAdmissionOpen)
				return false;
		}

		CodeServiceDocumentSynchronizationCoordinator documentCoordinator =
			_codeServiceDocumentSynchronizationCoordinator;
		if (documentCoordinator == null
			|| documentCoordinator.IsFailedClosed
			|| _codeServiceDocumentEditorBinding == null
			|| !IsValidGodotObject(_codeServiceDocumentQuietTimer))
		{
			return false;
		}

		CodeServiceClientCoordinator clientCoordinator = _codeServiceClientCoordinator;
		if (clientCoordinator == null
			|| !clientCoordinator.TryGetReadySessionInfo(out CodeServiceClientSessionInfo currentSession))
		{
			return false;
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

		InvalidateStaleAutocompleteCompletionRequestsAfterTextChanged(generation);

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
			|| !CanAutomaticallyRequestAutocompleteCompletion())
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

		HandleCapturedAutocompleteCompletionRequest(
			request,
			emitIngressDiagnostics: false
		);
	}

	#endregion
}
#endif
