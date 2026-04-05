using System;
using System.Collections.Generic;
using System.Linq;

namespace FinansalPusula.Server.Services;

/// <summary>
/// Finansal performans metriklerini hesaplayan servis.
/// </summary>
public class FinancialMetricsService
{
    private const double Tolerance = 1e-6;
    private const int MaxIterations = 100;

    /// <summary>
    /// İçsel Getiri Oranı (XIRR) hesaplar (Newton-Raphson yöntemi).
    /// </summary>
    /// <param name="flows">Tarih ve Nakit Akış Tutarı (Alışlar -, Satışlar/Değer +)</param>
    public double CalculateXirr(List<(DateTime Date, double Amount)> flows)
    {
        if (flows == null || flows.Count < 2) return 0;

        // Newton-Raphson başlangıç tahmini
        double r = 0.1; 
        
        for (int i = 0; i < MaxIterations; i++)
        {
            double f = 0;
            double df = 0;
            DateTime t0 = flows[0].Date;

            foreach (var flow in flows)
            {
                double days = (flow.Date - t0).TotalDays / 365.25;
                double denominator = Math.Pow(1 + r, days);
                
                f += flow.Amount / denominator;
                df -= (days * flow.Amount) / (denominator * (1 + r));
            }

            if (Math.Abs(df) < double.Epsilon) break;

            double nextR = r - f / df;
            if (Math.Abs(nextR - r) < Tolerance) return nextR;
            
            r = nextR;
        }

        return r; // Yakınsama olmazsa son tahmini dön
    }

    /// <summary>
    /// Yıllık Bileşik Büyüme Oranı (CAGR) hesaplar.
    /// Formül: (EndValue / StartValue) ^ (1 / Years) - 1
    /// </summary>
    public double CalculateCagr(double startValue, double endValue, double years)
    {
        if (startValue <= 0 || years <= 0) return 0;
        return Math.Pow(endValue / startValue, 1.0 / years) - 1;
    }
}
