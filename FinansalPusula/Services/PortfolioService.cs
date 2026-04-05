using System.Net.Http.Json;

namespace FinansalPusula.Services;

/// <summary>
/// Portföy işlemlerini sunucu tarafındaki SQLite veritabanında saklar.
/// Veriler HttpClient üzerinden API'den çekilir.
/// </summary>
public class PortfolioService
{
    private readonly HttpClient _httpClient;

    public PortfolioService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    /// <summary>
    /// İşlemi sunucuya kaydeder.
    /// </summary>
    public async Task AddTransactionAsync(PortfolioTransaction transaction)
    {
        try
        {
            var response = await _httpClient.PostAsJsonAsync("/api/portfolio", transaction);
            response.EnsureSuccessStatusCode();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PortfolioService] AddTransaction Hatası: {ex.Message}");
        }
    }

    /// <summary>
    /// Sunucudan tüm işlemleri çeker.
    /// </summary>
    public async Task<List<PortfolioTransaction>> GetTransactionsAsync()
    {
        try
        {
            var transactions = await _httpClient.GetFromJsonAsync<List<PortfolioTransaction>>("/api/portfolio");
            return transactions ?? new List<PortfolioTransaction>();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PortfolioService] GetTransactions Hatası: {ex.Message}");
            return new List<PortfolioTransaction>();
        }
    }

    /// <summary>
    /// Belirtilen ID'nin işlemini sunucudan siler.
    /// </summary>
    public async Task DeleteTransactionAsync(string id)
    {
        try
        {
            var response = await _httpClient.DeleteAsync($"/api/portfolio/{id}");
            response.EnsureSuccessStatusCode();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PortfolioService] DeleteTransaction Hatası: {ex.Message}");
        }
    }

    /// <summary>
    /// Portföy performans metriklerini (CAGR ve XIRR) çeker.
    /// Opsiyonel olarak sembol verilirse sadece o hisse için hesaplar.
    /// </summary>
    public async Task<PortfolioMetricsRecord?> GetPortfolioMetricsAsync(decimal currentValue, string? symbol = null)
    {
        try
        {
            var response = await _httpClient.PostAsJsonAsync("/api/portfolio/metrics", new { CurrentValue = currentValue, Symbol = symbol });
            return await response.Content.ReadFromJsonAsync<PortfolioMetricsRecord>();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PortfolioService] GetPortfolioMetrics Hatası: {ex.Message}");
            return null;
        }
    }
}

public record PortfolioMetricsRecord(double Cagr, double Xirr, double TargetInflation = 64.0);
