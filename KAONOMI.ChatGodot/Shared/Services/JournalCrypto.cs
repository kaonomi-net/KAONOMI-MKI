using System;
using System.Security.Cryptography;
using System.Text;

namespace KAONOMI.Chat.Services;

/// <summary>
/// Encryption of the journal entries of <c>/diary</c> with a password: AES-256 in GCM mode (authenticated: a wrong
/// password or a changed text is detected), the 256-bit key derived from the password with PBKDF2-SHA256 (600,000
/// iterations, random 16-byte salt), a random 12-byte nonce per encryption. The encrypted entry is saved in the
/// <c>text</c> field as <c>**CRYPTED**</c> followed by the encrypted text: <c>AES-256-GCM:v1:salt:nonce:tag:ciphertext</c>
/// (Base64). The password is never saved: without it the entry cannot be read again.
/// </summary>
public static class JournalCrypto
{
    /// <summary>Mark at the start of every encrypted text.</summary>
    public const string Marker = "**CRYPTED**";

    /// <summary>Start of an encrypted text: the mark, then the algorithm and the version of the format.</summary>
    public const string Prefix = Marker + "AES-256-GCM:v1:";

    /// <summary>Start of the texts encrypted by the first version (before the **CRYPTED** mark), still read.</summary>
    private const string LegacyPrefix = "KAONOMI-AES256-GCM:v1:";

    /// <summary>Shortest password accepted.</summary>
    public const int MinPasswordLength = 12;

    private const int SaltSize = 16;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;          // 256 bits
    private const int Iterations = 600_000;  // PBKDF2-SHA256, OWASP 2023 recommendation

    /// <summary>True when <paramref name="text"/> is an encrypted entry (it starts with **CRYPTED**).</summary>
    public static bool IsEncrypted(string? text) =>
        text is not null && (text.StartsWith(Marker, StringComparison.Ordinal) || text.StartsWith(LegacyPrefix, StringComparison.Ordinal));

    /// <summary>Encrypts <paramref name="plainText"/> with <paramref name="password"/> (a fraction of a second: run it off the UI thread).</summary>
    public static string Encrypt(string plainText, string password)
    {
        if (password.Length < MinPasswordLength)
            throw new ArgumentException($"The password must have at least {MinPasswordLength} characters", nameof(password));

        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var key = DeriveKey(password, salt);
        try
        {
            var plain = Encoding.UTF8.GetBytes(plainText);
            var cipher = new byte[plain.Length];
            var tag = new byte[TagSize];
            using (var aes = new AesGcm(key, TagSize))
                aes.Encrypt(nonce, plain, cipher, tag, Encoding.ASCII.GetBytes(Prefix));
            return Prefix + string.Join(':', Convert.ToBase64String(salt), Convert.ToBase64String(nonce),
                Convert.ToBase64String(tag), Convert.ToBase64String(cipher));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>
    /// The text of an encrypted entry; throws <see cref="CryptographicException"/> when the password is wrong (or the
    /// text was changed), <see cref="FormatException"/> when it is not an encrypted entry.
    /// </summary>
    public static string Decrypt(string encrypted, string password)
    {
        var prefix = encrypted.StartsWith(Prefix, StringComparison.Ordinal) ? Prefix
            : encrypted.StartsWith(LegacyPrefix, StringComparison.Ordinal) ? LegacyPrefix
            : throw new FormatException(IsEncrypted(encrypted) ? "Unknown encryption format" : "This entry is not encrypted");
        var parts = encrypted[prefix.Length..].Split(':');
        if (parts.Length != 4)
            throw new FormatException("The encrypted entry is damaged");

        var salt = Convert.FromBase64String(parts[0]);
        var nonce = Convert.FromBase64String(parts[1]);
        var tag = Convert.FromBase64String(parts[2]);
        var cipher = Convert.FromBase64String(parts[3]);
        var key = DeriveKey(password, salt);
        try
        {
            var plain = new byte[cipher.Length];
            using (var aes = new AesGcm(key, TagSize))
                aes.Decrypt(nonce, cipher, tag, plain, Encoding.ASCII.GetBytes(prefix));
            return Encoding.UTF8.GetString(plain);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    // The prefix is the associated data of AES-GCM: a ciphertext cannot be moved to another format version.

    private static byte[] DeriveKey(string password, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA256, KeySize);
}
