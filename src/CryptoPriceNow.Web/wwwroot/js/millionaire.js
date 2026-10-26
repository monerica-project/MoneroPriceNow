// millionaire.js: refreshes the "XMR needed for 1,000,000 USDT" number every 15s and draws
// the 90-day chart. Works from /api/millionaire; the page is fully rendered without it.
(function () {
    const POLL_MS = 15_000;
    const xmrEl = document.getElementById('mmXmr');
    const priceEl = document.getElementById('mmPrice');
    const canvas = document.getElementById('mmChart');
    let chart = null;

    const fmt2 = v => (v === null || v === undefined) ? '--'
        : Number(v).toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 });

    function render(d) {
        if (!d) return;
        if (d.xmr !== null && d.xmr !== undefined) {
            const old = xmrEl.textContent;
            xmrEl.textContent = fmt2(d.xmr);
            // Keep the tab title in step with the number on screen (same format as the server).
            document.title = 'Monero Millionaire: ' + xmrEl.textContent + ' XMR = 1,000,000 USDT \u2014 MoneroPriceNow';
            if (old !== xmrEl.textContent) {
                xmrEl.style.transition = 'none'; xmrEl.style.opacity = '.55';
                requestAnimationFrame(() => { xmrEl.style.transition = 'opacity .6s'; xmrEl.style.opacity = '1'; });
            }
        }
        if (d.price !== null && d.price !== undefined) priceEl.textContent = '$' + fmt2(d.price);

        (d.comparisons || []).forEach(c => {
            const card = document.querySelector('.mm-card[data-days="' + c.days + '"]');
            if (!card) return;
            card.querySelector('.mm-then').textContent = fmt2(c.xmr);
            card.querySelector('.mm-thenprice').textContent = c.price ? '$' + fmt2(c.price) : '--';
            const chg = card.querySelector('.mm-chg');
            chg.classList.remove('mm-up', 'mm-down');
            if (c.xmr !== null && d.xmr !== null) {
                const diff = d.xmr - c.xmr;
                chg.textContent = diff > 0 ? fmt2(Math.abs(diff)) + ' more XMR needed now'
                    : diff < 0 ? fmt2(Math.abs(diff)) + ' fewer XMR needed now' : 'same as now';
                if (diff > 0) chg.classList.add('mm-up'); else if (diff < 0) chg.classList.add('mm-down');
            } else {
                chg.textContent = '';
            }
        });

        drawChart(d);
    }

    function drawChart(d) {
        if (!canvas || typeof Chart === 'undefined') return;
        const pts = (d.series || []).map(p => ({ x: p.t, y: p.xmr }));
        if (d.xmr) pts.push({ x: Date.now(), y: d.xmr });
        const labels = pts.map(p => new Date(p.x).toLocaleDateString('en-US', { month: 'short', day: 'numeric' }));
        const data = pts.map(p => p.y);
        if (chart) {
            chart.data.labels = labels;
            chart.data.datasets[0].data = data;
            chart.update('none');
            return;
        }
        chart = new Chart(canvas, {
            type: 'line',
            data: {
                labels,
                datasets: [{
                    label: 'XMR needed',
                    data,
                    borderColor: '#ff8c3a',
                    backgroundColor: 'rgba(242,104,34,.12)',
                    fill: true,
                    pointRadius: 0,
                    tension: .25,
                }],
            },
            options: {
                responsive: true,
                maintainAspectRatio: false,
                animation: false,
                plugins: {
                    legend: { display: false },
                    tooltip: { callbacks: { label: c => fmt2(c.parsed.y) + ' XMR' } },
                },
                scales: {
                    x: { ticks: { color: '#8b8b96', maxTicksLimit: 8 }, grid: { color: 'rgba(255,255,255,.05)' } },
                    y: { ticks: { color: '#8b8b96', callback: v => fmt2(v) }, grid: { color: 'rgba(255,255,255,.05)' } },
                },
            },
        });
    }

    async function load() {
        try {
            const r = await fetch('/api/millionaire', { cache: 'no-store' });
            if (r.ok) render(await r.json());
        } catch { /* keep showing the last values */ }
    }

    // First paint from the server-rendered snapshot, then live.
    try { render(JSON.parse(document.getElementById('mmData').textContent)); } catch { }
    setInterval(() => { if (document.visibilityState === 'visible') load(); }, POLL_MS);
    document.addEventListener('visibilitychange', () => { if (document.visibilityState === 'visible') load(); });
})();
