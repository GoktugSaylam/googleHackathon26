using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using FinansalPusula;
using FinansalPusula.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });

// Custom Services
builder.Services.AddScoped<VisionApiService>();
builder.Services.AddScoped<GeminiService>();
builder.Services.AddScoped<BudgetService>();
builder.Services.AddScoped<InvestmentService>();
builder.Services.AddScoped<IsYatirimService>();
builder.Services.AddScoped<PortfolioService>();
builder.Services.AddScoped<YahooFinanceService>();

await builder.Build().RunAsync();
