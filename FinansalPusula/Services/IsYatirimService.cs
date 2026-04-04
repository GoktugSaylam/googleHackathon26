using System.Net.Http.Json;

namespace FinansalPusula.Services;

public class IsYatirimService
{
    private readonly HttpClient _httpClient;

    public IsYatirimService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<List<DividendData>> GetDividendsAsync(string symbol)
    {
        var cleanSymbol = symbol.Split('.')[0].ToUpper();
        try 
        {
            var url = $"/api/stock/dividends/{cleanSymbol}";
            var response = await _httpClient.GetFromJsonAsync<IsYatirimResponse<DividendData>>(url);
            return response?.Value ?? new List<DividendData>();
        }
        catch { return new List<DividendData>(); }
    }

    public async Task<List<SplitData>> GetSplitsAsync(string symbol)
    {
        var cleanSymbol = symbol.Split('.')[0].ToUpper();
        try 
        {
            var url = $"/api/stock/splits/{cleanSymbol}";
            var response = await _httpClient.GetFromJsonAsync<IsYatirimResponse<SplitData>>(url);
            return response?.Value ?? new List<SplitData>();
        }
        catch { return new List<SplitData>(); }
    }
}

public class IsYatirimResponse<T> 
{ 
    public List<T> Value { get; set; } = new(); 
}

public class DividendData 
{ 
    public string YIL { get; set; } = ""; 
    public string TARIH { get; set; } = ""; 
    public string HISSE_BASINA_TEMETTU_BRUT_TL { get; set; } = ""; 
}

public class SplitData 
{ 
    public string YIL { get; set; } = ""; 
    public string TARIH { get; set; } = ""; 
    public string BEDELSIZ_ARTIRIM_ORANI { get; set; } = ""; 
    public string BEDELLI_ARTIRIM_ORANI { get; set; } = "";
}
