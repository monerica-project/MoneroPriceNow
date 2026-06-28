// netfee.js — live updates for the network-fee summary block.
// Polls /api/network-fee for this page's coin and refreshes the tier values
// without a page reload. When a value changes it flashes (green = the fee
// dropped, red = the fee rose) and leaves a small ▲/▼ until the next change,
// so it's obvious when fees move. Static values stay neutral grey.
(() => {
    'use strict';

    const root = document.querySelector('.netfee');
    if (!root) return;

    const NETWORK = (window.__PAIR__ && window.__PAIR__.feeNetwork) || root.getAttribute('data-network') || '';
    if (!NETWORK) return;

    const grid = root.querySelector('.netfee-grid');
    const updatedEl = root.querySelector('.netfee-updated');
    if (!grid) return;

    const POLL_MS = 60_000;          // matches the server warm interval
    let prev = {};                   // label -> { primary, usd }
    let lastUpdatedMs = Date.now();

    function esc(s) {
        const d = document.createElement('div');
        d.textContent = s == null ? '' : s;
        return d.innerHTML;
    }

    function num(s) {
        if (s == null) return null;
        const v = parseFloat(String(s).replace(/[^0-9.]/g, ''));
        return isFinite(v) ? v : null;
    }

    // +1 = fee went up (pricier), -1 = down (cheaper), 0 = same/unknown.
    function direction(p, t) {
        let a = num(p.usd), b = num(t.usd);
        if (a == null || b == null) { a = num(p.primary); b = num(t.primary); }
        if (a == null || b == null) return 0;
        return b > a ? 1 : b < a ? -1 : 0;
    }

    function render(fee) {
        if (!fee || !Array.isArray(fee.tiers) || !fee.tiers.length) return;

        grid.innerHTML = fee.tiers.map(t => {
            const p = prev[t.label];
            let bump = '', arrow = '';
            const dir = p ? direction(p, t) : 0;
            if (dir > 0) { bump = ' netfee-bump-up'; arrow = '<span class="netfee-dir up">▲</span>'; }
            else if (dir < 0) { bump = ' netfee-bump-down'; arrow = '<span class="netfee-dir down">▼</span>'; }

            // The dollar cost is the headline (t.primary); the arrow rides with it.
            // If a separate USD field is ever present, it carries the arrow instead.
            const usdHtml = t.usd
                ? `<div class="netfee-tier-usd${bump}">≈ ${esc(t.usd)}${arrow}</div>`
                : '';
            const primaryArrow = t.usd ? '' : arrow;

            return `<div class="netfee-tier">
                <div class="netfee-tier-label">${esc(t.label)}</div>
                <div class="netfee-tier-primary${bump}">${esc(t.primary)}${primaryArrow}</div>
                ${usdHtml}
                <div class="netfee-tier-sub">${esc(t.secondary)}</div>
            </div>`;
        }).join('');

        prev = {};
        fee.tiers.forEach(t => { prev[t.label] = { primary: t.primary, usd: t.usd || '' }; });

        lastUpdatedMs = fee.updatedAtMs || Date.now();
        tickUpdated();
    }

    function tickUpdated() {
        if (!updatedEl) return;
        const s = Math.max(0, Math.round((Date.now() - lastUpdatedMs) / 1000));
        updatedEl.textContent = s < 5 ? 'updated just now'
            : s < 90 ? `updated ${s}s ago`
            : `updated ${Math.round(s / 60)}m ago`;
    }

    // Seed the baseline from the server-rendered values so the first poll can
    // already detect a change (fees may move between page load and first poll).
    root.querySelectorAll('.netfee-tier').forEach(el => {
        const label = el.querySelector('.netfee-tier-label')?.textContent?.trim();
        if (!label) return;
        const primary = el.querySelector('.netfee-tier-primary')?.textContent?.trim();
        const usd = el.querySelector('.netfee-tier-usd')?.textContent?.replace('≈', '').trim();
        prev[label] = { primary, usd: usd || '' };
    });

    async function load() {
        try {
            const res = await fetch(`/api/network-fee?network=${encodeURIComponent(NETWORK)}`, { cache: 'no-store' });
            if (!res.ok) return;
            const fee = await res.json();
            if (fee && fee.ok && fee.tiers) render(fee);
        } catch (e) {
            console.warn('[NetFee] fetch failed:', e);
        }
    }

    setInterval(() => { if (document.visibilityState === 'visible') load(); }, POLL_MS);
    setInterval(tickUpdated, 1000);
    document.addEventListener('visibilitychange', () => { if (document.visibilityState === 'visible') load(); });

    tickUpdated();
    load();
})();
