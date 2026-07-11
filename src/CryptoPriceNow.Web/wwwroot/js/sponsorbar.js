// sponsorbar.js — populates the top sponsor bar (#sponsorBarTrack) on EVERY page.
//
// The bar lives in _Layout, but historically only the homepage script rendered
// it, so it was blank on About/Contact/Sponsors/Network/etc. This tiny,
// self-contained script owns the bar everywhere: it fetches /api/sponsors,
// shows a spaced-out set of names, and rotates through the sets. It no-ops
// cleanly if the bar element or the API is absent.
(() => {
    'use strict';

    const track = document.getElementById('sponsorBarTrack');
    if (!track) return;

    const BANNER_ROTATE_MS = 60_000;   // how long each set stays up
    const BANNER_FADE_MS = 600;        // exit fade — keep in sync with CSS transition
    const TIER_ORDER = ['MainSponsor', 'CategorySponsor', 'SubCategorySponsor', 'SubSponsor'];

    let sponsorData = [];
    let bannerPages = [];
    let bannerIdx = 0;
    let bannerTid = null;

    function esc(s) {
        return String(s ?? '')
            .replaceAll('&', '&amp;').replaceAll('<', '&lt;')
            .replaceAll('>', '&gt;').replaceAll('"', '&quot;').replaceAll("'", '&#39;');
    }
    function normName(s) { return String(s ?? '').toLowerCase().replace(/[^a-z0-9]/g, ''); }

    function bannerSlots() {
        // fewer names at once on narrow screens so they stay readable
        return window.matchMedia('(max-width: 640px)').matches ? 2 : 5;
    }
    function chunkArr(arr, size) {
        const out = [];
        for (let i = 0; i < arr.length; i += size) out.push(arr.slice(i, i + size));
        return out;
    }
    function bannerNameHtml(s) {
        const nm = esc(s.name ?? '');
        const tierCls = 'sb-' + normName(s.sponsorshipType || 'other');
        const href = s.link ? esc(s.link) : null;
        return href
            ? `<a class="sb-sponsor ${tierCls}" href="${href}" target="_blank" rel="noopener sponsored">${nm}</a>`
            : `<span class="sb-sponsor ${tierCls}">${nm}</span>`;
    }
    function renderBannerPage(i) {
        if (!bannerPages[i]) return;
        // Fresh nodes re-run the CSS entrance animation — staggered fade-in.
        track.innerHTML = bannerPages[i].map(bannerNameHtml).join('');
    }

    function startBanner() {
        clearInterval(bannerTid);
        bannerTid = null;
        bannerIdx = 0;

        if (!sponsorData.length) { track.innerHTML = ''; return; }

        // Main sponsors get their own pages first, so the first cycle is always
        // main sponsors only — never padded out with lower tiers.
        const slots = bannerSlots();
        const mains = sponsorData.filter(s => s.sponsorshipType === 'MainSponsor');
        const rest = sponsorData.filter(s => s.sponsorshipType !== 'MainSponsor');
        bannerPages = chunkArr(mains, slots).concat(chunkArr(rest, slots));
        renderBannerPage(0);

        if (bannerPages.length <= 1) return;   // one set, nothing to rotate

        bannerTid = setInterval(() => {
            track.classList.add('is-fading');   // 1) fade current set out as a block
            setTimeout(() => {
                bannerIdx = (bannerIdx + 1) % bannerPages.length;
                track.style.transition = 'none'; // 2) snap back without animating…
                track.classList.remove('is-fading');
                renderBannerPage(bannerIdx);     // …so the per-name stagger leads the eye in
                void track.offsetWidth;          // commit the reflow
                track.style.transition = '';     // restore for next exit
            }, BANNER_FADE_MS);
        }, BANNER_ROTATE_MS);
    }

    async function load() {
        try {
            const res = await fetch('/api/sponsors', { cache: 'no-store' });
            if (!res.ok) return;
            const data = await res.json();
            const now = Date.now();
            sponsorData = [];
            for (const s of data) {
                if (!s.name) continue;
                const exp = s.expirationDate ? new Date(s.expirationDate).getTime() : Infinity;
                if (exp > now) sponsorData.push({ ...s, _exp: exp });
            }
            sponsorData.sort((a, b) => {
                const ta = TIER_ORDER.indexOf(a.sponsorshipType);
                const tb = TIER_ORDER.indexOf(b.sponsorshipType);
                const tierDiff = (ta < 0 ? 99 : ta) - (tb < 0 ? 99 : tb);
                return tierDiff !== 0 ? tierDiff : b._exp - a._exp;
            });
            startBanner();
        } catch (e) {
            console.warn('[SponsorBar] Could not load sponsors:', e);
        }
    }

    // Re-chunk to the new slot count if the viewport crosses the mobile breakpoint.
    let lastSlots = bannerSlots();
    window.addEventListener('resize', () => {
        const s = bannerSlots();
        if (s !== lastSlots) { lastSlots = s; startBanner(); }
    });

    load();
})();
