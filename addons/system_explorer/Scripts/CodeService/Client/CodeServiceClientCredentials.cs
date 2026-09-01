#if TOOLS
using System;
using System.Security.Cryptography;

namespace SystemExplorer.CodeService.Client;

internal sealed class CodeServiceClientCredentials : IDisposable
{
	private byte[] _authenticationToken;

	private CodeServiceClientCredentials(byte[] authenticationToken)
	{
		_authenticationToken = authenticationToken;
	}

	internal static bool TryCreateFromBase64(
		string encodedToken,
		out CodeServiceClientCredentials credentials
	)
	{
		credentials = null;
		if (
			string.IsNullOrEmpty(encodedToken)
			|| encodedToken.Length != CodeServiceClientProtocol.AuthenticationTokenBase64Length
		)
		{
			return false;
		}

		byte[] token = new byte[CodeServiceClientProtocol.AuthenticationTokenByteCount];
		try
		{
			if (
				!Convert.TryFromBase64String(encodedToken, token, out int bytesWritten)
				|| bytesWritten != CodeServiceClientProtocol.AuthenticationTokenByteCount
			)
			{
				CryptographicOperations.ZeroMemory(token);
				return false;
			}

			credentials = new CodeServiceClientCredentials(token);
			return true;
		}
		catch
		{
			CryptographicOperations.ZeroMemory(token);
			return false;
		}
	}

	internal string CreateBearerAuthorizationValue()
	{
		byte[] token = _authenticationToken
			?? throw new ObjectDisposedException(nameof(CodeServiceClientCredentials));
		return "Bearer " + Convert.ToBase64String(token);
	}

	public void Dispose()
	{
		byte[] token = System.Threading.Interlocked.Exchange(ref _authenticationToken, null);
		if (token != null)
			CryptographicOperations.ZeroMemory(token);
	}
}
#endif
