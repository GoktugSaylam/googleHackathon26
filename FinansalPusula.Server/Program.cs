using System.Net.Http;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllersWithViews();
builder.Services.AddRazorPages();

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
app.MapRazorPages();
app.MapControllers();

// ─── Yahoo Finance Proxy Endpoint ─────────────────────────────────────────────

app.MapGet("/api/stock/price/{symbol}", async (string symbol, string? date) =>
{
    using var client = new HttpClient();
    client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
    client.Timeout = TimeSpan.FromSeconds(15);

    try
    {
        var formattedSymbol = symbol.Trim().ToUpper();
        if (!formattedSymbol.EndsWith(".IS")) formattedSymbol += ".IS";

        string yahooUrl;

        if (!string.IsNullOrEmpty(date) && DateTime.TryParse(date, out var targetDate))
        {
            // Tek bir gün bazen boş dönebiliyor (zaman dilimi farkı vb), 
            // bu yüzden hedeflenen tarihin 3 gün öncesinden başlayıp 1 gün sonrasına kadar çekelim.
            var p1 = ((DateTimeOffset)DateTime.SpecifyKind(targetDate.Date.AddDays(-3), DateTimeKind.Local)).ToUnixTimeSeconds();
            var p2 = ((DateTimeOffset)DateTime.SpecifyKind(targetDate.Date.AddDays(1), DateTimeKind.Local)).ToUnixTimeSeconds();
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
        return Results.Content(json, "application/json");
    }
    catch (Exception ex)
    {
        return Results.Problem($"Istek basarisiz: {ex.Message}");
    }
});

app.MapGet("/api/stock/range/{symbol}", async (string symbol, string from, string to) =>
{
    using var client = new HttpClient();
    client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
    client.Timeout = TimeSpan.FromSeconds(15);

    try
    {
        var formattedSymbol = symbol.Trim().ToUpper();
        if (!formattedSymbol.EndsWith(".IS")) formattedSymbol += ".IS";

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

app.MapGet("/api/stock/dividends/{symbol}", async (string symbol) =>
{
    using var client = new HttpClient();
    client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0");
    var url = $"https://www.isyatirim.com.tr/_layouts/15/IsYatirim.Website/Common/Data.aspx/HisseTekilTemettu?hisse={symbol}";
    try {
        var json = await client.GetStringAsync(url);
        return Results.Content(json, "application/json");
    } catch (Exception ex) { return Results.Problem(ex.Message); }
});

app.MapGet("/api/stock/splits/{symbol}", async (string symbol) =>
{
    using var client = new HttpClient();
    client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0");
    var url = $"https://www.isyatirim.com.tr/_layouts/15/IsYatirim.Website/Common/Data.aspx/HisseTekilSermayeArtirimlari?hisse={symbol}";
    try {
        var json = await client.GetStringAsync(url);
        return Results.Content(json, "application/json");
    } catch (Exception ex) { return Results.Problem(ex.Message); }
});

// Any unmatched route goes to the Blazor WASM client
app.MapFallbackToFile("index.html");

app.Run();
