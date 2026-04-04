using System.Text.Json.Serialization;

namespace FinansalPusula.Services;

public class YahooFinanceResponse
{
    [JsonPropertyName("chart")]
    public ChartData? Chart { get; set; }
}

public class ChartData
{
    [JsonPropertyName("result")]
    public List<ChartResult>? Result { get; set; }
    
    [JsonPropertyName("error")]
    public object? Error { get; set; }
}

public class ChartResult
{
    [JsonPropertyName("meta")]
    public MetaData? Meta { get; set; }
    
    [JsonPropertyName("timestamp")]
    public List<long>? Timestamp { get; set; }
    
    [JsonPropertyName("indicators")]
    public Indicators? Indicators { get; set; }
    
    [JsonPropertyName("events")]
    public YahooEvents? Events { get; set; }
}

public class MetaData
{
    [JsonPropertyName("currency")]
    public string? Currency { get; set; }
    
    [JsonPropertyName("symbol")]
    public string? Symbol { get; set; }
    
    [JsonPropertyName("exchangeName")]
    public string? ExchangeName { get; set; }

    [JsonPropertyName("regularMarketPrice")]
    public decimal RegularMarketPrice { get; set; }
    
    [JsonPropertyName("previousClose")]
    public decimal PreviousClose { get; set; }

    [JsonPropertyName("fiftyDayAverage")]
    public decimal FiftyDayAverage { get; set; }

    [JsonPropertyName("twoHundredDayAverage")]
    public decimal TwoHundredDayAverage { get; set; }
}

public class Indicators
{
    [JsonPropertyName("quote")]
    public List<QuoteData>? Quote { get; set; }
}

public class QuoteData
{
    [JsonPropertyName("close")]
    public List<decimal?>? Close { get; set; }
    
    [JsonPropertyName("volume")]
    public List<long?>? Volume { get; set; }
}

public class YahooEvents
{
    [JsonPropertyName("dividends")]
    public Dictionary<string, YahooDividend>? Dividends { get; set; }
    
    [JsonPropertyName("splits")]
    public Dictionary<string, YahooSplit>? Splits { get; set; }
}

public class YahooDividend
{
    [JsonPropertyName("amount")]
    public decimal Amount { get; set; }
    
    [JsonPropertyName("date")]
    public long Date { get; set; }
}

public class YahooSplit
{
    [JsonPropertyName("date")]
    public long Date { get; set; }
    
    [JsonPropertyName("numerator")]
    public int Numerator { get; set; }
    
    [JsonPropertyName("denominator")]
    public int Denominator { get; set; }
    
    [JsonPropertyName("splitRatio")]
    public string? SplitRatio { get; set; }
}
