using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using System.Text.Json.Serialization;

namespace FinansalPusula.Services;

public class GeminiService
{
    private readonly HttpClient _httpClient;
    private readonly string _apiKey;

    public GeminiService(HttpClient httpClient, IConfiguration configuration)
    {
        _httpClient = httpClient;
        _apiKey = configuration["GeminiApiKey"] ?? string.Empty;
    }

    public async Task<ExpenseReport?> AnalyzeExpensesAsync(string ocrText)
    {
        if (string.IsNullOrEmpty(_apiKey) || _apiKey == "YOUR_GEMINI_API_KEY")
        {
            throw new Exception("Lütfen appsettings.json dosyasında geçerli bir Gemini API anahtarı sağlayın.");
        }

        var prompt = $@"Sen bir finansal danışmansın. Aşağıdaki OCR verisindeki harcamaları analiz et ve JSON formatında geri döndür.
Giderleri ve abonelikleri (Netflix, Spotify vb.) tespit et. Abonelikler için daha ekonomik alternatifler öner (örn: aile paketi, yıllık ödeme).
JSON yapısı (SADECE JSON döndür, açıklama ekleme):
{{
  ""expenses"": [
    {{ ""merchant"": ""Market/Firma Adı"", ""date"": ""GG.AA.YYYY"", ""amount"": 0.0, ""category"": ""Kategori"" }}
  ],
  ""subscriptions"": [
      {{ ""name"": ""Hizmet Adı"", ""cost"": 0.0, ""alternative"": ""Alternatif öneri"", ""savingsAdvice"": ""Tasarruf tavsiyesi"" }}
  ],
  ""totalSpending"": 0.0,
  ""summaryAdvice"": ""Genel finansal harcama özeti ve tasarruf tavsiyesi""
}}
Veriler: {ocrText}";

        var request = new
        {
            contents = new[]
            {
                new { parts = new[] { new { text = prompt } } }
            },
            generationConfig = new
            {
                responseMimeType = "application/json"
            }
        };

        var response = await _httpClient.PostAsJsonAsync($"https://generativelanguage.googleapis.com/v1beta/models/gemini-1.5-flash:generateContent?key={_apiKey}", request);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync();
            throw new Exception($"Gemini API Hatası: {error}");
        }

        var geminiResponse = await response.Content.ReadFromJsonAsync<GeminiResponse>();
        var jsonResult = geminiResponse?.Candidates?[0]?.Content?.Parts?[0]?.Text;

        if (string.IsNullOrEmpty(jsonResult)) return null;

        return System.Text.Json.JsonSerializer.Deserialize<ExpenseReport>(jsonResult, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    }

    public async Task<string> AnalyzePortfolioAsync(List<StockData> stocks)
    {
        if (string.IsNullOrEmpty(_apiKey) || _apiKey == "YOUR_GEMINI_API_KEY")
        {
            throw new Exception("Gemini API anahtarı eksik.");
        }

        var stockListText = string.Join(", ", stocks.Select(s => $"{s.Symbol}: ${s.Price} (%{s.Change})"));
        var prompt = $@"Aşağıdaki portföy verilerini incele ve finansal bir danışman gibi 'insan dilinde, basit ve samimi' bir analiz üret. 
Kullanıcıya portföyünün genel durumu, dikkat çeken hisseleri ve basit yatırım tavsiyeleri (Yatırım tavsiyesi olmadığını belirterek) ver. 
Hisseler: {stockListText}";

        var request = new
        {
            contents = new[] { new { parts = new[] { new { text = prompt } } } }
        };

        var response = await _httpClient.PostAsJsonAsync($"https://generativelanguage.googleapis.com/v1beta/models/gemini-1.5-flash:generateContent?key={_apiKey}", request);
        var geminiResponse = await response.Content.ReadFromJsonAsync<GeminiResponse>();
        return geminiResponse?.Candidates?[0]?.Content?.Parts?[0]?.Text ?? "Analiz üretilemedi.";
    }
}

// Gemini Response Models
public class GeminiResponse
{
    public List<Candidate>? Candidates { get; set; }
}

public class Candidate
{
    public Content? Content { get; set; }
}

public class Content
{
    public List<Part>? Parts { get; set; }
}

public class Part
{
    public string? Text { get; set; }
}

// Business Models
public class ExpenseReport
{
    [JsonPropertyName("expenses")]
    public List<ExpenseItem>? Expenses { get; set; }

    [JsonPropertyName("subscriptions")]
    public List<SubscriptionItem>? Subscriptions { get; set; }

    [JsonPropertyName("totalSpending")]
    public decimal TotalSpending { get; set; }

    [JsonPropertyName("summaryAdvice")]
    public string? SummaryAdvice { get; set; }
}

public class ExpenseItem
{
    public string? Merchant { get; set; }
    public string? Date { get; set; }
    public decimal Amount { get; set; }
    public string? Category { get; set; }
}

public class SubscriptionItem
{
    public string? Name { get; set; }
    public decimal Cost { get; set; }
    public string? Alternative { get; set; }
    public string? SavingsAdvice { get; set; }
}

public class StockData
{
    public string Symbol { get; set; } = "";
    public decimal Price { get; set; }
    public decimal Change { get; set; }
}
