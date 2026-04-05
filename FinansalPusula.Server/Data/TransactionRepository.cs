using Microsoft.Data.Sqlite;
using FinansalPusula.Services;

namespace FinansalPusula.Server.Data;

public class TransactionRepository
{
    private readonly string _connectionString;

    public TransactionRepository(IConfiguration configuration)
    {
        // AppData veya çalıştığı klasörde database oluşturur
        _connectionString = "Data Source=portfolio.db";
        InitializeDatabase();
    }

    private void InitializeDatabase()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        
        var command = connection.CreateCommand();
        command.CommandText = @"
            CREATE TABLE IF NOT EXISTS Transactions (
                Id TEXT PRIMARY KEY,
                GoogleUserId TEXT,
                Tarih TEXT NOT NULL,
                IslemTipi INTEGER NOT NULL,
                Sembol TEXT NOT NULL,
                Adet DECIMAL NOT NULL,
                BirimFiyat DECIMAL NOT NULL
            );";
        command.ExecuteNonQuery();

        EnsureColumnExists(connection, "Transactions", "GoogleUserId", "TEXT");

        var indexCommand = connection.CreateCommand();
        indexCommand.CommandText = "CREATE INDEX IF NOT EXISTS IX_Transactions_GoogleUserId ON Transactions(GoogleUserId);";
        indexCommand.ExecuteNonQuery();
    }

    private static void EnsureColumnExists(SqliteConnection connection, string tableName, string columnName, string columnDefinition)
    {
        var existsCommand = connection.CreateCommand();
        existsCommand.CommandText = $"PRAGMA table_info({tableName});";

        using var reader = existsCommand.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        var alterCommand = connection.CreateCommand();
        alterCommand.CommandText = $"ALTER TABLE {tableName} ADD COLUMN {columnName} {columnDefinition};";
        alterCommand.ExecuteNonQuery();
    }

    public async Task<List<PortfolioTransaction>> GetAllAsync(string googleUserId)
    {
        if (string.IsNullOrWhiteSpace(googleUserId))
        {
            return new List<PortfolioTransaction>();
        }

        var result = new List<PortfolioTransaction>();
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT Id, Tarih, IslemTipi, Sembol, Adet, BirimFiyat
            FROM Transactions
            WHERE GoogleUserId = $userId
            ORDER BY Tarih DESC";
        command.Parameters.AddWithValue("$userId", googleUserId);

        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(new PortfolioTransaction
            {
                Id = reader.GetString(0),
                Tarih = DateTime.Parse(reader.GetString(1)),
                IslemTipi = (TransactionType)reader.GetInt32(2),
                Sembol = reader.GetString(3),
                Adet = reader.GetDecimal(4),
                BirimFiyat = reader.GetDecimal(5)
            });
        }
        return result;
    }

    public async Task AddAsync(PortfolioTransaction tx, string googleUserId)
    {
        if (string.IsNullOrWhiteSpace(googleUserId))
        {
            throw new ArgumentException("Google user id zorunludur.", nameof(googleUserId));
        }

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO Transactions (Id, GoogleUserId, Tarih, IslemTipi, Sembol, Adet, BirimFiyat)
            VALUES ($id, $userId, $tarih, $tip, $sembol, $adet, $fiyat)";
        
        command.Parameters.AddWithValue("$id", tx.Id ?? Guid.NewGuid().ToString());
        command.Parameters.AddWithValue("$userId", googleUserId);
        command.Parameters.AddWithValue("$tarih", tx.Tarih.ToString("o"));
        command.Parameters.AddWithValue("$tip", (int)tx.IslemTipi);
        command.Parameters.AddWithValue("$sembol", tx.Sembol);
        command.Parameters.AddWithValue("$adet", tx.Adet);
        command.Parameters.AddWithValue("$fiyat", tx.BirimFiyat);

        await command.ExecuteNonQueryAsync();
    }

    public async Task DeleteAsync(string id, string googleUserId)
    {
        if (string.IsNullOrWhiteSpace(googleUserId))
        {
            throw new ArgumentException("Google user id zorunludur.", nameof(googleUserId));
        }

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM Transactions WHERE Id = $id AND GoogleUserId = $userId";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$userId", googleUserId);

        await command.ExecuteNonQueryAsync();
    }
}
