using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.HttpOverrides;

var builder = WebApplication.CreateBuilder(args);

var googleClientId = builder.Configuration["Authentication:Google:ClientId"];
var googleClientSecret = builder.Configuration["Authentication:Google:ClientSecret"];
var googleCallbackPath = builder.Configuration["Authentication:Google:CallbackPath"];

if (string.IsNullOrWhiteSpace(googleCallbackPath))
{
    googleCallbackPath = "/authentication/login-callback";
}

if (string.IsNullOrWhiteSpace(googleClientId))
{
    throw new InvalidOperationException("Authentication:Google:ClientId eksik. Server appsettings veya ortam degiskenine ekleyin.");
}

if (string.IsNullOrWhiteSpace(googleClientSecret))
{
    throw new InvalidOperationException("Authentication:Google:ClientSecret eksik. Bu degeri sadece server tarafinda User Secrets veya ortam degiskeni ile tanimlayin.");
}

builder.Services.AddAuthentication(options =>
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
})
.AddGoogle(options =>
{
    options.ClientId = googleClientId;
    options.ClientSecret = googleClientSecret;
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

builder.Services.AddAuthorization();
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseForwardedHeaders();
app.UseHttpsRedirection();
app.UseBlazorFrameworkFiles();
app.UseStaticFiles();

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/bff/login", (string? returnUrl) =>
{
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
