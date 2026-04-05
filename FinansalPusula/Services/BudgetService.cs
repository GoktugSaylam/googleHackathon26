namespace FinansalPusula.Services;

using System.Net.Http.Json;

public class BudgetService
{
    private readonly HttpClient _http;

    public BudgetService(HttpClient http)
    {
        _http = http;
    }

    public decimal MonthlySalary { get; set; } = 40000;
    public decimal AnnualInflationRate { get; set; } = 64.0m;
    
    public List<ExpenseReport> AllReports { get; set; } = new();

    public ExpenseReport? LastReport => AllReports.OrderByDescending(r => r.Period).FirstOrDefault();

    public decimal CurrentIdleCash { 
        get {
            if (LastReport == null) return MonthlySalary;
            return MonthlySalary - LastReport.TotalSpending;
        }
    }

    public async Task LoadReportsAsync()
    {
        try
        {
            var reports = await _http.GetFromJsonAsync<List<ExpenseReport>>("api/budget/reports");
            if (reports != null)
            {
                AllReports = reports;
            }

            // Ayrıca Ayarları Yükle
            await LoadSettingsAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[BudgetService] Veri yükleme hatası: {ex.Message}");
        }
    }

    public async Task LoadSettingsAsync()
    {
        try
        {
            var settings = await _http.GetFromJsonAsync<UserSettings>("api/settings");
            if (settings != null)
            {
                MonthlySalary = settings.MonthlySalary;
                AnnualInflationRate = settings.AnnualInflationRate;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[BudgetService] Ayar yükleme hatası: {ex.Message}");
        }
    }

    public async Task SaveSettingsAsync()
    {
        try
        {
            var settings = new UserSettings { MonthlySalary = MonthlySalary, AnnualInflationRate = AnnualInflationRate };
            await _http.PostAsJsonAsync("api/settings", settings);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[BudgetService] Ayar kaydetme hatası: {ex.Message}");
        }
    }

    public async Task AddOrUpdateReport(ExpenseReport newReport)
    {
        if (newReport == null) return;

        if (string.IsNullOrEmpty(newReport.Period))
        {
            newReport.Period = DateTime.Now.ToString("MM-yyyy");
        }

        try
        {
            var response = await _http.PostAsJsonAsync("api/budget/reports", newReport);
            if (response.IsSuccessStatusCode)
            {
                // Refresh data from server to get merged state
                await LoadReportsAsync();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[BudgetService] Veri kaydetme hatası: {ex.Message}");
            // Fallback: Local RAM update (temporary)
            UpdateLocalReports(newReport);
        }
    }

    public async Task<bool> ClearReportsAsync()
    {
        try
        {
            var response = await _http.DeleteAsync("api/budget/reports");
            if (response.IsSuccessStatusCode)
            {
                AllReports.Clear();
                return true;
            }

            var errorBody = await response.Content.ReadAsStringAsync();
            Console.WriteLine($"[BudgetService] Veri temizleme hatasi: {(int)response.StatusCode} - {errorBody}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[BudgetService] Veri temizleme hatasi: {ex.Message}");
        }

        return false;
    }

    private void UpdateLocalReports(ExpenseReport newReport)
    {
        var existing = AllReports.FirstOrDefault(r => r.Period == newReport.Period);
        if (existing != null)
        {
            // Simple merge for local fallback
            if (newReport.Expenses != null)
            {
                var existingExpenses = existing.Expenses ??= new List<ExpenseItem>();
                foreach(var exp in newReport.Expenses) 
                {
                    if (!existingExpenses.Any(e => e.Merchant == exp.Merchant && e.Date == exp.Date && e.Amount == exp.Amount))
                        existingExpenses.Add(exp);
                }
            }
            existing.SummaryAdvice = newReport.SummaryAdvice;
        }
        else
        {
            AllReports.Add(newReport);
        }
    }

    public decimal CalculateMonthlyErosion()
    {
        if (CurrentIdleCash <= 0) return 0;
        decimal monthlyInflation = AnnualInflationRate / 12 / 100;
        return CurrentIdleCash * monthlyInflation;
    }

    public decimal CalculateProjectedAnnualLoss()
    {
        return CalculateMonthlyErosion() * 12;
    }
}
