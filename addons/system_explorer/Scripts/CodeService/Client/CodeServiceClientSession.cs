#if TOOLS
using System;
using System.Threading;
using System.Threading.Tasks;
using SystemExplorer.CodeService.Runtime;
using SystemExplorer.CodeService.Documents;
using SystemExplorer.CodeService.Completion;

namespace SystemExplorer.CodeService.Client;

internal sealed class CodeServiceClientSession : IDisposable
{
	private CodeServiceProcessObservation _processObservation;
	private CodeServiceHandshakeClient _handshakeClient;
	private CodeServiceWorkspaceClient _workspaceClient;
	private CodeServiceDocumentClient _documentClient;
	private CodeServiceCompletionClient _completionClient;
	private CodeServiceCompletionResolveClient _completionResolveClient;
	private CodeServiceClientCredentials _credentials;
	private int _disposeState;

	internal CodeServiceClientSession(
		string sessionId,
		string serviceVersion,
		CodeServiceProcessIdentity godotOwnerIdentity,
		CodeServiceProcessIdentity serviceProcessIdentity,
		CodeServiceProcessObservation processObservation,
		string descriptorPath,
		string transport,
		string address,
		int port,
		CodeServiceHandshakeClient handshakeClient,
		CodeServiceClientCredentials credentials
	)
	{
		SessionId = sessionId ?? throw new ArgumentNullException(nameof(sessionId));
		ServiceVersion = serviceVersion ?? throw new ArgumentNullException(nameof(serviceVersion));
		GodotOwnerIdentity = godotOwnerIdentity;
		ServiceProcessIdentity = serviceProcessIdentity;
		_processObservation = processObservation ?? throw new ArgumentNullException(nameof(processObservation));
		DescriptorPath = descriptorPath ?? throw new ArgumentNullException(nameof(descriptorPath));
		Transport = transport ?? throw new ArgumentNullException(nameof(transport));
		Address = address ?? throw new ArgumentNullException(nameof(address));
		Port = port;
		_handshakeClient = handshakeClient ?? throw new ArgumentNullException(nameof(handshakeClient));
		_credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
		_workspaceClient = new CodeServiceWorkspaceClient(
			_handshakeClient.HttpClient,
			_credentials,
			SessionId
		);
		_documentClient = new CodeServiceDocumentClient(
			_handshakeClient.HttpClient,
			_credentials,
			SessionId
		);
		_completionClient = new CodeServiceCompletionClient(
			_handshakeClient.HttpClient,
			_credentials,
			SessionId
		);
		_completionResolveClient = new CodeServiceCompletionResolveClient(
			_handshakeClient.HttpClient,
			_credentials,
			SessionId
		);
	}

	internal string SessionId { get; }
	internal string ServiceVersion { get; }
	internal CodeServiceProcessIdentity GodotOwnerIdentity { get; }
	internal CodeServiceProcessIdentity ServiceProcessIdentity { get; }
	internal string DescriptorPath { get; }
	internal string Transport { get; }
	internal string Address { get; }
	internal int Port { get; }

	internal CodeServiceProcessObservation ProcessObservation
	{
		get
		{
			return Volatile.Read(ref _processObservation)
				?? throw new ObjectDisposedException(nameof(CodeServiceClientSession));
		}
	}

	internal Task<CodeServiceWorkspaceInitializeResult> InitializeWorkspaceAsync(
		string projectRoot,
		CancellationToken cancellationToken
	)
	{
		CodeServiceWorkspaceClient workspaceClient = Volatile.Read(ref _workspaceClient);
		if (workspaceClient == null)
		{
			return Task.FromResult(
				CodeServiceWorkspaceInitializeResult.TransportUnavailable(
					"CodeService client session was retired before workspace initialize."
				)
			);
		}

		return workspaceClient.InitializeAsync(projectRoot, cancellationToken);
	}

	internal Task<CodeServiceWorkspaceStatusResult> GetWorkspaceStatusAsync(
		CancellationToken cancellationToken
	)
	{
		CodeServiceWorkspaceClient workspaceClient = Volatile.Read(ref _workspaceClient);
		if (workspaceClient == null)
		{
			return Task.FromResult(
				CodeServiceWorkspaceStatusResult.TransportUnavailable(
					"CodeService client session was retired before workspace status."
				)
			);
		}

		return workspaceClient.GetStatusAsync(cancellationToken);
	}

	internal Task<CodeServiceDocumentEpochResult> ReconcileDocumentEpochAsync(
		long clientGeneration,
		string epochId,
		System.Collections.Generic.IReadOnlyList<string> openDocumentPaths,
		CancellationToken cancellationToken
	)
	{
		CodeServiceDocumentClient documentClient = Volatile.Read(ref _documentClient);
		if (documentClient == null)
		{
			return Task.FromResult(
				CodeServiceDocumentEpochResult.Failure(
					CodeServiceDocumentOutcome.Disposed,
					"CodeService client session was retired before document epoch reconciliation."
				)
			);
		}

		return documentClient.ReconcileEpochAsync(
			clientGeneration,
			epochId,
			openDocumentPaths,
			cancellationToken
		);
	}

	internal Task<CodeServiceDocumentSnapshotResult> SynchronizeDocumentSnapshotAsync(
		long clientGeneration,
		string epochId,
		CodeServiceDocumentSnapshot snapshot,
		CancellationToken cancellationToken
	)
	{
		CodeServiceDocumentClient documentClient = Volatile.Read(ref _documentClient);
		if (documentClient == null)
		{
			return Task.FromResult(
				CodeServiceDocumentSnapshotResult.Failure(
					CodeServiceDocumentOutcome.Disposed,
					"CodeService client session was retired before document snapshot synchronization."
				)
			);
		}

		return documentClient.SynchronizeSnapshotAsync(
			clientGeneration,
			epochId,
			snapshot,
			cancellationToken
		);
	}

	internal Task<CodeServiceCompletionResult> CompleteDocumentAsync(
		CodeServiceCompletionRequest request,
		CancellationToken cancellationToken
	)
	{
		CodeServiceCompletionClient completionClient = Volatile.Read(ref _completionClient);
		if (completionClient == null)
		{
			return Task.FromResult(
				CodeServiceCompletionResult.Failure(
					CodeServiceCompletionOutcome.Disposed,
					"CodeService client session was retired before completion."
				)
			);
		}

		return completionClient.CompleteAsync(request, cancellationToken);
	}

	internal Task<CodeServiceCompletionResolveResult> ResolveCompletionAsync(
		CodeServiceCompletionResolveRequest request,
		CancellationToken cancellationToken
	)
	{
		CodeServiceCompletionResolveClient resolveClient = Volatile.Read(ref _completionResolveClient);
		if (resolveClient == null)
		{
			return Task.FromResult(
				CodeServiceCompletionResolveResult.Failure(
					CodeServiceCompletionResolveOutcome.Disposed,
					"CodeService client session was retired before completion resolve."
				)
			);
		}

		return resolveClient.ResolveAsync(request, cancellationToken);
	}

	internal CodeServiceClientSessionInfo ToInfo()
	{
		return new CodeServiceClientSessionInfo(
			SessionId,
			ServiceVersion,
			GodotOwnerIdentity,
			ServiceProcessIdentity,
			DescriptorPath,
			Transport,
			Address,
			Port
		);
	}

	public void Dispose()
	{
		if (Interlocked.Exchange(ref _disposeState, 1) != 0)
			return;

		// Close completion/resolve/document/workspace request admission before disposing the shared HTTP transport/credentials.
		CodeServiceCompletionResolveClient completionResolveClient = Interlocked.Exchange(ref _completionResolveClient, null);
		completionResolveClient?.CloseAdmission();

		CodeServiceCompletionClient completionClient = Interlocked.Exchange(ref _completionClient, null);
		completionClient?.CloseAdmission();

		CodeServiceDocumentClient documentClient = Interlocked.Exchange(ref _documentClient, null);
		documentClient?.CloseAdmission();
		Interlocked.Exchange(ref _workspaceClient, null);

		CodeServiceHandshakeClient handshakeClient = Interlocked.Exchange(
			ref _handshakeClient,
			null
		);
		handshakeClient?.Dispose();

		CodeServiceProcessObservation processObservation = Interlocked.Exchange(
			ref _processObservation,
			null
		);
		processObservation?.Dispose();

		CodeServiceClientCredentials credentials = Interlocked.Exchange(
			ref _credentials,
			null
		);
		credentials?.Dispose();
	}
}

internal readonly struct CodeServiceClientSessionInfo
{
	internal CodeServiceClientSessionInfo(
		string sessionId,
		string serviceVersion,
		CodeServiceProcessIdentity godotOwnerIdentity,
		CodeServiceProcessIdentity serviceProcessIdentity,
		string descriptorPath,
		string transport,
		string address,
		int port
	)
	{
		SessionId = sessionId ?? "";
		ServiceVersion = serviceVersion ?? "";
		GodotOwnerIdentity = godotOwnerIdentity;
		ServiceProcessIdentity = serviceProcessIdentity;
		DescriptorPath = descriptorPath ?? "";
		Transport = transport ?? "";
		Address = address ?? "";
		Port = port;
	}

	internal string SessionId { get; }
	internal string ServiceVersion { get; }
	internal CodeServiceProcessIdentity GodotOwnerIdentity { get; }
	internal CodeServiceProcessIdentity ServiceProcessIdentity { get; }
	internal string DescriptorPath { get; }
	internal string Transport { get; }
	internal string Address { get; }
	internal int Port { get; }

	internal bool IsSameLogicalSession(CodeServiceClientSessionInfo other)
	{
		return string.Equals(SessionId, other.SessionId, StringComparison.Ordinal)
			&& ServiceProcessIdentity.ProcessId == other.ServiceProcessIdentity.ProcessId
			&& ServiceProcessIdentity.StartTimeUtcTicks
				== other.ServiceProcessIdentity.StartTimeUtcTicks;
	}
}
#endif
