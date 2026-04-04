namespace FinansalPusula.Services;

public class BudgetService
{
    public decimal MonthlySalary { get; set; } = 40000; // Mock salary
    public decimal AnnualInflationRate { get; set; } = 64.0m; // Mock inflation
    public ExpenseReport? LastReport { get; set; }

    public decimal CurrentIdleCash { 
        get {
            if (LastReport == null) return MonthlySalary;
            return MonthlySalary - LastReport.TotalSpending;
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
        // Simple linear projection (not compound for simplicity in MVP)
        return CalculateMonthlyErosion() * 12;
    }
}
