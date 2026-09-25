using Dapper;

namespace ComdirectFetch.Data;

/// <summary>
/// Speichert die verschlüsselte comdirect-Zugangsnummer/PIN (0007_credential_store.sql,
/// docs/konzept.md Abschnitt 10 B). Kennt bewusst nichts von Username/Password oder
/// Verschlüsselung - reiner Byte-Blob-Speicher (nonce/ciphertext/tag), analog zu
/// AuthTokenRepository, aber eine eigene Tabelle mit eigenem Schlüssel, da es sich um ein
/// anderes Geheimnis mit anderem Bedrohungsmodell handelt.
/// </summary>
public sealed class CredentialRepository(IDbConnectionFactory connectionFactory)
{
    public async Task SaveAsync(byte[] nonce, byte[] ciphertext, byte[] tag, CancellationToken cancellationToken = default)
    {
        using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        const string sql = """
            INSERT INTO credential_store (id, nonce, ciphertext, tag, updated_at)
            VALUES (1, @Nonce, @Ciphertext, @Tag, UTC_TIMESTAMP(3))
            ON DUPLICATE KEY UPDATE nonce = @Nonce, ciphertext = @Ciphertext, tag = @Tag, updated_at = UTC_TIMESTAMP(3);
            """;

        await connection.ExecuteAsync(sql, new { Nonce = nonce, Ciphertext = ciphertext, Tag = tag });
    }

    public async Task<StoredCredential?> LoadAsync(CancellationToken cancellationToken = default)
    {
        using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        const string sql = """
            SELECT nonce AS Nonce, ciphertext AS Ciphertext, tag AS Tag
            FROM credential_store
            WHERE id = 1;
            """;

        return await connection.QueryFirstOrDefaultAsync<StoredCredential>(sql);
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        const string sql = "DELETE FROM credential_store WHERE id = 1;";
        await connection.ExecuteAsync(sql);
    }
}

/// <summary>Roher, noch verschlüsselter Blob aus credential_store - Entschlüsselung passiert im Worker.</summary>
public sealed record StoredCredential(byte[] Nonce, byte[] Ciphertext, byte[] Tag);
