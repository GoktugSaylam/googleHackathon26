namespace FinansalPusula.Services;

public class BudgetService
{
    public decimal MonthlySalary { get; set; } = 40000; // Mock salary
    public decimal AnnualInflationRate { get; set; } = 64.0m; // Mock inflation
    
    // Geçmiş ekstreleri saklayan liste
    public List<ExpenseReport> AllReports { get; set; } = new();

    public ExpenseReport? LastReport => AllReports.OrderByDescending(r => r.Period).FirstOrDefault();

    public decimal CurrentIdleCash { 
        get {
            if (LastReport == null) return MonthlySalary;
            return MonthlySalary - LastReport.TotalSpending;
        }
    }

    public void AddOrUpdateReport(ExpenseReport newReport)
    {
        if (newReport == null)
        {
            return;
        }

        if (string.IsNullOrEmpty(newReport.Period))
        {
            newReport.Period = DateTime.Now.ToString("MM-yyyy");
        }

        var existing = AllReports.FirstOrDefault(r => r.Period == newReport.Period);
        if (existing != null)
        {
            // Mevcut raporun üzerine eklemek yerine birleştiriyoruz
            if (newReport.Expenses != null)
            {
                existing.Expenses ??= new List<ExpenseItem>();
                foreach (var exp in newReport.Expenses)
                {
                    if (exp == null)
                    {
                        continue;
                    }

                    // Basit bir duplication kontrolü (Merchant, Date, Amount aynıysa ekleme)
                    if (!existing.Expenses.Any(e => 
                        e.Merchant == exp.Merchant && 
                        e.Date == exp.Date && 
                        e.Amount == exp.Amount))
                    {
                        existing.Expenses.Add(exp);
                        existing.TotalSpending += exp.Amount;
                    }
                }
            }
            
            if (newReport.Subscriptions != null)
            {
                existing.Subscriptions ??= new List<SubscriptionItem>();
                foreach (var sub in newReport.Subscriptions)
                {
                    if (sub == null)
                    {
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(sub.Name))
                    {
                        continue;
                    }

                    var normalizedName = sub.Name.Trim();
                    var existingSub = existing.Subscriptions.FirstOrDefault(s =>
                        !string.IsNullOrWhiteSpace(s.Name) &&
                        string.Equals(s.Name!.Trim(), normalizedName, StringComparison.OrdinalIgnoreCase));

                    if (existingSub == null)
                    {
                        existing.Subscriptions.Add(new SubscriptionItem
                        {
                            Name = normalizedName,
                            Cost = sub.Cost,
                            Alternative = sub.Alternative,
                            SavingsAdvice = sub.SavingsAdvice
                        });
                        continue;
                    }

                    existingSub.Cost += sub.Cost;

                    if (string.IsNullOrWhiteSpace(existingSub.Alternative) && !string.IsNullOrWhiteSpace(sub.Alternative))
                    {
                        existingSub.Alternative = sub.Alternative;
                    }

                    if (string.IsNullOrWhiteSpace(existingSub.SavingsAdvice) && !string.IsNullOrWhiteSpace(sub.SavingsAdvice))
                    {
                        existingSub.SavingsAdvice = sub.SavingsAdvice;
                    }
                }
            }
            
            existing.SummaryAdvice = newReport.SummaryAdvice; // En son analizi kullan
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
