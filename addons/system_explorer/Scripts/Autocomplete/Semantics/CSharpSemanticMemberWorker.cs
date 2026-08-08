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

			if (!HasBaseFor(request.ProjectStateVersion, projectGeneration))
			{
				BuildBaseCompilation(request, cancellationToken);
				baseCompilationBuilt = true;
			}

			cancellationToken.ThrowIfCancellationRequested();

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

			SyntaxTree activeTree = CSharpSyntaxTree.ParseText(
				request.ActiveDocument.SourceText ?? "",
				CSharpSyntaxParseProfile.ParseOptions,
				scriptPath,
				cancellationToken: cancellationToken
			);
			cancellationToken.ThrowIfCancellationRequested();

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
			SemanticModel semanticModel = activeCompilation.GetSemanticModel(
				activeTree,
				ignoreAccessibility: false
			);
			CompilationUnitSyntax root = (CompilationUnitSyntax)activeTree.GetRoot(
				cancellationToken
			);
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
					continue;
				}

				SymbolInfo receiverSymbolInfo = semanticModel.GetSymbolInfo(
					memberAccess.Expression,
					cancellationToken
				);
				if (IsTypeReceiver(receiverSymbolInfo.Symbol))
					continue;

				TypeInfo receiverTypeInfo = semanticModel.GetTypeInfo(
					memberAccess.Expression,
					cancellationToken
				);
				INamedTypeSymbol receiverType =
					receiverTypeInfo.Type as INamedTypeSymbol
					?? receiverTypeInfo.ConvertedType as INamedTypeSymbol;

				if (
					receiverType == null
					|| receiverType.TypeKind == TypeKind.Error
					|| !IsProjectSourceType(receiverType, projectSourcePaths)
				)
				{
					continue;
				}

				int lookupPosition = memberNamePosition;
				IEnumerable<ISymbol> lookupSymbols = semanticModel.LookupSymbols(
					lookupPosition,
					container: receiverType,
					includeReducedExtensionMethods: false
				);
				IReadOnlyList<CSharpSemanticMemberSymbol> members = CreateMembers(
					receiverType,
					lookupSymbols,
					cancellationToken
				);

				if (members.Count == 0)
					continue;

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
			}

			cancellationToken.ThrowIfCancellationRequested();
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
				CreateExceptionDetail("Unexpected semantic member build failure", exception)
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
		IEnumerable<ISymbol> lookupSymbols,
		CancellationToken cancellationToken
	)
	{
		INamedTypeSymbol receiverDefinition = receiverType.OriginalDefinition;
		var membersByIdentity = new Dictionary<string, MutableMember>(StringComparer.Ordinal);

		foreach (ISymbol symbol in lookupSymbols ?? Array.Empty<ISymbol>())
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (
				symbol == null
				|| symbol.IsStatic
				|| symbol.IsImplicitlyDeclared
				|| string.IsNullOrWhiteSpace(symbol.Name)
				|| symbol.ContainingType == null
				|| !SymbolEqualityComparer.Default.Equals(
					symbol.ContainingType.OriginalDefinition,
					receiverDefinition
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

	private static bool TryGetMemberKind(
		ISymbol symbol,
		out CSharpSemanticMemberKind kind
	)
	{
		switch (symbol)
		{
			case IMethodSymbol method
				when method.MethodKind == MethodKind.Ordinary && method.Arity == 0:
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

	private static bool IsTypeReceiver(ISymbol symbol)
	{
		if (symbol is INamedTypeSymbol)
			return true;

		return symbol is IAliasSymbol alias && alias.Target is INamedTypeSymbol;
	}

	private static bool IsProjectSourceType(
		INamedTypeSymbol receiverType,
		HashSet<string> projectSourcePaths
	)
	{
		INamedTypeSymbol definition = receiverType.OriginalDefinition;

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

	private static string CreateExceptionDetail(string prefix, Exception exception)
	{
		return $"{prefix}: {exception?.GetType().Name ?? "Exception"}: "
			+ NormalizeMessage(exception?.Message);
	}

	private static string NormalizeMessage(string message)
	{
		if (string.IsNullOrWhiteSpace(message))
			return "Unknown error.";

		string normalized = message.Replace('\r', ' ').Replace('\n', ' ').Trim();
		return normalized.Length <= 500 ? normalized : normalized.Substring(0, 500);
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
