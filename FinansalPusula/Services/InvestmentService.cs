namespace FinansalPusula.Services;

public class InvestmentService
{
    private readonly HttpClient _httpClient;
    private readonly Random _random = new Random();

    public InvestmentService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<List<StockData>> GetStockQuotesAsync(List<string> symbols)
    {
        // Simulation for Hackathon Demo: 
        // In a real scenario, this would call RapidAPI / Yahoo Finance REST endpoint.
        await Task.Delay(800); // Simulate network lag

        var result = new List<StockData>();
        foreach (var symbol in symbols)
        {
            var basePrice = symbol switch
            {
                "AAPL" => 175.50m,
                "TSLA" => 240.20m,
                "BTC-USD" => 64000.00m,
                "THYAO.IS" => 285.40m,
                "MSFT" => 410.50m,
                _ => 100.00m
            };

            // Add some "live" flavor
            var fluctuation = (decimal)(_random.NextDouble() * 10 - 5);
            var change = (decimal)(_random.NextDouble() * 4 - 2);

            result.Add(new StockData
            {
                Symbol = symbol,
                Price = decimal.Round(basePrice + fluctuation, 2),
                Change = decimal.Round(change, 2)
            });
        }

        return result;
    }
}
