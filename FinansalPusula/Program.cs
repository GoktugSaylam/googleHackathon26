using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using FinansalPusula;
using FinansalPusula.Services;
using Microsoft.AspNetCore.Components.Authorization;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddScoped(sp => new HttpClient
{
	BaseAddress = new Uri(builder.HostEnvironment.BaseAddress),
	Timeout = TimeSpan.FromMinutes(20)
});
builder.Services.AddAuthorizationCore();
builder.Services.AddScoped<ServerAuthStateProvider>();
builder.Services.AddScoped<AuthenticationStateProvider>(sp => sp.GetRequiredService<ServerAuthStateProvider>());

// Custom Services
builder.Services.AddScoped<BudgetService>();
builder.Services.AddScoped<InvestmentService>();
builder.Services.AddScoped<IsYatirimService>();
builder.Services.AddScoped<PortfolioService>();
builder.Services.AddScoped<YahooFinanceService>();

await builder.Build().RunAsync();
