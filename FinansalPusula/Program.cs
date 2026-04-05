using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using FinansalPusula;
using FinansalPusula.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

<<<<<<< HEAD
builder.Services.AddScoped(sp => new HttpClient
{
	BaseAddress = new Uri(builder.HostEnvironment.BaseAddress),
	Timeout = TimeSpan.FromMinutes(20)
});
builder.Services.AddAuthorizationCore();
builder.Services.AddScoped<ServerAuthStateProvider>();
builder.Services.AddScoped<AuthenticationStateProvider>(sp => sp.GetRequiredService<ServerAuthStateProvider>());
=======
builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });
>>>>>>> 63ca2651a1b900fcc4e12909ce1f025e790bbaac

// Custom Services
builder.Services.AddScoped<BudgetService>();
builder.Services.AddScoped<InvestmentService>();
builder.Services.AddScoped<IsYatirimService>();
builder.Services.AddScoped<PortfolioService>();
builder.Services.AddScoped<YahooFinanceService>();

await builder.Build().RunAsync();
