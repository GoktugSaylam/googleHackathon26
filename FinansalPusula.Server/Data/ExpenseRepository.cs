using Microsoft.Data.Sqlite;
using FinansalPusula.Services;

namespace FinansalPusula.Server.Data;

public class ExpenseRepository
{
    private const string ReportsTable = "ExpenseReportsScoped";
    private const string ExpenseItemsTable = "ExpenseItemsScoped";
    private const string SubscriptionItemsTable = "SubscriptionItemsScoped";
    private const string SettingsTable = "UserSettingsScoped";

    private readonly string _connectionString;

    public ExpenseRepository(IConfiguration configuration)
    {
        _connectionString = "Data Source=portfolio.db";
        InitializeDatabase();
    }

    private void InitializeDatabase()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var pragmaCommand = connection.CreateCommand();
        pragmaCommand.CommandText = "PRAGMA foreign_keys = ON;";
        pragmaCommand.ExecuteNonQuery();

        var command = connection.CreateCommand();
        command.CommandText = $@"
            CREATE TABLE IF NOT EXISTS {ReportsTable} (
                GoogleUserId TEXT NOT NULL,
                Period TEXT NOT NULL,
                TotalSpending DECIMAL NOT NULL,
                SummaryAdvice TEXT,
                PRIMARY KEY (GoogleUserId, Period)
            );

            CREATE TABLE IF NOT EXISTS {ExpenseItemsTable} (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                GoogleUserId TEXT NOT NULL,
                ReportPeriod TEXT NOT NULL,
                Merchant TEXT,
                Date TEXT,
                Amount DECIMAL NOT NULL,
                Category TEXT,
                Donem TEXT,
                FOREIGN KEY (GoogleUserId, ReportPeriod) REFERENCES {ReportsTable}(GoogleUserId, Period) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS {SubscriptionItemsTable} (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                GoogleUserId TEXT NOT NULL,
                ReportPeriod TEXT NOT NULL,
                Name TEXT,
                Cost DECIMAL NOT NULL,
                Alternative TEXT,
                SavingsAdvice TEXT,
                FOREIGN KEY (GoogleUserId, ReportPeriod) REFERENCES {ReportsTable}(GoogleUserId, Period) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS {SettingsTable} (
                GoogleUserId TEXT PRIMARY KEY,
                MonthlySalary DECIMAL NOT NULL,
                AnnualInflationRate DECIMAL NOT NULL
            );

            CREATE INDEX IF NOT EXISTS IX_{ExpenseItemsTable}_UserPeriod ON {ExpenseItemsTable}(GoogleUserId, ReportPeriod);
            CREATE INDEX IF NOT EXISTS IX_{SubscriptionItemsTable}_UserPeriod ON {SubscriptionItemsTable}(GoogleUserId, ReportPeriod);";
        command.ExecuteNonQuery();
    }

    private async Task<SqliteConnection> OpenConnectionAsync()
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var pragmaCommand = connection.CreateCommand();
        pragmaCommand.CommandText = "PRAGMA foreign_keys = ON;";
        await pragmaCommand.ExecuteNonQueryAsync();

        return connection;
    }

    public async Task<List<ExpenseReport>> GetAllAsync(string googleUserId)
    {
        if (string.IsNullOrWhiteSpace(googleUserId))
        {
            return new List<ExpenseReport>();
        }

        var result = new List<ExpenseReport>();
        using var connection = await OpenConnectionAsync();

        var command = connection.CreateCommand();
        command.CommandText = $@"
            SELECT Period, TotalSpending, SummaryAdvice
            FROM {ReportsTable}
            WHERE GoogleUserId = $userId
            ORDER BY Period DESC";
        command.Parameters.AddWithValue("$userId", googleUserId);

        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var period = reader.GetString(0);
            var report = new ExpenseReport
            {
                Period = period,
                TotalSpending = reader.GetDecimal(1),
                SummaryAdvice = reader.IsDBNull(2) ? "" : reader.GetString(2),
                Expenses = new List<ExpenseItem>(),
                Subscriptions = new List<SubscriptionItem>()
            };

            // Load Items
            report.Expenses = await GetExpenseItemsAsync(period, googleUserId, connection);
            report.Subscriptions = await GetSubscriptionItemsAsync(period, googleUserId, connection);

            result.Add(report);
        }
        return result;
    }

    private static async Task<List<ExpenseItem>> GetExpenseItemsAsync(string period, string googleUserId, SqliteConnection connection)
    {
        var items = new List<ExpenseItem>();
        var command = connection.CreateCommand();
        command.CommandText = $@"
            SELECT Merchant, Date, Amount, Category, Donem
            FROM {ExpenseItemsTable}
            WHERE GoogleUserId = $userId AND ReportPeriod = $period";
        command.Parameters.AddWithValue("$userId", googleUserId);
        command.Parameters.AddWithValue("$period", period);

        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            items.Add(new ExpenseItem
            {
                Merchant = reader.IsDBNull(0) ? "" : reader.GetString(0),
                Date = reader.IsDBNull(1) ? "" : reader.GetString(1),
                Amount = reader.GetDecimal(2),
                Category = reader.IsDBNull(3) ? "" : reader.GetString(3),
                Donem = reader.IsDBNull(4) ? "" : reader.GetString(4)
            });
        }
        return items;
    }

    private static async Task<List<SubscriptionItem>> GetSubscriptionItemsAsync(string period, string googleUserId, SqliteConnection connection)
    {
        var items = new List<SubscriptionItem>();
        var command = connection.CreateCommand();
        command.CommandText = $@"
            SELECT Name, Cost, Alternative, SavingsAdvice
            FROM {SubscriptionItemsTable}
            WHERE GoogleUserId = $userId AND ReportPeriod = $period";
        command.Parameters.AddWithValue("$userId", googleUserId);
        command.Parameters.AddWithValue("$period", period);

        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            items.Add(new SubscriptionItem
            {
                Name = reader.IsDBNull(0) ? "" : reader.GetString(0),
                Cost = reader.GetDecimal(1),
                Alternative = reader.IsDBNull(2) ? "" : reader.GetString(2),
                SavingsAdvice = reader.IsDBNull(3) ? "" : reader.GetString(3)
            });
        }
        return items;
    }

    public async Task AddOrUpdateAsync(ExpenseReport report, string googleUserId)
    {
        if (string.IsNullOrWhiteSpace(googleUserId))
        {
            throw new ArgumentException("Google user id zorunludur.", nameof(googleUserId));
        }

        if (report == null || string.IsNullOrWhiteSpace(report.Period))
        {
            return;
        }

        var normalizedPeriod = report.Period.Trim();

        using var connection = await OpenConnectionAsync();
        using var transaction = connection.BeginTransaction();

        try
        {
            var checkCmd = connection.CreateCommand();
            checkCmd.Transaction = transaction;
            checkCmd.CommandText = $@"
                SELECT SummaryAdvice
                FROM {ReportsTable}
                WHERE GoogleUserId = $userId AND Period = $period";
            checkCmd.Parameters.AddWithValue("$userId", googleUserId);
            checkCmd.Parameters.AddWithValue("$period", normalizedPeriod);

            bool exists = false;
            string existingSummary = "";

            using (var reader = await checkCmd.ExecuteReaderAsync())
            {
                if (await reader.ReadAsync())
                {
                    existingSummary = reader.IsDBNull(0) ? "" : reader.GetString(0);
                    exists = true;
                }
            }

            if (!exists)
            {
                var initialTotal = report.Expenses is { Count: > 0 } ? 0m : report.TotalSpending;

                var insertCmd = connection.CreateCommand();
                insertCmd.Transaction = transaction;
                insertCmd.CommandText = $@"
                    INSERT INTO {ReportsTable} (GoogleUserId, Period, TotalSpending, SummaryAdvice)
                    VALUES ($userId, $period, $total, $summary)";
                insertCmd.Parameters.AddWithValue("$userId", googleUserId);
                insertCmd.Parameters.AddWithValue("$period", normalizedPeriod);
                insertCmd.Parameters.AddWithValue("$total", initialTotal);
                insertCmd.Parameters.AddWithValue("$summary", report.SummaryAdvice ?? "");
                await insertCmd.ExecuteNonQueryAsync();
            }
            else
            {
                var updateCmd = connection.CreateCommand();
                updateCmd.Transaction = transaction;
                updateCmd.CommandText = $@"
                    UPDATE {ReportsTable}
                    SET SummaryAdvice = $summary
                    WHERE GoogleUserId = $userId AND Period = $period";
                updateCmd.Parameters.AddWithValue("$userId", googleUserId);
                updateCmd.Parameters.AddWithValue("$period", normalizedPeriod);
                updateCmd.Parameters.AddWithValue("$summary", report.SummaryAdvice ?? existingSummary);
                await updateCmd.ExecuteNonQueryAsync();
            }

            if (report.Expenses != null)
            {
                foreach (var exp in report.Expenses)
                {
                    var dupCmd = connection.CreateCommand();
                    dupCmd.Transaction = transaction;
                    dupCmd.CommandText = $@"
                        SELECT COUNT(*)
                        FROM {ExpenseItemsTable}
                        WHERE GoogleUserId = $userId
                          AND ReportPeriod = $period
                          AND Merchant = $merchant
                          AND Date = $date
                          AND Amount = $amount";
                    dupCmd.Parameters.AddWithValue("$userId", googleUserId);
                    dupCmd.Parameters.AddWithValue("$period", normalizedPeriod);
                    dupCmd.Parameters.AddWithValue("$merchant", exp.Merchant ?? "");
                    dupCmd.Parameters.AddWithValue("$date", exp.Date ?? "");
                    dupCmd.Parameters.AddWithValue("$amount", exp.Amount);

                    var scalarCount = await dupCmd.ExecuteScalarAsync();
                    var count = scalarCount is null or DBNull
                        ? 0L
                        : Convert.ToInt64(scalarCount);
                    if (count == 0)
                    {
                        var itemCmd = connection.CreateCommand();
                        itemCmd.Transaction = transaction;
                        itemCmd.CommandText = $@"
                            INSERT INTO {ExpenseItemsTable} (GoogleUserId, ReportPeriod, Merchant, Date, Amount, Category, Donem)
                            VALUES ($userId, $period, $merchant, $date, $amount, $cat, $donem)";
                        itemCmd.Parameters.AddWithValue("$userId", googleUserId);
                        itemCmd.Parameters.AddWithValue("$period", normalizedPeriod);
                        itemCmd.Parameters.AddWithValue("$merchant", exp.Merchant ?? "");
                        itemCmd.Parameters.AddWithValue("$date", exp.Date ?? "");
                        itemCmd.Parameters.AddWithValue("$amount", exp.Amount);
                        itemCmd.Parameters.AddWithValue("$cat", exp.Category ?? "");
                        itemCmd.Parameters.AddWithValue("$donem", exp.Donem ?? "");
                        await itemCmd.ExecuteNonQueryAsync();
                    }
                }

                var recalcTotalCmd = connection.CreateCommand();
                recalcTotalCmd.Transaction = transaction;
                recalcTotalCmd.CommandText = $@"
                    UPDATE {ReportsTable}
                    SET TotalSpending = COALESCE((
                        SELECT SUM(Amount)
                        FROM {ExpenseItemsTable}
                        WHERE GoogleUserId = $userId AND ReportPeriod = $period
                    ), 0)
                    WHERE GoogleUserId = $userId AND Period = $period";
                recalcTotalCmd.Parameters.AddWithValue("$userId", googleUserId);
                recalcTotalCmd.Parameters.AddWithValue("$period", normalizedPeriod);
                await recalcTotalCmd.ExecuteNonQueryAsync();
            }

            if (report.Subscriptions != null)
            {
                foreach (var sub in report.Subscriptions)
                {
                    var dupCmd = connection.CreateCommand();
                    dupCmd.Transaction = transaction;
                    dupCmd.CommandText = $@"
                        SELECT Id
                        FROM {SubscriptionItemsTable}
                        WHERE GoogleUserId = $userId
                          AND ReportPeriod = $period
                          AND Name = $name";
                    dupCmd.Parameters.AddWithValue("$userId", googleUserId);
                    dupCmd.Parameters.AddWithValue("$period", normalizedPeriod);
                    dupCmd.Parameters.AddWithValue("$name", sub.Name ?? "");

                    long? existingId = null;
                    using (var reader = await dupCmd.ExecuteReaderAsync())
                    {
                        if (await reader.ReadAsync())
                        {
                            existingId = reader.GetInt64(0);
                        }
                    }

                    if (existingId == null)
                    {
                        var itemCmd = connection.CreateCommand();
                        itemCmd.Transaction = transaction;
                        itemCmd.CommandText = $@"
                            INSERT INTO {SubscriptionItemsTable} (GoogleUserId, ReportPeriod, Name, Cost, Alternative, SavingsAdvice)
                            VALUES ($userId, $period, $name, $cost, $alt, $adv)";
                        itemCmd.Parameters.AddWithValue("$userId", googleUserId);
                        itemCmd.Parameters.AddWithValue("$period", normalizedPeriod);
                        itemCmd.Parameters.AddWithValue("$name", sub.Name ?? "");
                        itemCmd.Parameters.AddWithValue("$cost", sub.Cost);
                        itemCmd.Parameters.AddWithValue("$alt", sub.Alternative ?? "");
                        itemCmd.Parameters.AddWithValue("$adv", sub.SavingsAdvice ?? "");
                        await itemCmd.ExecuteNonQueryAsync();
                    }
                    else
                    {
                        var itemCmd = connection.CreateCommand();
                        itemCmd.Transaction = transaction;
                        itemCmd.CommandText = $@"
                            UPDATE {SubscriptionItemsTable}
                            SET Cost = $cost,
                                Alternative = COALESCE(NULLIF($alt, ''), Alternative),
                                SavingsAdvice = COALESCE(NULLIF($adv, ''), SavingsAdvice)
                            WHERE Id = $id";
                        itemCmd.Parameters.AddWithValue("$id", existingId);
                        itemCmd.Parameters.AddWithValue("$cost", sub.Cost);
                        itemCmd.Parameters.AddWithValue("$alt", sub.Alternative ?? "");
                        itemCmd.Parameters.AddWithValue("$adv", sub.SavingsAdvice ?? "");
                        await itemCmd.ExecuteNonQueryAsync();
                    }
                }
            }

            await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    public async Task ClearReportsAsync(string googleUserId)
    {
        if (string.IsNullOrWhiteSpace(googleUserId))
        {
            throw new ArgumentException("Google user id zorunludur.", nameof(googleUserId));
        }

        using var connection = await OpenConnectionAsync();

        var command = connection.CreateCommand();
        command.CommandText = $@"
            DELETE FROM {ReportsTable}
            WHERE GoogleUserId = $userId";
        command.Parameters.AddWithValue("$userId", googleUserId);

        await command.ExecuteNonQueryAsync();
    }

    public async Task<UserSettings> GetSettingsAsync(string googleUserId)
    {
        if (string.IsNullOrWhiteSpace(googleUserId))
        {
            return new UserSettings { MonthlySalary = 40000, AnnualInflationRate = 64.0m };
        }

        using var connection = await OpenConnectionAsync();

        var command = connection.CreateCommand();
        command.CommandText = $@"
            SELECT MonthlySalary, AnnualInflationRate
            FROM {SettingsTable}
            WHERE GoogleUserId = $userId";
        command.Parameters.AddWithValue("$userId", googleUserId);

        using var reader = await command.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            return new UserSettings
            {
                MonthlySalary = reader.GetDecimal(0),
                AnnualInflationRate = reader.GetDecimal(1)
            };
        }
        return new UserSettings { MonthlySalary = 40000, AnnualInflationRate = 64.0m };
    }

    public async Task SaveSettingsAsync(UserSettings settings, string googleUserId)
    {
        if (settings == null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(googleUserId))
        {
            throw new ArgumentException("Google user id zorunludur.", nameof(googleUserId));
        }

        using var connection = await OpenConnectionAsync();

        var command = connection.CreateCommand();
        command.CommandText = $@"
            INSERT INTO {SettingsTable} (GoogleUserId, MonthlySalary, AnnualInflationRate)
            VALUES ($userId, $salary, $inflation)
            ON CONFLICT(GoogleUserId) DO UPDATE SET 
                MonthlySalary = excluded.MonthlySalary, 
                AnnualInflationRate = excluded.AnnualInflationRate";

        command.Parameters.AddWithValue("$userId", googleUserId);
        command.Parameters.AddWithValue("$salary", settings.MonthlySalary);
        command.Parameters.AddWithValue("$inflation", settings.AnnualInflationRate);

        await command.ExecuteNonQueryAsync();
    }
}
