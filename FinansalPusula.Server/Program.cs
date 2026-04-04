using Microsoft.AspNetCore.ResponseCompression;
using FinansalPusula.Server.Data;
using FinansalPusula.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllersWithViews();
builder.Services.AddRazorPages();
builder.Services.AddHttpClient();

// SQLite Repository
builder.Services.AddSingleton<TransactionRepository>();

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
