(() => {
    // ── DOM refs ──────────────────────────────────────────────────────
    const bodyEl = document.getElementById('pricesBody');
    const lastEl = document.getElementById('lastUpdated');
    const heroMidEl = document.getElementById('heroMid');
    const heroMidSub = document.getElementById('heroMidSub');
    const heroAvgBuyEl = document.getElementById('heroAvgBuy');
    const heroAvgBuyN = document.getElementById('heroAvgBuyN');
    const heroAvgSellEl = document.getElementById('heroAvgSell');
    const heroAvgSellN = document.getElementById('heroAvgSellN');
    const ringEl = document.getElementById('ringFill');
    const countEl = document.getElementById('countdownText');
    const tickTrack = document.getElementById('tickTrack');

    const INTERVAL = 15_000;
    const CIRCUMF = 37.7;
    const EPS = 1e-9;

    const prev = new Map();
    const flashTimers = new WeakMap();
    let heroFirstRender = true;
    let tableFirstRender = true;

    // ── Sort state ───────────────────────────────────────────────────
    let sortKey = null;   // null = default spread sort
    let sortDir = 1;
    let lastStats = null;

    // ── Sponsor data ─────────────────────────────────────────────────
    const sponsorKeys = new Set();
    const sponsorLinks = new Map();
    let sponsorData = [];

    function normName(s) {
        return String(s ?? '').toLowerCase().replace(/[^a-z0-9]/g, '');
    }

    async function loadSponsors() {
        try {
            const res = await fetch('/api/sponsors', { cache: 'no-store' });
            if (!res.ok) { console.warn('[MoneroPriceNow] Sponsor fetch HTTP', res.status); return; }
            const data = await res.json();
            const now = Date.now();
            sponsorKeys.clear();
            sponsorData = [];
            for (const s of data) {
                const exp = s.expirationDate ? new Date(s.expirationDate).getTime() : Infinity;
                if (s.name) {
                    const nk = normName(s.name);
                    sponsorKeys.add(nk);
                    if (s.link) sponsorLinks.set(nk, s.link);
                    if (exp > now) sponsorData.push({ ...s, _exp: exp });
                }
            }
            const TIER_ORDER = ['MainSponsor', 'CategorySponsor', 'SubCategorySponsor', 'SubSponsor'];
            sponsorData.sort((a, b) => {
                const ta = TIER_ORDER.indexOf(a.sponsorshipType);
                const tb = TIER_ORDER.indexOf(b.sponsorshipType);
                const tierDiff = (ta < 0 ? 99 : ta) - (tb < 0 ? 99 : tb);
                return tierDiff !== 0 ? tierDiff : b._exp - a._exp;
            });
            console.log('[MoneroPriceNow] Sponsors loaded:', [...sponsorKeys]);
            renderSponsorSection();
        } catch (e) {
            console.warn('[MoneroPriceNow] Could not load sponsor list:', e);
        }
    }

    // ── Star rating helper ────────────────────────────────────────────
    function starHtml(rating) {
        if (rating == null) return '';
        const pct = Math.round((rating / 5) * 100);
        return `<span class="stars-wrap">` +
            `<span class="stars-empty">★★★★★</span>` +
            `<span class="stars-fill" style="width:${pct}%">★★★★★</span>` +
            `</span>`;
    }

    // ── Render sponsor cards ──────────────────────────────────────────
    function renderSponsorSection() {
        const section = document.getElementById('sponsorSection');
        const tiersEl = document.getElementById('sponsorTiers');
        if (!section || !tiersEl || !sponsorData.length) return;

        const groups = new Map();
        for (const s of sponsorData) {
            const key = s.sponsorshipType ?? 'Other';
            if (!groups.has(key)) groups.set(key, []);
            groups.get(key).push(s);
        }

        function tierLabel(key) {
            if (key === 'MainSponsor') return 'Main Sponsors';
            if (key === 'CategorySponsor') return 'Category Sponsors';
            if (key === 'SubSponsor') return 'Sub Sponsors';
            const norm = key.replace(/[-_\s]/g, '').toLowerCase();
            if (norm === 'subcategorysponsor') return 'Subcategory Sponsors';
            return key.replace(/([A-Z])/g, ' $1').trim();
        }

        const ORDER = ['MainSponsor', 'CategorySponsor', 'SubSponsor'];
        const sortedGroups = [...groups.keys()].sort((a, b) => {
            const ia = ORDER.indexOf(a), ib = ORDER.indexOf(b);
            return (ia < 0 ? 99 : ia) - (ib < 0 ? 99 : ib);
        });

        tiersEl.innerHTML = sortedGroups.map(tierKey => {
            const sponsors = groups.get(tierKey);
            const cards = sponsors.map(s => {
                const link = esc(s.link ?? '');
                const name = esc(s.name ?? '');
                const desc = esc(s.description ?? '');
                const note = s.note ? esc(s.note) : '';
                const rating = s.reviewRating;
                const count = s.reviewCount ?? 0;
                const revLink = esc(s.reviewLink ?? '');

                const ratingHtml = (rating != null && count > 0)
                    ? `<div class="sponsor-card-rating">
              ${starHtml(rating)}
              <span>${rating.toFixed(1)}</span>
              <a href="${revLink}" target="_blank" rel="noopener">(${count} review${count !== 1 ? 's' : ''})</a>
             </div>`
                    : '';

                const expDate = new Date(s.expirationDate);
                const expStr = expDate.toLocaleDateString('en-US', { month: 'short', day: 'numeric', year: 'numeric' });

                return `<div class="sponsor-card">
          <div class="sponsor-card-name"><a href="${link}" target="_blank" rel="noopener sponsored">${name}</a></div>
          ${ratingHtml}
          ${desc ? `<div class="sponsor-card-desc">${desc}</div>` : ''}
          ${note ? `<div class="sponsor-card-note">${note}</div>` : ''}
          <div class="sponsor-card-expiry">Sponsored until ${expStr}</div>
        </div>`;
            }).join('');

            return `<div class="sponsor-tier">
        <div class="sponsor-tier-label">${tierLabel(tierKey)}</div>
        <div class="sponsor-grid">${cards}</div>
      </div>`;
        }).join('');

        section.style.display = 'block';
    }

    function isSponsor(siteName) {
        return !!siteName && sponsorKeys.has(normName(siteName));
    }

    // ── Trend dot history ────────────────────────────────────────────
    // Stores last N midPrice values; newest = index 0
    const TREND_MAX = 8;
    const priceHistory = [];   // [{price, dir}]  newest first

    // Opacity and size by age position (0 = newest, TREND_MAX-1 = oldest)
    const TREND_OPACITY = [1.0, 0.80, 0.60, 0.44, 0.30, 0.20, 0.12, 0.07];
    const TREND_SIZE = [8, 7, 6, 6, 5, 5, 4, 4];
    const FLAT_THRESHOLD = 0.000001;  // essentially zero — neutral only if price is identical

    function recordPrice(mid) {
        if (mid == null) return;
        let dir = 'neutral';
        if (priceHistory.length > 0) {
            const prev = priceHistory[0].price;
            const chg = (mid - prev) / prev;
            if (chg > FLAT_THRESHOLD) dir = 'up';
            else if (chg < -FLAT_THRESHOLD) dir = 'down';
        }
        priceHistory.unshift({ price: mid, dir });
        if (priceHistory.length > TREND_MAX) priceHistory.pop();
        renderTrendDots();
    }

    function renderTrendDots() {
        if (!tickTrack) return;
        // Sync DOM to priceHistory — reuse existing dots where possible
        const existing = [...tickTrack.children];
        priceHistory.forEach((entry, i) => {
            let dot = existing[i];
            if (!dot) {
                dot = document.createElement('div');
                dot.className = 'trend-dot';
                tickTrack.appendChild(dot);
            }
            // Update class (direction can't change but be safe)
            dot.className = `trend-dot ${entry.dir}`;
            // Size and opacity by age
            const size = TREND_SIZE[i] ?? 4;
            const opacity = TREND_OPACITY[i] ?? 0.05;
            dot.style.width = size + 'px';
            dot.style.height = size + 'px';
            dot.style.opacity = opacity;
        });
        // Remove extra dots if history shrank
        while (tickTrack.children.length > priceHistory.length) {
            tickTrack.lastChild.remove();
        }
    }

    // ── Timer ─────────────────────────────────────────────────────────
    let refreshAt = Date.now() + INTERVAL;
    let refreshTid = null;

    function scheduleNext() {
        clearTimeout(refreshTid);
        refreshAt = Date.now() + INTERVAL;
        refreshTid = setTimeout(doRefresh, INTERVAL);
    }

    async function doRefresh() {
        await refresh();
        scheduleNext();
    }

    setInterval(() => {
        const rem = Math.max(0, refreshAt - Date.now());
        const secs = Math.ceil(rem / 1000);
        ringEl.style.strokeDashoffset = CIRCUMF * (1 - rem / INTERVAL);
        countEl.textContent = secs + 's';
    }, 250);

    // ── Helpers ───────────────────────────────────────────────────────
    function esc(s) {
        return String(s ?? '')
            .replaceAll('&', '&amp;').replaceAll('<', '&lt;')
            .replaceAll('>', '&gt;').replaceAll('"', '&quot;')
            .replaceAll("'", '&#39;');
    }
    function num(v) { const n = Number(v); return Number.isFinite(n) ? n : null; }
    function fmt(n) { const x = Number(n); return Number.isFinite(x) ? '$' + x.toFixed(2) : '—'; }
    function eq(a, b) { return a !== null && b !== null && Math.abs(a - b) < EPS; }

    function fmtSpread(pct) {
        if (pct === Infinity || pct == null) return '—';
        return (pct * 100).toFixed(2) + '%';
    }

    function updateTitle(mid) {
        const m = (mid !== null && mid >= 1) ? '$' + mid.toFixed(2) : '—';
        document.title = m + ' · MoneroPriceNow.com';
    }

    function pick(obj, ...keys) {
        for (const k of keys) { const v = obj[k]; if (v !== undefined && v !== null && v !== '') return v; }
        return null;
    }

    function utcString(d) {
        return d.getUTCFullYear() + '-' +
            String(d.getUTCMonth() + 1).padStart(2, '0') + '-' +
            String(d.getUTCDate()).padStart(2, '0') + ' ' +
            String(d.getUTCHours()).padStart(2, '0') + ':' +
            String(d.getUTCMinutes()).padStart(2, '0') + ':' +
            String(d.getUTCSeconds()).padStart(2, '0') + ' UTC';
    }

    function mapRow(x) {
        const siteName = pick(x, 'siteName', 'SiteName', 'displayName', 'DisplayName', 'name', 'Name', 'label', 'Label');
        const siteUrl = pick(x, 'siteUrl', 'SiteUrl', 'url', 'Url', 'affiliateUrl', 'AffiliateUrl', 'link', 'Link');
        const exchangeKey = pick(x, 'exchange', 'Exchange', 'key', 'Key', 'id', 'Id') ?? '?';
        return {
            exchange: exchangeKey,
            siteName: siteName || exchangeKey,
            siteUrl: siteUrl || null,
            sell: num(pick(x, 'sell', 'Sell', 'sellPrice', 'SellPrice')),
            buy: num(pick(x, 'buy', 'Buy', 'buyPrice', 'BuyPrice')),
            tsUtc: pick(x, 'tsUtc', 'TsUtc', 'ts', 'Ts', 'timestamp', 'Timestamp'),
            privacy: pick(x, 'privacyLevel', 'PrivacyLevel', 'privacy', 'Privacy') ?? null
        };
    }

    function nameLink(row) {
        const n = esc(row.siteName);
        const sKey = normName(row.siteName);
        const href = sponsorLinks.has(sKey)
            ? esc(sponsorLinks.get(sKey))
            : row.siteUrl ? esc(row.siteUrl) : null;
        return href
            ? `<a class="ex-name" href="${href}" target="_blank" rel="noopener sponsored">${n}</a>`
            : `<span class="ex-name">${n}</span>`;
    }

    function privacyBadge(p) {
        if (!p) return '<span class="privacy-badge privacy-">—</span>';
        const l = String(p).toUpperCase().charAt(0);
        return `<span class="privacy-badge privacy-${l}">${l}</span>`;
    }

    function computeStats(rawData) {
        const rows = rawData.map(mapRow);
        const withBuy = rows.filter(r => r.buy !== null && r.buy >= 1);
        const withSell = rows.filter(r => r.sell !== null && r.sell >= 1);
        const bestBuyRow = withBuy.length ? withBuy.reduce((a, b) => b.buy < a.buy ? b : a) : null;
        const bestSellRow = withSell.length ? withSell.reduce((a, b) => b.sell > a.sell ? b : a) : null;

        rows.forEach(r => {
            if (r.buy !== null && r.sell !== null && r.buy >= 1 && r.sell >= 1) {
                r.spreadAbs = r.buy - r.sell;
                r.spreadPct = r.spreadAbs >= 0 ? r.spreadAbs / r.buy : Infinity;
            } else {
                r.spreadAbs = r.spreadPct = Infinity;
            }
        });

        const visibleRows = rows.filter(r =>
            (r.buy !== null && r.buy >= 1) ||
            (r.sell !== null && r.sell >= 1)
        );

        const avgBuyVal = withBuy.length ? withBuy.reduce((s, r) => s + r.buy, 0) / withBuy.length : null;
        const avgSellVal = withSell.length ? withSell.reduce((s, r) => s + r.sell, 0) / withSell.length : null;
        const allPrices = [...withBuy.map(r => r.buy), ...withSell.map(r => r.sell)];
        const midPrice = allPrices.length ? allPrices.reduce((s, v) => s + v, 0) / allPrices.length : null;

        return {
            rows: visibleRows, bestBuyRow, bestSellRow,
            bestBuyVal: withBuy.length ? Math.min(...withBuy.map(r => r.buy)) : null,
            worstBuyVal: withBuy.length ? Math.max(...withBuy.map(r => r.buy)) : null,
            bestSellVal: withSell.length ? Math.max(...withSell.map(r => r.sell)) : null,
            worstSellVal: withSell.length ? Math.min(...withSell.map(r => r.sell)) : null,
            avgBuyVal, avgSellVal, midPrice,
            avgBuyCount: withBuy.length,
            avgSellCount: withSell.length,
            priceCount: allPrices.length,
        };
    }

    // ── Sort ─────────────────────────────────────────────────────────
    function sortedRows(rows) {
        if (!sortKey) {
            return [...rows].sort((a, b) =>
                a.spreadPct !== b.spreadPct ? a.spreadPct - b.spreadPct :
                    a.spreadAbs !== b.spreadAbs ? a.spreadAbs - b.spreadAbs :
                        a.exchange.localeCompare(b.exchange));
        }
        return [...rows].sort((a, b) => {
            let v;
            if (sortKey === 'name') {
                v = (a.siteName ?? '').localeCompare(b.siteName ?? '');
            } else if (sortKey === 'buy') {
                const av = a.buy ?? (sortDir > 0 ? Infinity : -Infinity);
                const bv = b.buy ?? (sortDir > 0 ? Infinity : -Infinity);
                v = av - bv;
            } else if (sortKey === 'privacy') {
                const order = s => { if (!s) return 99; const i = 'ABCDF'.indexOf(String(s).toUpperCase()); return i < 0 ? 99 : i; };
                v = order(a.privacy) - order(b.privacy);
            } else if (sortKey === 'spread') {
                const av = a.spreadPct !== Infinity ? a.spreadPct : (sortDir > 0 ? Infinity : -Infinity);
                const bv = b.spreadPct !== Infinity ? b.spreadPct : (sortDir > 0 ? Infinity : -Infinity);
                v = av - bv;
            } else if (sortKey === 'sell') {
                const av = a.sell ?? (sortDir > 0 ? -Infinity : Infinity);
                const bv = b.sell ?? (sortDir > 0 ? -Infinity : Infinity);
                v = av - bv;
            }
            return v * sortDir;
        });
    }

    function updateSortHeaders() {
        document.querySelectorAll('.th-sort').forEach(th => {
            const arrow = th.querySelector('.sort-arrow');
            if (th.dataset.sort === sortKey) {
                th.classList.add('sort-active');
                arrow.textContent = sortDir > 0 ? ' ▲' : ' ▼';
            } else {
                th.classList.remove('sort-active');
                arrow.textContent = '';
            }
        });
    }

    // ── Hero ──────────────────────────────────────────────────────────
    function animateCountUp(el, target) {
        const start = target * 0.92, dur = 600, t0 = performance.now();
        el.classList.remove('hero-revealed'); void el.offsetWidth; el.classList.add('hero-revealed');
        (function step(now) {
            const p = Math.min((now - t0) / dur, 1), e = 1 - Math.pow(1 - p, 3);
            el.textContent = fmt(start + (target - start) * e);
            p < 1 ? requestAnimationFrame(step) : (el.textContent = fmt(target));
        })(performance.now());
    }

    function renderHero(s) {
        const first = heroFirstRender; heroFirstRender = false;

        // Center market price
        if (s.midPrice != null) {
            first ? animateCountUp(heroMidEl, s.midPrice) : (heroMidEl.textContent = fmt(s.midPrice));
            heroMidSub.textContent = `avg of ${s.priceCount} rates · ${s.rows.length} exchanges`;
        } else {
            heroMidEl.textContent = '—';
            heroMidSub.textContent = 'No data';
        }

        // Left: avg buy
        heroAvgBuyEl.textContent = s.avgBuyVal != null ? fmt(s.avgBuyVal) : '—';
        heroAvgBuyN.textContent = 'USDT → XMR · you pay per XMR';

        // Right: avg sell
        heroAvgSellEl.textContent = s.avgSellVal != null ? fmt(s.avgSellVal) : '—';
        heroAvgSellN.textContent = 'XMR → USDT · you receive per XMR';
    }

    // ── Table ─────────────────────────────────────────────────────────
    function pClass(val, best, worst) {
        if (val === null || val < 1) return '';
        return eq(val, best) ? 'price-good' : eq(val, worst) ? 'price-bad' : '';
    }

    function applySponsorClass(tr, siteName) {
        if (isSponsor(siteName)) tr.classList.add('is-sponsor');
        else tr.classList.remove('is-sponsor');
    }

    function renderTable(rows, bestBuy, worstBuy, bestSell, worstSell) {
        if (!rows.length) {
            bodyEl.innerHTML = `<tr><td colspan="6" style="padding:1.5rem 1rem;color:var(--text-muted);">No prices available.</td></tr>`;
            tableFirstRender = true; return;
        }

        const sorted = sortedRows(rows);

        if (tableFirstRender) {
            bodyEl.innerHTML = sorted.map(r => {
                const key = r.exchange.trim().toLowerCase();
                const sponsorCls = isSponsor(r.siteName) ? ' is-sponsor' : '';
                prev.set(key, { buy: r.buy, sell: r.sell });
                const bT = (r.buy !== null && r.buy >= 1) ? fmt(r.buy) : '—';
                const sT = (r.sell !== null && r.sell >= 1) ? fmt(r.sell) : '—';
                const ts = r.tsUtc ? new Date(r.tsUtc).toLocaleTimeString() : '—';
                const tsTitle = r.tsUtc ? utcString(new Date(r.tsUtc)) : '';
                return `<tr class="data-row${sponsorCls}" data-ex="${esc(key)}">
          <td>${nameLink(r)}</td>
          <td data-col="privacy">${privacyBadge(r.privacy)}</td>
          <td class="${pClass(r.buy, bestBuy, worstBuy)}" data-col="buy">${bT}</td>
          <td class="${pClass(r.sell, bestSell, worstSell)}" data-col="sell">${sT}</td>
          <td data-col="spread">${fmtSpread(r.spreadPct)}</td>
          <td class="ts-cell" data-col="ts" title="${tsTitle}">${ts}</td>
        </tr>`;
            }).join('');
            tableFirstRender = false;
            setTimeout(() => {
                bodyEl.querySelectorAll('tr.data-row').forEach(tr => tr.classList.remove('data-row'));
            }, 450);
            return;
        }

        const rowMap = new Map();
        bodyEl.querySelectorAll('tr[data-ex]').forEach(tr => rowMap.set(tr.dataset.ex, tr));

        const visibleKeys = new Set(rows.map(r => r.exchange.trim().toLowerCase()));
        rowMap.forEach((tr, key) => {
            if (!visibleKeys.has(key)) { tr.remove(); rowMap.delete(key); prev.delete(key); }
        });

        sorted.forEach(r => {
            const key = r.exchange.trim().toLowerCase();
            const p = prev.get(key) || { buy: null, sell: null };
            let tr = rowMap.get(key);
            if (!tr) {
                tr = document.createElement('tr');
                tr.dataset.ex = key;
                tr.innerHTML = `<td>${nameLink(r)}</td><td data-col="privacy"></td><td data-col="buy"></td><td data-col="sell"></td><td data-col="spread"></td><td class="ts-cell" data-col="ts"></td>`;
                bodyEl.appendChild(tr); rowMap.set(key, tr);
            }

            applySponsorClass(tr, r.siteName);

            const privacyCell = tr.querySelector('[data-col="privacy"]');
            const buyCell = tr.querySelector('[data-col="buy"]');
            const sellCell = tr.querySelector('[data-col="sell"]');
            const spreadCell = tr.querySelector('[data-col="spread"]');
            const tsCell = tr.querySelector('[data-col="ts"]');

            const bT = (r.buy !== null && r.buy >= 1) ? fmt(r.buy) : '—';
            const sT = (r.sell !== null && r.sell >= 1) ? fmt(r.sell) : '—';
            const bC = pClass(r.buy, bestBuy, worstBuy);
            const sC = pClass(r.sell, bestSell, worstSell);

            function setCell(cell, newText, baseClass, oldVal, newVal, better) {
                const tid = flashTimers.get(cell);
                if (tid != null) { clearTimeout(tid); flashTimers.delete(cell); }
                const valueChanged = newText !== cell.textContent;
                const realChange = oldVal !== null && newVal !== null && !eq(newVal, oldVal);
                const shouldFlash = valueChanged && realChange;
                const flashCls = shouldFlash ? (better ? 'flash-good' : 'flash-bad') : '';
                cell.className = [flashCls, baseClass].filter(Boolean).join(' ');
                cell.textContent = newText;
                if (flashCls) {
                    const t = setTimeout(() => { cell.className = baseClass; flashTimers.delete(cell); }, 950);
                    flashTimers.set(cell, t);
                }
            }

            setCell(buyCell, bT, bC, p.buy, r.buy, r.buy !== null && p.buy !== null && r.buy < p.buy);
            setCell(sellCell, sT, sC, p.sell, r.sell, r.sell !== null && p.sell !== null && r.sell > p.sell);

            privacyCell.innerHTML = privacyBadge(r.privacy);
            spreadCell.textContent = fmtSpread(r.spreadPct);
            tsCell.textContent = r.tsUtc ? new Date(r.tsUtc).toLocaleTimeString() : '—';
            tsCell.title = r.tsUtc ? utcString(new Date(r.tsUtc)) : '';

            prev.set(key, { buy: r.buy, sell: r.sell });
        });

        // Only reorder if order changed
        const currentOrder = [...bodyEl.querySelectorAll('tr[data-ex]')].map(tr => tr.dataset.ex);
        const desiredOrder = sorted.map(r => r.exchange.trim().toLowerCase());
        const orderChanged = currentOrder.length !== desiredOrder.length ||
            currentOrder.some((k, i) => k !== desiredOrder[i]);
        if (orderChanged) {
            desiredOrder.forEach(key => {
                const tr = bodyEl.querySelector(`tr[data-ex="${key}"]`);
                if (tr) bodyEl.appendChild(tr);
            });
        }
    }

    // ── Fetch ─────────────────────────────────────────────────────────
    async function refresh() {
        try {
            const res = await fetch('/api/prices/two-way?base=XMR&quote=USDTTRC', { cache: 'no-store' });
            if (!res.ok) throw new Error('HTTP ' + res.status);
            const data = await res.json();
            if (!Array.isArray(data)) throw new Error('Bad JSON');
            if (!data.length) {
                heroMidEl.textContent = '—';
                heroMidSub.textContent = 'No data';
                heroAvgBuyEl.textContent = '—';
                heroAvgSellEl.textContent = '—';
                bodyEl.innerHTML = `<tr><td colspan="6" style="padding:1.5rem 1rem;color:var(--text-muted);">No prices available.</td></tr>`;
                lastEl.textContent = 'Last updated: ' + new Date().toLocaleTimeString();
                lastEl.title = utcString(new Date());
                return;
            }
            const s = computeStats(data);
            lastStats = s;
            renderHero(s);
            updateTitle(s.midPrice);
            renderTable(s.rows, s.bestBuyVal, s.worstBuyVal, s.bestSellVal, s.worstSellVal);
            updateSortHeaders();
            lastEl.textContent = 'Last updated: ' + new Date().toLocaleTimeString();
            lastEl.title = utcString(new Date());
            recordPrice(s.midPrice);
        } catch (err) {
            console.error('[MoneroPriceNow] Refresh error:', err);
            lastEl.textContent = 'Update failed (retrying…)';
        }
    }

    // ── Sort header click handler ────────────────────────────────────
    document.querySelectorAll('.th-sort').forEach(th => {
        th.addEventListener('click', () => {
            const col = th.dataset.sort;
            if (sortKey === col) {
                sortDir = -sortDir;
            } else {
                sortKey = col;
                sortDir = col === 'sell' ? -1 : 1;
            }
            updateSortHeaders();
            if (lastStats) {
                renderTable(lastStats.rows, lastStats.bestBuyVal, lastStats.worstBuyVal,
                    lastStats.bestSellVal, lastStats.worstSellVal);
            }
        });
    });

    // ── Boot ─────────────────────────────────────────────────────────
    loadSponsors();
    refresh().then(scheduleNext);
})();