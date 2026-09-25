using Dapper;

namespace ComdirectFetch.Data;

/// <summary>
/// Speichert den verschlüsselten comdirect-Session-Token (0006_auth_token_store.sql), um eine
/// Session über Container-Neustarts hinweg wiederherzustellen zu können. Kennt bewusst nichts
/// von OAuthToken oder Verschlüsselung - reiner Byte-Blob-Speicher (nonce/ciphertext/tag),
/// die Verschlüsselung liegt im Worker (siehe ComdirectAuthCoordinator), da Api und Data sich
/// laut Architektur nicht gegenseitig referenzieren dürfen.
/// </summary>
public sealed class AuthTokenRepository(IDbConnectionFactory connectionFactory)
{
    public async Task SaveAsync(byte[] nonce, byte[] ciphertext, byte[] tag, CancellationToken cancellationToken = default)
    {
        using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        const string sql = """
            INSERT INTO auth_token_store (id, nonce, ciphertext, tag, updated_at)
            VALUES (1, @Nonce, @Ciphertext, @Tag, UTC_TIMESTAMP(3))
            ON DUPLICATE KEY UPDATE nonce = @Nonce, ciphertext = @Ciphertext, tag = @Tag, updated_at = UTC_TIMESTAMP(3);
            """;

        await connection.ExecuteAsync(sql, new { Nonce = nonce, Ciphertext = ciphertext, Tag = tag });
    }

    public async Task<StoredAuthToken?> LoadAsync(CancellationToken cancellationToken = default)
    {
        using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        const string sql = """
            SELECT nonce AS Nonce, ciphertext AS Ciphertext, tag AS Tag
            FROM auth_token_store
            WHERE id = 1;
            """;

        return await connection.QueryFirstOrDefaultAsync<StoredAuthToken>(sql);
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        const string sql = "DELETE FROM auth_token_store WHERE id = 1;";
        await connection.ExecuteAsync(sql);
    }
}

/// <summary>Roher, noch verschlüsselter Blob aus auth_token_store - Entschlüsselung passiert im Worker.</summary>
public sealed record StoredAuthToken(byte[] Nonce, byte[] Ciphertext, byte[] Tag);
