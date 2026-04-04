namespace FinansalPusula.Services;

public class InvestmentService
{
    private readonly HttpClient _httpClient;
    private readonly Random _random = new Random();

    // Major BIST stocks for simulation
    private readonly Dictionary<string, decimal> _bistBasePrices = new()
    {
        { "THYAO.IS", 285.40m },
        { "EREGL.IS", 45.20m },
        { "SASA.IS", 38.50m },
        { "SISE.IS", 48.10m },
        { "TUPRS.IS", 165.30m },
        { "KCHOL.IS", 172.90m },
        { "ASELS.IS", 52.40m },
        { "AKBNK.IS", 42.10m },
        { "ISCTR.IS", 28.50m }
    };

    public InvestmentService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<List<StockData>> GetStockQuotesAsync(List<string> symbols)
    {
        // Simulation for Hackathon Demo: 
        // Focus on BIST (.IS) symbols.
        await Task.Delay(500); 

        var result = new List<StockData>();
        foreach (var symbol in symbols)
        {
            var upperSymbol = symbol.ToUpper();
            
            // If it's a known BIST stock, use base price, else generate one
            if (!_bistBasePrices.TryGetValue(upperSymbol, out var basePrice))
            {
                // Generate a consistent base price based on symbol name for the demo
                basePrice = (upperSymbol.Length * 17) % 300 + 10; 
            }

            // Real-time fluctuation
            var fluctuation = (decimal)(_random.NextDouble() * 4 - 2);
            var change = (decimal)(_random.NextDouble() * 3 - 1.5);

            result.Add(new StockData
            {
                Symbol = upperSymbol,
                Price = decimal.Round(basePrice + fluctuation, 2),
                Change = decimal.Round(change, 2)
            });
        }

        return result;
    }

    public bool IsValidBistSymbol(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol)) return false;
        var upper = symbol.ToUpper();
        // In this MVP, we enforce .IS suffix for Yahoo/BIST consistency
        return upper.EndsWith(".IS") && upper.Length >= 4;
    }
}
