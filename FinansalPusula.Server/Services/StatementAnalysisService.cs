using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;

namespace FinansalPusula.Server.Services;

public sealed class StatementAnalysisService
{
    private const string GeminiModel = "gemini-2.5-flash";
    private const int PdfPageBatchSize = 5;
    private const int DefaultPdfPageLimit = 600;
    private const int MinPdfPageLimit = 5;
    private const int MaxPdfPageLimit = 1200;
    private const int DefaultGeminiChunkCharLimit = 30000;
    private const int LargeDocumentChunkCharLimit = 22000;
    private const int DefaultGeminiParallelRequests = 3;
    private const int MaxGeminiParallelRequests = 6;
    private const int MaxChunkSplitDepth = 2;
    private const int MinChunkLengthForSplit = 2000;
    private const int MinDirectPdfTextChars = 2000;
    private const int MinDirectPdfTextFallbackChars = 800;
    private const int LargeTextThreshold = 180000;
    private const int MinFilteredTransactionLines = 80;
    private const int MinFilteredTextChars = 25000;
    private const int RepeatedBoilerplateThreshold = 8;
    private const decimal MaxFallbackAmount = 100000000m;

    private static readonly Regex DatePattern = new(
        @"\b\d{1,2}[./-]\d{1,2}[./-]\d{2,4}\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex AmountPattern = new(
        @"\b\d{1,3}(?:[.,]\d{3})*(?:[.,]\d{2})\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex TrailingCommaPattern = new(
        @",\s*(?<close>[}\]])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex JsonNumericFieldPattern = new(
        @"""(?<prop>amount|cost|totalSpending)""\s*:\s*(?<value>""?[-+0-9₺TLTRY\s.,]+""?)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly string[] SubscriptionKeywords =
    [
        "netflix", "spotify", "youtube", "prime", "amazon", "disney", "microsoft", "google one", "icloud"
    ];

    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<StatementAnalysisService> _logger;

    public StatementAnalysisService(HttpClient httpClient, IConfiguration configuration, ILogger<StatementAnalysisService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<string> AnalyzeExpensesAsync(byte[] fileBytes, string? fileName, CancellationToken cancellationToken = default)
    {
        var visionApiKey = _configuration["GoogleCloudVisionApiKey"];
        var geminiApiKey = _configuration["GeminiApiKey"];

        if (string.IsNullOrWhiteSpace(geminiApiKey))
        {
            throw new InvalidOperationException("Sunucuda GeminiApiKey tanimli degil.");
        }

        var isPdf = IsPdf(fileBytes, fileName);
        _logger.LogInformation("Statement analysis started. isPdf={IsPdf}, fileName={FileName}, byteCount={ByteCount}", isPdf, fileName, fileBytes.Length);

        if (!isPdf && string.IsNullOrWhiteSpace(visionApiKey))
        {
            throw new InvalidOperationException("Sunucuda GoogleCloudVisionApiKey tanimli degil. Gorsel dosya analizi icin gerekli.");
        }

        var ocrText = isPdf
            ? await ExtractTextFromPdfAsync(fileBytes, visionApiKey, cancellationToken)
            : await ExtractTextFromImageAsync(fileBytes, visionApiKey!, cancellationToken);

        if (string.IsNullOrWhiteSpace(ocrText))
        {
            throw new InvalidOperationException("Yuklenen belgeden metin cikarilamadi. Daha net bir goruntu veya metin secilebilir PDF deneyin.");
        }

        var analysisText = PrepareTextForAnalysis(ocrText);
        var chunkCharLimit = GetConfiguredGeminiChunkCharLimit();
        if (analysisText.Length > 300000)
        {
            // Large docs can produce truncated JSON; keep chunks tighter for more stable parsing.
            chunkCharLimit = Math.Min(chunkCharLimit, LargeDocumentChunkCharLimit);
        }

        _logger.LogInformation("Text prepared for Gemini. ocrChars={OcrChars}, analysisChars={AnalysisChars}, chunkLimit={ChunkLimit}",
            ocrText.Length,
            analysisText.Length,
            chunkCharLimit);

        var textChunks = SplitTextIntoChunks(analysisText, chunkCharLimit);
        var parallelRequests = GetConfiguredGeminiParallelRequests();

        _logger.LogInformation("Gemini chunking ready. chunkCount={ChunkCount}, parallelRequests={ParallelRequests}", textChunks.Count, parallelRequests);

        var partialReports = new List<ExpenseReportPayload>();

        using var semaphore = new SemaphoreSlim(Math.Min(parallelRequests, Math.Max(1, textChunks.Count)));
        var chunkTasks = textChunks
            .Select((chunk, index) => AnalyzeChunkWithConcurrencyAsync(
                chunk,
                geminiApiKey,
                index + 1,
                textChunks.Count,
                semaphore,
                cancellationToken))
            .ToList();

        var chunkResults = await Task.WhenAll(chunkTasks);
        var failedChunks = 0;
        foreach (var result in chunkResults)
        {
            if (!result.Success)
            {
                failedChunks++;
            }

            if (result.Reports.Count > 0)
            {
                partialReports.AddRange(result.Reports);
            }
        }

        if (partialReports.Count == 0)
        {
            var fallbackReport = BuildRegexFallbackReport(analysisText);
            if (fallbackReport is not null)
            {
                fallbackReport.SummaryAdvice = AppendRegexFallbackWarning(fallbackReport.SummaryAdvice);
                _logger.LogWarning("Gemini produced no usable chunk. Regex fallback was used. expenseCount={ExpenseCount}", fallbackReport.Expenses?.Count ?? 0);
                return SerializeReport(fallbackReport);
            }

            throw new InvalidOperationException("Gemini analiz sonucu bos dondu. Tum parcalar islenemedi.");
        }

        var mergedReport = MergeReports(partialReports);

        if (mergedReport.Expenses is null || mergedReport.Expenses.Count == 0)
        {
            var fallbackReport = BuildRegexFallbackReport(analysisText);
            if (fallbackReport is not null)
            {
                fallbackReport.SummaryAdvice = AppendRegexFallbackWarning(fallbackReport.SummaryAdvice);
                _logger.LogWarning("Merged report had zero expenses. Regex fallback was used. expenseCount={ExpenseCount}", fallbackReport.Expenses?.Count ?? 0);
                return SerializeReport(fallbackReport);
            }

            throw new InvalidOperationException("Belgedeki islemler analiz edilemedi. Daha okunakli bir PDF veya farkli bir dokuman deneyin.");
        }

        if (failedChunks > 0)
        {
            mergedReport.SummaryAdvice = AppendChunkFailureWarning(mergedReport.SummaryAdvice, failedChunks, textChunks.Count);
            _logger.LogWarning("Statement analysis completed with partial data. failedChunks={FailedChunks}, totalChunks={TotalChunks}, expenseCount={ExpenseCount}",
                failedChunks,
                textChunks.Count,
                mergedReport.Expenses?.Count ?? 0);
        }
        else
        {
            _logger.LogInformation("Statement analysis completed successfully. totalChunks={TotalChunks}, expenseCount={ExpenseCount}",
                textChunks.Count,
                mergedReport.Expenses?.Count ?? 0);
        }

        return SerializeReport(mergedReport);
    }

    private static string SerializeReport(ExpenseReportPayload report)
    {
        return JsonSerializer.Serialize(report, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
    }

    private static bool IsPdf(byte[] fileBytes, string? fileName)
    {
        if (!string.IsNullOrWhiteSpace(fileName) && fileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return fileBytes.Length >= 4
            && fileBytes[0] == 0x25 // %
            && fileBytes[1] == 0x50 // P
            && fileBytes[2] == 0x44 // D
            && fileBytes[3] == 0x46; // F
    }

    private async Task<string> ExtractTextFromImageAsync(byte[] fileContent, string apiKey, CancellationToken cancellationToken)
    {
        var base64Content = Convert.ToBase64String(fileContent);

        var request = new
        {
            requests = new[]
            {
                new
                {
                    image = new { content = base64Content },
                    features = new[] { new { type = "DOCUMENT_TEXT_DETECTION" } }
                }
            }
        };

        var response = await _httpClient.PostAsJsonAsync(
            $"https://vision.googleapis.com/v1/images:annotate?key={apiKey}",
            request,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"Google Vision API hatasi: {error}");
        }

        var visionResponse = await response.Content.ReadFromJsonAsync<VisionImageResponse>(cancellationToken: cancellationToken);
        var first = visionResponse?.Responses?.FirstOrDefault();

        if (!string.IsNullOrWhiteSpace(first?.Error?.Message))
        {
            throw new InvalidOperationException($"Google Vision API yanit hatasi: {first.Error.Message}");
        }

        return first?.FullTextAnnotation?.Text
            ?? first?.TextAnnotations?.FirstOrDefault()?.Description
            ?? string.Empty;
    }

    private async Task<string> ExtractTextFromPdfAsync(byte[] fileContent, string? apiKey, CancellationToken cancellationToken)
    {
        var maxPdfPages = GetConfiguredPdfPageLimit();
        _logger.LogInformation("PDF text extraction started. maxPdfPages={MaxPdfPages}", maxPdfPages);

        var directPdfText = TryExtractTextFromPdfDirect(fileContent, maxPdfPages, out var directReachedPageLimit);
        var directTextUseful = IsUsefulDirectPdfText(directPdfText);

        _logger.LogInformation("Direct PDF extraction done. chars={Chars}, useful={Useful}, reachedLimit={ReachedLimit}",
            directPdfText.Length,
            directTextUseful,
            directReachedPageLimit);

        if (directTextUseful && !directReachedPageLimit)
        {
            return directPdfText;
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            if (directTextUseful || directPdfText.Length >= MinDirectPdfTextFallbackChars)
            {
                if (directReachedPageLimit)
                {
                    throw new InvalidOperationException($"PDF analiz limiti {maxPdfPages} sayfada kaldi. Tum belgeyi kapsamak icin VisionPdfMaxPages degerini artirin.");
                }

                return directPdfText;
            }

            throw new InvalidOperationException("GoogleCloudVisionApiKey tanimli degil ve PDF'den dogrudan cikarilan metin yetersiz.");
        }

        var base64Content = Convert.ToBase64String(fileContent);

        var extractedChunks = new List<string>();
        var emptyBatchCount = 0;
        var reachedNaturalEnd = false;

        for (var startPage = 1; startPage <= maxPdfPages; startPage += PdfPageBatchSize)
        {
            var pageNumbers = Enumerable.Range(startPage, PdfPageBatchSize).ToArray();
            PdfBatchResult batchResult;
            try
            {
                batchResult = await ExtractPdfBatchTextAsync(base64Content, apiKey, pageNumbers, cancellationToken);
            }
            catch (InvalidOperationException) when (directTextUseful || directPdfText.Length >= MinDirectPdfTextFallbackChars)
            {
                _logger.LogWarning("Vision OCR failed. Falling back to direct PDF text. directChars={DirectChars}", directPdfText.Length);
                return directPdfText;
            }

            if (batchResult.OutOfRange)
            {
                reachedNaturalEnd = true;
                break;
            }

            if (string.IsNullOrWhiteSpace(batchResult.Text))
            {
                emptyBatchCount++;
                if (emptyBatchCount >= 2 && extractedChunks.Count > 0)
                {
                    reachedNaturalEnd = true;
                    break;
                }

                continue;
            }

            emptyBatchCount = 0;
            extractedChunks.Add(batchResult.Text);
        }

        if (extractedChunks.Count == 0)
        {
            if (directTextUseful || directPdfText.Length >= MinDirectPdfTextFallbackChars)
            {
                _logger.LogWarning("Vision OCR returned no text. Falling back to direct PDF text. directChars={DirectChars}", directPdfText.Length);
                return directPdfText;
            }

            return string.Empty;
        }

        if (!reachedNaturalEnd)
        {
            throw new InvalidOperationException($"PDF analiz limiti {maxPdfPages} sayfada kaldi. Tum belgeyi kapsamak icin VisionPdfMaxPages degerini artirin.");
        }

        var ocrText = string.Join("\n", extractedChunks);
        _logger.LogInformation("Vision OCR extraction completed. chars={Chars}", ocrText.Length);
        return ocrText;
    }

    private int GetConfiguredPdfPageLimit()
    {
        var raw = _configuration["VisionPdfMaxPages"];
        if (!int.TryParse(raw, out var parsed))
        {
            return DefaultPdfPageLimit;
        }

        return Math.Clamp(parsed, MinPdfPageLimit, MaxPdfPageLimit);
    }

    private int GetConfiguredGeminiChunkCharLimit()
    {
        var raw = _configuration["GeminiChunkCharLimit"];
        if (!int.TryParse(raw, out var parsed))
        {
            return DefaultGeminiChunkCharLimit;
        }

        return Math.Clamp(parsed, 10000, 120000);
    }

    private int GetConfiguredGeminiParallelRequests()
    {
        var raw = _configuration["GeminiParallelRequests"];
        if (!int.TryParse(raw, out var parsed))
        {
            return DefaultGeminiParallelRequests;
        }

        return Math.Clamp(parsed, 1, MaxGeminiParallelRequests);
    }

    private static string TryExtractTextFromPdfDirect(byte[] fileContent, int maxPdfPages, out bool reachedPageLimit)
    {
        reachedPageLimit = false;

        try
        {
            using var ms = new MemoryStream(fileContent);
            using var pdf = PdfDocument.Open(ms);

            var totalPages = pdf.NumberOfPages;
            var pagesToRead = Math.Min(totalPages, maxPdfPages);
            reachedPageLimit = totalPages > maxPdfPages;

            var sb = new StringBuilder(pagesToRead * 1200);

            for (var pageNo = 1; pageNo <= pagesToRead; pageNo++)
            {
                var pageText = pdf.GetPage(pageNo).Text;
                if (string.IsNullOrWhiteSpace(pageText))
                {
                    continue;
                }

                sb.AppendLine(pageText);
            }

            return sb.ToString();
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool IsUsefulDirectPdfText(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length < MinDirectPdfTextChars)
        {
            return false;
        }

        var dateHits = DatePattern.Matches(text).Count;
        var amountHits = AmountPattern.Matches(text).Count;

        return dateHits >= 3 && amountHits >= 8;
    }

    private static string PrepareTextForAnalysis(string rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText))
        {
            return rawText;
        }

        var withoutBoilerplate = RemoveRepeatedBoilerplate(rawText);

        if (withoutBoilerplate.Length < LargeTextThreshold)
        {
            return withoutBoilerplate;
        }

        var lines = withoutBoilerplate.Split('\n');
        var keepIndices = new SortedSet<int>();

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (!IsLikelyTransactionLine(line))
            {
                continue;
            }

            keepIndices.Add(i);
            if (i > 0) keepIndices.Add(i - 1);
            if (i + 1 < lines.Length) keepIndices.Add(i + 1);
        }

        if (keepIndices.Count < MinFilteredTransactionLines)
        {
            return withoutBoilerplate;
        }

        var sb = new StringBuilder(keepIndices.Count * 80);
        foreach (var idx in keepIndices)
        {
            sb.AppendLine(lines[idx]);
        }

        var filteredText = sb.ToString();
        if (filteredText.Length < MinFilteredTextChars)
        {
            return withoutBoilerplate;
        }

        return filteredText;
    }

    private static string RemoveRepeatedBoilerplate(string text)
    {
        var lines = text.Split('\n');
        if (lines.Length < 20)
        {
            return text;
        }

        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in lines)
        {
            var normalized = NormalizeLineForFrequency(rawLine);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                continue;
            }

            counts[normalized] = counts.TryGetValue(normalized, out var current)
                ? current + 1
                : 1;
        }

        var cleanedLines = new List<string>(lines.Length);
        foreach (var rawLine in lines)
        {
            var normalized = NormalizeLineForFrequency(rawLine);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                cleanedLines.Add(rawLine);
                continue;
            }

            var isBoilerplate = counts.TryGetValue(normalized, out var count)
                && count >= RepeatedBoilerplateThreshold
                && !IsLikelyTransactionLine(rawLine);

            if (!isBoilerplate)
            {
                cleanedLines.Add(rawLine);
            }
        }

        if (cleanedLines.Count == 0)
        {
            return text;
        }

        return string.Join('\n', cleanedLines);
    }

    private static string NormalizeLineForFrequency(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return string.Empty;
        }

        var normalized = Regex.Replace(line, "\\s+", " ").Trim();
        if (normalized.Length < 4 || normalized.Length > 140)
        {
            return string.Empty;
        }

        if (DatePattern.IsMatch(normalized) || AmountPattern.IsMatch(normalized))
        {
            return string.Empty;
        }

        return normalized.ToLowerInvariant();
    }

    private static bool IsLikelyTransactionLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var normalized = line.Trim().ToLowerInvariant();
        var hasDate = DatePattern.IsMatch(normalized);
        var hasAmount = AmountPattern.IsMatch(normalized)
            || normalized.Contains(" tl")
            || normalized.Contains("try")
            || normalized.Contains("₺");

        if (hasDate && hasAmount)
        {
            return true;
        }

        if (hasAmount && SubscriptionKeywords.Any(keyword => normalized.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return false;
    }

    private async Task<ChunkAnalysisResult> AnalyzeChunkWithConcurrencyAsync(
        string chunkText,
        string geminiApiKey,
        int chunkIndex,
        int totalChunks,
        SemaphoreSlim semaphore,
        CancellationToken cancellationToken)
    {
        await semaphore.WaitAsync(cancellationToken);
        try
        {
            var reports = await AnalyzeChunkWithFallbackAsync(
                chunkText,
                geminiApiKey,
                chunkIndex,
                totalChunks,
                splitDepth: 0,
                cancellationToken);

            return new ChunkAnalysisResult(reports, reports.Count > 0);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Chunk analysis failed and will be skipped. chunk={Chunk}/{Total}, chars={Chars}", chunkIndex, totalChunks, chunkText.Length);

            var fallbackReport = BuildRegexFallbackReport(chunkText);
            if (fallbackReport is not null)
            {
                fallbackReport.SummaryAdvice = AppendRegexFallbackWarning(fallbackReport.SummaryAdvice);
                _logger.LogInformation("Regex fallback produced chunk-level expenses. chunk={Chunk}/{Total}, expenseCount={ExpenseCount}",
                    chunkIndex,
                    totalChunks,
                    fallbackReport.Expenses?.Count ?? 0);
                return new ChunkAnalysisResult([fallbackReport], false);
            }

            return new ChunkAnalysisResult([], false);
        }
        finally
        {
            semaphore.Release();
        }
    }

    private async Task<List<ExpenseReportPayload>> AnalyzeChunkWithFallbackAsync(
        string chunkText,
        string geminiApiKey,
        int chunkIndex,
        int totalChunks,
        int splitDepth,
        CancellationToken cancellationToken)
    {
        try
        {
            var chunkJson = await GenerateExpenseReportJsonAsync(
                chunkText,
                geminiApiKey,
                GeminiModel,
                chunkIndex,
                totalChunks,
                splitDepth,
                cancellationToken);

            var partialReport = DeserializeExpenseReport(chunkJson);
            return partialReport is null
                ? []
                : [partialReport];
        }
        catch (InvalidOperationException ex) when (IsJsonRelatedFailure(ex)
            && splitDepth < MaxChunkSplitDepth
            && chunkText.Length >= MinChunkLengthForSplit)
        {
            var (left, right) = SplitChunkInHalf(chunkText);

            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            {
                throw;
            }

            var leftReports = await AnalyzeChunkWithFallbackAsync(
                left,
                geminiApiKey,
                chunkIndex,
                totalChunks,
                splitDepth + 1,
                cancellationToken);

            var rightReports = await AnalyzeChunkWithFallbackAsync(
                right,
                geminiApiKey,
                chunkIndex,
                totalChunks,
                splitDepth + 1,
                cancellationToken);

            leftReports.AddRange(rightReports);
            return leftReports;
        }
    }

    private static bool IsJsonRelatedFailure(InvalidOperationException ex)
    {
        var message = ex.Message.ToLowerInvariant();
        return message.Contains("json") || message.Contains("bos sonuc");
    }

    private static (string Left, string Right) SplitChunkInHalf(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length < 2)
        {
            return (text, string.Empty);
        }

        var middle = text.Length / 2;
        var splitIndex = text.LastIndexOf('\n', middle);

        if (splitIndex < text.Length * 0.2)
        {
            splitIndex = text.IndexOf('\n', middle);
        }

        if (splitIndex <= 0 || splitIndex >= text.Length - 1)
        {
            splitIndex = middle;
        }

        var left = text[..splitIndex].Trim();
        var right = text[(splitIndex + 1)..].Trim();

        return (left, right);
    }

    private async Task<PdfBatchResult> ExtractPdfBatchTextAsync(
        string base64Pdf,
        string apiKey,
        int[] pageNumbers,
        CancellationToken cancellationToken)
    {
        var request = new
        {
            requests = new[]
            {
                new
                {
                    inputConfig = new
                    {
                        mimeType = "application/pdf",
                        content = base64Pdf
                    },
                    features = new[] { new { type = "DOCUMENT_TEXT_DETECTION" } },
                    pages = pageNumbers
                }
            }
        };

        var response = await _httpClient.PostAsJsonAsync(
            $"https://vision.googleapis.com/v1/files:annotate?key={apiKey}",
            request,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            if (IsPageOutOfRangeError(error))
            {
                return new PdfBatchResult(string.Empty, true);
            }

            throw new InvalidOperationException($"Google Vision PDF API hatasi: {error}");
        }

        var visionResponse = await response.Content.ReadFromJsonAsync<VisionFileResponse>(cancellationToken: cancellationToken);
        var fileResponse = visionResponse?.Responses?.FirstOrDefault();
        var fileError = fileResponse?.Error?.Message;

        if (!string.IsNullOrWhiteSpace(fileError))
        {
            if (IsPageOutOfRangeError(fileError))
            {
                return new PdfBatchResult(string.Empty, true);
            }

            throw new InvalidOperationException($"Google Vision PDF yanit hatasi: {fileError}");
        }

        var pageTexts = (fileResponse?.Responses ?? [])
            .Select(p => p.FullTextAnnotation?.Text ?? p.TextAnnotations?.FirstOrDefault()?.Description)
            .Where(t => !string.IsNullOrWhiteSpace(t));

        return new PdfBatchResult(string.Join("\n", pageTexts!), false);
    }

    private static bool IsPageOutOfRangeError(string? error)
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            return false;
        }

        var lowered = error.ToLowerInvariant();
        return lowered.Contains("out of range")
            || (lowered.Contains("page") && lowered.Contains("range"))
            || lowered.Contains("invalid page")
            || lowered.Contains("no pages found");
    }

    private static List<string> SplitTextIntoChunks(string text, int maxChunkChars)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        if (text.Length <= maxChunkChars)
        {
            return [text];
        }

        var chunks = new List<string>();
        var index = 0;

        while (index < text.Length)
        {
            var remaining = text.Length - index;
            var size = Math.Min(maxChunkChars, remaining);
            var endExclusive = index + size;

            if (endExclusive < text.Length)
            {
                var newlinePos = text.LastIndexOf('\n', endExclusive - 1, size);
                if (newlinePos > index + (maxChunkChars / 2))
                {
                    endExclusive = newlinePos + 1;
                }
            }

            chunks.Add(text.Substring(index, endExclusive - index));
            index = endExclusive;
        }

        return chunks;
    }

    private async Task<string> GenerateExpenseReportJsonAsync(
        string ocrTextChunk,
        string apiKey,
        string model,
        int chunkIndex,
        int totalChunks,
        int splitDepth,
        CancellationToken cancellationToken)
    {
        var chunkLabel = splitDepth == 0
            ? $"{chunkIndex}/{totalChunks}"
            : $"{chunkIndex}/{totalChunks} alt-{splitDepth}";

        _logger.LogInformation("Calling Gemini. model={Model}, chunk={ChunkLabel}, chunkChars={ChunkChars}", model, chunkLabel, ocrTextChunk.Length);

        var prompt = $@"Sen bir finansal danismansin.
Bu metin, buyuk bir ekstre dokumaninin {chunkLabel} parcasi.
Sadece bu parca icindeki islem satirlarini cikar.
Birden fazla yil/donem varsa hepsini dikkate al; tek bir yila daraltma.

JSON yapisi (SADECE JSON dondur, aciklama ekleme):
{{
  ""period"": ""AA-YYYY veya Coklu-Donem"",
  ""expenses"": [
    {{ ""merchant"": ""Market/Firma Adi"", ""date"": ""GG.AA.YYYY"", ""donem"": ""AA-YYYY"", ""amount"": 0.0, ""category"": ""Kategori"" }}
  ],
  ""subscriptions"": [
      {{ ""name"": ""Hizmet Adi"", ""cost"": 0.0, ""alternative"": ""Alternatif oneri"", ""savingsAdvice"": ""Tasarruf tavsiyesi"" }}
  ],
  ""totalSpending"": 0.0,
  ""summaryAdvice"": ""Bu parcanin finansal ozeti""
}}
Kurallar:
- Sadece bu parcadaki metni kullan.
- expense satirlarinda donem alanini mutlaka doldur (AA-YYYY).
- Donem belirlenemiyorsa date alanindan tahmin et.

Veriler: {ocrTextChunk}";

        var request = new
        {
            contents = new[]
            {
                new { parts = new[] { new { text = prompt } } }
            },
            generationConfig = new
            {
                responseMimeType = "application/json",
                temperature = 0.1,
                maxOutputTokens = 12288
            }
        };

        var response = await _httpClient.PostAsJsonAsync(
            $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={apiKey}",
            request,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"Gemini API hatasi ({model}): {error}");
        }

        var geminiResponse = await response.Content.ReadFromJsonAsync<GeminiResponse>(cancellationToken: cancellationToken);
        var jsonResult = geminiResponse?.Candidates?.FirstOrDefault()?.Content?.Parts?.FirstOrDefault()?.Text;

        if (string.IsNullOrWhiteSpace(jsonResult))
        {
            throw new InvalidOperationException("Gemini bos sonuc dondu.");
        }

        var normalizedJson = NormalizeAndValidateJsonPayload(jsonResult);

        _logger.LogInformation("Gemini response parsed successfully. chunk={ChunkLabel}", chunkLabel);

        return normalizedJson;
    }

    private static string NormalizeAndValidateJsonPayload(string rawResponse)
    {
        foreach (var candidate in ExtractJsonPayloadCandidates(rawResponse))
        {
            var normalized = NormalizeJsonPayload(candidate);
            try
            {
                JsonDocument.Parse(normalized);
                return normalized;
            }
            catch (JsonException)
            {
                // Try next candidate.
            }
        }

        throw new InvalidOperationException("Gemini yaniti gecerli JSON formatinda degil.");
    }

    private static IEnumerable<string> ExtractJsonPayloadCandidates(string rawResponse)
    {
        if (string.IsNullOrWhiteSpace(rawResponse))
        {
            yield break;
        }

        var cleaned = StripCodeFenceWrapper(rawResponse.Trim());

        var objectCandidate = TryExtractFirstBalancedSegment(cleaned, '{', '}');
        if (!string.IsNullOrWhiteSpace(objectCandidate))
        {
            yield return objectCandidate;
        }

        var arrayCandidate = TryExtractFirstBalancedSegment(cleaned, '[', ']');
        if (!string.IsNullOrWhiteSpace(arrayCandidate))
        {
            yield return arrayCandidate;
        }

        var start = cleaned.IndexOf('{');
        var end = cleaned.LastIndexOf('}');
        if (start >= 0 && end > start)
        {
            yield return cleaned.Substring(start, end - start + 1);
        }

        yield return cleaned;
    }

    private static string StripCodeFenceWrapper(string text)
    {
        var cleaned = text;

        if (cleaned.StartsWith("```", StringComparison.Ordinal))
        {
            var firstLineBreak = cleaned.IndexOf('\n');
            if (firstLineBreak >= 0 && firstLineBreak < cleaned.Length - 1)
            {
                cleaned = cleaned[(firstLineBreak + 1)..];
            }

            var lastFence = cleaned.LastIndexOf("```", StringComparison.Ordinal);
            if (lastFence >= 0)
            {
                cleaned = cleaned[..lastFence];
            }
        }

        return cleaned;
    }

    private static string? TryExtractFirstBalancedSegment(string text, char openChar, char closeChar)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var depth = 0;
        var startIndex = -1;
        var inString = false;
        var escaped = false;

        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];

            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                    continue;
                }

                if (ch == '\\')
                {
                    escaped = true;
                    continue;
                }

                if (ch == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (ch == '"')
            {
                inString = true;
                continue;
            }

            if (ch == openChar)
            {
                if (depth == 0)
                {
                    startIndex = i;
                }

                depth++;
                continue;
            }

            if (ch == closeChar && depth > 0)
            {
                depth--;
                if (depth == 0 && startIndex >= 0)
                {
                    return text.Substring(startIndex, i - startIndex + 1);
                }
            }
        }

        return null;
    }

    private static string NormalizeJsonPayload(string json)
    {
        var normalized = json.Trim();
        normalized = normalized.Replace("\u00A0", " ");
        normalized = TrailingCommaPattern.Replace(normalized, "${close}");
        normalized = JsonNumericFieldPattern.Replace(normalized, match =>
        {
            var prop = match.Groups["prop"].Value;
            var valueToken = match.Groups["value"].Value;

            if (!TryParseLooseDecimal(valueToken, out var value))
            {
                return match.Value;
            }

            return $"\"{prop}\": {value.ToString(CultureInfo.InvariantCulture)}";
        });

        return normalized;
    }

    private static bool TryParseLooseDecimal(string token, out decimal value)
    {
        value = 0;

        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        var cleaned = token.Trim().Trim('"', '\'');
        cleaned = cleaned
            .Replace("₺", string.Empty, StringComparison.Ordinal)
            .Replace("TL", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("TRY", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace("\u00A0", string.Empty, StringComparison.Ordinal);

        if (cleaned.EndsWith("-", StringComparison.Ordinal) && cleaned.Length > 1)
        {
            cleaned = $"-{cleaned[..^1]}";
        }

        var hasDot = cleaned.Contains('.');
        var hasComma = cleaned.Contains(',');

        if (hasDot && hasComma)
        {
            if (cleaned.LastIndexOf(',') > cleaned.LastIndexOf('.'))
            {
                cleaned = cleaned.Replace(".", string.Empty, StringComparison.Ordinal);
                cleaned = cleaned.Replace(',', '.');
            }
            else
            {
                cleaned = cleaned.Replace(",", string.Empty, StringComparison.Ordinal);
            }
        }
        else if (hasComma)
        {
            cleaned = cleaned.Replace(',', '.');
        }

        cleaned = Regex.Replace(cleaned, @"[^0-9.\-+]", string.Empty);
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            return false;
        }

        cleaned = NormalizeMultipleDecimalPoints(cleaned);

        return decimal.TryParse(
            cleaned,
            NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture,
            out value);
    }

    private static string NormalizeMultipleDecimalPoints(string raw)
    {
        var dotCount = 0;
        foreach (var ch in raw)
        {
            if (ch == '.')
            {
                dotCount++;
            }
        }

        if (dotCount <= 1)
        {
            return raw;
        }

        var lastDotIndex = raw.LastIndexOf('.');
        if (lastDotIndex <= 0)
        {
            return raw.Replace(".", string.Empty, StringComparison.Ordinal);
        }

        var prefix = raw[..lastDotIndex].Replace(".", string.Empty, StringComparison.Ordinal);
        return prefix + raw[lastDotIndex..];
    }

    private static ExpenseReportPayload? DeserializeExpenseReport(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<ExpenseReportPayload>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                NumberHandling = JsonNumberHandling.AllowReadingFromString
            });
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Gemini JSON cozumleme hatasi: {ex.Message}");
        }
    }

    private static ExpenseReportPayload MergeReports(List<ExpenseReportPayload> partialReports)
    {
        var mergedExpenses = new List<ExpenseItemPayload>();
        var expenseKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var mergedSubscriptions = new Dictionary<string, SubscriptionItemPayload>(StringComparer.OrdinalIgnoreCase);

        foreach (var report in partialReports)
        {
            foreach (var rawExpense in report.Expenses ?? [])
            {
                var normalizedExpense = NormalizeExpense(rawExpense, report.Period);
                var key = BuildExpenseKey(normalizedExpense);
                if (expenseKeys.Add(key))
                {
                    mergedExpenses.Add(normalizedExpense);
                }
            }

            foreach (var rawSub in report.Subscriptions ?? [])
            {
                if (string.IsNullOrWhiteSpace(rawSub.Name))
                {
                    continue;
                }

                var key = rawSub.Name.Trim();
                if (mergedSubscriptions.TryGetValue(key, out var existing))
                {
                    existing.Cost += rawSub.Cost;
                    existing.Alternative ??= rawSub.Alternative;
                    existing.SavingsAdvice ??= rawSub.SavingsAdvice;
                    continue;
                }

                mergedSubscriptions[key] = new SubscriptionItemPayload
                {
                    Name = key,
                    Cost = rawSub.Cost,
                    Alternative = rawSub.Alternative,
                    SavingsAdvice = rawSub.SavingsAdvice
                };
            }
        }

        mergedExpenses = mergedExpenses
            .OrderBy(e => ParseDateForSort(e.Date))
            .ThenBy(e => e.Merchant)
            .ToList();

        var periods = mergedExpenses
            .Select(e => NormalizePeriod(e.Donem))
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p)
            .ToList();

        var total = mergedExpenses.Sum(e => e.Amount);
        var period = periods.Count switch
        {
            0 => NormalizePeriod(partialReports.Select(r => r.Period).FirstOrDefault(p => !string.IsNullOrWhiteSpace(p)))
                ?? DateTime.Now.ToString("MM-yyyy"),
            1 => periods[0]!,
            _ => "Coklu-Donem"
        };

        return new ExpenseReportPayload
        {
            Period = period,
            Expenses = mergedExpenses,
            Subscriptions = mergedSubscriptions.Values.ToList(),
            TotalSpending = total,
            SummaryAdvice = BuildSummaryAdvice(mergedExpenses, mergedSubscriptions.Values.ToList(), periods, total)
        };
    }

    private static ExpenseItemPayload NormalizeExpense(ExpenseItemPayload raw, string? fallbackPeriod)
    {
        var normalizedPeriod = NormalizePeriod(raw.Donem)
            ?? ExtractPeriodFromDate(raw.Date)
            ?? NormalizePeriod(fallbackPeriod)
            ?? DateTime.Now.ToString("MM-yyyy");

        return new ExpenseItemPayload
        {
            Merchant = string.IsNullOrWhiteSpace(raw.Merchant) ? "Bilinmeyen Islem" : raw.Merchant.Trim(),
            Date = string.IsNullOrWhiteSpace(raw.Date) ? "-" : raw.Date.Trim(),
            Donem = normalizedPeriod,
            Amount = raw.Amount,
            Category = string.IsNullOrWhiteSpace(raw.Category) ? "Diger" : raw.Category.Trim()
        };
    }

    private static string BuildExpenseKey(ExpenseItemPayload item)
    {
        return string.Join("|",
            item.Merchant?.Trim().ToLowerInvariant() ?? string.Empty,
            item.Date?.Trim().ToLowerInvariant() ?? string.Empty,
            item.Amount.ToString("0.00", CultureInfo.InvariantCulture),
            item.Category?.Trim().ToLowerInvariant() ?? string.Empty,
            item.Donem?.Trim().ToLowerInvariant() ?? string.Empty);
    }

    private static string? NormalizePeriod(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var value = raw.Trim();
        if (value.Equals("Coklu-Donem", StringComparison.OrdinalIgnoreCase)
            || value.Equals("Multi-Period", StringComparison.OrdinalIgnoreCase))
        {
            return "Coklu-Donem";
        }

        if (DateTime.TryParseExact(value, "MM-yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var monthYear))
        {
            return monthYear.ToString("MM-yyyy");
        }

        if (DateTime.TryParse(value, CultureInfo.GetCultureInfo("tr-TR"), DateTimeStyles.None, out var parsed))
        {
            return parsed.ToString("MM-yyyy");
        }

        return value;
    }

    private static string? ExtractPeriodFromDate(string? rawDate)
    {
        if (string.IsNullOrWhiteSpace(rawDate))
        {
            return null;
        }

        var formats = new[]
        {
            "dd.MM.yyyy", "d.M.yyyy", "dd/MM/yyyy", "d/M/yyyy", "dd-MM-yyyy", "d-M-yyyy",
            "yyyy-MM-dd", "yyyy/MM/dd", "MM.yyyy", "M.yyyy", "MM-yyyy", "M-yyyy"
        };

        if (DateTime.TryParseExact(rawDate.Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedExact))
        {
            return parsedExact.ToString("MM-yyyy");
        }

        if (DateTime.TryParse(rawDate.Trim(), CultureInfo.GetCultureInfo("tr-TR"), DateTimeStyles.None, out var parsed))
        {
            return parsed.ToString("MM-yyyy");
        }

        return null;
    }

    private static DateTime ParseDateForSort(string? rawDate)
    {
        var formats = new[]
        {
            "dd.MM.yyyy", "d.M.yyyy", "dd/MM/yyyy", "d/M/yyyy", "dd-MM-yyyy", "d-M-yyyy", "yyyy-MM-dd"
        };

        if (!string.IsNullOrWhiteSpace(rawDate)
            && DateTime.TryParseExact(rawDate.Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            return parsed;
        }

        return DateTime.MaxValue;
    }

    private static ExpenseReportPayload? BuildRegexFallbackReport(string sourceText)
    {
        if (string.IsNullOrWhiteSpace(sourceText))
        {
            return null;
        }

        var expenses = new List<ExpenseItemPayload>();
        var dedupe = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rawLine in sourceText.Split('\n'))
        {
            if (!TryExtractFallbackExpenseFromLine(rawLine, out var expense))
            {
                continue;
            }

            var key = BuildExpenseKey(expense);
            if (dedupe.Add(key))
            {
                expenses.Add(expense);
            }
        }

        if (expenses.Count == 0)
        {
            return null;
        }

        expenses = expenses
            .OrderBy(e => ParseDateForSort(e.Date))
            .ThenBy(e => e.Merchant)
            .ToList();

        var subscriptions = expenses
            .Where(e => IsSubscriptionMerchant(e.Merchant))
            .GroupBy(e => e.Merchant ?? "Bilinmeyen Abonelik", StringComparer.OrdinalIgnoreCase)
            .Select(g => new SubscriptionItemPayload
            {
                Name = g.First().Merchant?.Trim() ?? g.Key,
                Cost = g.Sum(x => x.Amount),
                Alternative = "Aile/yillik paket seceneklerini kontrol edin",
                SavingsAdvice = "Duzensiz kullanim varsa abonelik dondurma veya iptal degerlendirilebilir"
            })
            .OrderByDescending(s => s.Cost)
            .ToList();

        var periods = expenses
            .Select(e => NormalizePeriod(e.Donem))
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p)
            .ToList();

        var total = expenses.Sum(e => e.Amount);
        var period = periods.Count switch
        {
            0 => DateTime.Now.ToString("MM-yyyy"),
            1 => periods[0]!,
            _ => "Coklu-Donem"
        };

        return new ExpenseReportPayload
        {
            Period = period,
            Expenses = expenses,
            Subscriptions = subscriptions,
            TotalSpending = total,
            SummaryAdvice = BuildSummaryAdvice(expenses, subscriptions, periods, total)
        };
    }

    private static bool TryExtractFallbackExpenseFromLine(string rawLine, out ExpenseItemPayload expense)
    {
        expense = new ExpenseItemPayload();

        if (string.IsNullOrWhiteSpace(rawLine))
        {
            return false;
        }

        var line = Regex.Replace(rawLine, "\\s+", " ").Trim();
        if (!IsLikelyTransactionLine(line))
        {
            return false;
        }

        var dateMatch = DatePattern.Match(line);
        if (!dateMatch.Success)
        {
            return false;
        }

        var amountMatches = AmountPattern.Matches(line);
        if (amountMatches.Count == 0)
        {
            return false;
        }

        decimal amount = 0;
        for (var i = amountMatches.Count - 1; i >= 0; i--)
        {
            var amountMatch = amountMatches[i];
            if (!TryParseLooseDecimal(amountMatch.Value, out var parsed))
            {
                continue;
            }

            parsed = Math.Abs(parsed);
            if (parsed <= 0 || parsed > MaxFallbackAmount)
            {
                continue;
            }

            amount = parsed;
            break;
        }

        if (amount <= 0)
        {
            return false;
        }

        var merchant = line;
        merchant = merchant.Replace(dateMatch.Value, " ", StringComparison.OrdinalIgnoreCase);
        foreach (Match amountMatch in amountMatches)
        {
            merchant = merchant.Replace(amountMatch.Value, " ", StringComparison.OrdinalIgnoreCase);
        }

        merchant = merchant.Replace("₺", " ", StringComparison.Ordinal);
        merchant = Regex.Replace(merchant, @"\b(?:TL|TRY|BORC|ALACAK|BAKIYE|TUTAR|ISLEM|ISLEMI)\b", " ", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        merchant = Regex.Replace(merchant, "\\s+", " ").Trim(' ', '-', ':', ';', '|', '/', '\\');

        if (string.IsNullOrWhiteSpace(merchant))
        {
            merchant = "Bilinmeyen Islem";
        }

        expense = new ExpenseItemPayload
        {
            Merchant = merchant,
            Date = dateMatch.Value,
            Donem = ExtractPeriodFromDate(dateMatch.Value) ?? DateTime.Now.ToString("MM-yyyy"),
            Amount = amount,
            Category = IsSubscriptionMerchant(merchant) ? "Abonelik" : "Diger"
        };

        return true;
    }

    private static bool IsSubscriptionMerchant(string? merchant)
    {
        if (string.IsNullOrWhiteSpace(merchant))
        {
            return false;
        }

        return SubscriptionKeywords.Any(keyword => merchant.Contains(keyword, StringComparison.OrdinalIgnoreCase));
    }

    private static string BuildSummaryAdvice(
        List<ExpenseItemPayload> expenses,
        List<SubscriptionItemPayload> subscriptions,
        List<string?> periods,
        decimal total)
    {
        if (expenses.Count == 0)
        {
            return "Belgede harcama satiri bulunamadi.";
        }

        var culture = CultureInfo.GetCultureInfo("tr-TR");
        var topCategories = expenses
            .GroupBy(e => string.IsNullOrWhiteSpace(e.Category) ? "Diger" : e.Category)
            .Select(g => new { Category = g.Key, Total = g.Sum(x => x.Amount) })
            .OrderByDescending(x => x.Total)
            .Take(3)
            .Select(x => $"{x.Category} ({x.Total.ToString("C0", culture)})")
            .ToList();

        var periodText = periods.Count switch
        {
            0 => "tek donem",
            1 => $"{periods[0]} donemi",
            _ => $"{periods.Count} farkli donem"
        };

        var categoryText = topCategories.Count > 0
            ? string.Join(", ", topCategories)
            : "Kategori dagilimi belirlenemedi";

        return $"Belge genelinde {periodText} kapsanarak toplam {total.ToString("C2", culture)} harcama tespit edildi. En yuksek harcama kategorileri: {categoryText}. Tespit edilen abonelik sayisi: {subscriptions.Count}.";
    }

    private static string AppendChunkFailureWarning(string? summaryAdvice, int failedChunks, int totalChunks)
    {
        var baseSummary = string.IsNullOrWhiteSpace(summaryAdvice)
            ? "Belge analizi tamamlandi."
            : summaryAdvice.Trim();

        return $"{baseSummary} Not: {totalChunks} parcanin {failedChunks} tanesi analiz edilemedi, sonuclar kismi olabilir.";
    }

    private static string AppendRegexFallbackWarning(string? summaryAdvice)
    {
        var baseSummary = string.IsNullOrWhiteSpace(summaryAdvice)
            ? "Belge analizi tamamlandi."
            : summaryAdvice.Trim();

        return $"{baseSummary} Not: Gemini yanitinda JSON bozuldugu icin metin tabanli yedek analiz kullanildi; kritik kalemleri kontrol etmeniz onerilir.";
    }

    private readonly record struct PdfBatchResult(string Text, bool OutOfRange);
    private readonly record struct ChunkAnalysisResult(List<ExpenseReportPayload> Reports, bool Success);

    private sealed class ExpenseReportPayload
    {
        [JsonPropertyName("period")]
        public string? Period { get; set; }

        [JsonPropertyName("expenses")]
        public List<ExpenseItemPayload>? Expenses { get; set; }

        [JsonPropertyName("subscriptions")]
        public List<SubscriptionItemPayload>? Subscriptions { get; set; }

        [JsonPropertyName("totalSpending")]
        public decimal TotalSpending { get; set; }

        [JsonPropertyName("summaryAdvice")]
        public string? SummaryAdvice { get; set; }
    }

    private sealed class ExpenseItemPayload
    {
        [JsonPropertyName("merchant")]
        public string? Merchant { get; set; }

        [JsonPropertyName("date")]
        public string? Date { get; set; }

        [JsonPropertyName("donem")]
        public string? Donem { get; set; }

        [JsonPropertyName("amount")]
        public decimal Amount { get; set; }

        [JsonPropertyName("category")]
        public string? Category { get; set; }
    }

    private sealed class SubscriptionItemPayload
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("cost")]
        public decimal Cost { get; set; }

        [JsonPropertyName("alternative")]
        public string? Alternative { get; set; }

        [JsonPropertyName("savingsAdvice")]
        public string? SavingsAdvice { get; set; }
    }

    private sealed class VisionImageResponse
    {
        [JsonPropertyName("responses")]
        public List<AnnotateImageResponse>? Responses { get; set; }
    }

    private sealed class VisionFileResponse
    {
        [JsonPropertyName("responses")]
        public List<AnnotateFileResponse>? Responses { get; set; }
    }

    private sealed class AnnotateFileResponse
    {
        [JsonPropertyName("responses")]
        public List<AnnotateImageResponse>? Responses { get; set; }

        [JsonPropertyName("error")]
        public VisionError? Error { get; set; }
    }

    private sealed class AnnotateImageResponse
    {
        [JsonPropertyName("fullTextAnnotation")]
        public FullTextAnnotation? FullTextAnnotation { get; set; }

        [JsonPropertyName("textAnnotations")]
        public List<TextAnnotation>? TextAnnotations { get; set; }

        [JsonPropertyName("error")]
        public VisionError? Error { get; set; }
    }

    private sealed class FullTextAnnotation
    {
        [JsonPropertyName("text")]
        public string? Text { get; set; }
    }

    private sealed class TextAnnotation
    {
        [JsonPropertyName("description")]
        public string? Description { get; set; }
    }

    private sealed class VisionError
    {
        [JsonPropertyName("message")]
        public string? Message { get; set; }
    }

    private sealed class GeminiResponse
    {
        [JsonPropertyName("candidates")]
        public List<Candidate>? Candidates { get; set; }
    }

    private sealed class Candidate
    {
        [JsonPropertyName("content")]
        public GeminiContent? Content { get; set; }
    }

    private sealed class GeminiContent
    {
        [JsonPropertyName("parts")]
        public List<Part>? Parts { get; set; }
    }

    private sealed class Part
    {
        [JsonPropertyName("text")]
        public string? Text { get; set; }
    }
}
