window.chartInterop = {
    // Genel amaçlı chart render
    renderChart: (canvasId, config) => {
        const ctx = document.getElementById(canvasId);
        if (!ctx) return;
        const old = Chart.getChart(ctx);
        if (old) old.destroy();
        new Chart(ctx, config);
    },

    // Portföy dağılım halka grafiği
    renderDoughnut: (canvasId, labels, data, colors) => {
        const ctx = document.getElementById(canvasId);
        if (!ctx) return;
        const old = Chart.getChart(ctx);
        if (old) old.destroy();

        new Chart(ctx, {
            type: 'doughnut',
            data: {
                labels: labels,
                datasets: [{
                    data: data,
                    backgroundColor: colors,
                    borderWidth: 2,
                    borderColor: 'rgba(255,255,255,0.1)'
                }]
            },
            options: {
                responsive: true,
                maintainAspectRatio: false,
                plugins: {
                    legend: {
                        position: 'right',
                        labels: { color: 'white', font: { family: 'Outfit' } }
                    },
                    tooltip: {
                        callbacks: {
                            label: ctx => {
                                const val = ctx.parsed;
                                const total = ctx.dataset.data.reduce((a, b) => a + b, 0);
                                const pct = total > 0 ? ((val / total) * 100).toFixed(1) : 0;
                                return ` ₺${val.toLocaleString('tr-TR', {minimumFractionDigits:2})} (%${pct})`;
                            }
                        }
                    }
                }
            }
        });
    }
};
