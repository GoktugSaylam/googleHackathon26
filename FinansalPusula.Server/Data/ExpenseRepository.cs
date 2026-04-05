using Microsoft.Data.Sqlite;
using FinansalPusula.Services;

namespace FinansalPusula.Server.Data;

public class ExpenseRepository
{
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

        var command = connection.CreateCommand();
        command.CommandText = @"
            CREATE TABLE IF NOT EXISTS ExpenseReports (
                Period TEXT PRIMARY KEY,
                TotalSpending DECIMAL NOT NULL,
                SummaryAdvice TEXT
            );
            CREATE TABLE IF NOT EXISTS ExpenseItems (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                ReportPeriod TEXT NOT NULL,
                Merchant TEXT,
                Date TEXT,
                Amount DECIMAL NOT NULL,
                Category TEXT,
                Donem TEXT,
                FOREIGN KEY (ReportPeriod) REFERENCES ExpenseReports(Period) ON DELETE CASCADE
            );
            CREATE TABLE IF NOT EXISTS SubscriptionItems (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                ReportPeriod TEXT NOT NULL,
                Name TEXT,
                Cost DECIMAL NOT NULL,
                Alternative TEXT,
                SavingsAdvice TEXT,
                FOREIGN KEY (ReportPeriod) REFERENCES ExpenseReports(Period) ON DELETE CASCADE
            );
            CREATE TABLE IF NOT EXISTS UserSettings (
                Id INTEGER PRIMARY KEY,
                MonthlySalary DECIMAL NOT NULL,
                AnnualInflationRate DECIMAL NOT NULL
            );";
        command.ExecuteNonQuery();
    }

    public async Task<List<ExpenseReport>> GetAllAsync()
    {
        var result = new List<ExpenseReport>();
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM ExpenseReports ORDER BY Period DESC";

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
            report.Expenses = await GetExpenseItemsAsync(period, connection);
            report.Subscriptions = await GetSubscriptionItemsAsync(period, connection);

            result.Add(report);
        }
        return result;
    }

    private async Task<List<ExpenseItem>> GetExpenseItemsAsync(string period, SqliteConnection connection)
    {
        var items = new List<ExpenseItem>();
        var command = connection.CreateCommand();
        command.CommandText = "SELECT Merchant, Date, Amount, Category, Donem FROM ExpenseItems WHERE ReportPeriod = $period";
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

    private async Task<List<SubscriptionItem>> GetSubscriptionItemsAsync(string period, SqliteConnection connection)
    {
        var items = new List<SubscriptionItem>();
        var command = connection.CreateCommand();
        command.CommandText = "SELECT Name, Cost, Alternative, SavingsAdvice FROM SubscriptionItems WHERE ReportPeriod = $period";
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

    public async Task AddOrUpdateAsync(ExpenseReport report)
    {
        if (report == null || string.IsNullOrEmpty(report.Period)) return;

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        using var transaction = connection.BeginTransaction();

        try
        {
            // Check if report exists
            var checkCmd = connection.CreateCommand();
            checkCmd.Transaction = transaction;
            checkCmd.CommandText = "SELECT TotalSpending, SummaryAdvice FROM ExpenseReports WHERE Period = $period";
            checkCmd.Parameters.AddWithValue("$period", report.Period);

            decimal existingTotal = 0;
            string existingSummary = "";
            bool exists = false;

            using (var reader = await checkCmd.ExecuteReaderAsync())
            {
                if (await reader.ReadAsync())
                {
                    existingTotal = reader.GetDecimal(0);
                    existingSummary = reader.IsDBNull(1) ? "" : reader.GetString(1);
                    exists = true;
                }
            }

            if (!exists)
            {
                var insertCmd = connection.CreateCommand();
                insertCmd.Transaction = transaction;
                insertCmd.CommandText = "INSERT INTO ExpenseReports (Period, TotalSpending, SummaryAdvice) VALUES ($period, $total, $summary)";
                insertCmd.Parameters.AddWithValue("$period", report.Period);
                insertCmd.Parameters.AddWithValue("$total", report.TotalSpending);
                insertCmd.Parameters.AddWithValue("$summary", report.SummaryAdvice ?? "");
                await insertCmd.ExecuteNonQueryAsync();
            }
            else
            {
                // Update report level info if needed (last summary is usually better)
                var updateCmd = connection.CreateCommand();
                updateCmd.Transaction = transaction;
                updateCmd.CommandText = "UPDATE ExpenseReports SET SummaryAdvice = $summary WHERE Period = $period";
                updateCmd.Parameters.AddWithValue("$period", report.Period);
                updateCmd.Parameters.AddWithValue("$summary", report.SummaryAdvice ?? existingSummary);
                await updateCmd.ExecuteNonQueryAsync();
            }

            // Sync Expenses (avoid duplicates)
            if (report.Expenses != null)
            {
                foreach (var exp in report.Expenses)
                {
                    var dupCmd = connection.CreateCommand();
                    dupCmd.Transaction = transaction;
                    dupCmd.CommandText = "SELECT COUNT(*) FROM ExpenseItems WHERE ReportPeriod = $period AND Merchant = $merchant AND Date = $date AND Amount = $amount";
                    dupCmd.Parameters.AddWithValue("$period", report.Period);
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
                        itemCmd.CommandText = "INSERT INTO ExpenseItems (ReportPeriod, Merchant, Date, Amount, Category, Donem) VALUES ($period, $merchant, $date, $amount, $cat, $donem)";
                        itemCmd.Parameters.AddWithValue("$period", report.Period);
                        itemCmd.Parameters.AddWithValue("$merchant", exp.Merchant ?? "");
                        itemCmd.Parameters.AddWithValue("$date", exp.Date ?? "");
                        itemCmd.Parameters.AddWithValue("$amount", exp.Amount);
                        itemCmd.Parameters.AddWithValue("$cat", exp.Category ?? "");
                        itemCmd.Parameters.AddWithValue("$donem", exp.Donem ?? "");
                        await itemCmd.ExecuteNonQueryAsync();
                        
                        // Increment total spending in ExpenseReports
                        var incCmd = connection.CreateCommand();
                        incCmd.Transaction = transaction;
                        incCmd.CommandText = "UPDATE ExpenseReports SET TotalSpending = TotalSpending + $amount WHERE Period = $period";
                        incCmd.Parameters.AddWithValue("$period", report.Period);
                        incCmd.Parameters.AddWithValue("$amount", exp.Amount);
                        await incCmd.ExecuteNonQueryAsync();
                    }
                }
            }

            // Sync Subscriptions
            if (report.Subscriptions != null)
            {
                foreach (var sub in report.Subscriptions)
                {
                    var dupCmd = connection.CreateCommand();
                    dupCmd.Transaction = transaction;
                    dupCmd.CommandText = "SELECT Id, Cost FROM SubscriptionItems WHERE ReportPeriod = $period AND Name = $name";
                    dupCmd.Parameters.AddWithValue("$period", report.Period);
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
                        itemCmd.CommandText = "INSERT INTO SubscriptionItems (ReportPeriod, Name, Cost, Alternative, SavingsAdvice) VALUES ($period, $name, $cost, $alt, $adv)";
                        itemCmd.Parameters.AddWithValue("$period", report.Period);
                        itemCmd.Parameters.AddWithValue("$name", sub.Name ?? "");
                        itemCmd.Parameters.AddWithValue("$cost", sub.Cost);
                        itemCmd.Parameters.AddWithValue("$alt", sub.Alternative ?? "");
                        itemCmd.Parameters.AddWithValue("$adv", sub.SavingsAdvice ?? "");
                        await itemCmd.ExecuteNonQueryAsync();
                    }
                    else
                    {
                        // Update cost if it changed significantly or just keep it
                        var itemCmd = connection.CreateCommand();
                        itemCmd.Transaction = transaction;
                        itemCmd.CommandText = "UPDATE SubscriptionItems SET Cost = $cost, Alternative = COALESCE(NULLIF($alt, ''), Alternative), SavingsAdvice = COALESCE(NULLIF($adv, ''), SavingsAdvice) WHERE Id = $id";
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

    public async Task<UserSettings> GetSettingsAsync()
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = "SELECT MonthlySalary, AnnualInflationRate FROM UserSettings WHERE Id = 1";

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

    public async Task SaveSettingsAsync(UserSettings settings)
    {
        if (settings == null) return;
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO UserSettings (Id, MonthlySalary, AnnualInflationRate) 
            VALUES (1, $salary, $inflation)
            ON CONFLICT(Id) DO UPDATE SET 
                MonthlySalary = excluded.MonthlySalary, 
                AnnualInflationRate = excluded.AnnualInflationRate";
        
        command.Parameters.AddWithValue("$salary", settings.MonthlySalary);
        command.Parameters.AddWithValue("$inflation", settings.AnnualInflationRate);
        
        await command.ExecuteNonQueryAsync();
    }
}
