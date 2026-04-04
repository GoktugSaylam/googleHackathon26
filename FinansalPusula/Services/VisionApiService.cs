using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;

namespace FinansalPusula.Services;

public class VisionApiService
{
    private readonly HttpClient _httpClient;
    private readonly string _apiKey;

    public VisionApiService(HttpClient httpClient, IConfiguration configuration)
    {
        _httpClient = httpClient;
        _apiKey = configuration["GoogleCloudVisionApiKey"] ?? string.Empty;
    }

    public async Task<string> ExtractTextAsync(byte[] fileContent)
    {
        if (string.IsNullOrEmpty(_apiKey) || _apiKey == "YOUR_VISION_API_KEY")
        {
            throw new Exception("Lütfen appsettings.json dosyasında geçerli bir Vision API anahtarı sağlayın.");
        }

        var base64Content = Convert.ToBase64String(fileContent);

        var request = new
        {
            requests = new[]
            {
                new {
                    image = new { content = base64Content },
                    features = new[] { new { type = "TEXT_DETECTION" } }
                }
            }
        };

        var response = await _httpClient.PostAsJsonAsync($"https://vision.googleapis.com/v1/images:annotate?key={_apiKey}", request);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync();
            throw new Exception($"Google Vision API Hatası: {error}");
        }

        var visionResponse = await response.Content.ReadFromJsonAsync<VisionResponse>();

        // textAnnotations[0] contains the full text
        return visionResponse?.Responses?[0]?.FullTextAnnotation?.Text ?? "Metin bulunamadı.";
    }
}

// Minimal models for JSON deserialization
public class VisionResponse
{
    public List<AnnotateImageResponse>? Responses { get; set; }
}

public class AnnotateImageResponse
{
    public FullTextAnnotation? FullTextAnnotation { get; set; }
}

public class FullTextAnnotation
{
    public string? Text { get; set; }
}
