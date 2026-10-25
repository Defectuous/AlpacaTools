# Alpaca Dashboard

A Windows desktop app (WPF, .NET 10) for managing one or more Alpaca accounts:
account summary, equity history chart, positions with allocation and live prices,
per-symbol price charts, recent activity and open orders, a screener, and buy/sell
windows (orders are placed only after you confirm). Each account's full history is
kept in a local ledger.

## Free and paid market data plans

Account data (equity, positions, orders, activity, portfolio history) comes from the
Trading API and works the same on every plan. Live prices and price charts use the
Market Data API, where the app adapts to the account's plan:

| | Basic (free) | Algo Trader Plus (paid) |
|---|---|---|
| Live stock prices | IEX real-time, largest 30 positions (the rest update on the 60s refresh) | SIP real-time (all US exchanges), no symbol limit |
| Stock price charts | SIP bars, ending 15 minutes ago | SIP bars, up to now |
| Crypto prices and charts | Real-time | Real-time |

The plan is **auto-detected** per account: the app requests SIP bars ending *now*,
which Alpaca only allows with a subscription (403 otherwise). You can also set it
manually under **Edit**. If SIP is set but the stream reports "insufficient
subscription", the app falls back to Basic on its own.

Alpaca allows about one connection per stream endpoint per Alpaca user, so only the
account on screen streams. If another app already holds the connection, the status
line says so and the app retries every 60 seconds.

## Ledger

Every account's full activity history (fills, dividends, interest, fees, deposits,
withdrawals, transfers, corporate actions) is copied into a local SQLite database at
`%APPDATA%\AlpacaTools\ledger.db`. Later features (performance, cost-basis lots,
income) are computed from it instead of from Alpaca's responses. Entries use the app's
own model (`Models/LedgerEntry.cs`) and keep Alpaca's raw JSON, so another broker can
feed the same ledger later.

- **First run**: history downloads newest first, one page of 100 every 3 seconds (15s
  with bot priority), in the background. It resumes where it left off after a restart.
- **After that**: new activity comes from the activity page each refresh already
  fetches, so keeping current costs no extra requests. Accounts not on screen sync
  every 30 minutes.
- **Check**: once history is complete, the header compares shares from the ledger's
  trades with Alpaca's stock positions ("matches positions", or amber with the
  differences in the tooltip). Crypto, and stocks with splits, mergers, transfers or
  option events, aren't compared.
- If an account's keys start pointing at a different Alpaca account (new keys, paper
  reset), its ledger is cleared and downloaded again. Removing an account removes its
  ledger.

## Price alerts

The **Alerts** tab watches any stock or crypto pair. Pick a symbol (or press **Alert**
on a position row), choose a condition, and add it:

- price at or above / at or below a value
- up / down by at least a percentage today (against the previous close)
- a position's unrealized P/L: profit or loss of at least $X, or gain or loss of at
  least X% of cost basis. These follow the position in the account on screen when the
  alert is made, even after switching accounts (an account not on screen costs one
  positions request per check while it has such an alert). They don't fire while the
  symbol isn't held

The list also shows each symbol's P/L where you hold it.

Each alert fires once, as a Windows notification from the tray icon, then shows
"Fired" until you **Re-arm** it. Clicking the notification opens the Alerts tab. Checks
keep running while the window is minimized, using the keys of the account on screen.
On the Basic plan stock prices are IEX real-time. Alerts are saved to
`%APPDATA%\AlpacaTools\alerts.json`.

## Going easy on the API

Alpaca's limits are per API key and shared with anything else using that key, such as
trading bots or screeners. The dashboard keeps its share small:

| Data | How often |
|---|---|
| Positions, open orders, activity | When an order changes (pushed by Alpaca's trade-updates stream), plus a safety refresh every 5 min. Every 60s if the stream is down. |
| Account totals | With the above. Equity moves with live prices in between. |
| Portfolio history | Every 5 min on 1D, 30 min on longer ranges |
| Accounts not on screen | Every 5 min |
| Crypto fee tier (30-day fills) | Every 60 min |
| Ledger history download | Once per account, one page every 3s (15s with bot priority); afterwards free |
| Price-chart windows | Cached per range for 5 min; Refresh forces a reload |
| Symbol search list (all stocks + crypto) | Two requests the first time you search, cached 6 hours per account; typing costs nothing |
| Price alerts | Every 30s, one request for all stock alerts and one for crypto; only while an alert is watching. Streamed positions are checked instantly |
| While minimized | Nothing but price alerts; the rest catches up when restored |

Every request also goes through a per-key budget (`Services/ApiBudget.cs`). The
dashboard caps itself at 40 trading and 60 market-data requests a minute. It reads
Alpaca's `X-RateLimit-*` headers and pauses until the reset when less than 25% of the
key's budget is left, or after a 429. The header shows the dashboard's own request
rate ("API 4/min"); hover over it for the key's remaining budget (trading and market
data) and the current plan's limits: 200 market-data requests/min, 30 streamed stocks
and 15-minute-delayed charts on Basic. If the plan is set to Algo Trader Plus but
Alpaca refuses the SIP feed (e.g. the subscription was cancelled), the dashboard keeps
to the Basic limits until the account is edited.

### Sharing a key with a bot

Tick **Shared with a bot: give it priority** under **Edit** for any account whose
keys a bot also uses. For that account the dashboard:

- opens **no websockets**, so the bot keeps Alpaca's stream connections;
- polls prices instead, in one batched snapshot request per asset type every 15s
  (market-data API, not the trading budget the bot needs);
- caps itself at 10 trading and 20 market-data requests a minute, refreshes account
  data every 2 minutes, and backs off once half the key's budget is used.

## Run

```
dotnet run --project src/AlpacaDashboard
```

Click **+ Add account**, paste an API key ID + secret from the Alpaca dashboard,
tick **Paper** for paper accounts, then **Test connection** and **Save**. Add as many
accounts as you like; the sidebar shows each one's equity and day change, and the
selected account refreshes fully every 60 seconds.

## Publish a single .exe

```
dotnet publish src/AlpacaDashboard -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

Output: `src/AlpacaDashboard/bin/Release/net10.0-windows/win-x64/publish/AlpacaDashboard.exe`.

## Where things live

| Path | What |
|---|---|
| `Services/AlpacaClient.cs` | HTTP client for the Trading API (`/v2/account`, `/v2/positions`, `/v2/account/portfolio/history`, `/v2/account/activities`, `/v2/orders`) |
| `Services/MarketDataPlan.cs` | Plan enum and the feeds/limits each plan uses |
| `Services/MarketDataClient.cs` | Market Data REST: plan detection, stock + crypto bars and snapshots |
| `Services/MarketDataStream.cs` | Websocket price stream (IEX, SIP or crypto) with reconnect/backoff |
| `Services/TradeUpdatesStream.cs` | Order-event stream that triggers refreshes instead of polling |
| `Services/ApiBudget.cs` | Per-key request cap, rate-limit header tracking, 429 back-off |
| `ViewModels/AlertsViewModel.cs` | Price alerts: checking, firing, the Alerts tab |
| `Services/AlertNotifier.cs` | Tray icon and Windows notifications for alerts |
| `Services/FeeEstimator.cs` | Estimated fees to close a position (rates from Alpaca's fee schedule) |
| `ViewModels/SymbolChartViewModel.cs` | Candlestick + volume chart window |
| `ViewModels/SymbolSearchViewModel.cs` + `Views/SymbolSearchBox.xaml` | Type-ahead symbol search in the tab strip (Ctrl+F) |
| `Models/LedgerEntry.cs` | Broker-neutral ledger entry and sync state |
| `Services/LedgerStore.cs` | SQLite ledger: entries, sync state, summary |
| `Services/AlpacaLedgerSync.cs` | Alpaca activities → ledger: backfill and incremental sync, type mapping |
| `Services/LedgerReconciler.cs` | Ledger shares vs. broker positions check |
| `Services/AccountStore.cs` | Saves profiles to `%APPDATA%\AlpacaTools\accounts.json`; keys encrypted with Windows DPAPI (current user) |
| `ViewModels/AccountViewModel.cs` | Per-account data loading and chart series |
| `ViewModels/Palette.cs` + `App.xaml` | Colors (keep the two in sync) |
| `MainWindow.xaml` | Dashboard layout |

## Testing without real keys

Point every account at mock servers with these environment variables:

| Variable | Replaces | Example |
|---|---|---|
| `ALPACA_BASE_URL_OVERRIDE` | `https://api.alpaca.markets/`, `https://paper-api.alpaca.markets/` | `http://127.0.0.1:8765/` |
| `ALPACA_DATA_URL_OVERRIDE` | `https://data.alpaca.markets/` | `http://127.0.0.1:8765/` |
| `ALPACA_STREAM_URL_OVERRIDE` | `wss://stream.data.alpaca.markets/` | `ws://127.0.0.1:8766/` |
| `ALPACA_TRADE_STREAM_URL_OVERRIDE` | `wss://{paper-}api.alpaca.markets/stream` | `ws://127.0.0.1:8777/stream` |
| `ALPACA_LEDGER_PATH_OVERRIDE` | `%APPDATA%\AlpacaTools\ledger.db` | `C:\temp\ledger-test.db` |

Unhandled errors are logged to `%APPDATA%\AlpacaTools\error.log`.

## License

Free for personal, educational, non-profit and internal use, with credit to the
original author. Using it in a product or service that's sold or offered by
subscription requires a 1% royalty on that product's revenue. See [LICENSE](LICENSE)
for the full terms.
