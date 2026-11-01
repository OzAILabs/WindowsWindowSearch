using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace WindowSearcher.Core;

/// <summary>
/// The keys that encrypt the index and saved previews. One random master key per user is stored wrapped by
/// Windows (DPAPI), so only this Windows account can unwrap it. The separate keys are derived from it.
/// </summary>
public sealed class DataKeys
{
    private const int KeyBytes = 32;
    private const int NonceBytes = 12;
    private const int TagBytes = 16;
    private static readonly byte[] Entropy = "WindowSearcher.DataKeys"u8.ToArray();

    private readonly byte[] _fileKey;

    public DataKeys(byte[] master, bool isNew = false)
    {
        IsNew = isNew;
        DatabaseKey = Convert.ToBase64String(HKDF.DeriveKey(HashAlgorithmName.SHA256, master, KeyBytes, info: "database"u8.ToArray()));
        _fileKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, master, KeyBytes, info: "files"u8.ToArray());
    }

    /// <summary>The key was just created, so nothing saved earlier can be read with it.</summary>
    public bool IsNew { get; }

    /// <summary>Passphrase for <see cref="SearchIndex"/>.</summary>
    public string DatabaseKey { get; }

    /// <summary>Loads this user's key, or creates one if there's none or it can't be unwrapped (another account or PC, or damaged).</summary>
    [SupportedOSPlatform("windows")]
    public static DataKeys LoadOrCreate(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var master = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);
                if (master.Length == KeyBytes) return new DataKeys(master);
            }
        }
        catch (CryptographicException)
        {
            // Falls through to a new key; the index is a cache and gets rebuilt.
        }
        var created = RandomNumberGenerator.GetBytes(KeyBytes);
        File.WriteAllBytes(path, ProtectedData.Protect(created, Entropy, DataProtectionScope.CurrentUser));
        return new DataKeys(created, isNew: true);
    }

    /// <summary>Encrypts data for a file: nonce, then authentication tag, then ciphertext.</summary>
    public byte[] Seal(ReadOnlySpan<byte> plain)
    {
        var sealedData = new byte[NonceBytes + TagBytes + plain.Length];
        var nonce = sealedData.AsSpan(0, NonceBytes);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(_fileKey, TagBytes);
        aes.Encrypt(nonce, plain, sealedData.AsSpan(NonceBytes + TagBytes), sealedData.AsSpan(NonceBytes, TagBytes));
        return sealedData;
    }

    /// <summary>Decrypts what <see cref="Seal"/> produced. Throws <see cref="CryptographicException"/> if it's damaged or used another key.</summary>
    public byte[] Open(ReadOnlySpan<byte> sealedData)
    {
        if (sealedData.Length < NonceBytes + TagBytes) throw new CryptographicException("Not an encrypted file");
        var plain = new byte[sealedData.Length - NonceBytes - TagBytes];
        using var aes = new AesGcm(_fileKey, TagBytes);
        aes.Decrypt(sealedData[..NonceBytes], sealedData[(NonceBytes + TagBytes)..], sealedData.Slice(NonceBytes, TagBytes), plain);
        return plain;
    }
}
