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
                Tarih TEXT NOT NULL,
                IslemTipi INTEGER NOT NULL,
                Sembol TEXT NOT NULL,
                Adet DECIMAL NOT NULL,
                BirimFiyat DECIMAL NOT NULL
            );";
        command.ExecuteNonQuery();
    }

    public async Task<List<PortfolioTransaction>> GetAllAsync()
    {
        var result = new List<PortfolioTransaction>();
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Transactions ORDER BY Tarih DESC";

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

    public async Task AddAsync(PortfolioTransaction tx)
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO Transactions (Id, Tarih, IslemTipi, Sembol, Adet, BirimFiyat)
            VALUES ($id, $tarih, $tip, $sembol, $adet, $fiyat)";
        
        command.Parameters.AddWithValue("$id", tx.Id ?? Guid.NewGuid().ToString());
        command.Parameters.AddWithValue("$tarih", tx.Tarih.ToString("o"));
        command.Parameters.AddWithValue("$tip", (int)tx.IslemTipi);
        command.Parameters.AddWithValue("$sembol", tx.Sembol);
        command.Parameters.AddWithValue("$adet", tx.Adet);
        command.Parameters.AddWithValue("$fiyat", tx.BirimFiyat);

        await command.ExecuteNonQueryAsync();
    }

    public async Task DeleteAsync(string id)
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM Transactions WHERE Id = $id";
        command.Parameters.AddWithValue("$id", id);

        await command.ExecuteNonQueryAsync();
    }
}
