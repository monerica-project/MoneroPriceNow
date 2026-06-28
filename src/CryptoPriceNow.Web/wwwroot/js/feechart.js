// feechart.js — network fee history chart fed by /api/fee-history.
// Plots the typical-transaction fee in USD over time for this page's coin
// (Bitcoin / Ethereum / Monero), with the native rate shown in the tooltip.
// Self-contained: if Chart.js failed to load or the API reports no database,
// the section stays hidden and the rest of the page is untouched.
(() => {
    'use strict';

    const section = document.getElementById('feeChartSection');
    const canvas = document.getElementById('feeChart');
    const trendEl = document.getElementById('feeChartTrend');
    const emptyEl = document.getElementById('feeChartEmpty');
    const rangesEl = document.getElementById('feeChartRanges');
    if (!section || !canvas || typeof Chart === 'undefined') return;

    const NETWORK = (window.__PAIR__ && window.__PAIR__.feeNetwork) || '';
    if (!NETWORK) return;

    const REFRESH_MS = 60_000;

    let currentRange = '1h';
    let chart = null;
    let refreshTid = null;
    let everHadData = false;
    let nativeUnit = '';

    const css = getComputedStyle(document.documentElement);
    const cVar = (name, fallback) => (css.getPropertyValue(name) || '').trim() || fallback;
    const COLOR_FEE = cVar('--xmr-orange2', '#ff8c3a');
    const COLOR_MUTED = cVar('--text-muted', '#8a7a6a');
    const COLOR_TEXT = cVar('--text', '#e8dfd0');
    const COLOR_GRID = 'rgba(242,104,34,.08)';
    const FONT_MONO = cVar('--mono', 'ui-monospace, monospace');

    function fmtLabel(ms, bucketSeconds) {
        const d = new Date(ms);
        const hm = d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', hour12: false });
        if (bucketSeconds >= 3600) {
            const md = d.toLocaleDateString([], { month: 'short', day: 'numeric' });
            return `${md} ${hm}`;
        }
        return hm;
    }

    function fmtUsd(v) {
        if (v == null) return '—';
        const n = Number(v);
        if (n >= 0.01) return '$' + n.toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 });
        return '$' + n.toLocaleString(undefined, { maximumFractionDigits: 6 });
    }

    function fmtNative(v) {
        if (v == null) return '';
        return Number(v).toLocaleString(undefined, { maximumFractionDigits: 3 }) + (nativeUnit ? ' ' + nativeUnit : '');
    }

    function updateTrend(points, rangeKey) {
        if (!trendEl) return;
        const vals = points.map(p => p.usd).filter(v => v != null);
        if (vals.length < 2) { trendEl.textContent = ''; trendEl.className = 'chart-trend'; return; }
        const first = Number(vals[0]);
        const last = Number(vals[vals.length - 1]);
        if (!first) { trendEl.textContent = ''; return; }
        const pct = ((last - first) / first) * 100;
        const up = pct >= 0;
        trendEl.textContent = `${up ? '▲' : '▼'} ${up ? '+' : ''}${pct.toFixed(1)}% / ${rangeKey}`;
        // For fees, rising is "bad" (more expensive) — reuse the same up/down classes.
        trendEl.className = 'chart-trend ' + (up ? 'trend-down' : 'trend-up');
    }

    function applyAvailableRanges(available) {
        if (!rangesEl || !Array.isArray(available) || available.length === 0) return;
        const set = new Set(available);
        const btns = [...rangesEl.querySelectorAll('.chart-range-btn')];
        btns.forEach(b => { b.hidden = !set.has(b.dataset.range); });
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
        const usd = points.map(p => p.usd != null ? Number(p.usd) : null);
        const native = points.map(p => p.native != null ? Number(p.native) : null);

        // Need at least two points to draw a meaningful line; until then show a
        // clear "still collecting" message instead of an empty-looking chart.
        if (points.length >= 2) everHadData = true;
        if (emptyEl) {
            emptyEl.hidden = points.length >= 2;
            emptyEl.textContent = points.length === 0
                ? 'Collecting fee history… check back soon.'
                : `Collecting fee history… (${points.length} point${points.length === 1 ? '' : 's'} so far — a line appears once there are 2+)`;
        }
        updateTrend(points, payload.range || currentRange);

        if (chart) {
            chart.data.labels = labels;
            chart.data.datasets[0].data = usd;
            chart.$native = native;
            chart.update('none');
            return;
        }

        chart = new Chart(canvas.getContext('2d'), {
            type: 'line',
            data: {
                labels,
                datasets: [{
                    label: 'Typical tx fee (USD)',
                    data: usd,
                    borderColor: COLOR_FEE,
                    backgroundColor: 'rgba(242,104,34,.10)',
                    borderWidth: 2,
                    pointRadius: 0,
                    pointHitRadius: 8,
                    tension: 0.25,
                    spanGaps: true,
                    fill: true
                }]
            },
            options: {
                responsive: true,
                maintainAspectRatio: false,
                animation: false,
                interaction: { mode: 'index', intersect: false },
                plugins: {
                    legend: { display: false },
                    tooltip: {
                        backgroundColor: 'rgba(13,10,7,.95)',
                        borderColor: 'rgba(242,104,34,.35)',
                        borderWidth: 1,
                        titleColor: COLOR_TEXT,
                        bodyColor: COLOR_MUTED,
                        titleFont: { family: FONT_MONO, size: 11 },
                        bodyFont: { family: FONT_MONO, size: 11 },
                        callbacks: {
                            label: ctx => {
                                const nat = chart && chart.$native ? chart.$native[ctx.dataIndex] : null;
                                const natStr = nat != null ? `  (${fmtNative(nat)})` : '';
                                return ` ${fmtUsd(ctx.parsed.y)} per tx${natStr}`;
                            }
                        }
                    }
                },
                scales: {
                    x: {
                        ticks: { color: COLOR_MUTED, font: { family: FONT_MONO, size: 9 }, maxTicksLimit: 8, maxRotation: 0 },
                        grid: { color: COLOR_GRID, drawTicks: false }
                    },
                    y: {
                        position: 'right',
                        ticks: { color: COLOR_MUTED, font: { family: FONT_MONO, size: 9 }, callback: v => fmtUsd(v) },
                        grid: { color: COLOR_GRID, drawTicks: false }
                    }
                }
            }
        });
        chart.$native = native;
    }

    async function load() {
        try {
            const url = `/api/fee-history?network=${encodeURIComponent(NETWORK)}&range=${encodeURIComponent(currentRange)}`;
            const res = await fetch(url, { cache: 'no-store' });
            if (!res.ok) return;
            const data = await res.json();
            if (!data.enabled) { if (!everHadData) section.hidden = true; return; }
            // Capture native unit from the first point that has one (for tooltips).
            const withNative = (data.points || []).find(p => p.native != null);
            if (withNative) nativeUnit = NETWORK === 'bitcoin' ? 'sat/vB' : NETWORK === 'ethereum' ? 'Gwei' : 'pXMR/byte';
            section.hidden = false;
            render(data);
        } catch (e) {
            console.warn('[FeeChart] history fetch failed:', e);
        }
    }

    function schedule() {
        clearInterval(refreshTid);
        refreshTid = setInterval(() => { if (document.visibilityState === 'visible') load(); }, REFRESH_MS);
    }

    if (rangesEl) {
        rangesEl.addEventListener('click', (e) => {
            const btn = e.target.closest('.chart-range-btn');
            if (!btn || btn.dataset.range === currentRange) return;
            currentRange = btn.dataset.range;
            rangesEl.querySelectorAll('.chart-range-btn')
                .forEach(b => b.classList.toggle('chart-range-active', b === btn));
            load();
            schedule();
        });
    }

    document.addEventListener('visibilitychange', () => {
        if (document.visibilityState === 'visible') load();
    });

    load();
    schedule();
})();
