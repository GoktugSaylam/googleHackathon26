namespace FinansalPusula.Services;

public interface IStockDataService
{
    Task<List<StockData>> GetStockQuotesAsync(List<string> symbols);
    Task<StockDetail?> GetStockDetailAsync(string symbol);
    bool IsValidBistSymbol(string symbol);
    Task<List<HistoricalDataPoint>> GetHistoricalDataAsync(string symbol, DateTime from, DateTime to);
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

// --- Yeni Simülasyon ve İşlem Modelleri ---

public enum TransactionType 
{ 
    Buy = 0, Alis = 0, 
    Sell = 1, Satis = 1, 
    Temettu = 2, 
    Bolunme = 3 
}

public class PortfolioTransaction
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public DateTime Tarih { get; set; } = DateTime.Now;
    public TransactionType IslemTipi { get; set; } = TransactionType.Alis;
    public string Sembol { get; set; } = string.Empty;
    public decimal Adet { get; set; }
    public decimal BirimFiyat { get; set; }
    public decimal ToplamTutar => Adet * BirimFiyat;
}

public class StockDetailMetrics
{
    public string Sembol { get; set; } = string.Empty;
    public decimal ToplamYatirimTL { get; set; }
    public decimal ToplamYatirimUSD { get; set; }
    public decimal OrtalamMaliyetLot { get; set; }
    public decimal GuncelDeger { get; set; }
    public decimal ToplamLot { get; set; }
    public decimal DRIPLot { get; set; }
    public decimal ToplamTemettu { get; set; }
    public decimal SMA50 { get; set; }
    public decimal SMA200 { get; set; }
}

public class TransactionRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Symbol { get; set; } = "";
    public DateTime Date { get; set; } = DateTime.Now;
    public TransactionType Type { get; set; } = TransactionType.Buy;
    public decimal Quantity { get; set; }
    public decimal Price { get; set; }
}

public class HistoricalDataPoint
{
    public DateTime Date { get; set; }
    public decimal PriceTL { get; set; }
    public decimal PriceUSD { get; set; }
    public decimal PorfolyoDegeriTL { get; set; }
}

public class SimulationResult
{
    public string Symbol { get; set; } = "";
    public List<HistoricalDataPoint> DailyPoints { get; set; } = new();
    public List<YearlySummary> YearlySummaries { get; set; } = new();
    public List<DetailedDividendEvent> DividendEvents { get; set; } = new();
    
    // Özet Metrikler
    public decimal TotalInvestedTL { get; set; }
    public decimal TotalInvestedUSD { get; set; }
    public decimal CurrentValueTL { get; set; }
    public decimal CurrentValueUSD { get; set; }
    public decimal TotalLots { get; set; }
    public decimal DripLots { get; set; }
    public decimal TotalNetDividendTL { get; set; }
    public decimal TotalNetDividendUSD { get; set; }
    public decimal AvgCostTL => TotalLots > 0 ? TotalInvestedTL / TotalLots : 0;
    public decimal AvgCostUSD => TotalLots > 0 ? TotalInvestedUSD / TotalLots : 0;
}

public class YearlySummary
{
    public int Year { get; set; }
    public decimal LotCount { get; set; }
    public decimal NetDividendTL { get; set; }
    public decimal ValueIncreaseTL { get; set; }
    public decimal IncreasePercentage { get; set; }
    public decimal YearEndValueTL { get; set; }
    public decimal YearEndValueUSD { get; set; }
}

public class DetailedDividendEvent
{
    public DateTime Date { get; set; }
    public decimal DividendPerShare { get; set; }
    public decimal OwnedLots { get; set; }
    public decimal GrossIncome { get; set; }
    public decimal TaxAmount => GrossIncome * 0.10m;
    public decimal NetIncome => GrossIncome * 0.90m;
    public decimal BuyPriceT2 { get; set; }
    public decimal LotsBought { get; set; }
    public decimal RemainingCash { get; set; }
}
