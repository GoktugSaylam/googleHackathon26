using System;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace FinansalPusula.Services;

public interface IYahooFinanceService
{
    Task<decimal> GetLivePriceAsync(string symbol);
    Task<decimal> GetPriceOnDateAsync(string symbol, DateTime date);
    Task<(decimal sma50, decimal sma200)> GetSmaAsync(string symbol);
}

/// <summary>
/// Yahoo Finance verilerini ASP.NET Core Hosted backend'imiz üzerinden proxy ile çeker.
/// Tarayıcı direkt backend'imize istek atar, CORS engeline takılmaz.
/// </summary>
public class YahooFinanceService : IYahooFinanceService
{
    private readonly HttpClient _httpClient;

    public YahooFinanceService(HttpClient httpClient)
    {
        _httpClient = httpClient;
        _httpClient.Timeout = TimeSpan.FromSeconds(20);
    }

    private static string FormatSymbol(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol)) return string.Empty;
        var s = symbol.Trim().ToUpper();
        return s.EndsWith(".IS") ? s : s + ".IS";
    }

    private static long ToUnix(DateTime date) =>
        ((DateTimeOffset)DateTime.SpecifyKind(date.Date, DateTimeKind.Local)).ToUnixTimeSeconds();

    // ─── Güncel Fiyat ─────────────────────────────────────────────────────────

    public async Task<decimal> GetLivePriceAsync(string symbol)
    {
        try
        {
            var sym = FormatSymbol(symbol);
            // Direkt backend api endpoint'imize gidiyoruz
            var url = $"/api/stock/price/{Uri.EscapeDataString(sym)}";
            
            var json = await _httpClient.GetStringAsync(url);
            return ParseRegularMarketPrice(json);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[YahooFinance] GetLivePrice({symbol}) hata: {ex.Message}");
            return 0m;
        }
    }

    // ─── Geçmiş Tarih ─────────────────────────────────────────────────────────

    public async Task<decimal> GetPriceOnDateAsync(string symbol, DateTime date)
    {
        try
        {
            var sym = FormatSymbol(symbol);

            // Önce sadece hedeflenen tarihi çek
            var price = await FetchPriceForDate(sym, date);
            if (price > 0) return price;

            // Eğer bulunamadıysa (hafta sonu vb.) son 7 güne doğru geriye sar
            for (int offset = 1; offset <= 7; offset++)
            {
                var altDate = date.Date.AddDays(-offset);
                price = await FetchPriceForDate(sym, altDate);
                if (price > 0)
                {
                    Console.WriteLine($"[YahooFinance] {sym} @ {altDate:dd.MM.yyyy} = {price:N2} TL (geri:{offset})");
                    return price;
                }
            }

            // Gelişmiş fallback: 30 günlük aralığı çekip en yakın olanı bul
            return await FetchPriceFromRange(sym, date.AddDays(-30), date.AddDays(1));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[YahooFinance] GetPriceOnDate({symbol}) hata: {ex.Message}");
            return 0m;
        }
    }

    private async Task<decimal> FetchPriceForDate(string sym, DateTime date)
    {
        try
        {
            var dateStr = date.Date.ToString("yyyy-MM-dd");
            var url = $"/api/stock/price/{Uri.EscapeDataString(sym)}?date={dateStr}";
            var json = await _httpClient.GetStringAsync(url);
            return ParseClosePrice(json);
        }
        catch { return 0m; }
    }

    private async Task<decimal> FetchPriceFromRange(string sym, DateTime from, DateTime to)
    {
        try
        {
            var fromStr = from.Date.ToString("yyyy-MM-dd");
            var toStr   = to.Date.ToString("yyyy-MM-dd");
            var url = $"/api/stock/range/{Uri.EscapeDataString(sym)}?from={fromStr}&to={toStr}";
            var json = await _httpClient.GetStringAsync(url);
            return ParseBestClosePrice(json, to); // En yakın değeri alır
        }
        catch { return 0m; }
    }

    // ─── SMA ──────────────────────────────────────────────────────────────────

    public async Task<(decimal sma50, decimal sma200)> GetSmaAsync(string symbol)
    {
        try
        {
            var sym = FormatSymbol(symbol);
            var to   = DateTime.Today;
            var from = to.AddYears(-1);
            var url = $"/api/stock/range/{Uri.EscapeDataString(sym)}?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}";
            
            var json = await _httpClient.GetStringAsync(url);
            
            using var doc = JsonDocument.Parse(json);
            var q = doc.RootElement
                .GetProperty("chart")
                .GetProperty("result")[0]
                .GetProperty("indicators")
                .GetProperty("quote")[0];

            if (!q.TryGetProperty("close", out var ca) || ca.ValueKind != JsonValueKind.Array)
                return (0m, 0m);

            var prices = ca.EnumerateArray()
                .Where(x => x.ValueKind != JsonValueKind.Null)
                .Select(x => x.GetDecimal())
                .ToList();

            if (prices.Count < 50) return (0m, 0m);

            var sma50  = prices.TakeLast(50).Average();
            var sma200 = prices.Count >= 200 ? prices.TakeLast(200).Average() : 0m;

            return (Math.Round(sma50, 2), Math.Round(sma200, 2));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[YahooFinance] GetSma({symbol}) hata: {ex.Message}");
            return (0m, 0m);
        }
    }

    // ─── JSON Parse Fonksiyonları ──────────────────────────────────────────────

    private static decimal ParseRegularMarketPrice(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var meta = doc.RootElement
            .GetProperty("chart")
            .GetProperty("result")[0]
            .GetProperty("meta");

        if (meta.TryGetProperty("regularMarketPrice", out var p) && p.ValueKind != JsonValueKind.Null)
            return p.GetDecimal();
        return 0m;
    }

    private static decimal ParseClosePrice(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var result = doc.RootElement.GetProperty("chart").GetProperty("result")[0];

        if (result.TryGetProperty("indicators", out var ind) &&
            ind.TryGetProperty("quote", out var qa) && qa.GetArrayLength() > 0)
        {
            var q = qa[0];
            if (q.TryGetProperty("close", out var ca))
                foreach (var item in ca.EnumerateArray())
                    if (item.ValueKind != JsonValueKind.Null)
                        return item.GetDecimal();
        }

        return ParseRegularMarketPrice(json);
    }

    private static decimal ParseBestClosePrice(string json, DateTime targetDate)
    {
        using var doc = JsonDocument.Parse(json);
        var result = doc.RootElement.GetProperty("chart").GetProperty("result")[0];

        var timestamps = result.GetProperty("timestamp").EnumerateArray()
            .Select(t => t.GetInt64()).ToList();
        var closes = result.GetProperty("indicators").GetProperty("quote")[0]
            .GetProperty("close").EnumerateArray().ToList();

        var targetTs = ((DateTimeOffset)DateTime.SpecifyKind(targetDate.Date, DateTimeKind.Local)).ToUnixTimeSeconds();
        long    bestDiff  = long.MaxValue;
        decimal bestPrice = 0m;

        for (int i = 0; i < Math.Min(timestamps.Count, closes.Count); i++)
        {
            if (closes[i].ValueKind == JsonValueKind.Null) continue;
            var diff = Math.Abs(timestamps[i] - targetTs);
            if (diff < bestDiff) { bestDiff = diff; bestPrice = closes[i].GetDecimal(); }
        }

        return bestPrice > 0 ? bestPrice : ParseRegularMarketPrice(json);
    }
}
