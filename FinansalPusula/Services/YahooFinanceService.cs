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
/// Yahoo Finance v8 API'sını ücretsiz ve key'siz kullanan servis.
/// Strateji: önce doğrudan Yahoo'ya git, CORS engeli varsa proxy'ye geç.
/// Birden fazla proxy denenir; hafta sonu/tatil günlerinde önceki güne kayar.
/// </summary>
public class YahooFinanceService : IYahooFinanceService
{
    private readonly HttpClient _httpClient;

    // Deneme sırası: önce proxy'siz, sonra proxy'li - birden fazla yedek
    private static readonly string[] Proxies = new[]
    {
        "",                                         // 1) Direkt istek (CORS izni varsa)
        "https://api.allorigins.win/raw?url=",      // 2) allorigins
        "https://corsproxy.io/?url=",               // 3) corsproxy.io
        "https://api.codetabs.com/v1/proxy?quest=", // 4) codetabs
        "https://thingproxy.freeboard.io/fetch/",   // 5) thingproxy
    };

    private static readonly string[] QueryHosts = { "query1", "query2" };

    public YahooFinanceService(HttpClient httpClient)
    {
        _httpClient = httpClient;
        _httpClient.Timeout = TimeSpan.FromSeconds(20);
    }

    // ─── Yardımcılar ──────────────────────────────────────────────────────────

    private static string FormatSymbol(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol)) return string.Empty;
        var s = symbol.Trim().ToUpper();
        return s.EndsWith(".IS") ? s : s + ".IS";
    }

    private static long ToUnix(DateTime date) =>
        ((DateTimeOffset)DateTime.SpecifyKind(date.Date, DateTimeKind.Local)).ToUnixTimeSeconds();

    /// <summary>
    /// Belirtilen Yahoo URL'sini tüm proxy/host kombinasyonlarıyla dener.
    /// URL şablonunda {host} yoksa aynı url ile dener.
    /// </summary>
    private async Task<string?> FetchAsync(string yahooUrlTemplate)
    {
        foreach (var proxy in Proxies)
        {
            foreach (var host in QueryHosts)
            {
                try
                {
                    var targetUrl = yahooUrlTemplate.Contains("{host}")
                        ? yahooUrlTemplate.Replace("{host}", host)
                        : yahooUrlTemplate;

                    string finalUrl;
                    if (string.IsNullOrEmpty(proxy))
                        finalUrl = targetUrl;                                      // direkt
                    else
                        finalUrl = proxy + Uri.EscapeDataString(targetUrl);        // proxy'li

                    using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(10));
                    var resp = await _httpClient.GetAsync(finalUrl, cts.Token);

                    if (!resp.IsSuccessStatusCode) continue;

                    var body = await resp.Content.ReadAsStringAsync();
                    // HTML hata sayfası veya boş yanıt gelirse atla
                    if (string.IsNullOrWhiteSpace(body) || body.TrimStart().StartsWith("<"))
                        continue;

                    // Geçerli JSON mı? Kısa kontrol
                    if (!body.Contains("chart")) continue;

                    return body;
                }
                catch { /* bu kombinasyon başarısız, sonrakini dene */ }
            }
        }
        return null; // hiçbiri çalışmadı
    }

    // ─── Güncel Fiyat ─────────────────────────────────────────────────────────

    public async Task<decimal> GetLivePriceAsync(string symbol)
    {
        try
        {
            var sym = FormatSymbol(symbol);
            var url = $"https://{{host}}.finance.yahoo.com/v8/finance/chart/{sym}?interval=1d&range=5d";
            var json = await FetchAsync(url);
            return json != null ? ParseRegularMarketPrice(json) : 0m;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[YahooFinance] GetLivePrice({symbol}) hata: {ex.Message}");
            return 0m;
        }
    }

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

    // ─── Geçmiş Fiyat ─────────────────────────────────────────────────────────

    public async Task<decimal> GetPriceOnDateAsync(string symbol, DateTime date)
    {
        try
        {
            var sym = FormatSymbol(symbol);

            // Önce seçilen günden geriye doğru 7 güne kadar ara (hafta sonu / tatil için)
            for (int offset = 0; offset <= 7; offset++)
            {
                var targetDate = date.Date.AddDays(-offset);
                var p1 = ToUnix(targetDate);
                var p2 = ToUnix(targetDate.AddDays(1));
                var url = $"https://{{host}}.finance.yahoo.com/v8/finance/chart/{sym}?period1={p1}&period2={p2}&interval=1d";

                var json = await FetchAsync(url);
                if (json == null) continue;

                var price = ParseClosePrice(json);
                if (price > 0)
                {
                    Console.WriteLine($"[YahooFinance] {sym} @ {targetDate:dd.MM.yyyy} = {price:N2} TL (geri:{offset})");
                    return price;
                }
            }

            // Dar aralıkta bulunamazsa 30 günlük veri çek, en yakın tarihi bul
            return await GetPriceFromRange30(sym, date);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[YahooFinance] GetPriceOnDate({symbol}, {date:dd.MM.yyyy}) hata: {ex.Message}");
            return 0m;
        }
    }

    private async Task<decimal> GetPriceFromRange30(string sym, DateTime date)
    {
        try
        {
            var p1  = ToUnix(date.AddDays(-30));
            var p2  = ToUnix(date.AddDays(2));
            var url = $"https://{{host}}.finance.yahoo.com/v8/finance/chart/{sym}?period1={p1}&period2={p2}&interval=1d";
            var json = await FetchAsync(url);
            if (json == null) return 0m;

            using var doc = JsonDocument.Parse(json);
            var result = doc.RootElement.GetProperty("chart").GetProperty("result")[0];

            var timestamps = result.GetProperty("timestamp").EnumerateArray()
                .Select(t => t.GetInt64()).ToList();
            var closes = result.GetProperty("indicators").GetProperty("quote")[0]
                .GetProperty("close").EnumerateArray().ToList();

            var targetTs = ToUnix(date);
            long   bestDiff  = long.MaxValue;
            decimal bestPrice = 0m;

            for (int i = 0; i < Math.Min(timestamps.Count, closes.Count); i++)
            {
                if (closes[i].ValueKind == JsonValueKind.Null) continue;
                var diff = Math.Abs(timestamps[i] - targetTs);
                if (diff < bestDiff)
                {
                    bestDiff  = diff;
                    bestPrice = closes[i].GetDecimal();
                }
            }

            return bestPrice;
        }
        catch { return 0m; }
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
            {
                foreach (var item in ca.EnumerateArray())
                    if (item.ValueKind != JsonValueKind.Null)
                        return item.GetDecimal();
            }
        }

        // Fallback: meta.regularMarketPrice
        return ParseRegularMarketPrice(json);
    }

    // ─── SMA ──────────────────────────────────────────────────────────────────

    public async Task<(decimal sma50, decimal sma200)> GetSmaAsync(string symbol)
    {
        try
        {
            var sym = FormatSymbol(symbol);
            var url = $"https://{{host}}.finance.yahoo.com/v8/finance/chart/{sym}?interval=1d&range=1y";
            var json = await FetchAsync(url);
            if (json == null) return (0m, 0m);

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
}
