#if TOOLS
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using SystemExplorer.Autocomplete.Indexing;
using SystemExplorer.EditorIntegration.ScriptEditing;

namespace SystemExplorer.Autocomplete.Semantics;

internal sealed class CSharpSemanticMemberWorker
{
	private readonly CSharpSemanticMetadataReferenceProvider _metadataReferenceProvider;

	private CSharpCompilation _baseCompilation;
	private Dictionary<string, SyntaxTree> _baseTreesByResourcePath =
		new(StringComparer.OrdinalIgnoreCase);
	private long _baseProjectGeneration;
	private long _baseProjectStateVersion;
	private int _baseMetadataReferenceFailureCount;
	private int _baseProjectFingerprintMismatchCount;
	private string _baseDiagnosticDetail = "";

	internal CSharpSemanticMemberWorker(
		CSharpSemanticMetadataReferenceProvider metadataReferenceProvider
	)
	{
		_metadataReferenceProvider =
			metadataReferenceProvider
			?? throw new ArgumentNullException(nameof(metadataReferenceProvider));
	}

	internal bool HasBaseFor(long projectStateVersion, long projectGeneration)
	{
		return Volatile.Read(ref _baseProjectStateVersion) == projectStateVersion
			&& Volatile.Read(ref _baseProjectGeneration) == projectGeneration;
	}

	internal CSharpSemanticMemberBuildResult Build(
		CSharpSemanticMemberBuildRequest request,
		CancellationToken cancellationToken
	)
	{
		ArgumentNullException.ThrowIfNull(request);
		var stopwatch = Stopwatch.StartNew();
		long projectGeneration = request.ProjectSnapshot?.Generation ?? 0;
		long activeRevision = request.ActiveDocument?.Revision ?? 0;
		string scriptPath = ScriptPathUtility.Normalize(
			request.ActiveDocument?.ScriptPath
		);
		bool baseCompilationBuilt = false;
		string failurePhase = "ValidateRequest";
		string activeSourceText = request.ActiveDocument?.SourceText ?? "";
		CSharpParseOptions activeParseOptions = null;

		try
		{
			cancellationToken.ThrowIfCancellationRequested();

			if (
				request.ProjectSnapshot == null
				|| !request.ProjectSnapshot.HasBuiltAtLeastOnce
				|| projectGeneration <= 0
			)
			{
				return CreateFailedResult(
					projectGeneration,
					activeRevision,
					scriptPath,
					stopwatch,
					baseCompilationBuilt,
					"A built project snapshot is required."
				);
			}

			failurePhase = "BuildBaseCompilation";
			if (!HasBaseFor(request.ProjectStateVersion, projectGeneration))
			{
				BuildBaseCompilation(request, cancellationToken);
				baseCompilationBuilt = true;
			}

			cancellationToken.ThrowIfCancellationRequested();
			failurePhase = "ValidateActiveDocument";

			if (request.ActiveDocument == null)
			{
				stopwatch.Stop();
				return new CSharpSemanticMemberBuildResult(
					projectGeneration,
					0,
					"",
					CSharpSemanticMemberBuildStatus.Succeeded,
					stopwatch.Elapsed,
					0,
					0,
					baseCompilationBuilt,
					_baseMetadataReferenceFailureCount,
					_baseProjectFingerprintMismatchCount,
					_baseDiagnosticDetail,
					failureDetail: "",
					snapshot: null
				);
			}

			if (!IsCSharpScriptPath(scriptPath))
			{
				return CreateFailedResult(
					projectGeneration,
					activeRevision,
					scriptPath,
					stopwatch,
					baseCompilationBuilt,
					"Active semantic script path is not a valid C# resource path."
				);
			}

			failurePhase = "ParseActiveDocument";
			activeSourceText = request.ActiveDocument.SourceText ?? "";
			activeParseOptions = CSharpSyntaxParseProfile.ParseOptions;
			SyntaxTree activeTree = CSharpSyntaxTree.ParseText(
				activeSourceText,
				activeParseOptions,
				scriptPath,
				cancellationToken: cancellationToken
			);
			cancellationToken.ThrowIfCancellationRequested();

			failurePhase = "ComposeActiveCompilation";
			CSharpCompilation activeCompilation = _baseCompilation;
			if (
				_baseTreesByResourcePath.TryGetValue(
					scriptPath,
					out SyntaxTree diskTree
				)
			)
			{
				activeCompilation = activeCompilation.ReplaceSyntaxTree(
					diskTree,
					activeTree
				);
			}
			else
			{
				activeCompilation = activeCompilation.AddSyntaxTrees(activeTree);
			}

			cancellationToken.ThrowIfCancellationRequested();
			failurePhase = "CreateSemanticModel";
			SemanticModel semanticModel = activeCompilation.GetSemanticModel(
				activeTree,
				ignoreAccessibility: false
			);
			CompilationUnitSyntax root = (CompilationUnitSyntax)activeTree.GetRoot(
				cancellationToken
			);
			failurePhase = "CreateProjectSourcePathSet";
			var projectSourcePaths = new HashSet<string>(
				request.ProjectSnapshot.FilesByResourcePath.Keys
					.Select(ScriptPathUtility.Normalize)
					.Where(path => !string.IsNullOrWhiteSpace(path)),
				StringComparer.OrdinalIgnoreCase
			)
			{
				scriptPath,
			};

			var memberAccesses = new List<CSharpSemanticMemberAccess>();
			int memberCount = 0;

			failurePhase = "TraverseMemberAccesses";
			foreach (
				MemberAccessExpressionSyntax memberAccess in root
					.DescendantNodes()
					.OfType<MemberAccessExpressionSyntax>()
			)
			{
				cancellationToken.ThrowIfCancellationRequested();

				if (!memberAccess.IsKind(SyntaxKind.SimpleMemberAccessExpression))
					continue;
				if (memberAccess.Expression is BaseExpressionSyntax)
					continue;
				if (memberAccess.Name == null)
					continue;

				failurePhase = "ResolveMemberAccessPosition";
				if (
					!TryGetMemberAccessPosition(
						memberAccess,
						activeTree,
						cancellationToken,
						out int memberNamePosition,
						out FileLinePositionSpan memberLineSpan
					)
				)
				{
					failurePhase = "TraverseMemberAccesses";
					continue;
				}

				failurePhase = "ResolveReceiver.GetSymbolInfo";
				if (
					!TryResolveReceiver(
						semanticModel,
						memberAccess.Expression,
						cancellationToken,
						ref failurePhase,
						out INamedTypeSymbol receiverType,
						out ReceiverMode receiverMode
					)
				)
				{
					failurePhase = "TraverseMemberAccesses";
					continue;
				}

				failurePhase = "ValidateReceiver";
				if (
					receiverType.TypeKind == TypeKind.Error
					|| !IsProjectSourceType(receiverType, projectSourcePaths)
				)
				{
					failurePhase = "TraverseMemberAccesses";
					continue;
				}

				failurePhase = "LookupSymbols";
				int lookupPosition = memberNamePosition;
				IEnumerable<ISymbol> lookupSymbols = semanticModel.LookupSymbols(
					lookupPosition,
					container: receiverType,
					includeReducedExtensionMethods: false
				);
				failurePhase = "CreateMembers";
				IReadOnlyList<CSharpSemanticMemberSymbol> members = CreateMembers(
					receiverType,
					receiverMode,
					lookupSymbols,
					projectSourcePaths,
					cancellationToken
				);

				if (members.Count == 0)
				{
					failurePhase = "TraverseMemberAccesses";
					continue;
				}

				memberCount += members.Count;
				memberAccesses.Add(
					new CSharpSemanticMemberAccess(
						memberLineSpan.StartLinePosition.Line,
						memberLineSpan.StartLinePosition.Character,
						receiverType.ToDisplayString(
							SymbolDisplayFormat.MinimallyQualifiedFormat
						),
						members
					)
				);
				failurePhase = "TraverseMemberAccesses";
			}

			cancellationToken.ThrowIfCancellationRequested();
			failurePhase = "CreateSnapshot";
			var snapshot = new CSharpSemanticMemberIndexSnapshot(
				projectGeneration,
				activeRevision,
				scriptPath,
				memberAccesses,
				hasBuiltAtLeastOnce: true
			);

			stopwatch.Stop();
			return new CSharpSemanticMemberBuildResult(
				projectGeneration,
				activeRevision,
				scriptPath,
				CSharpSemanticMemberBuildStatus.Succeeded,
				stopwatch.Elapsed,
				snapshot.MemberAccesses.Count,
				memberCount,
				baseCompilationBuilt,
				_baseMetadataReferenceFailureCount,
				_baseProjectFingerprintMismatchCount,
				_baseDiagnosticDetail,
				failureDetail: "",
				snapshot
			);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			stopwatch.Stop();
			return new CSharpSemanticMemberBuildResult(
				projectGeneration,
				activeRevision,
				scriptPath,
				CSharpSemanticMemberBuildStatus.Cancelled,
				stopwatch.Elapsed,
				0,
				0,
				baseCompilationBuilt,
				_baseMetadataReferenceFailureCount,
				_baseProjectFingerprintMismatchCount,
				_baseDiagnosticDetail,
				"Build cancellation was requested.",
				snapshot: null
			);
		}
		catch (Exception exception)
		{
			return CreateFailedResult(
				projectGeneration,
				activeRevision,
				scriptPath,
				stopwatch,
				baseCompilationBuilt,
				CreateUnexpectedFailureDetail(
					failurePhase,
					request,
					projectGeneration,
					activeSourceText,
					activeParseOptions,
					exception
				)
			);
		}
	}

	private void BuildBaseCompilation(
		CSharpSemanticMemberBuildRequest request,
		CancellationToken cancellationToken
	)
	{
		var syntaxTrees = new List<SyntaxTree>();
		var treesByResourcePath = new Dictionary<string, SyntaxTree>(
			StringComparer.OrdinalIgnoreCase
		);
		var diagnostics = new List<string>();
		int fingerprintMismatchCount = 0;

		foreach (
			CSharpFileIndexEntry fileEntry in request.ProjectSnapshot.FilesByResourcePath
				.Values
				.OrderBy(entry => entry.ResourcePath, StringComparer.OrdinalIgnoreCase)
				.ThenBy(entry => entry.ResourcePath, StringComparer.Ordinal)
		)
		{
			cancellationToken.ThrowIfCancellationRequested();
			string resourcePath = ScriptPathUtility.Normalize(fileEntry.ResourcePath);

			if (
				!IsCSharpScriptPath(resourcePath)
				|| string.IsNullOrWhiteSpace(fileEntry.GlobalPath)
			)
			{
				fingerprintMismatchCount++;
				AddDiagnostic(
					diagnostics,
					$"Project fingerprint unavailable for '{resourcePath}'."
				);
				continue;
			}

			if (!MatchesSnapshotFingerprint(fileEntry))
			{
				fingerprintMismatchCount++;
				AddDiagnostic(
					diagnostics,
					$"Project fingerprint changed before semantic read: '{resourcePath}'."
				);
				continue;
			}

			string sourceText;
			try
			{
				sourceText = File.ReadAllText(fileEntry.GlobalPath, Encoding.UTF8);
			}
			catch (Exception exception) when (IsExpectedFileException(exception))
			{
				fingerprintMismatchCount++;
				AddDiagnostic(
					diagnostics,
					$"Project source read failed for '{resourcePath}': "
						+ $"{exception.GetType().Name}: {NormalizeMessage(exception.Message)}"
				);
				continue;
			}

			cancellationToken.ThrowIfCancellationRequested();
			if (!MatchesSnapshotFingerprint(fileEntry))
			{
				fingerprintMismatchCount++;
				AddDiagnostic(
					diagnostics,
					$"Project fingerprint changed during semantic read: '{resourcePath}'."
				);
				continue;
			}

			SyntaxTree syntaxTree = CSharpSyntaxTree.ParseText(
				sourceText,
				CSharpSyntaxParseProfile.ParseOptions,
				resourcePath,
				cancellationToken: cancellationToken
			);
			syntaxTrees.Add(syntaxTree);
			treesByResourcePath[resourcePath] = syntaxTree;
		}

		cancellationToken.ThrowIfCancellationRequested();
		CSharpSemanticMetadataReferenceProvider.MetadataReferenceSet referenceSet =
			_metadataReferenceProvider.GetReferences();
		foreach (string failure in referenceSet.Failures)
			AddDiagnostic(diagnostics, failure);

		var compilationOptions = new CSharpCompilationOptions(
			OutputKind.DynamicallyLinkedLibrary
		);
		CSharpCompilation compilation = CSharpCompilation.Create(
			$"SystemExplorer.Semantic.Generation{request.ProjectSnapshot.Generation}",
			syntaxTrees,
			referenceSet.References,
			compilationOptions
		);
		cancellationToken.ThrowIfCancellationRequested();

		_baseCompilation = compilation;
		_baseTreesByResourcePath = treesByResourcePath;
		_baseMetadataReferenceFailureCount = referenceSet.Failures.Count;
		_baseProjectFingerprintMismatchCount = fingerprintMismatchCount;
		_baseDiagnosticDetail = string.Join(" | ", diagnostics);
		Volatile.Write(ref _baseProjectGeneration, request.ProjectSnapshot.Generation);
		Volatile.Write(ref _baseProjectStateVersion, request.ProjectStateVersion);
	}

	private static bool TryGetMemberAccessPosition(
		MemberAccessExpressionSyntax memberAccess,
		SyntaxTree activeTree,
		CancellationToken cancellationToken,
		out int memberNamePosition,
		out FileLinePositionSpan memberLineSpan
	)
	{
		memberNamePosition = -1;
		memberLineSpan = default;

		SyntaxToken memberIdentifier = memberAccess.Name.Identifier;
		SyntaxToken operatorToken = memberAccess.OperatorToken;

		if (!memberIdentifier.IsMissing)
		{
			memberNamePosition = memberIdentifier.SpanStart;
			memberLineSpan = activeTree.GetLineSpan(
				new TextSpan(memberNamePosition, 0),
				cancellationToken
			);
			FileLinePositionSpan operatorLineSpan = activeTree.GetLineSpan(
				operatorToken.Span,
				cancellationToken
			);
			int memberLine = memberLineSpan.StartLinePosition.Line;
			int operatorLine = operatorLineSpan.StartLinePosition.Line;

			if (memberLine == operatorLine)
				return true;

			if (memberLine < operatorLine)
				return false;

			if (operatorToken.IsMissing || !operatorToken.IsKind(SyntaxKind.DotToken))
			{
				return false;
			}

			memberNamePosition = operatorToken.Span.End;
			memberLineSpan = activeTree.GetLineSpan(
				new TextSpan(memberNamePosition, 0),
				cancellationToken
			);
			return memberLineSpan.StartLinePosition.Line == operatorLine;
		}

		if (operatorToken.IsMissing || !operatorToken.IsKind(SyntaxKind.DotToken))
			return false;

		memberNamePosition = operatorToken.Span.End;
		memberLineSpan = activeTree.GetLineSpan(
			new TextSpan(memberNamePosition, 0),
			cancellationToken
		);
		return true;
	}

	private static IReadOnlyList<CSharpSemanticMemberSymbol> CreateMembers(
		INamedTypeSymbol receiverType,
		ReceiverMode receiverMode,
		IEnumerable<ISymbol> lookupSymbols,
		HashSet<string> projectSourcePaths,
		CancellationToken cancellationToken
	)
	{
		HashSet<ISymbol> allowedContainingTypes =
			CreateAllowedProjectSourceContainingTypes(
				receiverType,
				projectSourcePaths,
				cancellationToken
			);
		var membersByIdentity = new Dictionary<string, MutableMember>(StringComparer.Ordinal);

		foreach (ISymbol symbol in lookupSymbols ?? Array.Empty<ISymbol>())
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (
				symbol == null
				|| !IsMemberStaticStateAllowed(symbol, receiverMode)
				|| symbol.IsImplicitlyDeclared
				|| string.IsNullOrWhiteSpace(symbol.Name)
				|| symbol.ContainingType == null
				|| !allowedContainingTypes.Contains(
					symbol.ContainingType.OriginalDefinition
				)
			)
			{
				continue;
			}

			if (!TryGetMemberKind(symbol, out CSharpSemanticMemberKind kind))
				continue;

			string identity = $"{(int)kind}\u001f{symbol.Name}";
			if (!membersByIdentity.TryGetValue(identity, out MutableMember member))
			{
				membersByIdentity.Add(
					identity,
					new MutableMember(symbol.Name, kind)
				);
				continue;
			}

			if (kind == CSharpSemanticMemberKind.Method)
				member.OverloadCount++;
		}

		return membersByIdentity.Values
			.OrderBy(member => member.Name, StringComparer.OrdinalIgnoreCase)
			.ThenBy(member => member.Name, StringComparer.Ordinal)
			.ThenBy(member => member.Kind)
			.Select(
				member =>
					new CSharpSemanticMemberSymbol(
						member.Name,
						member.Kind,
						member.OverloadCount
					)
			)
			.ToArray();
	}

	private static HashSet<ISymbol> CreateAllowedProjectSourceContainingTypes(
		INamedTypeSymbol receiverType,
		HashSet<string> projectSourcePaths,
		CancellationToken cancellationToken
	)
	{
		var allowedTypes = new HashSet<ISymbol>(SymbolEqualityComparer.Default);

		for (INamedTypeSymbol type = receiverType; type != null; type = type.BaseType)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (IsProjectSourceType(type, projectSourcePaths))
				allowedTypes.Add(type.OriginalDefinition);
		}

		foreach (INamedTypeSymbol interfaceType in receiverType.AllInterfaces)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (IsProjectSourceType(interfaceType, projectSourcePaths))
				allowedTypes.Add(interfaceType.OriginalDefinition);
		}

		return allowedTypes;
	}

	private static bool TryGetMemberKind(
		ISymbol symbol,
		out CSharpSemanticMemberKind kind
	)
	{
		switch (symbol)
		{
			case IMethodSymbol method
				when method.MethodKind == MethodKind.Ordinary
					&& method.Arity == 0
					&& !method.IsExtensionMethod:
				kind = CSharpSemanticMemberKind.Method;
				return true;
			case IPropertySymbol property when !property.IsIndexer:
				kind = CSharpSemanticMemberKind.Property;
				return true;
			case IFieldSymbol:
				kind = CSharpSemanticMemberKind.Field;
				return true;
			case IEventSymbol:
				kind = CSharpSemanticMemberKind.Event;
				return true;
			default:
				kind = default;
				return false;
		}
	}

	private static bool TryResolveReceiver(
		SemanticModel semanticModel,
		ExpressionSyntax receiverExpression,
		CancellationToken cancellationToken,
		ref string failurePhase,
		out INamedTypeSymbol receiverType,
		out ReceiverMode receiverMode
	)
	{
		failurePhase = "ResolveReceiver.GetSymbolInfo";
		SymbolInfo receiverSymbolInfo = semanticModel.GetSymbolInfo(
			receiverExpression,
			cancellationToken
		);

		failurePhase = "ResolveReceiver.TypeReceiverCheck";
		if (TryGetTypeReceiver(receiverSymbolInfo.Symbol, out receiverType))
		{
			receiverMode = ReceiverMode.Type;
			return true;
		}

		failurePhase = "ResolveReceiver.GetTypeInfo";
		TypeInfo receiverTypeInfo = semanticModel.GetTypeInfo(
			receiverExpression,
			cancellationToken
		);
		receiverType =
			receiverTypeInfo.Type as INamedTypeSymbol
			?? receiverTypeInfo.ConvertedType as INamedTypeSymbol;
		receiverMode = ReceiverMode.Instance;
		return receiverType != null;
	}

	private static bool TryGetTypeReceiver(
		ISymbol symbol,
		out INamedTypeSymbol receiverType
	)
	{
		if (symbol is INamedTypeSymbol namedType)
		{
			receiverType = namedType;
			return true;
		}

		if (symbol is IAliasSymbol alias && alias.Target is INamedTypeSymbol aliasedType)
		{
			receiverType = aliasedType;
			return true;
		}

		receiverType = null;
		return false;
	}

	private static bool IsMemberStaticStateAllowed(
		ISymbol symbol,
		ReceiverMode receiverMode
	)
	{
		return receiverMode == ReceiverMode.Type ? symbol.IsStatic : !symbol.IsStatic;
	}

	private static bool IsProjectSourceType(
		INamedTypeSymbol type,
		HashSet<string> projectSourcePaths
	)
	{
		INamedTypeSymbol definition = type.OriginalDefinition;

		foreach (Location location in definition.Locations)
		{
			if (!location.IsInSource || location.SourceTree == null)
				continue;

			string sourcePath = ScriptPathUtility.Normalize(location.SourceTree.FilePath);
			if (projectSourcePaths.Contains(sourcePath))
				return true;
		}

		return false;
	}

	private static bool MatchesSnapshotFingerprint(CSharpFileIndexEntry fileEntry)
	{
		try
		{
			var fileInfo = new FileInfo(fileEntry.GlobalPath);
			fileInfo.Refresh();
			return fileInfo.Exists
				&& fileInfo.Length == fileEntry.Length
				&& fileInfo.LastWriteTimeUtc.Ticks == fileEntry.LastWriteTimeUtcTicks;
		}
		catch (Exception exception) when (IsExpectedFileException(exception))
		{
			return false;
		}
	}

	private static bool IsExpectedFileException(Exception exception)
	{
		return exception is FileNotFoundException
			|| exception is DirectoryNotFoundException
			|| exception is DriveNotFoundException
			|| exception is UnauthorizedAccessException
			|| exception is IOException
			|| exception is ArgumentException
			|| exception is NotSupportedException
			|| exception is PathTooLongException;
	}

	private static void AddDiagnostic(List<string> diagnostics, string detail)
	{
		if (diagnostics.Count < 8 && !string.IsNullOrWhiteSpace(detail))
			diagnostics.Add(detail);
	}

	private CSharpSemanticMemberBuildResult CreateFailedResult(
		long projectGeneration,
		long activeRevision,
		string scriptPath,
		Stopwatch stopwatch,
		bool baseCompilationBuilt,
		string failureDetail
	)
	{
		if (stopwatch.IsRunning)
			stopwatch.Stop();

		return new CSharpSemanticMemberBuildResult(
			projectGeneration,
			activeRevision,
			scriptPath,
			CSharpSemanticMemberBuildStatus.Failed,
			stopwatch.Elapsed,
			0,
			0,
			baseCompilationBuilt,
			_baseMetadataReferenceFailureCount,
			_baseProjectFingerprintMismatchCount,
			_baseDiagnosticDetail,
			failureDetail,
			snapshot: null
		);
	}

	private static bool IsCSharpScriptPath(string scriptPath)
	{
		return !string.IsNullOrWhiteSpace(scriptPath)
			&& scriptPath.StartsWith("res://", StringComparison.OrdinalIgnoreCase)
			&& scriptPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);
	}

	private string CreateUnexpectedFailureDetail(
		string failurePhase,
		CSharpSemanticMemberBuildRequest request,
		long requestedProjectGeneration,
		string activeSourceText,
		CSharpParseOptions activeParseOptions,
		Exception exception
	)
	{
		const int maximumFailureDetailLength = 5000;
		const int maximumContextLength = 1900;
		const int preferredExceptionLength = 3000;

		long cachedBaseProjectStateVersion = Volatile.Read(ref _baseProjectStateVersion);
		long cachedBaseProjectGeneration = Volatile.Read(ref _baseProjectGeneration);
		Dictionary<string, SyntaxTree> baseTreesByResourcePath = _baseTreesByResourcePath;
		bool hasBaseForCurrentRequest = HasBaseFor(
			request.ProjectStateVersion,
			requestedProjectGeneration
		);
		CSharpParseOptions diagnosticParseOptions =
			activeParseOptions ?? CSharpSyntaxParseProfile.ParseOptions;
		string runtimeContext = CSharpRoslynRuntimeDiagnostics.CreateParseFailureContext(
			activeSourceText,
			diagnosticParseOptions
		);
		string context = NormalizeSingleLine(
			$"Unexpected semantic member build failure: Phase='{failurePhase}', "
				+ $"RequestedProjectStateVersion={request.ProjectStateVersion}, "
				+ $"RequestedProjectGeneration={requestedProjectGeneration}, "
				+ $"CachedBaseProjectStateVersion={cachedBaseProjectStateVersion}, "
				+ $"CachedBaseProjectGeneration={cachedBaseProjectGeneration}, "
				+ $"HasBaseForCurrentRequest={hasBaseForCurrentRequest}, "
				+ $"BaseCompilationNull={_baseCompilation == null}, "
				+ $"BaseTreeCount={baseTreesByResourcePath?.Count ?? -1}, "
				+ runtimeContext,
			maximumLength: maximumContextLength,
			fallback: "Unexpected semantic member build failure."
		);
		const string exceptionPrefix = ", Exception='";
		const string exceptionSuffix = "'";
		int availableExceptionLength = Math.Max(
			500,
			maximumFailureDetailLength
				- context.Length
				- exceptionPrefix.Length
				- exceptionSuffix.Length
		);
		int exceptionLength = Math.Min(
			preferredExceptionLength,
			availableExceptionLength
		);
		string exceptionDetail = NormalizeSingleLineHeadAndTail(
			exception?.ToString(),
			exceptionLength,
			fallback: "Exception details unavailable."
		);

		string detail = $"{context}{exceptionPrefix}{exceptionDetail}{exceptionSuffix}";
		return detail.Length <= maximumFailureDetailLength
			? detail
			: NormalizeSingleLineHeadAndTail(
				detail,
				maximumFailureDetailLength,
				"Unexpected semantic member build failure."
			);
	}

	private static string NormalizeSingleLine(
		string detail,
		int maximumLength,
		string fallback
	)
	{
		if (string.IsNullOrWhiteSpace(detail))
			return fallback;

		string normalized = detail
			.Replace('\r', ' ')
			.Replace('\n', ' ')
			.Replace('\t', ' ')
			.Trim();
		return normalized.Length <= maximumLength
			? normalized
			: normalized.Substring(0, maximumLength);
	}

	private static string NormalizeSingleLineHeadAndTail(
		string detail,
		int maximumLength,
		string fallback
	)
	{
		if (string.IsNullOrWhiteSpace(detail))
			return fallback;

		string normalized = detail
			.Replace('\r', ' ')
			.Replace('\n', ' ')
			.Replace('\t', ' ')
			.Trim();
		if (normalized.Length <= maximumLength)
			return normalized;

		const string truncationMarker = " ... <truncated> ... ";
		if (maximumLength <= truncationMarker.Length + 2)
			return normalized.Substring(0, Math.Max(0, maximumLength));

		int remainingLength = maximumLength - truncationMarker.Length;
		int headLength = (remainingLength * 55) / 100;
		int tailLength = remainingLength - headLength;
		return normalized.Substring(0, headLength)
			+ truncationMarker
			+ normalized.Substring(normalized.Length - tailLength, tailLength);
	}

	private static string NormalizeMessage(string message)
	{
		if (string.IsNullOrWhiteSpace(message))
			return "Unknown error.";

		string normalized = message.Replace('\r', ' ').Replace('\n', ' ').Trim();
		return normalized.Length <= 500 ? normalized : normalized.Substring(0, 500);
	}

	private enum ReceiverMode
	{
		Instance,
		Type,
	}

	private sealed class MutableMember
	{
		internal MutableMember(string name, CSharpSemanticMemberKind kind)
		{
			Name = name;
			Kind = kind;
			OverloadCount = 1;
		}

		internal string Name { get; }
		internal CSharpSemanticMemberKind Kind { get; }
		internal int OverloadCount { get; set; }
	}
}
#endif
