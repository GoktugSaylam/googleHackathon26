using System.Security.Claims;
using FinansalPusula.Server.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.ResponseCompression;
using FinansalPusula.Server.Data;
using FinansalPusula.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllersWithViews();
builder.Services.AddRazorPages();
builder.Services.AddHttpClient();

var googleClientId = builder.Configuration["Authentication:Google:ClientId"];
var googleClientSecret = builder.Configuration["Authentication:Google:ClientSecret"];
var googleCallbackPath = builder.Configuration["Authentication:Google:CallbackPath"];

if (string.IsNullOrWhiteSpace(googleCallbackPath))
{
    googleCallbackPath = "/signin-google";
}

var isGoogleConfigured = !string.IsNullOrWhiteSpace(googleClientId)
    && !string.IsNullOrWhiteSpace(googleClientSecret);

var authBuilder = builder.Services.AddAuthentication(options =>
{
    options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    options.DefaultAuthenticateScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    options.DefaultSignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = CookieAuthenticationDefaults.AuthenticationScheme;
})
.AddCookie(options =>
{
    options.Cookie.Name = "__Host.FinansalPusula.Auth";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.ExpireTimeSpan = TimeSpan.FromHours(8);
    options.SlidingExpiration = true;

    options.Events.OnRedirectToLogin = context =>
    {
        if (context.Request.Path.StartsWithSegments("/bff", StringComparison.OrdinalIgnoreCase)
            || context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        }

        context.Response.Redirect(context.RedirectUri);
        return Task.CompletedTask;
    };

    options.Events.OnRedirectToAccessDenied = context =>
    {
        if (context.Request.Path.StartsWithSegments("/bff", StringComparison.OrdinalIgnoreCase)
            || context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        }

        context.Response.Redirect(context.RedirectUri);
        return Task.CompletedTask;
    };
});

if (isGoogleConfigured)
{
    authBuilder.AddGoogle(options =>
    {
        options.ClientId = googleClientId!;
        options.ClientSecret = googleClientSecret!;
        options.CallbackPath = googleCallbackPath;
        options.SaveTokens = true;
        options.Scope.Add("openid");
        options.Scope.Add("profile");
        options.Scope.Add("email");
        options.ClaimActions.MapJsonKey("picture", "picture");

        options.Events.OnRemoteFailure = context =>
        {
            var message = Uri.EscapeDataString(context.Failure?.Message ?? "Google OAuth hatasi olustu.");
            context.Response.Redirect($"/?authError={message}");
            context.HandleResponse();
            return Task.CompletedTask;
        };

        options.Events.OnTicketReceived = async context =>
        {
            var googleUserId = TryResolveGoogleUserId(context.Principal);
            if (string.IsNullOrWhiteSpace(googleUserId))
            {
                return;
            }

            var email = context.Principal?.FindFirst(ClaimTypes.Email)?.Value
                ?? context.Principal?.FindFirst("email")?.Value;
            var displayName = context.Principal?.FindFirst("name")?.Value
                ?? context.Principal?.Identity?.Name;
            var pictureUrl = context.Principal?.FindFirst("picture")?.Value;
            var utcNow = DateTime.UtcNow;

            try
            {
                if (context.HttpContext.RequestServices.GetService(typeof(GoogleAccountRepository)) is GoogleAccountRepository accountRepository)
                {
                    await accountRepository.UpsertAsync(googleUserId, email, displayName, pictureUrl, utcNow);
                }
            }
            catch (Exception ex)
            {
                if (context.HttpContext.RequestServices.GetService(typeof(ILoggerFactory)) is ILoggerFactory loggerFactory)
                {
                    var logger = loggerFactory.CreateLogger("GoogleAccountSync");
                    logger.LogWarning(ex, "Google hesap kaydi guncellenemedi. UserId: {GoogleUserId}", googleUserId);
                }
            }
        };
    });
}

builder.Services.AddAuthorization();
builder.Services.AddHttpClient<StatementAnalysisService>(client =>
{
    client.Timeout = TimeSpan.FromMinutes(15);
});
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});

// SQLite Repository
builder.Services.AddSingleton<TransactionRepository>();
builder.Services.AddSingleton<ExpenseRepository>();
builder.Services.AddSingleton<GoogleAccountRepository>();
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
app.UseAuthentication();
app.UseAuthorization();

if (!isGoogleConfigured)
{
    app.Logger.LogWarning("Google OAuth devre disi: Authentication:Google:ClientId veya ClientSecret eksik.");
}

// ─── API Authentication ───────────────────────────────────────────────────────

app.MapGet("/api/auth/login", async Task<IResult> (HttpContext context, string? returnUrl) =>
{
    if (!isGoogleConfigured)
    {
        return Results.Problem(
            detail: "Google OAuth sunucuda tam tanimli degil. Authentication:Google:ClientId ve ClientSecret gereklidir.",
            title: "Google OAuth devre disi",
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    var normalizedReturnUrl = NormalizeReturnUrl(returnUrl);
    var properties = new AuthenticationProperties
    {
        RedirectUri = normalizedReturnUrl
    };

    await context.ChallengeAsync(GoogleDefaults.AuthenticationScheme, properties);
    return Results.Empty;
});

app.MapGet("/api/auth/logout", async (HttpContext context, string? returnUrl) =>
{
    var normalizedReturnUrl = NormalizeReturnUrl(returnUrl);
    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    context.Response.Redirect(normalizedReturnUrl);
});

app.MapGet("/api/auth/user", async (HttpContext context, GoogleAccountRepository accountRepository) =>
{
    if (context.User.Identity?.IsAuthenticated != true)
    {
        return Results.Unauthorized();
    }

    var googleUserId = TryResolveGoogleUserId(context.User);
    if (string.IsNullOrWhiteSpace(googleUserId))
    {
        return Results.Unauthorized();
    }

    await accountRepository.TouchLastSeenAsync(googleUserId, DateTime.UtcNow);

    var claims = context.User.Claims
        .Where(c => !string.IsNullOrWhiteSpace(c.Type) && !string.IsNullOrWhiteSpace(c.Value))
        .Select(c => new AuthClaim(c.Type, c.Value))
        .ToArray();

    return Results.Ok(new AuthUserResponse(true, claims));
});

// ─── Budget API ───────────────────────────────────────────────────────────────

app.MapGet("/api/budget/reports", async (HttpContext context, ExpenseRepository repo) =>
{
    var googleUserId = TryResolveGoogleUserId(context.User);
    if (string.IsNullOrWhiteSpace(googleUserId))
    {
        return Results.Unauthorized();
    }

    return Results.Ok(await repo.GetAllAsync(googleUserId));
});

app.MapPost("/api/budget/reports", async (HttpContext context, ExpenseReport report, ExpenseRepository repo) =>
{
    var googleUserId = TryResolveGoogleUserId(context.User);
    if (string.IsNullOrWhiteSpace(googleUserId))
    {
        return Results.Unauthorized();
    }

    if (report is null || string.IsNullOrWhiteSpace(report.Period))
    {
        return Results.BadRequest(new { message = "Rapor donemi zorunludur." });
    }

    await repo.AddOrUpdateAsync(report, googleUserId);
    return Results.Ok();
});
app.MapDelete("/api/budget/reports", async (HttpContext context, ExpenseRepository repo) =>
{
    var googleUserId = TryResolveGoogleUserId(context.User);
    if (string.IsNullOrWhiteSpace(googleUserId))
    {
        return Results.Unauthorized();
    }

    await repo.ClearReportsAsync(googleUserId);
    return Results.Ok();
});

// ─── Portfolio API ────────────────────────────────────────────────────────────

app.MapGet("/api/portfolio", async (HttpContext context, TransactionRepository repo) =>
{
    var googleUserId = TryResolveGoogleUserId(context.User);
    if (string.IsNullOrWhiteSpace(googleUserId))
    {
        return Results.Unauthorized();
    }

    return Results.Ok(await repo.GetAllAsync(googleUserId));
});

app.MapGet("/api/settings", async (HttpContext context, ExpenseRepository repo) =>
{
    var googleUserId = TryResolveGoogleUserId(context.User);
    if (string.IsNullOrWhiteSpace(googleUserId))
    {
        return Results.Unauthorized();
    }

    return Results.Ok(await repo.GetSettingsAsync(googleUserId));
});

app.MapPost("/api/settings", async (HttpContext context, UserSettings settings, ExpenseRepository repo) =>
{
    var googleUserId = TryResolveGoogleUserId(context.User);
    if (string.IsNullOrWhiteSpace(googleUserId))
    {
        return Results.Unauthorized();
    }

    if (settings is null)
    {
        return Results.BadRequest(new { message = "Ayarlar bos olamaz." });
    }

    await repo.SaveSettingsAsync(settings, googleUserId);
    return Results.Ok();
});

app.MapPost("/api/ai/analyze-expenses", async (
    HttpContext context,
    AnalyzeExpensesRequest request,
    StatementAnalysisService analysisService,
    CancellationToken cancellationToken) =>
{
    if (context.User.Identity?.IsAuthenticated != true)
    {
        return Results.Unauthorized();
    }

    if (request is null || string.IsNullOrWhiteSpace(request.FileBytesBase64))  
    {
        return Results.BadRequest(new { message = "Dosya verisi zorunludur." });
    }

    byte[] fileBytes;
    try
    {
        fileBytes = Convert.FromBase64String(request.FileBytesBase64);
    }
    catch (FormatException)
    {
        return Results.BadRequest(new { message = "Dosya verisi gecersiz formatta." });
    }

    if (fileBytes.Length == 0)
    {
        return Results.BadRequest(new { message = "Bos dosya gonderilemez." }); 
    }

    const int maxFileSizeBytes = 10 * 1024 * 1024;
    if (fileBytes.Length > maxFileSizeBytes)
    {
        return Results.BadRequest(new { message = "Maksimum dosya boyutu 10 MB olmalidir." });
    }

    try
    {
        var reportJson = await analysisService.AnalyzeExpensesAsync(fileBytes, request.FileName, cancellationToken);
        return Results.Content(reportJson, "application/json");
    }
    catch (InvalidOperationException ex)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status502BadGateway);
    }
    catch (Exception ex)
    {
        return Results.Problem($"Beklenmeyen analiz hatasi: {ex.Message}", statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapPost("/api/portfolio", async (HttpContext context, PortfolioTransaction tx, TransactionRepository repo) =>
{
    var googleUserId = TryResolveGoogleUserId(context.User);
    if (string.IsNullOrWhiteSpace(googleUserId))
    {
        return Results.Unauthorized();
    }

    await repo.AddAsync(tx, googleUserId);
    return Results.Ok();
});

app.MapDelete("/api/portfolio/{id}", async (HttpContext context, string id, TransactionRepository repo) =>
{
    var googleUserId = TryResolveGoogleUserId(context.User);
    if (string.IsNullOrWhiteSpace(googleUserId))
    {
        return Results.Unauthorized();
    }

    await repo.DeleteAsync(id, googleUserId);
    return Results.Ok();
});

app.MapPost("/api/portfolio/metrics", async (HttpContext context, MetricsRequest request, TransactionRepository repo, ExpenseRepository expenseRepo, FinancialMetricsService metricsService) =>
{
    var googleUserId = TryResolveGoogleUserId(context.User);
    if (string.IsNullOrWhiteSpace(googleUserId))
    {
        return Results.Unauthorized();
    }

    var settings = await expenseRepo.GetSettingsAsync(googleUserId);
    var currentValue = request.CurrentValue;
    var txs = await repo.GetAllAsync(googleUserId);
    
    // Eğer sembol parametresi geldiyse sadece o hisseyi filtrele
    if (!string.IsNullOrEmpty(request.Symbol))
    {
        txs = txs.Where(t => t.Sembol?.Trim().ToUpper() == request.Symbol.Trim().ToUpper()).ToList();
    }

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
    double totalWithdrawn = flows.Where(f => f.Amount > 0 && f.Date != DateTime.Now.Date).Sum(f => f.Amount);
    
    double cagr = 0;
    if (totalInvested > 0)
    {
        var firstTransactionDate = flows.Where(f => f.Amount < 0).Min(f => f.Date);
        // Gerçek süreyi kullan; 30 günden kısa yatırımlar için min 30 gün uygula (sonsuz değerleri engeller)
        double years = Math.Max((DateTime.Now - firstTransactionDate).TotalDays / 365.25, 30.0 / 365.25);
        
        // Gerçekçi CAGR için: Güncel Değer + Çekilen Nakitler
        double totalEndValue = (double)currentValue + totalWithdrawn;
        cagr = metricsService.CalculateCagr(totalInvested, totalEndValue, years);
    }

    if (double.IsNaN(cagr) || double.IsInfinity(cagr)) cagr = 0;

    return Results.Ok(new { Cagr = cagr * 100, Xirr = xirr * 100, TargetInflation = (double)settings.AnnualInflationRate });
});

// ─── Yahoo Finance Proxy (With Split Correction for Raw Prices) ─────────────────

app.MapGet("/api/stock/price/{symbol}", async (string symbol, string? date, IHttpClientFactory factory) =>
{
    using var client = factory.CreateClient();
    client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");

    try
    {
        var formattedSymbol = symbol.Trim().ToUpper();
        // Kur sembollerini (= içerenleri) veya zaten uzantısı olanları .IS ile bozma
        if (!formattedSymbol.Contains("=") && !formattedSymbol.Contains("."))
        {
            formattedSymbol += ".IS";
        }

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
                            
                            // Bu veri noktasından SONRA gerçekleşen tüm bölünmeleri find ve fiyatı geri çarparak "un-adjust" et
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

static string NormalizeReturnUrl(string? returnUrl)
{
    if (string.IsNullOrWhiteSpace(returnUrl))
    {
        return "/";
    }

    if (!Uri.TryCreate(returnUrl, UriKind.Relative, out var parsed))
    {
        return "/";
    }

    var value = parsed.ToString();
    if (value.StartsWith("//", StringComparison.Ordinal) || value.StartsWith("\\\\", StringComparison.Ordinal))
    {
        return "/";
    }

    if (!value.StartsWith('/'))
    {
        value = $"/{value}";
    }

    return value;
}

static string? TryResolveGoogleUserId(ClaimsPrincipal? principal)
{
    if (principal?.Identity?.IsAuthenticated != true)
    {
        return null;
    }

    var nameIdentifier = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
    if (!string.IsNullOrWhiteSpace(nameIdentifier))
    {
        return nameIdentifier;
    }

    var sub = principal.FindFirst("sub")?.Value;
    if (!string.IsNullOrWhiteSpace(sub))
    {
        return sub;
    }

    var email = principal.FindFirst(ClaimTypes.Email)?.Value
        ?? principal.FindFirst("email")?.Value;

    return string.IsNullOrWhiteSpace(email) ? null : email;
}

internal sealed record AnalyzeExpensesRequest(string FileBytesBase64, string? FileName);
internal sealed record AuthClaim(string Type, string Value);
internal sealed record AuthUserResponse(bool IsAuthenticated, AuthClaim[] Claims);
public record MetricsRequest(decimal CurrentValue, string? Symbol = null);
