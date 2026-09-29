using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Pos.SharedKernel.Security;

/// <summary>
/// Secretos aleatorios de la aplicación: tokens de sesión y credenciales de equipo (256 bits) y códigos numéricos de un
/// solo uso. En la BD solo se guarda su SHA-256 (D3-01).
/// </summary>
public static class SecureTokens
{
    public static string Create() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string Hash(string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    }

    /// <summary>Compara un secreto con su hash en tiempo constante.</summary>
    public static bool Matches(string token, string expectedHash)
    {
        ArgumentNullException.ThrowIfNull(expectedHash);
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Hash(token)), Encoding.ASCII.GetBytes(expectedHash));
    }

    /// <summary>Código numérico uniforme (p. ej. emparejamiento de equipos: 6 dígitos).</summary>
    public static string NumericCode(int digits = 6) =>
        string.Concat(Enumerable.Range(0, digits).Select(_ => RandomNumberGenerator.GetInt32(10).ToString(CultureInfo.InvariantCulture)));
}
