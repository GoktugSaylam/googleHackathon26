using Microsoft.Data.Sqlite;

namespace FinansalPusula.Server.Data;

public sealed class GoogleAccountRepository
{
    private readonly string _connectionString;

    public GoogleAccountRepository(IConfiguration configuration)
    {
        _connectionString = "Data Source=portfolio.db";
        InitializeDatabase();
    }

    private void InitializeDatabase()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = @"
            CREATE TABLE IF NOT EXISTS GoogleAccounts (
                GoogleUserId TEXT PRIMARY KEY,
                Email TEXT,
                DisplayName TEXT,
                PictureUrl TEXT,
                CreatedAtUtc TEXT NOT NULL,
                LastLoginAtUtc TEXT NOT NULL,
                LastSeenAtUtc TEXT NOT NULL,
                IsActive INTEGER NOT NULL DEFAULT 1
            );

            CREATE INDEX IF NOT EXISTS IX_GoogleAccounts_Email ON GoogleAccounts(Email);";
        command.ExecuteNonQuery();
    }

    public async Task UpsertAsync(
        string googleUserId,
        string? email,
        string? displayName,
        string? pictureUrl,
        DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(googleUserId))
        {
            throw new ArgumentException("Google user id zorunludur.", nameof(googleUserId));
        }

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO GoogleAccounts (
                GoogleUserId,
                Email,
                DisplayName,
                PictureUrl,
                CreatedAtUtc,
                LastLoginAtUtc,
                LastSeenAtUtc,
                IsActive)
            VALUES (
                $userId,
                $email,
                $displayName,
                $pictureUrl,
                $createdAt,
                $lastLoginAt,
                $lastSeenAt,
                1)
            ON CONFLICT(GoogleUserId) DO UPDATE SET
                Email = excluded.Email,
                DisplayName = excluded.DisplayName,
                PictureUrl = excluded.PictureUrl,
                LastLoginAtUtc = excluded.LastLoginAtUtc,
                LastSeenAtUtc = excluded.LastSeenAtUtc,
                IsActive = 1;";

        var nowText = nowUtc.ToString("o");
        command.Parameters.AddWithValue("$userId", googleUserId);
        command.Parameters.AddWithValue("$email", DbValue(email));
        command.Parameters.AddWithValue("$displayName", DbValue(displayName));
        command.Parameters.AddWithValue("$pictureUrl", DbValue(pictureUrl));
        command.Parameters.AddWithValue("$createdAt", nowText);
        command.Parameters.AddWithValue("$lastLoginAt", nowText);
        command.Parameters.AddWithValue("$lastSeenAt", nowText);

        await command.ExecuteNonQueryAsync();
    }

    public async Task TouchLastSeenAsync(string googleUserId, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(googleUserId))
        {
            return;
        }

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var nowText = nowUtc.ToString("o");

        var updateCommand = connection.CreateCommand();
        updateCommand.CommandText = @"
            UPDATE GoogleAccounts
            SET LastSeenAtUtc = $lastSeenAt,
                IsActive = 1
            WHERE GoogleUserId = $userId;";
        updateCommand.Parameters.AddWithValue("$lastSeenAt", nowText);
        updateCommand.Parameters.AddWithValue("$userId", googleUserId);

        var updatedRows = await updateCommand.ExecuteNonQueryAsync();
        if (updatedRows > 0)
        {
            return;
        }

        var insertCommand = connection.CreateCommand();
        insertCommand.CommandText = @"
            INSERT OR IGNORE INTO GoogleAccounts (
                GoogleUserId,
                CreatedAtUtc,
                LastLoginAtUtc,
                LastSeenAtUtc,
                IsActive)
            VALUES (
                $userId,
                $createdAt,
                $lastLoginAt,
                $lastSeenAt,
                1);";
        insertCommand.Parameters.AddWithValue("$userId", googleUserId);
        insertCommand.Parameters.AddWithValue("$createdAt", nowText);
        insertCommand.Parameters.AddWithValue("$lastLoginAt", nowText);
        insertCommand.Parameters.AddWithValue("$lastSeenAt", nowText);

        await insertCommand.ExecuteNonQueryAsync();
    }

    private static object DbValue(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? DBNull.Value : value.Trim();
    }
}
