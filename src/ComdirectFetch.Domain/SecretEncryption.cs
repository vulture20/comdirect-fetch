using System.Security.Cryptography;

namespace ComdirectFetch.Domain;

/// <summary>
/// Reine AES-256-GCM-Verschlüsselung ohne jede Abhängigkeit auf Api/Data, damit sie sowohl
/// vom persistenten Token-Speicher (Data) als auch vom Auth-Koordinator (Worker) genutzt
/// werden kann. Kennt nichts von OAuthToken oder der DB - reine Byte-Ein-/Ausgabe.
/// </summary>
public static class SecretEncryption
{
    public const int KeySizeBytes = 32;
    private const int NonceSizeBytes = 12;
    private const int TagSizeBytes = 16;

    public static EncryptedSecret Encrypt(byte[] key, byte[] plaintext)
    {
        if (key.Length != KeySizeBytes)
        {
            throw new ArgumentException($"Schlüssel muss {KeySizeBytes} Bytes lang sein (AES-256).", nameof(key));
        }

        var nonce = RandomNumberGenerator.GetBytes(NonceSizeBytes);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSizeBytes];

        using var aesGcm = new AesGcm(key, TagSizeBytes);
        aesGcm.Encrypt(nonce, plaintext, ciphertext, tag);

        return new EncryptedSecret(nonce, ciphertext, tag);
    }

    public static byte[] Decrypt(byte[] key, EncryptedSecret secret)
    {
        if (key.Length != KeySizeBytes)
        {
            throw new ArgumentException($"Schlüssel muss {KeySizeBytes} Bytes lang sein (AES-256).", nameof(key));
        }

        var plaintext = new byte[secret.Ciphertext.Length];
        using var aesGcm = new AesGcm(key, TagSizeBytes);
        aesGcm.Decrypt(secret.Nonce, secret.Ciphertext, secret.Tag, plaintext);
        return plaintext;
    }
}

/// <summary>Ergebnis einer AES-GCM-Verschlüsselung: Nonce und Auth-Tag werden getrennt vom Ciphertext gehalten, wie es das DB-Schema (nonce/ciphertext/tag-Spalten) erwartet.</summary>
public sealed record EncryptedSecret(byte[] Nonce, byte[] Ciphertext, byte[] Tag);
