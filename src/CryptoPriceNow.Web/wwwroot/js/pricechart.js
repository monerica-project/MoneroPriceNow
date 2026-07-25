// pricechart.js — XMR/USDT history chart fed by /api/history.
// Self-contained: if Chart.js failed to load or the API reports the database
// is unavailable, the section simply stays hidden and the rest of the site is
// untouched.
(() => {
    'use strict';

    const section = document.getElementById('chartSection');
    const canvas = document.getElementById('priceChart');
    const trendEl = document.getElementById('chartTrend');
    const emptyEl = document.getElementById('chartEmpty');
    const rangesEl = document.getElementById('chartRanges');
    if (!section || !canvas || typeof Chart === 'undefined') return;

    const REFRESH_MS = 30_000;          // real-time refresh

    // Per-page pair config (injected by _PriceBoard.cshtml). Falls back to USDT.
    const PINFO = Object.assign({
        historyPair: 'XMR/USDT:Tron',
        symbol: '$',
        suffix: '',
        decimals: 2
    }, window.__PAIR__ || {});
    let PAIR = PINFO.historyPair;
    // When set (exchange pages), the chart shows just this one exchange's quotes.
    const EXCHANGE = PINFO.exchangeKey || null;

    let currentRange = '1d';
    let chart = null;
    let refreshTid = null;
    let everHadData = false;

    // ── Theme colors from the site's CSS variables ────────────────────────────
    const css = getComputedStyle(document.documentElement);
    const cVar = (name, fallback) => (css.getPropertyValue(name) || '').trim() || fallback;
    const COLOR_BUY = cVar('--good', '#3ecf8e');
    const COLOR_SELL = cVar('--xmr-orange2', '#ff8c3a');
    const COLOR_MARKET = cVar('--text', '#e8dfd0');
    const COLOR_MUTED = cVar('--text-muted', '#8a7a6a');
    const COLOR_GRID = 'rgba(242,104,34,.08)';
    const FONT_MONO = cVar('--mono', 'ui-monospace, monospace');

    // ── Label formatting ──────────────────────────────────────────────────────
    function fmtLabel(ms, bucketSeconds) {
        const d = new Date(ms);
        const hm = d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', hour12: false });
        if (bucketSeconds >= 3600) {
            // Multi-day views: include date
            const md = d.toLocaleDateString([], { month: 'short', day: 'numeric' });
            return `${md} ${hm}`;
        }
        return hm;
    }

    function fmtPrice(v) {
        if (v == null) return '—';
        return Number(v).toLocaleString(undefined, {
            minimumFractionDigits: PINFO.decimals,
            maximumFractionDigits: PINFO.decimals
        });
    }

    // ── Trend readout (direction over the visible range) ─────────────────────
    function updateTrend(points, rangeKey) {
        if (!trendEl) return;
        const vals = points.map(p => p.market).filter(v => v != null);
        if (vals.length < 2) { trendEl.textContent = ''; trendEl.className = 'chart-trend'; return; }

        const first = Number(vals[0]);
        const last = Number(vals[vals.length - 1]);
        if (!first) { trendEl.textContent = ''; return; }

        const pct = ((last - first) / first) * 100;
        const up = pct >= 0;
        trendEl.textContent = `${up ? '▲' : '▼'} ${up ? '+' : ''}${pct.toFixed(2)}% / ${rangeKey}`;
        trendEl.className = 'chart-trend ' + (up ? 'trend-up' : 'trend-down');
    }

    // ── Chart construction / update ───────────────────────────────────────────
    function dataset(label, color, data, opts = {}) {
        return Object.assign({
            label,
            data,
            borderColor: color,
            backgroundColor: color,
            borderWidth: 1.6,
            pointRadius: 0,
            pointHitRadius: 8,
            tension: 0.25,
            spanGaps: true,
            fill: false
        }, opts);
    }

    // ── Range button availability ─────────────────────────────────────────────
    // Only show ranges the data can actually fill. As history accumulates the
    // server reports more available ranges and the buttons appear automatically.
    function applyAvailableRanges(available) {
        if (!rangesEl || !Array.isArray(available) || available.length === 0) return;
        const set = new Set(available);
        const btns = [...rangesEl.querySelectorAll('.chart-range-btn')];
        btns.forEach(b => { b.hidden = !set.has(b.dataset.range); });

        // If the active range is no longer available, fall back to the longest
        // available one (buttons are in ascending order).
        if (!set.has(currentRange)) {
            const visible = btns.filter(b => !b.hidden);
            const fallback = visible.length ? visible[visible.length - 1].dataset.range : null;
            if (fallback && fallback !== currentRange) {
                currentRange = fallback;
                btns.forEach(b => b.classList.toggle('chart-range-active', b.dataset.range === currentRange));
                load();
            }
        }
    }

    function render(payload) {
        applyAvailableRanges(payload.availableRanges);
        const points = payload.points || [];
        const labels = points.map(p => fmtLabel(p.t, payload.bucketSeconds));
        const buy = points.map(p => p.buy != null ? Number(p.buy) : null);
        const sell = points.map(p => p.sell != null ? Number(p.sell) : null);
        const market = points.map(p => p.market != null ? Number(p.market) : null);

        if (points.length > 0) everHadData = true;
        if (emptyEl) emptyEl.hidden = points.length > 0;

        updateTrend(points, payload.range || currentRange);

        if (chart) {
            chart.data.labels = labels;
            chart.data.datasets[0].data = buy;
            chart.data.datasets[1].data = sell;
            chart.data.datasets[2].data = market;
            chart.update('none');
            return;
        }

        chart = new Chart(canvas.getContext('2d'), {
            type: 'line',
            data: {
                labels,
                datasets: [
                    dataset('Buy', COLOR_BUY, buy),
                    dataset('Sell', COLOR_SELL, sell),
                    dataset('Market', COLOR_MARKET, market, { borderWidth: 2.2 })
                ]
            },
            options: {
                responsive: true,
                maintainAspectRatio: false,
                animation: false,
                interaction: { mode: 'index', intersect: false },
                plugins: {
                    legend: {
                        display: true,
                        labels: {
                            color: COLOR_MUTED,
                            font: { family: FONT_MONO, size: 10 },
                            boxWidth: 14,
                            boxHeight: 2
                        }
                    },
                    tooltip: {
                        backgroundColor: 'rgba(13,10,7,.95)',
                        borderColor: 'rgba(242,104,34,.35)',
                        borderWidth: 1,
                        titleColor: COLOR_MARKET,
                        bodyColor: COLOR_MUTED,
                        titleFont: { family: FONT_MONO, size: 11 },
                        bodyFont: { family: FONT_MONO, size: 11 },
                        callbacks: {
                            label: ctx => ` ${ctx.dataset.label}: ${PINFO.symbol}${fmtPrice(ctx.parsed.y)}${PINFO.suffix}`
                        }
                    }
                },
                scales: {
                    x: {
                        ticks: {
                            color: COLOR_MUTED,
                            font: { family: FONT_MONO, size: 9 },
                            maxTicksLimit: 8,
                            maxRotation: 0
                        },
                        grid: { color: COLOR_GRID, drawTicks: false }
                    },
                    y: {
                        position: 'right',
                        ticks: {
                            color: COLOR_MUTED,
                            font: { family: FONT_MONO, size: 9 },
                            callback: v => PINFO.symbol + fmtPrice(v) + PINFO.suffix
                        },
                        grid: { color: COLOR_GRID, drawTicks: false }
                    }
                }
            }
        });
    }

    // ── Loading overlay ───────────────────────────────────────────────────────
    // Only shown for user-initiated changes (range / rate toggle), never for the
    // silent 30s auto-refresh, so the chart doesn't flicker a spinner on its own.
    const loadingEl = document.getElementById('chartLoading');
    let loadSeq = 0;
    function setLoading(on) { if (loadingEl) loadingEl.hidden = !on; }

    // ── Data loading ──────────────────────────────────────────────────────────
    async function load(showSpinner = false) {
        const seq = ++loadSeq;
        if (showSpinner) setLoading(true);
        try {
            const rt = window.__RATE_TYPE__ || 'float';
            let url = `/api/history?pair=${encodeURIComponent(PAIR)}&range=${encodeURIComponent(currentRange)}&rateType=${encodeURIComponent(rt)}`;
            if (EXCHANGE) url += `&exchange=${encodeURIComponent(EXCHANGE)}`;
            const res = await fetch(url, { cache: 'no-store' });
            if (!res.ok) return;
            const data = await res.json();

            // A newer request started while this was in flight — drop this stale one.
            if (seq !== loadSeq) return;

            if (!data.enabled) {
                // No database configured / temporarily down — keep section hidden
                if (!everHadData) section.hidden = true;
                return;
            }

            section.hidden = false;
            render(data);
        } catch (e) {
            console.warn('[PriceChart] history fetch failed:', e);
        } finally {
            // Only the most recent load clears the overlay.
            if (seq === loadSeq) setLoading(false);
        }
    }

    function schedule() {
        clearInterval(refreshTid);
        refreshTid = setInterval(() => {
            if (document.visibilityState === 'visible') load();
        }, REFRESH_MS);
    }

    // ── Rate-type toggle (fired by the board's Float/Fixed buttons) ────────────
    window.addEventListener('ratetypechange', () => load(true));

    // ── Range buttons ─────────────────────────────────────────────────────────
    if (rangesEl) {
        rangesEl.addEventListener('click', (e) => {
            const btn = e.target.closest('.chart-range-btn');
            if (!btn || btn.dataset.range === currentRange) return;
            currentRange = btn.dataset.range;
            rangesEl.querySelectorAll('.chart-range-btn')
                .forEach(b => b.classList.toggle('chart-range-active', b === btn));
            load(true);
            schedule(); // reset the 30s cadence on manual range change
        });
    }

    // ── Pair tabs (exchange pages only) ───────────────────────────────────────
    // Buttons carry data-history-pair / -decimals / -symbol / -suffix. Switching
    // re-points the same chart at another pair and rebuilds it so the axis reformats.
    const pairTabsEl = document.getElementById('chartPairTabs');
    if (pairTabsEl) {
        pairTabsEl.addEventListener('click', (e) => {
            const btn = e.target.closest('.ex-pair-btn');
            if (!btn || btn.dataset.historyPair === PAIR) return;

            PAIR = btn.dataset.historyPair;
            if (btn.dataset.decimals) PINFO.decimals = parseInt(btn.dataset.decimals, 10);
            if (btn.dataset.symbol != null) PINFO.symbol = btn.dataset.symbol;
            if (btn.dataset.suffix != null) PINFO.suffix = btn.dataset.suffix;

            pairTabsEl.querySelectorAll('.ex-pair-btn')
                .forEach(b => b.classList.toggle('ex-pair-active', b === btn));

            currentRange = '1d';
            if (chart) { chart.destroy(); chart = null; }  // rebuild so decimals/labels reset
            everHadData = false;
            load(true);
            schedule();
        });
    }

    // Refresh immediately when the tab becomes visible again
    document.addEventListener('visibilitychange', () => {
        if (document.visibilityState === 'visible') load();
    });

    load();
    schedule();
})();
