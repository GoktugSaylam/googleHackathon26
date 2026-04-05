using Microsoft.AspNetCore.ResponseCompression;
using FinansalPusula.Server.Data;
using FinansalPusula.Server.Services;
using FinansalPusula.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllersWithViews();
builder.Services.AddRazorPages();
builder.Services.AddHttpClient();

// SQLite Repository
builder.Services.AddSingleton<TransactionRepository>();
builder.Services.AddSingleton<FinancialMetricsService>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseWebAssemblyDebugging();
}
else
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseBlazorFrameworkFiles();
app.UseStaticFiles();

app.UseRouting();

// ─── Portfolio API ────────────────────────────────────────────────────────────

app.MapGet("/api/portfolio", async (TransactionRepository repo) =>
{
    return Results.Ok(await repo.GetAllAsync());
});

app.MapPost("/api/portfolio", async (PortfolioTransaction tx, TransactionRepository repo) =>
{
    await repo.AddAsync(tx);
    return Results.Ok();
});

app.MapDelete("/api/portfolio/{id}", async (string id, TransactionRepository repo) =>
{
    await repo.DeleteAsync(id);
    return Results.Ok();
});

app.MapPost("/api/portfolio/metrics", async (MetricsRequest request, TransactionRepository repo, FinancialMetricsService metricsService) =>
{
    var currentValue = request.CurrentValue;
    var txs = await repo.GetAllAsync();
    if (!txs.Any()) return Results.Ok(new { Cagr = 0.0, Xirr = 0.0 });

    var flows = new List<(DateTime Date, double Amount)>();
    
    // İşlemleri nakit akışına çevir (Alışlar -, Satışlar/Temettüler +)
    foreach (var tx in txs.OrderBy(t => t.Tarih))
    {
        double amount = (double)(tx.Adet * tx.BirimFiyat);
        if (tx.IslemTipi == TransactionType.Alis || tx.IslemTipi == TransactionType.Buy)
        {
            flows.Add((tx.Tarih, -amount));
        }
        else if (tx.IslemTipi == TransactionType.Satis || tx.IslemTipi == TransactionType.Sell || tx.IslemTipi == TransactionType.Temettu)
        {
            flows.Add((tx.Tarih, amount));
        }
    }

    // Bugünün tarihi ve güncel portföy değerini ekle (+)
    flows.Add((DateTime.Now, (double)currentValue));

    // XIRR Hesapla
    double xirr = metricsService.CalculateXirr(flows);
    if (double.IsNaN(xirr) || double.IsInfinity(xirr)) xirr = 0;

    // CAGR Hesapla
    double totalInvested = Math.Abs(flows.Where(f => f.Amount < 0).Sum(f => f.Amount));
    double cagr = 0;
    if (totalInvested > 0)
    {
        var firstTransactionDate = flows.Min(f => f.Date);
        // Kısa Süre Koruması: Astronomik sonuçları engellemek için yılı min 1'e sabitle
        double years = Math.Max((DateTime.Now - firstTransactionDate).TotalDays / 365.25, 1.0);
        cagr = metricsService.CalculateCagr(totalInvested, (double)currentValue, years);
    }

    if (double.IsNaN(cagr) || double.IsInfinity(cagr)) cagr = 0;

    return Results.Ok(new { Cagr = cagr * 100, Xirr = xirr * 100 });
});

// ─── Yahoo Finance Proxy (With Split Correction for Raw Prices) ─────────────────

app.MapGet("/api/stock/price/{symbol}", async (string symbol, string? date, IHttpClientFactory factory) =>
{
    using var client = factory.CreateClient();
    client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");

    try
    {
        var cleanSymbol = symbol.Split('.')[0].ToUpper();
        var formattedSymbol = cleanSymbol + ".IS";

        string yahooUrl;
        DateTime? targetDate = null;
        if (!string.IsNullOrEmpty(date) && DateTime.TryParse(date, out var parsedDate))
        {
            targetDate = parsedDate;
            var p1 = ((DateTimeOffset)DateTime.SpecifyKind(targetDate.Value.Date.AddDays(-5), DateTimeKind.Local)).ToUnixTimeSeconds();
            var p2 = ((DateTimeOffset)DateTime.SpecifyKind(targetDate.Value.Date.AddDays(2), DateTimeKind.Local)).ToUnixTimeSeconds();
            yahooUrl = $"https://query1.finance.yahoo.com/v8/finance/chart/{formattedSymbol}?period1={p1}&period2={p2}&interval=1d&includeAdjustedClose=false";
        }
        else
        {
            yahooUrl = $"https://query1.finance.yahoo.com/v8/finance/chart/{formattedSymbol}?interval=1d&range=5d&includeAdjustedClose=false";
        }

        var response = await client.GetAsync(yahooUrl);
        if (!response.IsSuccessStatusCode)
        {
            yahooUrl = yahooUrl.Replace("query1", "query2");
            response = await client.GetAsync(yahooUrl);
        }

        if (!response.IsSuccessStatusCode)
            return Results.NotFound(new { error = "Yahoo Finance veri döndürmedi." });

        var json = await response.Content.ReadAsStringAsync();
        
        // Eğer tarihsel bir fiyat isteniyorsa ve bölünme düzeltmesi gerekiyorsa
        if (targetDate.HasValue)
        {
            // Yahoo'dan gelen (muhtemelen düzeltilmiş) fiyatı çek
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var result = doc.RootElement.GetProperty("chart").GetProperty("result")[0];
            var timestamps = result.GetProperty("timestamp").EnumerateArray().Select(t => t.GetInt64()).ToList();
            var closes = result.GetProperty("indicators").GetProperty("quote")[0].GetProperty("close").EnumerateArray().ToList();
            
            var targetTs = ((DateTimeOffset)DateTime.SpecifyKind(targetDate.Value.Date, DateTimeKind.Local)).ToUnixTimeSeconds();
            decimal yahooPrice = 0;
            long bestDiff = long.MaxValue;

            for(int i=0; i<timestamps.Count; i++) {
                if (closes[i].ValueKind == System.Text.Json.JsonValueKind.Null) continue;
                var diff = Math.Abs(timestamps[i] - targetTs);
                if (diff < bestDiff) { bestDiff = diff; yahooPrice = closes[i].GetDecimal(); }
            }

            if (yahooPrice > 0) 
            {
                // Yahoo Finance split datasını al
                var splitUrl = $"https://query2.finance.yahoo.com/v8/finance/chart/{formattedSymbol}?interval=1mo&events=split&range=max";
                var splitResponse = await client.GetAsync(splitUrl);
                if (splitResponse.IsSuccessStatusCode)
                {
                    var splitJson = await splitResponse.Content.ReadAsStringAsync();
                    using var splitDoc = System.Text.Json.JsonDocument.Parse(splitJson);
                    var resultNode = splitDoc.RootElement.GetProperty("chart").GetProperty("result")[0];
                    
                    decimal cumulativeFactor = 1.0m;
                    if (resultNode.TryGetProperty("events", out var eventsNode) && eventsNode.TryGetProperty("splits", out var splitsObj))
                    {
                        var splitItems = splitsObj.EnumerateObject();
                        foreach(var s in splitItems) 
                        {
                            var sValue = s.Value;
                            var sDateUnix = sValue.GetProperty("date").GetInt64();
                            var sDate = DateTimeOffset.FromUnixTimeSeconds(sDateUnix).LocalDateTime;
                            
                            // Sadece işlem tarihinden SONRA gerçekleşen bölünmeleri sayıyoruz (tersine düzeltme için)
                            if (sDate > targetDate.Value.AddDays(1)) 
                            {
                                var num = sValue.GetProperty("numerator").GetDecimal();
                                var den = sValue.GetProperty("denominator").GetDecimal();
                                if (den > 0) 
                                {
                                    cumulativeFactor *= (num / den);
                                }
                            }
                        }
                    }

                    // Yahoo fiyatını kümülatif faktörle çarparak NOMİNAL (HAM) fiyata ulaşıyoruz
                    var nominalPrice = yahooPrice * cumulativeFactor;
                    return Results.Ok(new { price = nominalPrice, symbol = formattedSymbol, date = targetDate.Value, factor = cumulativeFactor, isRaw = true });
                }
            }
        }

        return Results.Content(json, "application/json");
    }
    catch (Exception ex)
    {
        return Results.Problem($"İstek başarısız: {ex.Message}");
    }
});

app.MapGet("/api/stock/range/{symbol}", async (string symbol, string from, string to, IHttpClientFactory factory) =>
{
    using var client = factory.CreateClient();
    client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");

    try
    {
        var formattedSymbol = symbol.Trim().ToUpper();
        // Kur sembollerini (= içerenleri) veya zaten uzantı olanları .IS ile bozma
        if (!formattedSymbol.Contains("=") && !formattedSymbol.Contains("."))
        {
            formattedSymbol += ".IS";
        }

        if (!DateTime.TryParse(from, out var fromDate) || !DateTime.TryParse(to, out var toDate))
            return Results.BadRequest(new { error = "Geçersiz tarih." });

        var p1 = ((DateTimeOffset)DateTime.SpecifyKind(fromDate.Date, DateTimeKind.Local)).ToUnixTimeSeconds();
        var p2 = ((DateTimeOffset)DateTime.SpecifyKind(toDate.Date.AddDays(1), DateTimeKind.Local)).ToUnixTimeSeconds();
        var yahooUrl = $"https://query1.finance.yahoo.com/v8/finance/chart/{formattedSymbol}?period1={p1}&period2={p2}&interval=1d&includeAdjustedClose=false&events=div%7Csplit";
        
        var response = await client.GetAsync(yahooUrl);
        if (!response.IsSuccessStatusCode)
        {
            yahooUrl = yahooUrl.Replace("query1", "query2");
            response = await client.GetAsync(yahooUrl);
        }

        if (!response.IsSuccessStatusCode)
            return Results.NotFound(new { error = "Veri bulunamadı." });

        var json = await response.Content.ReadAsStringAsync();

        // --- Split Reversal (Ham Fiyat Düzeltme) Mantığı ---
        try
        {
            // Tüm bölünmeleri çek
            var splitUrl = $"https://query2.finance.yahoo.com/v8/finance/chart/{formattedSymbol}?interval=1mo&events=split&range=max&includeAdjustedClose=false";
            var splitResponse = await client.GetAsync(splitUrl);
            if (splitResponse.IsSuccessStatusCode)
            {
                var splitJson = await splitResponse.Content.ReadAsStringAsync();
                using var splitDoc = System.Text.Json.JsonDocument.Parse(splitJson);
                var splitResultNode = splitDoc.RootElement.GetProperty("chart").GetProperty("result")[0];
                
                var allSplits = new List<(long Date, decimal Factor)>();
                if (splitResultNode.TryGetProperty("events", out var eventsNode) && eventsNode.TryGetProperty("splits", out var splitsObj))
                {
                    foreach (var s in splitsObj.EnumerateObject())
                    {
                        var sValue = s.Value;
                        var sDateUnix = sValue.GetProperty("date").GetInt64();
                        var num = sValue.GetProperty("numerator").GetDecimal();
                        var den = sValue.GetProperty("denominator").GetDecimal();
                        if (den > 0) allSplits.Add((sDateUnix, num / den));
                    }
                }

                if (allSplits.Count > 0)
                {
                    // Chart JSON'u düzenlenebilir hale getir
                    var options = new System.Text.Json.JsonSerializerOptions { WriteIndented = false };
                    var doc = System.Text.Json.Nodes.JsonNode.Parse(json);
                    var resultNode = doc?["chart"]?["result"]?[0];
                    var timestamps = resultNode?["timestamp"]?.AsArray();
                    var closes = resultNode?["indicators"]?["quote"]?[0]?["close"]?.AsArray();

                    if (timestamps != null && closes != null)
                    {
                        for (int i = 0; i < timestamps.Count; i++)
                        {
                            if (closes[i] == null || closes[i]!.GetValueKind() == System.Text.Json.JsonValueKind.Null) continue;
                            
                            var ts = timestamps[i]!.GetValue<long>();
                            var price = closes[i]!.GetValue<decimal>();
                            
                            // Bu veri noktasından SONRA gerçekleşen tüm bölünmeleri bul ve fiyatı geri çarparak "un-adjust" et
                            decimal cumulativeFactor = 1.0m;
                            foreach (var s in allSplits)
                            {
                                if (s.Date > ts + 86400) // 1 gün marj
                                {
                                    cumulativeFactor *= s.Factor;
                                }
                            }

                            if (cumulativeFactor != 1.0m)
                            {
                                closes[i] = price * cumulativeFactor;
                            }
                        }
                        json = doc!.ToJsonString(options);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Backend] Split reversal hatası: {ex.Message}");
            // Hata olsa bile dokunulmamış veriyi döndür (tolerable failure)
        }

        return Results.Content(json, "application/json");
    }
    catch (Exception ex)
    {
        return Results.Problem($"Hata: {ex.Message}");
    }
});

// ─── İş Yatırım API (401 Verdiği İçin Yahoo Finance Üzerinden Simüle Edildi) ──

app.MapGet("/api/stock/dividends/{symbol}", async (string symbol, IHttpClientFactory factory) =>
{
    using var client = factory.CreateClient();
    client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
    
    var formattedSymbol = symbol.Trim().Split('.')[0].ToUpper() + ".IS";
    var url = $"https://query2.finance.yahoo.com/v8/finance/chart/{formattedSymbol}?interval=1mo&events=div&range=max";
    
    try {
        var response = await client.GetStringAsync(url);
        using var doc = System.Text.Json.JsonDocument.Parse(response);
        var resultNode = doc.RootElement.GetProperty("chart").GetProperty("result")[0];
        
        var list = new List<Dictionary<string, string>>();
        if (resultNode.TryGetProperty("events", out var eventsNode) && eventsNode.TryGetProperty("dividends", out var divObj))
        {
            foreach (var d in divObj.EnumerateObject())
            {
                var val = d.Value;
                var dt = DateTimeOffset.FromUnixTimeSeconds(val.GetProperty("date").GetInt64()).LocalDateTime;
                var amt = Math.Round(val.GetProperty("amount").GetDecimal(), 5).ToString(System.Globalization.CultureInfo.InvariantCulture);
                list.Add(new Dictionary<string, string> { 
                    { "YIL", dt.Year.ToString() }, 
                    { "TARIH", dt.ToString("dd.MM.yyyy") }, 
                    { "HISSE_BASINA_TEMETTU_BRUT_TL", amt } 
                });
            }
        }
        return Results.Ok(new { value = list.OrderByDescending(x => x["YIL"]).ToList() });
    } catch (Exception ex) { return Results.Problem(ex.Message); }
});

app.MapGet("/api/stock/splits/{symbol}", async (string symbol, IHttpClientFactory factory) =>
{
    using var client = factory.CreateClient();
    client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
    
    var formattedSymbol = symbol.Trim().Split('.')[0].ToUpper() + ".IS";
    var url = $"https://query2.finance.yahoo.com/v8/finance/chart/{formattedSymbol}?interval=1mo&events=split&range=max";
    
    try {
        var response = await client.GetStringAsync(url);
        using var doc = System.Text.Json.JsonDocument.Parse(response);
        var resultNode = doc.RootElement.GetProperty("chart").GetProperty("result")[0];
        
        var list = new List<Dictionary<string, string>>();
        if (resultNode.TryGetProperty("events", out var eventsNode) && eventsNode.TryGetProperty("splits", out var splitsObj))
        {
            foreach (var s in splitsObj.EnumerateObject())
            {
                var val = s.Value;
                var dt = DateTimeOffset.FromUnixTimeSeconds(val.GetProperty("date").GetInt64()).LocalDateTime;
                var num = val.GetProperty("numerator").GetDecimal();
                var den = val.GetProperty("denominator").GetDecimal();
                
                if (den > 0) 
                {
                    var multiplier = num / den;
                    var bedelsizOran = ((multiplier - 1m) * 100m).ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
                    list.Add(new Dictionary<string, string> { 
                        { "YIL", dt.Year.ToString() }, 
                        { "TARIH", dt.ToString("dd.MM.yyyy") }, 
                        { "BEDELSIZ_ARTIRIM_ORANI", bedelsizOran }, 
                        { "BEDELLI_ARTIRIM_ORANI", "0" } 
                    });
                }
            }
        }
        return Results.Ok(new { value = list.OrderByDescending(x => x["YIL"]).ToList() });
    } catch (Exception ex) { return Results.Problem(ex.Message); }
});

app.MapRazorPages();
app.MapControllers();
app.MapFallbackToFile("index.html");

app.Run();

public record MetricsRequest(decimal CurrentValue);
