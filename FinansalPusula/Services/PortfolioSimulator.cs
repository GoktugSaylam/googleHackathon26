namespace FinansalPusula.Services;

public static class PortfolioSimulator
{
    public static SimulationResult Simulate(
        string symbol,
        List<HistoricalDataPoint> history,
        List<DividendHistory> dividends,
        List<SplitInfo> splits,
        List<TransactionRecord> transactions)
    {
        var result = new SimulationResult 
        { 
            Symbol = symbol, 
            DailyPoints = history.OrderBy(h => h.Date).ToList() 
        };
        
        var currentLots = 0m;
        var totalInvestedTL = 0m;
        var totalInvestedUSD = 0m;
        var dripLots = 0m;
        var totalNetDivTL = 0m;
        
        // Tarihsel olayları birleştir ve kronolojik işle
        var startDate = transactions.Any() ? transactions.Min(t => t.Date).Date : DateTime.Today.AddYears(-5);
        var relevantHistory = result.DailyPoints.Where(h => h.Date >= startDate).ToList();
        
        var pendingDrips = new List<(DateTime ExecutionDate, decimal NetAmount, DateTime OriginalDivDate)>();
        var yearEndLots = new Dictionary<int, decimal>();
        var yearEndValuesTL = new Dictionary<int, decimal>();
        var yearEndValuesUSD = new Dictionary<int, decimal>();
        var lastProcessedYear = -1;

        foreach (var day in relevantHistory)
        {
            var date = day.Date.Date;
            if (lastProcessedYear != -1 && lastProcessedYear != date.Year)
            {
                yearEndLots[lastProcessedYear] = currentLots;
            }
            lastProcessedYear = date.Year;
            
            // 1. Bölünmeleri Uygula
            var todaySp = splits.FirstOrDefault(s => s.Date.Date == date);
            if (todaySp != null)
            {
                var parts = todaySp.Ratio.Split(':');
                if (parts.Length == 2 && decimal.TryParse(parts[0], out var num) && decimal.TryParse(parts[1], out var den))
                {
                    var factor = num / den;
                    currentLots *= factor;
                }
            }

            // 2. Alım/Satımları Uygula
            var todayTxs = transactions.Where(t => t.Date.Date == date).ToList();
            foreach (var tx in todayTxs)
            {
                if (tx.Type == TransactionType.Buy)
                {
                    currentLots += tx.Quantity;
                    totalInvestedTL += tx.Quantity * tx.Price;
                    // Sıfıra bölünmeyi önle: PriceUSD 0 ise USD hesabı atla
                    if (day.PriceUSD > 0 && day.PriceTL > 0)
                    {
                        var kur = day.PriceTL / day.PriceUSD;
                        totalInvestedUSD += kur > 0 ? (tx.Quantity * tx.Price) / kur : 0;
                    }
                }
                else
                {
                    currentLots -= tx.Quantity;
                    // Satışlarda yatırım tutarından düşmek yerine dashboard basic değer değişimini gösterir
                }
            }

            // 3. Bekleyen DRIP'leri Gerçekleştir (T+2)
            var dripsToExecute = pendingDrips.Where(p => p.ExecutionDate <= date).ToList();
            foreach (var drip in dripsToExecute)
            {
                if (day.PriceTL > 0)
                {
                    var bought = drip.NetAmount / day.PriceTL;
                    currentLots += bought;
                    dripLots += bought;
                    
                    var divEvt = result.DividendEvents.FirstOrDefault(e => e.Date == drip.OriginalDivDate);
                    if (divEvt != null)
                    {
                        divEvt.BuyPriceT2 = day.PriceTL;
                        divEvt.LotsBought = bought;
                    }
                }
                pendingDrips.Remove(drip);
            }

            // 4. Temettüleri Yakala (Bugünden itibaren sahiplik kontrolü)
            var todayDiv = dividends.FirstOrDefault(d => d.Date.Date == date);
            if (todayDiv != null && currentLots > 0)
            {
                var gross = currentLots * todayDiv.Amount;
                var net = gross * 0.90m;
                totalNetDivTL += net;
                
                // T+2 (İş günü simülasyonu için +2 takvim günü)
                pendingDrips.Add((date.AddDays(2), net, date));

                result.DividendEvents.Add(new DetailedDividendEvent
                {
                    Date = date,
                    DividendPerShare = todayDiv.Amount,
                    OwnedLots = currentLots,
                    GrossIncome = gross,
                    BuyPriceT2 = 0,
                    LotsBought = 0
                });
            }

            // 5. Günlük Değeri Kaydet
            day.PorfolyoDegeriTL = currentLots * day.PriceTL;
        }

        // Simülasyon sonrası DailyPoints'i relevantHistory olarak güncelle (grafik için anlamlı periyot)
        result.DailyPoints = relevantHistory;

        // Final Metrikler
        result.TotalLots = Math.Round(currentLots, 3);
        result.DripLots = Math.Round(dripLots, 3);
        result.TotalNetDividendTL = totalNetDivTL;
        result.TotalInvestedTL = totalInvestedTL;
        result.TotalInvestedUSD = totalInvestedUSD;
        
        var lastPrice = result.DailyPoints.LastOrDefault();
        result.CurrentValueTL = currentLots * (lastPrice?.PriceTL ?? 0);
        result.CurrentValueUSD = currentLots * (lastPrice?.PriceUSD ?? 0);

        if (lastProcessedYear != -1) 
        {
            yearEndLots[lastProcessedYear] = currentLots;
            var lastHistoryDay = relevantHistory.LastOrDefault();
            if (lastHistoryDay != null)
            {
                yearEndValuesTL[lastProcessedYear] = currentLots * lastHistoryDay.PriceTL;
                yearEndValuesUSD[lastProcessedYear] = currentLots * lastHistoryDay.PriceUSD;
            }
        }

        // Yıllık Performans Özeti
        var yearlyGroups = relevantHistory.GroupBy(h => h.Date.Year).OrderBy(g => g.Key);
        foreach (var group in yearlyGroups)
        {
            var yearEnd = group.Last();
            var yYear = group.Key;
            
            var eoyLots = yearEndLots.ContainsKey(yYear) ? yearEndLots[yYear] : currentLots;
            
            result.YearlySummaries.Add(new YearlySummary
            {
                Year = yYear,
                LotCount = Math.Round(eoyLots, 0),
                NetDividendTL = result.DividendEvents.Where(e => e.Date.Year == yYear).Sum(e => e.NetIncome),
                YearEndValueTL = eoyLots * yearEnd.PriceTL,
                YearEndValueUSD = eoyLots * yearEnd.PriceUSD
            });
        }

        return result;
    }
}
