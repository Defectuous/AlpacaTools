namespace AlpacaDashboard.Services;

/// <summary>Estimated cost of closing a position, with a human-readable breakdown.</summary>
public sealed record FeeEstimate(decimal Total, string Breakdown);

/// <summary>Crypto fee tier, chosen by the account's trailing 30-day crypto volume.</summary>
public sealed record CryptoFeeTier(decimal FromVolume, decimal MakerRate, decimal TakerRate)
{
    // Alpaca crypto spot fees (https://docs.alpaca.markets/docs/crypto-fees). Equities volume does not count.
    public static readonly CryptoFeeTier[] All =
    [
        new(0m, 0.0015m, 0.0025m),
        new(100_000m, 0.0012m, 0.0022m),
        new(500_000m, 0.0010m, 0.0020m),
        new(1_000_000m, 0.0008m, 0.0018m),
        new(10_000_000m, 0.0005m, 0.0015m),
        new(25_000_000m, 0.0002m, 0.0013m),
        new(50_000_000m, 0.0002m, 0.0012m),
        new(100_000_000m, 0.0000m, 0.0010m),
    ];

    public static CryptoFeeTier ForVolume(decimal thirtyDayVolume) =>
        All.Last(t => thirtyDayVolume >= t.FromVolume);
}

/// <summary>
/// Estimates the fees Alpaca charges to close a position at market. Rates come from the Alpaca
/// Securities brokerage fee schedule effective Oct 1, 2026 (https://alpaca.markets/disclosures);
/// update the constants below when it changes.
/// </summary>
public static class FeeEstimator
{
    private const decimal SecFeeRate = 0.0000206m;   // × sale value, sells only (stocks and options)
    private const decimal FinraTafPerShare = 0m;      // sells only; currently $0
    private const decimal CatPerShare = 0.000003m;    // buys and sells, per executed equivalent share
    private const decimal OptionsOrfPerContract = 0.015m;
    private const decimal OptionsOccPerContract = 0.025m;
    private const int OptionsMultiplier = 100;

    public static FeeEstimate Estimate(string assetClass, bool isShort, decimal qty, decimal marketValue,
        CryptoFeeTier cryptoTier)
    {
        var units = Math.Abs(qty);
        var value = Math.Abs(marketValue);
        // Closing a long is a sell; closing a short is a buy (no SEC/TAF on buys).
        var isSell = !isShort;

        switch (assetClass)
        {
            case "crypto":
            {
                var fee = RoundUpToCent(value * cryptoTier.TakerRate);
                return new(fee,
                    $"Crypto taker fee {cryptoTier.TakerRate:P2} on {value:C2} (market order)\n" +
                    $"A limit order that rests on the book pays maker {cryptoTier.MakerRate:P2} instead\n" +
                    $"Tier based on 30-day crypto volume ≥ {cryptoTier.FromVolume:C0}");
            }
            case "us_option":
            {
                var sec = isSell ? RoundUpToCent(value * SecFeeRate) : 0m;
                var taf = isSell ? RoundUpToCent(units * FinraTafPerShare) : 0m;
                var cat = RoundUpToCent(units * OptionsMultiplier * CatPerShare);
                var orf = RoundUpToCent(units * OptionsOrfPerContract);
                var occ = RoundUpToCent(units * OptionsOccPerContract);
                return new(sec + taf + cat + orf + occ,
                    Lines(("SEC fee", sec), ("FINRA TAF", taf), ("CAT fee", cat), ("Options regulatory fee", orf), ("OCC clearing fee", occ)) +
                    "\nIndex options also pay $0.50/contract plus exchange fees (not included)");
            }
            case "us_equity":
            {
                var sec = isSell ? RoundUpToCent(value * SecFeeRate) : 0m;
                var taf = isSell ? RoundUpToCent(units * FinraTafPerShare) : 0m;
                var cat = RoundUpToCent(units * CatPerShare);
                return new(sec + taf + cat,
                    "No commission\n" + Lines(("SEC fee", sec), ("FINRA TAF", taf), ("CAT fee", cat)));
            }
            default:
                return new(0m, "No fee estimate for this asset class");
        }
    }

    // Alpaca aggregates each fee type per account per day, then rounds up to the cent. Rounding each
    // position up on its own can overstate slightly, never understate.
    private static decimal RoundUpToCent(decimal amount) => amount <= 0 ? 0 : Math.Ceiling(amount * 100m) / 100m;

    private static string Lines(params (string Name, decimal Amount)[] fees) =>
        string.Join("\n", fees.Select(f => $"{f.Name}: {f.Amount:C2}"));
}
