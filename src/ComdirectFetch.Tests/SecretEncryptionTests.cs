using System.Security.Cryptography;
using System.Text;
using ComdirectFetch.Domain;

namespace ComdirectFetch.Tests;

public class SecretEncryptionTests
{
    private static byte[] MakeKey() => RandomNumberGenerator.GetBytes(SecretEncryption.KeySizeBytes);

    [Fact]
    public void Verschluesselt_und_entschluesselt_denselben_Klartext()
    {
        var key = MakeKey();
        var plaintext = Encoding.UTF8.GetBytes("""{"access_token":"abc","refresh_token":"def"}""");

        var encrypted = SecretEncryption.Encrypt(key, plaintext);
        var decrypted = SecretEncryption.Decrypt(key, encrypted);

        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void Erzeugt_unterschiedliche_Nonces_und_Ciphertexte_bei_wiederholter_Verschluesselung()
    {
        var key = MakeKey();
        var plaintext = Encoding.UTF8.GetBytes("gleicher Klartext");

        var first = SecretEncryption.Encrypt(key, plaintext);
        var second = SecretEncryption.Encrypt(key, plaintext);

        Assert.NotEqual(first.Nonce, second.Nonce);
        Assert.NotEqual(first.Ciphertext, second.Ciphertext);
    }

    [Fact]
    public void Schlaegt_fehl_wenn_falscher_Schluessel_verwendet_wird()
    {
        var encrypted = SecretEncryption.Encrypt(MakeKey(), Encoding.UTF8.GetBytes("geheim"));

        Assert.Throws<AuthenticationTagMismatchException>(() => SecretEncryption.Decrypt(MakeKey(), encrypted));
    }

    [Fact]
    public void Schlaegt_fehl_wenn_Ciphertext_manipuliert_wurde()
    {
        var key = MakeKey();
        var encrypted = SecretEncryption.Encrypt(key, Encoding.UTF8.GetBytes("geheim"));
        var tamperedCiphertext = (byte[])encrypted.Ciphertext.Clone();
        tamperedCiphertext[0] ^= 0xFF;
        var tampered = encrypted with { Ciphertext = tamperedCiphertext };

        Assert.Throws<AuthenticationTagMismatchException>(() => SecretEncryption.Decrypt(key, tampered));
    }

    [Fact]
    public void Lehnt_Schluessel_mit_falscher_Laenge_ab()
    {
        var tooShortKey = RandomNumberGenerator.GetBytes(16);

        Assert.Throws<ArgumentException>(() => SecretEncryption.Encrypt(tooShortKey, Encoding.UTF8.GetBytes("x")));
    }
}
