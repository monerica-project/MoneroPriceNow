// exchangepage.js — live 15s ticker, Float/Fixed rate selector, and sponsor handling for
// /exchange/{name}.
//   • Polls this exchange's current buy/sell every 15s and flashes cards green/red on change.
//   • Keeps floating and fixed rates SEPARATE (never averaged); the Float/Fixed toggle
//     switches the cards and the chart together. This is also why the forming chart bucket
//     no longer swings — a bucket only ever holds one rate type.
//   • Marks the exchange as a Monerica sponsor and, since a paid sponsor must not be sent
//     through an affiliate URL, swaps the outbound link to the sponsor's plain link.
(function () {
    const EX = window.__EXCHANGE__;
    if (!EX || !EX.key) return;

    const INTERVAL = 15_000;
    const flashTimers = new WeakMap();
    let currentRate = EX.defaultRate || 'float';

    function normName(s) { return String(s ?? '').toLowerCase().replace(/[^a-z0-9]/g, ''); }

    function fmt(v, p) {
        if (v == null || !isFinite(v)) return '—';
        const n = Number(v).toLocaleString('en-US', {
            minimumFractionDigits: p.decimals, maximumFractionDigits: p.decimals
        });
        return `${p.symbol}${n}${p.suffix}`;
    }

    // last[apiQuote] = { float:{buy,sell}, fixed:{buy,sell} } — seeded from the server render
    // so a toggle before the first poll still has values to show.
    const last = {};
    for (const p of EX.pairs) last[p.apiQuote] = { float: p.float || null, fixed: p.fixed || null };

    function pconf(quote) { return EX.pairs.find(x => x.apiQuote === quote); }

    // Render one card from stored values for the current rate. flash=true animates on change.
    function renderCard(card, flash) {
        const quote = card.dataset.quote;
        const p = pconf(quote);
        const store = last[quote];
        if (!p || !store) return;
        const side = store[currentRate] || null;

        const buyCell = card.querySelector('[data-side="buy"]');
        const sellCell = card.querySelector('[data-side="sell"]');
        setCell(buyCell, side ? side.buy : null, p, flash);
        setCell(sellCell, side ? side.sell : null, p, flash);
    }

    function setCell(cell, value, p, flash) {
        if (!cell) return;
        const text = fmt(value, p);
        if (cell.textContent === text) return;
        const prevNum = parseFloat(String(cell.textContent).replace(/[^0-9.\-]/g, ''));
        cell.textContent = text;
        if (!flash) return;
        const dir = (isFinite(prevNum) && value != null) ? Math.sign(value - prevNum) : 0;
        const cls = dir > 0 ? 'flash-good' : dir < 0 ? 'flash-bad' : null;
        if (!cls) return;
        const prev = flashTimers.get(cell);
        if (prev) { clearTimeout(prev); cell.classList.remove('flash-good', 'flash-bad'); }
        void cell.offsetWidth;
        cell.classList.add(cls);
        flashTimers.set(cell, setTimeout(() => cell.classList.remove(cls), 950));
    }

    async function tickOne(card) {
        const quote = card.dataset.quote;
        try {
            const res = await fetch(`/api/prices/two-way?base=XMR&quote=${encodeURIComponent(quote)}`, { cache: 'no-store' });
            if (!res.ok) return;
            const rows = await res.json();
            const mine = (rows || []).filter(r =>
                normName(r.exchange || r.Exchange || r.key || r.Key) === normName(EX.key));
            if (!mine.length) return;

            const store = last[quote] || (last[quote] = { float: null, fixed: null });
            for (const r of mine) {
                const rt = (r.rateType || r.RateType || 'float').toLowerCase() === 'fixed' ? 'fixed' : 'float';
                store[rt] = { buy: r.buy ?? r.Buy ?? null, sell: r.sell ?? r.Sell ?? null };
            }
            renderCard(card, true);
        } catch { /* transient */ }
    }

    // ── Countdown ring to the next update (mirrors the homepage) ──────────────
    const CIRCUMF = 37.7;
    const ringEl = document.getElementById('exRingFill');
    const countEl = document.getElementById('exCountdown');
    let nextAt = Date.now() + INTERVAL;
    setInterval(() => {
        const rem = Math.max(0, nextAt - Date.now());
        if (countEl) countEl.textContent = Math.ceil(rem / 1000) + 's';
        if (ringEl) ringEl.style.strokeDashoffset = CIRCUMF * (1 - rem / INTERVAL);
    }, 250);

    function tickAll() {
        nextAt = Date.now() + INTERVAL;
        document.querySelectorAll('.ex-card[data-quote]').forEach(tickOne);
        const el = document.getElementById('exUpdated');
        if (el) el.textContent = 'Updated ' + new Date().toLocaleTimeString([], { hour12: false });
    }

    // ── Float / Fixed selector ────────────────────────────────────────────────
    const rateToggle = document.getElementById('exRateToggle');
    if (rateToggle) {
        rateToggle.addEventListener('click', (e) => {
            const btn = e.target.closest('.ex-rate-btn');
            if (!btn || btn.dataset.rate === currentRate) return;
            currentRate = btn.dataset.rate;
            window.__RATE_TYPE__ = currentRate;

            rateToggle.querySelectorAll('.ex-rate-btn')
                .forEach(b => b.classList.toggle('ex-rate-active', b === btn));

            // Re-render cards for the new rate (no flash — it's a view change, not a price move).
            document.querySelectorAll('.ex-card[data-quote]').forEach(c => renderCard(c, false));

            const note = document.getElementById('chartRateNote');
            if (note) note.textContent = currentRate === 'fixed' ? 'fixed rate' : 'floating rate';
            const help = document.getElementById('exRateHelp');
            if (help) help.textContent = currentRate === 'fixed'
                ? 'Fixed rates are locked when you start the swap.'
                : 'Floating rates move with the market until your swap completes.';

            // Tell the chart to reload for this rate type (pricechart.js listens for this).
            window.dispatchEvent(new Event('ratetypechange'));
        });
    }

    async function checkSponsor() {
        try {
            const res = await fetch('/api/sponsors', { cache: 'no-store' });
            if (!res.ok) return;
            const data = await res.json();
            const nk = normName(EX.name);
            const now = Date.now();
            const match = (data || []).find(s => {
                if (normName(s.name) !== nk) return false;
                const exp = s.expirationDate ? new Date(s.expirationDate).getTime() : Infinity;
                return exp > now;
            });
            if (!match) return;

            const badge = document.getElementById('exSponsorBadge');
            const note = document.getElementById('exSponsorNote');
            if (badge) badge.hidden = false;
            if (note) note.hidden = false;
            const tierEl = document.getElementById('exSponsorTier');
            if (tierEl && match.sponsorshipType) {
                tierEl.textContent = `(${String(match.sponsorshipType).replace(/([a-z])([A-Z])/g, '$1 $2')})`;
            }

            const out = document.getElementById('exOutLink');
            if (out) {
                if (match.link) { out.href = match.link; out.rel = 'noopener'; }
                else { out.remove(); }
            }
        } catch { /* leave affiliate link as-is */ }
    }

    // Seed cards from server values, then go live.
    document.querySelectorAll('.ex-card[data-quote]').forEach(c => renderCard(c, false));
    checkSponsor();
    tickAll();
    setInterval(() => { if (document.visibilityState === 'visible') tickAll(); }, INTERVAL);
    document.addEventListener('visibilitychange', () => { if (document.visibilityState === 'visible') tickAll(); });
})();
