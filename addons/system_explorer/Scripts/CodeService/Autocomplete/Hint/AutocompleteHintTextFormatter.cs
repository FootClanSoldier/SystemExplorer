#if TOOLS
#nullable enable annotations
using SystemExplorer.CodeService.Completion;

namespace SystemExplorer.CodeService.Autocomplete.Hint;

internal static class AutocompleteHintTextFormatter
{
	private const int FieldServiceKind = 5;
	private const int VariableServiceKind = 6;
	private const int ConstantServiceKind = 21;
	private const int EventServiceKind = 23;
	private const int ClassServiceKind = 7;
	private const int InterfaceServiceKind = 8;
	private const int NamespaceServiceKind = 9;
	private const int PropertyServiceKind = 10;
	private const int EnumServiceKind = 13;
	private const int KeywordServiceKind = 14;
	private const int StructServiceKind = 22;

	internal static string Format(AutocompleteHintContent content)
	{
		string displayText = content?.DisplayText ?? "";
		if (content == null)
			return displayText;

		if (TryFormatMethod(content.MethodSignatureSet, displayText, out string methodText))
			return methodText;

		return content.ServiceKind switch
		{
			FieldServiceKind => FormatValue(content.ValueType, displayText),
			VariableServiceKind => FormatValue(content.ValueType, displayText),
			ConstantServiceKind => FormatValue(content.ValueType, displayText),
			EventServiceKind => FormatValue(content.ValueType, displayText),
			ClassServiceKind => FormatNamedType("Class", displayText, content.ContainingNamespace),
			InterfaceServiceKind => FormatNamedType("Interface", displayText, content.ContainingNamespace),
			StructServiceKind => FormatNamedType("Struct", displayText, content.ContainingNamespace),
			EnumServiceKind => FormatNamedType("Enum", displayText, content.ContainingNamespace),
			NamespaceServiceKind => "Namespace " + displayText,
			PropertyServiceKind => FormatProperty(
				content.ValueType,
				displayText,
				content.PropertyAccessorSet),
			KeywordServiceKind => displayText + " Keyword",
			_ => displayText,
		};
	}

	private static bool TryFormatMethod(
		CodeServiceCompletionMethodSignatureSet? methodSignatureSet,
		string displayText,
		out string text)
	{
		text = "";
		if (methodSignatureSet?.Signatures == null
			|| methodSignatureSet.Signatures.Count == 0)
		{
			return false;
		}

		CodeServiceCompletionMethodSignature signature = methodSignatureSet.Signatures[0];
		if (signature == null)
			return false;

		text = FormatMethod(signature, displayText);
		int overloadCount = methodSignatureSet.TotalCount - 1;
		if (overloadCount == 1)
			text += " (+1 overload)";
		else if (overloadCount > 1)
			text += $" (+{overloadCount} overloads)";

		return true;
	}

	private static string FormatMethod(
		CodeServiceCompletionMethodSignature signature,
		string displayText)
	{
		if (string.IsNullOrEmpty(signature.ReturnType)
			|| !TryGetMethodReturnTypeDisplay(signature.DisplayText, displayText, out string returnTypeDisplay))
		{
			return displayText;
		}

		return FormatValue(returnTypeDisplay, displayText);
	}

	private static bool TryGetMethodReturnTypeDisplay(
		string signatureDisplayText,
		string completionDisplayText,
		out string returnTypeDisplay)
	{
		returnTypeDisplay = "";
		if (string.IsNullOrEmpty(signatureDisplayText))
			return false;

		int signatureParameterListStart = FindOuterParameterListStart(signatureDisplayText);
		if (signatureParameterListStart <= 0)
			return false;

		string signatureHead = signatureDisplayText.Substring(0, signatureParameterListStart).TrimEnd();
		int completionParameterListStart = FindOuterParameterListStart(completionDisplayText);
		if (completionParameterListStart > 0)
		{
			string completionHead = completionDisplayText.Substring(0, completionParameterListStart).TrimEnd();
			if (!string.IsNullOrEmpty(completionHead)
				&& signatureHead.EndsWith(completionHead, System.StringComparison.Ordinal))
			{
				int returnTypeLength = signatureHead.Length - completionHead.Length;
				if (returnTypeLength > 0
					&& char.IsWhiteSpace(signatureHead[returnTypeLength - 1]))
				{
					returnTypeDisplay = signatureHead.Substring(0, returnTypeLength).TrimEnd();
					return returnTypeDisplay.Length != 0;
				}
			}
		}

		int separatorIndex = signatureHead.Length - 1;
		while (separatorIndex >= 0 && !char.IsWhiteSpace(signatureHead[separatorIndex]))
			separatorIndex--;

		if (separatorIndex <= 0)
			return false;

		returnTypeDisplay = signatureHead.Substring(0, separatorIndex).TrimEnd();
		return returnTypeDisplay.Length != 0;
	}

	private static int FindOuterParameterListStart(string displayText)
	{
		if (string.IsNullOrEmpty(displayText))
			return -1;

		int depth = 0;
		for (int index = displayText.Length - 1; index >= 0; index--)
		{
			char current = displayText[index];
			if (current == ')')
			{
				depth++;
			}
			else if (current == '(')
			{
				if (depth == 0)
					continue;

				depth--;
				if (depth == 0)
					return index;
			}
		}

		return -1;
	}

	private static string FormatNamedType(
		string kindName,
		string displayText,
		string? containingNamespace)
	{
		string firstLine = kindName + " " + displayText;
		return string.IsNullOrEmpty(containingNamespace)
			? firstLine
			: firstLine + "\n" + containingNamespace;
	}

	private static string FormatValue(string? valueType, string displayText)
	{
		return string.IsNullOrEmpty(valueType)
			? displayText
			: valueType + " " + displayText;
	}

	private static string FormatProperty(
		string? valueType,
		string displayText,
		CodeServiceCompletionPropertyAccessorSet? propertyAccessorSet)
	{
		string propertyHead = FormatValue(valueType, displayText);

		if (!TryFormatPropertyAccessorSet(propertyAccessorSet, out string accessorText))
			return propertyHead;

		return propertyHead + " " + accessorText;
	}

	private static bool TryFormatPropertyAccessorSet(
		CodeServiceCompletionPropertyAccessorSet? propertyAccessorSet,
		out string text)
	{
		text = "";
		if (propertyAccessorSet == null
			|| !IsValidAccessibility(propertyAccessorSet.PropertyAccessibility))
		{
			return false;
		}

		if (!TryFormatPropertyAccessor(
			propertyAccessorSet.Getter,
			propertyAccessorSet.PropertyAccessibility,
			isGetter: true,
			out string getterText)
			|| !TryFormatPropertyAccessor(
				propertyAccessorSet.Setter,
				propertyAccessorSet.PropertyAccessibility,
				isGetter: false,
				out string setterText))
		{
			return false;
		}

		if (getterText.Length == 0 && setterText.Length == 0)
			return false;

		if (getterText.Length != 0 && setterText.Length != 0)
			text = "{ " + getterText + " " + setterText + " }";
		else
			text = "{ " + (getterText.Length != 0 ? getterText : setterText) + " }";

		return true;
	}

	private static bool TryFormatPropertyAccessor(
		CodeServiceCompletionPropertyAccessor? accessor,
		string propertyAccessibility,
		bool isGetter,
		out string text)
	{
		text = "";
		if (accessor == null)
			return true;
		if (string.IsNullOrEmpty(accessor.Kind)
			|| !IsValidAccessibility(accessor.Accessibility))
		{
			return false;
		}

		bool kindValid = isGetter
			? string.Equals(accessor.Kind, "get", System.StringComparison.Ordinal)
			: string.Equals(accessor.Kind, "set", System.StringComparison.Ordinal)
				|| string.Equals(accessor.Kind, "init", System.StringComparison.Ordinal);
		if (!kindValid)
			return false;

		string prefix = string.Equals(
			accessor.Accessibility,
			propertyAccessibility,
			System.StringComparison.Ordinal)
			? ""
			: accessor.Accessibility + " ";

		text = prefix + accessor.Kind + ";";
		return true;
	}

	private static bool IsValidAccessibility(string value)
	{
		return value == "private"
			|| value == "private protected"
			|| value == "protected"
			|| value == "internal"
			|| value == "protected internal"
			|| value == "public";
	}
}
#endif
