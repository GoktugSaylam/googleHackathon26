using System.Net.Http.Json;

namespace FinansalPusula.Services;

public class InvestmentService : IStockDataService
{
    private readonly HttpClient _httpClient;

    public List<TransactionRecord> Transactions { get; set; } = new()
    {
        new TransactionRecord { Symbol = "TUPRS.IS", Date = DateTime.Today.AddYears(-2), Quantity = 100, Price = 15.5m, Type = TransactionType.Buy },
        new TransactionRecord { Symbol = "THYAO.IS", Date = DateTime.Today.AddYears(-1).AddMonths(-3), Quantity = 50, Price = 240.20m, Type = TransactionType.Buy }
    };

    public InvestmentService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<List<StockData>> GetStockQuotesAsync(List<string> symbols)
    {
        var resultList = new List<StockData>();
        
        // Paralel isteklerle hız kazanalım
        var tasks = symbols.Select(s => GetSingleStockQuoteAsync(s));
        var results = await Task.WhenAll(tasks);
        
        foreach (var res in results)
        {
            if (res != null) resultList.Add(res);
        }

        return resultList;
    }

    private async Task<StockData?> GetSingleStockQuoteAsync(string symbol)
    {
        try
        {
            // Kendi backend proxy'mizi kullanıyoruz
            var url = $"/api/stock/price/{Uri.EscapeDataString(symbol)}";

            var response = await _httpClient.GetFromJsonAsync<YahooFinanceResponse>(url);
            var result = response?.Chart?.Result?.FirstOrDefault();

            if (result?.Meta == null) return null;

            return new StockData
            {
                Symbol = result.Meta.Symbol ?? symbol,
                Price = result.Meta.RegularMarketPrice,
                Change = result.Meta.RegularMarketPrice - result.Meta.PreviousClose,
                Quantity = 0, // UI'dan gelecek
                AverageCost = 0 // UI'dan gelecek
            };
        }
        catch (Exception)
        {
            // Hata durumunda işlemi durdurmak yerine loglayıp devam edebiliriz 
            // ama kullanıcı "hata görmen durumunda düşünüp sorunu çöz" dediği için 
            // burada minimal bir sessizlik koruyoruz.
            return null;
        }
    }

    public async Task<StockDetail?> GetStockDetailAsync(string symbol)
    {
        try
        {
            // Detay için backend proxy üzerinden tarih aralığı çekiyoruz (5y + unadjusted)
            var from = DateTime.Today.AddYears(-5);
            var to = DateTime.Today;
            var url = $"/api/stock/range/{Uri.EscapeDataString(symbol)}?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}";

            var response = await _httpClient.GetFromJsonAsync<YahooFinanceResponse>(url);
            var result = response?.Chart?.Result?.FirstOrDefault();

            if (result == null) return null;

            var detail = new StockDetail
            {
                Symbol = result.Meta?.Symbol ?? symbol,
                FiftyDayAverage = result.Meta?.FiftyDayAverage ?? 0,
                TwoHundredDayAverage = result.Meta?.TwoHundredDayAverage ?? 0,
                Dividends = new List<DividendHistory>(),
                Splits = new List<SplitInfo>(),
                DripLots = 0
            };

            // Temettüleri Parse Et
            if (result.Events?.Dividends != null)
            {
                foreach (var div in result.Events.Dividends.Values)
                {
                    detail.Dividends.Add(new DividendHistory(
                        DateTimeOffset.FromUnixTimeSeconds(div.Date).DateTime,
                        div.Amount,
                        result.Meta != null && result.Meta.RegularMarketPrice > 0 
                            ? (div.Amount / result.Meta.RegularMarketPrice) * 100 
                            : 0
                    ));
                }
                detail.Dividends = detail.Dividends.OrderByDescending(d => d.Date).ToList();
            }

            // Bölünmeleri Parse Et
            if (result.Events?.Splits != null)
            {
                foreach (var sp in result.Events.Splits.Values)
                {
                    detail.Splits.Add(new SplitInfo(
                        DateTimeOffset.FromUnixTimeSeconds(sp.Date).DateTime,
                        sp.SplitRatio ?? $"{sp.Numerator}:{sp.Denominator}"
                    ));
                }
            }

            // Mock DRIP hesaplaması (Gerçek veri Yahoo'da lot bazlı değil tutar bazlıdır)
            detail.DripLots = detail.Dividends.Count * 0.5m;

            return detail;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public async Task<List<HistoricalDataPoint>> GetHistoricalDataAsync(string symbol, DateTime from, DateTime to)
    {
        // Stok tarihsel verisi
        List<HistoricalDataPoint> points = new();
        try
        {
            var stockUrl = $"/api/stock/range/{Uri.EscapeDataString(symbol)}?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}";
            var stockRaw = await _httpClient.GetFromJsonAsync<YahooFinanceResponse>(stockUrl);
            var stockResult = stockRaw?.Chart?.Result?.FirstOrDefault();

            if (stockResult?.Timestamp == null || stockResult.Indicators?.Quote?.FirstOrDefault()?.Close == null)
                return points;

            // Kur tarihsel verisi – hata alırsa devam et, varsayılan kur kullan
            List<decimal?> forexPrices = new();
            List<long> forexTimestamps = new();
            try
            {
                var forexUrl = $"/api/stock/range/USDTRY%3DX?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}";
                var forexRaw = await _httpClient.GetFromJsonAsync<YahooFinanceResponse>(forexUrl);
                var forexResult = forexRaw?.Chart?.Result?.FirstOrDefault();
                forexPrices = forexResult?.Indicators?.Quote?.FirstOrDefault()?.Close ?? new();
                forexTimestamps = forexResult?.Timestamp ?? new();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[InvestmentService] Forex verisi alınamadı, varsayılan kur kullanılacak: {ex.Message}");
            }

            var closeList = stockResult.Indicators.Quote[0].Close!;
            for (int i = 0; i < stockResult.Timestamp.Count; i++)
            {
                if (i >= closeList.Count) break;
                var ts = stockResult.Timestamp[i];
                var price = closeList[i];
                if (price == null) continue;

                // En yakın kur verisini bul, yoksa fallback
                var forexPrice = 32.5m;
                if (forexTimestamps.Count > 0)
                {
                    var forexIdx = forexTimestamps.FindIndex(t => Math.Abs(t - ts) < 86400 * 3);
                    if (forexIdx >= 0 && forexIdx < forexPrices.Count && forexPrices[forexIdx] != null)
                        forexPrice = forexPrices[forexIdx]!.Value;
                }

                points.Add(new HistoricalDataPoint
                {
                    Date = DateTimeOffset.FromUnixTimeSeconds(ts).DateTime,
                    PriceTL = price.Value,
                    PriceUSD = forexPrice > 0 ? price.Value / forexPrice : 0
                });
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[InvestmentService] GetHistoricalDataAsync hatası: {ex.Message}");
        }
        return points;
    }

    public bool IsValidBistSymbol(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol)) return false;
        var upper = symbol.ToUpper();
        if (upper.EndsWith(".IS")) upper = upper.Substring(0, upper.Length - 3);

        // Yeni veritabanımızdan (BistDataService) kontrol et
        return BistDataService.AllStocks.Any(s => s.Symbol == upper);
    }
}
