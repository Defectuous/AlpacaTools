using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AlpacaDashboard.ViewModels;

/// <summary>A tradable stock or crypto pair from Alpaca's asset list.</summary>
public sealed record SymbolAsset(string Symbol, string Name, bool IsCrypto)
{
    /// <summary>"BTC" for "BTC/USD"; the symbol itself for stocks.</summary>
    public string Base { get; } = Symbol.Split('/')[0];
}

/// <summary>One row in the search dropdown.</summary>
public sealed record SymbolSuggestion(SymbolAsset Asset, bool IsHeld)
{
    public string Symbol => Asset.Symbol;
    public string Name => Asset.Name;
    public bool IsCrypto => Asset.IsCrypto;
}

/// <summary>
/// Search box over every tradable stock and crypto pair, matched by symbol or company name. Suggestions narrow
/// with each character typed, entirely client-side: the asset list costs two requests per account, cached 6 hours.
/// </summary>
public sealed partial class SymbolSearchViewModel : ObservableObject
{
    private const int MaxSuggestions = 10;
    private static readonly TimeSpan AssetsMaxAge = TimeSpan.FromHours(6);

    private readonly Func<AccountViewModel?> _account;
    private readonly Action<SymbolAsset> _pick;
    private readonly Dictionary<Guid, (DateTime Fetched, Task<List<SymbolAsset>> Load)> _assets = [];

    /// <param name="account">The account on screen; its keys load the asset list.</param>
    /// <param name="pick">Shows the chosen symbol.</param>
    public SymbolSearchViewModel(Func<AccountViewModel?> account, Action<SymbolAsset> pick)
    {
        _account = account;
        _pick = pick;
    }

    [ObservableProperty] private string _query = "";
    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private SymbolSuggestion? _selected;

    /// <summary>Shown in the dropdown instead of, or above, suggestions: loading, no matches, or an error.</summary>
    [ObservableProperty] private string? _message;

    public ObservableCollection<SymbolSuggestion> Suggestions { get; } = [];

    partial void OnQueryChanged(string value) => _ = UpdateAsync();

    /// <summary>Starts loading the asset list when the box gets focus, so the first keystroke has results sooner.</summary>
    public void Prefetch()
    {
        if (_account() is { } account) _ = LoadAssetsAsync(account);
    }

    /// <summary>Reopens the dropdown for the current text (e.g. when the box regains focus).</summary>
    public void Reopen()
    {
        if (Query.Trim().Length > 0) IsOpen = true;
    }

    public void MoveSelection(int delta)
    {
        if (!IsOpen) { Reopen(); return; }
        if (Suggestions.Count == 0) return;
        var index = Selected is null ? (delta > 0 ? 0 : Suggestions.Count - 1) : Suggestions.IndexOf(Selected) + delta;
        Selected = Suggestions[Math.Clamp(index, 0, Suggestions.Count - 1)];
    }

    /// <summary>Picks the highlighted suggestion, or the best match when none is highlighted.</summary>
    public void PickSelected()
    {
        if ((Selected ?? Suggestions.FirstOrDefault()) is { } suggestion) Pick(suggestion);
    }

    public void Pick(SymbolSuggestion suggestion)
    {
        Query = ""; // clears the box and closes the dropdown
        _pick(suggestion.Asset);
    }

    public void Close() => IsOpen = false;

    private async Task UpdateAsync()
    {
        var query = Query.Trim();
        if (query.Length == 0 || _account() is not { } account)
        {
            Suggestions.Clear();
            Message = null;
            IsOpen = false;
            return;
        }
        IsOpen = true;

        List<SymbolAsset> assets;
        try
        {
            var load = LoadAssetsAsync(account);
            if (!load.IsCompleted)
            {
                Suggestions.Clear();
                Message = "Loading symbols…";
            }
            assets = await load;
        }
        catch (Exception ex)
        {
            if (query == Query.Trim()) Message = $"Couldn't load symbols: {ex.Message}";
            return;
        }
        if (query != Query.Trim() || _account() != account) return; // superseded by a newer keystroke

        var matches = assets
            .Select(a => (Asset: a, Rank: Rank(a, query)))
            .Where(m => m.Rank is not null)
            .OrderBy(m => m.Rank).ThenBy(m => m.Asset.Symbol.Length).ThenBy(m => m.Asset.Symbol, StringComparer.Ordinal)
            .Take(MaxSuggestions)
            .Select(m => new SymbolSuggestion(m.Asset, account.FindPosition(m.Asset.Symbol) is not null))
            .ToList();

        Suggestions.Clear();
        foreach (var match in matches) Suggestions.Add(match);
        Selected = Suggestions.FirstOrDefault();
        Message = matches.Count == 0 ? $"No symbols match \"{query}\"" : null;
    }

    /// <summary>Lower is better; null is no match. Symbols beat names, and prefixes beat matches mid-word.</summary>
    private static int? Rank(SymbolAsset asset, string query)
    {
        const StringComparison ic = StringComparison.OrdinalIgnoreCase;
        if (asset.Symbol.Equals(query, ic) || asset.Base.Equals(query, ic)) return 0;
        if (asset.Symbol.StartsWith(query, ic)) return 1;
        if (StartsAWord(asset.Name, query)) return 2;
        if (asset.Symbol.Contains(query, ic)) return 3;
        if (asset.Name.Contains(query, ic)) return 4;
        return null;
    }

    /// <summary>The query starts the name or any word in it ("micro" matches "Advanced Micro Devices").</summary>
    private static bool StartsAWord(string name, string query)
    {
        for (var i = name.IndexOf(query, StringComparison.OrdinalIgnoreCase); i >= 0;
             i = name.IndexOf(query, i + 1, StringComparison.OrdinalIgnoreCase))
        {
            if (i == 0 || !char.IsLetterOrDigit(name[i - 1])) return true;
        }
        return false;
    }

    private Task<List<SymbolAsset>> LoadAssetsAsync(AccountViewModel account)
    {
        var id = account.Profile.Id;
        if (_assets.TryGetValue(id, out var cached) && !cached.Load.IsFaulted && DateTime.Now - cached.Fetched < AssetsMaxAge)
            return cached.Load;

        var load = FetchAssetsAsync(account);
        _assets[id] = (DateTime.Now, load);
        return load;
    }

    private static async Task<List<SymbolAsset>> FetchAssetsAsync(AccountViewModel account)
    {
        var stocks = account.Trading.GetAssetsAsync("us_equity");
        var crypto = account.Trading.GetAssetsAsync("crypto");
        await Task.WhenAll(stocks, crypto);
        return
        [
            .. stocks.Result.Concat(crypto.Result)
                .Where(a => a.Tradable)
                .DistinctBy(a => a.Symbol)
                .Select(a => new SymbolAsset(a.Symbol, a.Name ?? "", a.AssetClass == "crypto")),
        ];
    }
}
