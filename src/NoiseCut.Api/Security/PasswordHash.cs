using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace NoiseCut.Api.Security;

/// <summary>
/// The admin password is never stored, only a slow salted hash of it: <c>pbkdf2-sha256$&lt;iterations&gt;$&lt;salt&gt;$&lt;hash&gt;</c>.
/// PBKDF2 with 600,000 iterations (the OWASP recommendation for SHA-256) makes each guess deliberately expensive, so a
/// leaked hash can't be brute-forced cheaply. Generate one with: <c>dotnet NoiseCut.Api.dll hash-password</c> (reads stdin).
/// </summary>
public static class PasswordHash
{
    private const string Scheme = "pbkdf2-sha256";
    private const int Iterations = 600_000;

    public static string Create(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA256, 32);
        return string.Create(CultureInfo.InvariantCulture, $"{Scheme}${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}");
    }

    public static bool Verify(string password, string? stored)
    {
        var parts = stored?.Split('$');
        if (parts is not [Scheme, var iterationsText, var saltText, var hashText]
            || !int.TryParse(iterationsText, NumberStyles.None, CultureInfo.InvariantCulture, out var iterations)
            || iterations < 10_000)
        {
            return false;
        }

        byte[] salt, expected;
        try
        {
            salt = Convert.FromBase64String(saltText);
            expected = Convert.FromBase64String(hashText);
        }
        catch (FormatException)
        {
            return false;
        }

        var actual = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
