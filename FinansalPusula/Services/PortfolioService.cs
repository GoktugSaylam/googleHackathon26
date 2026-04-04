using Microsoft.JSInterop;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FinansalPusula.Services;

/// <summary>
/// Portföy işlemlerini localStorage üzerinde tutar.
/// Firebase/Firestore gerektirmez — tamamen offline çalışır.
/// </summary>
public class PortfolioService
{
    private readonly IJSRuntime _jsRuntime;

    public PortfolioService(IJSRuntime jsRuntime)
    {
        _jsRuntime = jsRuntime;
    }

    /// <summary>
    /// İşlemi localStorage'a kaydeder.
    /// </summary>
    public async Task AddTransactionAsync(PortfolioTransaction transaction)
    {
        try
        {
            // JS tarafına gönderirken snake_case/camelCase field names kullan
            var dto = new
            {
                id         = transaction.Id,
                tarih      = transaction.Tarih.ToString("o"),   // ISO 8601
                islemTipi  = (int)transaction.IslemTipi,
                sembol     = transaction.Sembol,
                adet       = transaction.Adet,
                birimFiyat = transaction.BirimFiyat
            };
            await _jsRuntime.InvokeVoidAsync("firestoreInterop.addTransaction", dto);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PortfolioService] AddTransaction Hatası: {ex.Message}");
        }
    }

    /// <summary>
    /// localStorage'dan tüm işlemleri çeker ve PortfolioTransaction listesine dönüştürür.
    /// </summary>
    public async Task<List<PortfolioTransaction>> GetTransactionsAsync()
    {
        try
        {
            // JS'ten raw JSON array olarak al
            var raw = await _jsRuntime.InvokeAsync<JsonElement[]>("firestoreInterop.getTransactions");
            var result = new List<PortfolioTransaction>();

            foreach (var el in raw)
            {
                try
                {
                    var tx = new PortfolioTransaction();

                    if (el.TryGetProperty("id", out var idProp))
                        tx.Id = idProp.GetString() ?? Guid.NewGuid().ToString();

                    if (el.TryGetProperty("tarih", out var tarihProp))
                    {
                        var tarihStr = tarihProp.GetString();
                        if (DateTime.TryParse(tarihStr, out var dt))
                            tx.Tarih = dt;
                    }

                    if (el.TryGetProperty("islemTipi", out var tipProp))
                    {
                        var tipInt = tipProp.GetInt32();
                        tx.IslemTipi = tipInt == 1 ? TransactionType.Satis : TransactionType.Alis;
                    }

                    if (el.TryGetProperty("sembol", out var sembolProp))
                        tx.Sembol = sembolProp.GetString() ?? "";

                    if (el.TryGetProperty("adet", out var adetProp))
                        tx.Adet = adetProp.GetDecimal();

                    if (el.TryGetProperty("birimFiyat", out var fiyatProp))
                        tx.BirimFiyat = fiyatProp.GetDecimal();

                    result.Add(tx);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[PortfolioService] İşlem parse hatası: {ex.Message}");
                }
            }

            return result;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PortfolioService] GetTransactions Hatası: {ex.Message}");
            return new List<PortfolioTransaction>();
        }
    }

    /// <summary>
    /// Belirtilen ID'nin işlemini siler.
    /// </summary>
    public async Task DeleteTransactionAsync(string id)
    {
        try
        {
            await _jsRuntime.InvokeVoidAsync("firestoreInterop.deleteTransaction", id);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PortfolioService] DeleteTransaction Hatası: {ex.Message}");
        }
    }
}
