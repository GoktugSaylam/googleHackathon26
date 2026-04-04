window.chartInterop = {
    renderChart: (canvasId, config) => {
        const ctx = document.getElementById(canvasId);
        if (!ctx) return;
        
        // Varsa eski grafiği temizle
        const oldChart = Chart.getChart(ctx);
        if (oldChart) oldChart.destroy();
        
        new Chart(ctx, config);
    }
};
