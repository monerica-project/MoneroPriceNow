namespace CryptoPriceNow.Data.Models;

/// <summary>A single fee sample handed to the sink for logging.</summary>
public sealed record NetworkFeeSnapshot(
    string Network,
    decimal Native,
    string NativeUnit,
    decimal? UsdPerTx,
    DateTimeOffset CapturedUtc);
