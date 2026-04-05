using FinansalPusula;
using FinansalPusula.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

// Root Components
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// HTTP Client
builder.Services.AddScoped(sp => new HttpClient
{
    BaseAddress = new Uri(builder.HostEnvironment.BaseAddress),
    Timeout = TimeSpan.FromMinutes(5)
});

// Authentication & Authorization
builder.Services.AddAuthorizationCore();
builder.Services.AddScoped<ServerAuthStateProvider>();
builder.Services.AddScoped<AuthenticationStateProvider>(
    sp => sp.GetRequiredService<ServerAuthStateProvider>());

// Application Services
builder.Services.AddScoped<BudgetService>();
builder.Services.AddScoped<InvestmentService>();
builder.Services.AddScoped<IsYatirimService>();
builder.Services.AddScoped<PortfolioService>();
builder.Services.AddScoped<YahooFinanceService>();

try
{
    await builder.Build().RunAsync();
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Fatal application error: {ex.Message}");
    Console.Error.WriteLine(ex.StackTrace);
}
