namespace FinansalPusula.Services;

public interface IStockDataService
{
    Task<List<StockData>> GetStockQuotesAsync(List<string> symbols);
    Task<StockDetail?> GetStockDetailAsync(string symbol);
    bool IsValidBistSymbol(string symbol);
}

public class StockData
{
    public string Symbol { get; set; } = "";
    public decimal Price { get; set; }
    public decimal Change { get; set; }
    public decimal Quantity { get; set; }
    public decimal AverageCost { get; set; }
    public decimal TotalValue => Price * Quantity;
    public decimal ProfitLoss => TotalValue - (AverageCost * Quantity);
    public decimal ProfitLossPercentage => (AverageCost != 0 && Quantity != 0) ? (ProfitLoss / (AverageCost * Quantity)) * 100 : 0;
}

public record DividendHistory(DateTime Date, decimal Amount, decimal Yield);
public record SplitInfo(DateTime Date, string Ratio);

public class StockDetail
{
    public string Symbol { get; set; } = string.Empty;
    public decimal FiftyDayAverage { get; set; }
    public decimal TwoHundredDayAverage { get; set; }
    public List<DividendHistory> Dividends { get; set; } = new();
    public List<SplitInfo> Splits { get; set; } = new();
    public decimal DripLots { get; set; }
}
