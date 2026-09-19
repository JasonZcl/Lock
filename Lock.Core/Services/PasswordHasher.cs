using System.Security.Cryptography;
using System.Text;
using Lock.Core.Models;

namespace Lock.Core.Services;

/// <summary>
/// 使用 PBKDF2-SHA256 保存与校验密码 / 恢复密钥。
/// </summary>
public static class PasswordHasher
{
    private const int SaltSize = 16;
    private const int HashSize = 32;
    private const int DefaultIterations = 100_000;

    public static SecretRecord Create(string secret)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Rfc2898DeriveBytes.Pbkdf2(secret, salt, DefaultIterations, HashAlgorithmName.SHA256, HashSize);

        return new SecretRecord
        {
            Salt = Convert.ToBase64String(salt),
            Hash = Convert.ToBase64String(hash),
            Iterations = DefaultIterations,
        };
    }

    public static bool Verify(SecretRecord? record, string secret)
    {
        if (record == null || record.Iterations <= 0) return false;

        try
        {
            var salt = Convert.FromBase64String(record.Salt);
            var expected = Convert.FromBase64String(record.Hash);
            var actual = Rfc2898DeriveBytes.Pbkdf2(secret, salt, record.Iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

/// <summary>
/// 恢复密钥：形如 ABCDE-FGHIJ-KLMNO-PQRST，去掉了容易混淆的字符。
/// </summary>
public static class RecoveryKey
{
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    private const int Groups = 4;
    private const int GroupLength = 5;

    public static string Generate()
    {
        var sb = new StringBuilder();
        for (var g = 0; g < Groups; g++)
        {
            if (g > 0) sb.Append('-');
            for (var i = 0; i < GroupLength; i++)
                sb.Append(Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)]);
        }
        return sb.ToString();
    }

    /// <summary>去掉分隔符和空白并转大写，方便用户随意输入。</summary>
    public static string Normalize(string input)
    {
        var sb = new StringBuilder(input.Length);
        foreach (var c in input)
        {
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToUpperInvariant(c));
        }
        return sb.ToString();
    }
}
