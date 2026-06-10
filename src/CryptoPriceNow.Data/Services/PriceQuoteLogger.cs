using System.Threading.Channels;
using CryptoPriceNow.Data.Entities;
using CryptoPriceNow.Data.Interfaces;
using CryptoPriceNow.Data.Models;
using CryptoPriceNow.Data.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CryptoPriceNow.Data.Services;

/// <summary>
/// Single-consumer background writer. PriceService enqueues snapshots
/// (non-blocking); this service drains the channel and:
///   1. Self-registers any exchange key it hasn't seen (new integrations
///      appear in the Exchanges table automatically and log from then on).
///   2. Inserts PriceQuote rows (buy/sell/rate type/timestamp), deduped by
///      the exchange's own quote timestamp so cached repeats aren't re-logged.
///   3. Prunes old rows per RetentionDays.
/// All failures are logged and swallowed — the site never goes down because
/// the database does.
/// </summary>
public sealed class PriceQuoteLogger : BackgroundService, IPriceQuoteSink
{
    private readonly IDbContextFactory<PriceDbContext> _dbFactory;
    private readonly ILogger<PriceQuoteLogger> _log;
    private readonly QuoteLoggingOptions _opt;

    private readonly Channel<QuoteSnapshot> _channel =
        Channel.CreateBounded<QuoteSnapshot>(new BoundedChannelOptions(128)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });

    // exchangeKey -> cached registry row (single consumer, no locking needed)
    private readonly Dictionary<string, CachedExchange> _exchanges = new(StringComparer.OrdinalIgnoreCase);

    // "exchangeKey|pair" -> last quote timestamp inserted (dedupe)
    private readonly Dictionary<string, DateTimeOffset> _lastQuoteTs = new(StringComparer.OrdinalIgnoreCase);

    // pair -> last snapshot capture time logged (throttle)
    private readonly Dictionary<string, DateTimeOffset> _lastLogged = new(StringComparer.OrdinalIgnoreCase);

    private DateTimeOffset _lastRetentionRun = DateTimeOffset.MinValue;

    private sealed class CachedExchange
    {
        public int Id;
        public string SiteName = string.Empty;
        public string? SiteUrl;
        public string? PrivacyLevel;
        public string RateType = "float";
        public DateTimeOffset LastTouchUtc;
    }

    public PriceQuoteLogger(
        IDbContextFactory<PriceDbContext> dbFactory,
        IOptions<QuoteLoggingOptions> options,
        ILogger<PriceQuoteLogger> log)
    {
        _dbFactory = dbFactory;
        _log = log;
        _opt = options.Value;
    }

    // ── IPriceQuoteSink ───────────────────────────────────────────────────────
    public ValueTask EnqueueAsync(QuoteSnapshot snapshot, CancellationToken ct = default)
    {
        if (_opt.Enabled)
            _channel.Writer.TryWrite(snapshot); // DropOldest — never blocks the hot path
        return ValueTask.CompletedTask;
    }

    // ── Consumer loop ─────────────────────────────────────────────────────────
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!_opt.Enabled)
        {
            _log.LogInformation("[QuoteLogger] Disabled via QuoteLogging:Enabled=false");
            return;
        }

        _log.LogInformation(
            "[QuoteLogger] Started — interval={Interval}s dedupe={Dedupe} retention={Retention}d",
            _opt.LogIntervalSeconds, _opt.DedupeByQuoteTimestamp, _opt.RetentionDays);

        try
        {
            await foreach (var snapshot in _channel.Reader.ReadAllAsync(ct))
            {
                try { await ProcessAsync(snapshot, ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "[QuoteLogger] Failed to log snapshot for {Pair}", snapshot.Pair);
                }

                try { await RunRetentionIfDueAsync(ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "[QuoteLogger] Retention pruning failed");
                }
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
    }

    private async Task ProcessAsync(QuoteSnapshot snapshot, CancellationToken ct)
    {
        // Throttle: at most one logged snapshot per pair per LogIntervalSeconds
        var minGap = TimeSpan.FromSeconds(Math.Clamp(_opt.LogIntervalSeconds, 5, 3600));
        if (_lastLogged.TryGetValue(snapshot.Pair, out var last) &&
            snapshot.CapturedUtc - last < minGap)
            return;
        _lastLogged[snapshot.Pair] = snapshot.CapturedUtc;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var quotes = new List<PriceQuote>();
        var now = snapshot.CapturedUtc.ToUniversalTime();

        foreach (var row in snapshot.Rows)
        {
            if (string.IsNullOrWhiteSpace(row.ExchangeKey)) continue;

            // 1) Ensure the exchange exists in the registry (self-registration)
            var ex = await EnsureExchangeAsync(db, row, now, ct);

            // 2) Skip rows with no price at all (exchange errored / pair unsupported)
            if (row.Buy is null && row.Sell is null) continue;

            var ts = (row.QuoteTsUtc ?? snapshot.CapturedUtc).ToUniversalTime();

            // 3) Dedupe: same exchange quote timestamp = same cached quote, skip
            var dedupeKey = $"{row.ExchangeKey}|{snapshot.Pair}";
            if (_opt.DedupeByQuoteTimestamp &&
                _lastQuoteTs.TryGetValue(dedupeKey, out var prevTs) && prevTs == ts)
                continue;
            _lastQuoteTs[dedupeKey] = ts;

            quotes.Add(new PriceQuote
            {
                ExchangeId = ex.Id,
                Pair = snapshot.Pair,
                Buy = row.Buy,
                Sell = row.Sell,
                RateType = row.RateType,
                TimestampUtc = ts
            });
        }

        if (quotes.Count > 0)
        {
            db.PriceQuotes.AddRange(quotes);
            await db.SaveChangesAsync(ct);
        }
        else if (db.ChangeTracker.HasChanges())
        {
            await db.SaveChangesAsync(ct); // metadata-only updates
        }
    }

    private async Task<CachedExchange> EnsureExchangeAsync(
        PriceDbContext db, QuoteRowDto row, DateTimeOffset now, CancellationToken ct)
    {
        var privacy = row.PrivacyLevel?.ToString();
        var metaTtl = TimeSpan.FromMinutes(Math.Clamp(_opt.MetadataRefreshMinutes, 1, 24 * 60));

        // Fast path: cached, metadata unchanged, touched recently
        if (_exchanges.TryGetValue(row.ExchangeKey, out var cached) &&
            now - cached.LastTouchUtc < metaTtl &&
            cached.SiteName == row.SiteName &&
            cached.SiteUrl == row.SiteUrl &&
            cached.PrivacyLevel == privacy &&
            cached.RateType == row.RateType)
        {
            return cached;
        }

        var entity = await db.Exchanges
            .FirstOrDefaultAsync(e => e.ExchangeKey == row.ExchangeKey, ct);

        if (entity is null)
        {
            entity = new Exchange
            {
                ExchangeKey = row.ExchangeKey,
                SiteName = string.IsNullOrWhiteSpace(row.SiteName) ? row.ExchangeKey : row.SiteName,
                SiteUrl = row.SiteUrl,
                PrivacyLevel = privacy,
                RateType = row.RateType,
                FirstSeenUtc = now,
                LastSeenUtc = now,
                IsActive = true
            };
            db.Exchanges.Add(entity);
            await db.SaveChangesAsync(ct); // need the Id immediately for quote FKs
            _log.LogInformation("[QuoteLogger] Registered new exchange '{Key}' (id={Id})",
                entity.ExchangeKey, entity.Id);
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(row.SiteName)) entity.SiteName = row.SiteName;
            entity.SiteUrl = row.SiteUrl;
            entity.PrivacyLevel = privacy;
            entity.RateType = row.RateType;
            entity.LastSeenUtc = now;
            entity.IsActive = true;
            // Saved by caller's SaveChangesAsync
        }

        cached = new CachedExchange
        {
            Id = entity.Id,
            SiteName = entity.SiteName,
            SiteUrl = entity.SiteUrl,
            PrivacyLevel = entity.PrivacyLevel,
            RateType = entity.RateType,
            LastTouchUtc = now
        };
        _exchanges[row.ExchangeKey] = cached;
        return cached;
    }

    private async Task RunRetentionIfDueAsync(CancellationToken ct)
    {
        if (_opt.RetentionDays <= 0) return;

        var now = DateTimeOffset.UtcNow;
        if (now - _lastRetentionRun < TimeSpan.FromHours(6)) return;
        _lastRetentionRun = now;

        var cutoff = now.AddDays(-_opt.RetentionDays);

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var deleted = await db.PriceQuotes
            .Where(q => q.TimestampUtc < cutoff)
            .ExecuteDeleteAsync(ct);

        if (deleted > 0)
            _log.LogInformation("[QuoteLogger] Retention: deleted {Count} quotes older than {Cutoff:u}",
                deleted, cutoff);
    }
}
