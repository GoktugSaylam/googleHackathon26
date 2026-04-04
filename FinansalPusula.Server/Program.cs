using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.HttpOverrides;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllersWithViews();
builder.Services.AddRazorPages();

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
        if (context.Request.Path.StartsWithSegments("/bff", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        }

        context.Response.Redirect(context.RedirectUri);
        return Task.CompletedTask;
    };

    options.Events.OnRedirectToAccessDenied = context =>
    {
        if (context.Request.Path.StartsWithSegments("/bff", StringComparison.OrdinalIgnoreCase))
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
            context.Response.Redirect($"/login?error={message}");
            context.HandleResponse();
            return Task.CompletedTask;
        };
    });
}

builder.Services.AddAuthorization();
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseWebAssemblyDebugging();
}
else
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseForwardedHeaders();
app.UseHttpsRedirection();
app.UseBlazorFrameworkFiles();
app.UseStaticFiles();

app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

app.MapRazorPages();
app.MapControllers();

app.MapGet("/bff/login", (string? returnUrl) =>
{
    if (!isGoogleConfigured)
    {
        return Results.Problem(
            "Google OAuth ayarlari eksik. Authentication:Google:ClientId ve Authentication:Google:ClientSecret degerlerini server tarafinda tanimlayin.",
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    var safeReturnUrl = NormalizeReturnUrl(returnUrl);
    var properties = new AuthenticationProperties
    {
        RedirectUri = safeReturnUrl
    };

    return Results.Challenge(properties, [GoogleDefaults.AuthenticationScheme]);
}).AllowAnonymous();

app.MapGet("/bff/logout", async (HttpContext context, string? returnUrl) =>
{
    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);

    var safeReturnUrl = NormalizeReturnUrl(returnUrl);
    return Results.Redirect($"/login?returnUrl={Uri.EscapeDataString(safeReturnUrl)}");
}).AllowAnonymous();

app.MapGet("/bff/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous();

app.MapGet("/bff/user", (ClaimsPrincipal user) =>
{
    if (user.Identity?.IsAuthenticated != true)
    {
        return Results.Unauthorized();
    }

    var claims = user.Claims
        .Select(c => new AuthClaim(c.Type, c.Value))
        .ToArray();

    return Results.Ok(new AuthUserResponse(true, claims));
});

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
            return Results.NotFound(new { error = "Yahoo Finance veri dondurmedi." });

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
            return Results.BadRequest(new { error = "Gecersiz tarih." });

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

internal sealed record AuthClaim(string Type, string Value);
internal sealed record AuthUserResponse(bool IsAuthenticated, AuthClaim[] Claims);
