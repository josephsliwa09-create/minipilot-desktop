using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace Mintpilot;

public sealed class Engine
{
    public Vault Vault { get; }
    public AppState State => Vault.State;
    public string Address => Sol.Address(State.Seed);
    public long? BalanceLamports { get; private set; }
    public Dictionary<string, decimal> Holdings { get; private set; } = [];
    public Dictionary<string, Market> Markets { get; private set; } = [];
    public string MarketStatus { get; private set; } = "Waiting for live data";
    public string Status { get; private set; } = "Ready";
    public bool Running { get; private set; }
    public bool Busy => _gate.CurrentCount == 0;
    public DateTimeOffset? NextLaunch { get; private set; }
    public event Action? Changed;
    private readonly Network _net;
    private readonly LaunchServices _services;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _aiCancellation;
    private bool _storageFault;
    private DateTimeOffset _lastMarkets;
    private Rpc Rpc => new(_net, State.Settings.RpcUrl);
    public long Reserved => State.Operations.Where(o => o.Pending).Sum(o => o.ReservationLamports > 0 ? o.ReservationLamports : o.LimitLamports);
    public long Available => Math.Max(0, (BalanceLamports ?? 0) - Reserved);

    public Engine(Vault vault, HttpClient? http = null)
    {
        Vault = vault; _net = new Network(http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(90) }); _services = new LaunchServices(_net);
        bool repaired = false;
        foreach (var op in State.Operations)
        {
            if (op.Status == "Preparing") { op.Status = "Failed"; op.Error = "Preparation was interrupted before submission."; repaired = true; }
            if (op.TelegramStatus == "Posting") { op.TelegramStatus = "Unknown"; repaired = true; }
            if (op.XStatus == "Posting") { op.XStatus = "Unknown"; repaired = true; }
        }
        if (repaired) Persist();
    }
    private void Persist()
    {
        try { Vault.Save(); _storageFault = false; }
        catch
        {
            _storageFault = true; Pause();
            throw new InvalidOperationException("Unable to save the local transaction journal. Restore disk access and restart Mintpilot before sending funds.");
        }
    }
    private void Writable()
    {
        if (_storageFault) throw new InvalidOperationException("Local storage needs attention. Restart after restoring disk access.");
    }
    public void Start()
    {
        Writable(); State.Settings.Validate(launching: true, agent: true);
        if (State.Operations.Any(o => o.Pending)) throw new InvalidOperationException("Wait for the pending transaction to settle before starting the agent.");
        Running = true; NextLaunch = DateTimeOffset.UtcNow; Status = "Agent started"; Changed?.Invoke();
    }
    public void Pause()
    {
        Running = false; NextLaunch = null; _aiCancellation?.Cancel(); Changed?.Invoke();
    }
    public async Task SaveSettings(Settings settings)
    {
        settings.Validate(); await _gate.WaitAsync();
        try { Writable();
            if (!settings.AutoExitEnabled && State.Settings.AutoExitEnabled)
                foreach (var launch in State.Operations.Where(o => o.Kind == "Create"))
                { launch.ExitPeakUsd = 0; launch.ExitTriggered = false; }
            State.Settings = settings; Persist(); Status = "Settings saved"; }
        finally { _gate.Release(); Changed?.Invoke(); }
    }
    public async Task Poll()
    {
        if (!await _gate.WaitAsync(0)) return;
        try
        {
            State.Settings.Validate();
            var rpc = Rpc; await rpc.Mainnet();
            await Reconcile(rpc);
            BalanceLamports = await rpc.Balance(Address);
            if (State.BalanceHistory.Count == 0 || DateTimeOffset.UtcNow - State.BalanceHistory[^1].Time > TimeSpan.FromMinutes(1))
            {
                State.BalanceHistory.Add(new BalancePoint(DateTimeOffset.UtcNow, BalanceLamports.Value));
                if (State.BalanceHistory.Count > 3000) State.BalanceHistory.RemoveRange(0, State.BalanceHistory.Count - 3000);
                Persist();
            }
            if (DateTimeOffset.UtcNow - _lastMarkets > TimeSpan.FromMinutes(1))
            {
                Holdings = await _services.Holdings(rpc, Address); _lastMarkets = DateTimeOffset.UtcNow;
                Dictionary<string, Market>? fresh = null;
                try
                {
                    Markets = fresh = await _services.Markets(State.Operations.Where(o => o.Kind == "Create" && o.Status == "Confirmed").Select(o => o.Mint));
                    MarketStatus = "DexScreener · " + DateTime.Now.ToShortTimeString();
                }
                catch { MarketStatus = "Market feed unavailable · last successful values retained"; }
                if (State.Settings.AutoExitEnabled && await MonitorExits(rpc, fresh)) return;
            }
            await PublishQueued();
            if (Running && NextLaunch <= DateTimeOffset.UtcNow && !State.Operations.Any(o => o.Pending))
            {
                CheckBudget();
                _aiCancellation = new CancellationTokenSource();
                Status = "Generating a coin concept"; Changed?.Invoke();
                var idea = await _services.Idea(State.Settings, _aiCancellation.Token);
                _aiCancellation.Dispose(); _aiCancellation = null;
                if (!Running) return;
                await CreateCore(idea, Artwork.Render(idea));
                if (Running) NextLaunch = DateTimeOffset.UtcNow.AddMinutes(State.Settings.IntervalMinutes);
            }
        }
        catch (OperationCanceledException) { Status = "Agent paused"; }
        catch (Exception ex) { Pause(); Status = ex.Message; }
        finally
        {
            _aiCancellation?.Dispose(); _aiCancellation = null;
            _gate.Release(); Changed?.Invoke();
        }
    }
    private void CheckBudget()
    {
        Writable(); State.Settings.Validate(launching: true);
        var settings = State.Settings;
        if (State.Operations.Any(o => o.Pending)) throw new InvalidOperationException("A pending transaction must settle before another launch.");
        var today = State.Operations.Where(o => o.Kind == "Create" && o.CreatedUtc.UtcDateTime.Date == DateTime.UtcNow.Date).ToList();
        long committed = today.Sum(o => o.Pending ? o.LimitLamports : Math.Max(0, o.DebitLamports ?? 0));
        if (today.Count(o => o.Status == "Confirmed" || o.Pending) >= settings.MaxDailyLaunches || committed + Sol.Lamports(settings.MaxLaunchSol) > Sol.Lamports(settings.DailyCapSol))
            throw new InvalidOperationException("Today's UTC launch limit or SOL budget has been reached.");
        if (BalanceLamports is null || BalanceLamports < Sol.Lamports(settings.ReserveSol + settings.MaxLaunchSol))
            throw new InvalidOperationException("Fund the wallet to cover the launch limit and the configured SOL reserve.");
    }
    public async Task Launch(CoinIdea idea, byte[] image)
    {
        await _gate.WaitAsync();
        try
        {
            Writable(); var rpc = Rpc; await rpc.Mainnet(); await Reconcile(rpc);
            BalanceLamports = await rpc.Balance(Address); CheckBudget();
            await CreateCore(idea, image);
        }
        finally { _gate.Release(); Changed?.Invoke(); }
    }
    private async Task CreateCore(CoinIdea idea, byte[] image)
    {
        idea.Name = idea.Name.Trim(); idea.Symbol = idea.Symbol.Trim().ToUpperInvariant(); idea.Validate();
        if (image.Length is < 1 or > 5_000_000) throw new InvalidOperationException("Choose an image under 5 MB.");
        var s = State.Settings; var seed = RandomNumberGenerator.GetBytes(32);
        var op = new Operation
        {
            Kind = "Create", Name = idea.Name, Symbol = idea.Symbol, Mint = Sol.Address(seed),
            LimitLamports = Sol.Lamports(s.MaxLaunchSol), ReservationLamports = Sol.Lamports(s.MaxLaunchSol),
            Promotion = idea.Post + "\nAutomated creator · $" + idea.Symbol + "\nhttps://pump.fun/coin/" + Sol.Address(seed),
            TelegramStatus = s.TelegramEnabled ? "Queued" : "Disabled", XStatus = s.XEnabled ? "Queued" : "Disabled"
        };
        State.Operations.Add(op); Persist();
        try
        {
            Status = "Uploading artwork and metadata"; Changed?.Invoke();
            string imageUri = await _services.Upload(s, image, "token.png", "image/png");
            op.Uri = await _services.Upload(s, JsonSerializer.SerializeToUtf8Bytes(new
            { name = idea.Name, symbol = idea.Symbol, description = idea.Description, image = imageUri, showName = true, createdOn = "https://pump.fun" }), "metadata.json", "application/json");
            Persist();
            Status = "Preparing the pump.fun transaction"; Changed?.Invoke();
            var tx = await _services.Build(s, new
            {
                publicKey = Address, action = "create", tokenMetadata = new { name = idea.Name, symbol = idea.Symbol, uri = op.Uri },
                mint = op.Mint, denominatedInSol = "true", amount = s.BuySol, slippage = s.Slippage,
                priorityFee = s.PriorityFeeSol, pool = "pump"
            });
            var rpc = Rpc;
            tx.Audit(await tx.ResolveKeys(rpc), Address, op, idea, Sol.Lamports(decimal.Ceiling(s.BuySol * (1 + s.Slippage / 100m) * 1_000_000_000m) / 1_000_000_000m));
            op.Signature = tx.Sign(State.Seed, seed); op.Blockhash = tx.Blockhash; op.Raw = Convert.ToBase64String(tx.Raw);
            await rpc.Simulate(tx, Address, op.LimitLamports);
            await Submit(rpc, tx, op);
        }
        catch (Exception ex)
        {
            if (!op.Pending) { op.Status = "Failed"; op.Error = ex.Message; Persist(); }
            Pause(); throw;
        }
        finally { CryptographicOperations.ZeroMemory(seed); }
    }
    private async Task Submit(Rpc rpc, WireTransaction tx, Operation op)
    {
        op.Status = "Signed"; Persist();
        try
        {
            var result = await rpc.Call("sendTransaction", [Convert.ToBase64String(tx.Raw), new { encoding = "base64", skipPreflight = false, preflightCommitment = "confirmed", maxRetries = 3 }]);
            if (result.GetString() != op.Signature) throw new InvalidDataException("RPC returned an unexpected transaction signature.");
            op.Status = "Pending"; op.Error = ""; Persist();
            Status = "Transaction submitted · awaiting finalization";
            await Reconcile(rpc);
        }
        catch (Exception ex)
        {
            op.Status = "Pending"; op.Error = ex.Message; Persist();
            Pause(); Status = "Submission outcome is pending. Checking the saved signature.";
        }
    }
    private async Task Reconcile(Rpc rpc)
    {
        foreach (var op in State.Operations.Where(o => o.Pending).ToList())
        {
            var status = (await rpc.Call("getSignatureStatuses", [new[] { op.Signature }, new { searchTransactionHistory = true }])).GetProperty("value")[0];
            bool finalized = status.ValueKind != JsonValueKind.Null && status.TryGetProperty("confirmationStatus", out var confirmation) && confirmation.GetString() == "finalized";
            if (finalized)
            {
                var detail = await rpc.Call("getTransaction", [op.Signature, new { encoding = "json", commitment = "finalized", maxSupportedTransactionVersion = 0 }]);
                if (detail.ValueKind == JsonValueKind.Null) continue;
                var meta = detail.GetProperty("meta");
                if (meta.ValueKind == JsonValueKind.Null) continue;
                op.FeeLamports = meta.GetProperty("fee").GetInt64();
                op.DebitLamports = meta.GetProperty("preBalances")[0].GetInt64() - meta.GetProperty("postBalances")[0].GetInt64();
                bool failed = meta.GetProperty("err").ValueKind != JsonValueKind.Null;
                op.Status = failed ? "Failed" : "Confirmed";
                op.Error = failed ? "On-chain execution failed: " + meta.GetProperty("err").GetRawText() : "";
                op.Raw = "";
                Persist();
                Status = failed ? op.Error : op.Kind + " finalized";
                if (failed) Pause();
            }
            else if (status.ValueKind == JsonValueKind.Null && !(await rpc.Call("isBlockhashValid", [op.Blockhash, new { commitment = "finalized" }])).GetProperty("value").GetBoolean())
            {
                var detail = await rpc.Call("getTransaction", [op.Signature, new { commitment = "finalized", maxSupportedTransactionVersion = 0 }]);
                if (detail.ValueKind != JsonValueKind.Null) continue;
                var check = (await rpc.Call("getSignatureStatuses", [new[] { op.Signature }, new { searchTransactionHistory = true }])).GetProperty("value")[0];
                if (check.ValueKind != JsonValueKind.Null) continue;
                op.Status = "Expired"; op.Error = "The original transaction expired without a recorded on-chain result."; op.Raw = ""; Persist(); Pause();
            }
        }
    }
    public async Task Withdraw(string destination, decimal? requested)
    {
        Pause(); Sol.AddressBytes(destination); await _gate.WaitAsync();
        try
        {
            Writable(); var rpc = Rpc; await rpc.Mainnet(); await Reconcile(rpc);
            BalanceLamports = await rpc.Balance(Address);
            string hash = (await rpc.Call("getLatestBlockhash", [new { commitment = "confirmed" }])).GetProperty("value").GetProperty("blockhash").GetString()!;
            long fee = await rpc.Fee(Sol.Transfer(Address, destination, 1, hash));
            long amount = requested is decimal n ? Sol.Lamports(n) : Available - fee;
            if (amount <= 0 || amount > Available - fee) throw new InvalidOperationException("Withdrawal exceeds available SOL after the network fee and pending launch reservations.");
            var op = new Operation { Kind = "Withdraw", Name = "SOL withdrawal", Recipient = destination, LimitLamports = amount, ReservationLamports = checked(amount + fee) };
            var tx = Sol.Transfer(Address, destination, amount, hash);
            tx.Audit(tx.Keys, Address, op, null, 0);
            op.Signature = tx.Sign(State.Seed); op.Blockhash = hash; op.Raw = Convert.ToBase64String(tx.Raw);
            await rpc.Simulate(tx, Address, checked(amount + fee));
            State.Operations.Add(op); Persist(); await Submit(rpc, tx, op);
        }
        finally { _gate.Release(); Changed?.Invoke(); }
    }
    public async Task Sell(Operation launch)
    {
        Pause(); await _gate.WaitAsync();
        try
        {
            var rpc = Rpc; await rpc.Mainnet(); await SellCore(launch, rpc, false);
        }
        finally { _gate.Release(); Changed?.Invoke(); }
    }
    private async Task SellCore(Operation launch, Rpc rpc, bool automatic)
    {
            Writable();
            if (launch.Kind != "Create" || launch.Status != "Confirmed") throw new InvalidOperationException("Select a finalized launch.");
            if (State.Operations.Any(o => o.Pending)) throw new InvalidOperationException("Wait for the pending transaction to settle.");
            
            var settings = State.Settings;
            var op = new Operation { Kind = "Sell", AutomaticSale = automatic, Name = launch.Name, Symbol = launch.Symbol, Mint = launch.Mint, LimitLamports = Sol.Lamports(settings.MaxLaunchSol) };
            State.Operations.Add(op); Persist();
            try
            {
                var tx = await _services.Build(settings, new
            {
                publicKey = Address, action = "sell", mint = launch.Mint, denominatedInSol = "false", amount = "100%",
                slippage = settings.Slippage, priorityFee = settings.PriorityFeeSol, pool = "auto"
            });
            tx.Audit(await tx.ResolveKeys(rpc), Address, op, null, 0);
            op.Signature = tx.Sign(State.Seed); op.Blockhash = tx.Blockhash; op.Raw = Convert.ToBase64String(tx.Raw);
            await rpc.Simulate(tx, Address, op.LimitLamports);
            await Submit(rpc, tx, op);
            }
            catch (Exception ex)
            {
                if (!op.Pending) { op.Status = "Failed"; op.Error = ex.Message; Persist(); }
                throw;
            }
    }

    private async Task<bool> MonitorExits(Rpc rpc, Dictionary<string, Market>? fresh)
    {
        Writable();
        var launches = State.Operations.Where(o => o.Kind == "Create" && o.Status == "Confirmed").ToList();
        bool changed = false;
        Operation? next = null;
        foreach (var launch in launches)
        {
            if (Holdings.GetValueOrDefault(launch.Mint) <= 0)
            {
                if (launch.ExitPeakUsd != 0 || launch.ExitTriggered)
                { launch.ExitPeakUsd = 0; launch.ExitTriggered = false; changed = true; }
                continue;
            }
            if (fresh != null && fresh.TryGetValue(launch.Mint, out var market))
                changed |= launch.ObserveExitPrice(market.PriceUsd, State.Settings.ExitDropPercent);
            if (!launch.ExitTriggered || next != null) continue;
            var attempts = State.Operations.Where(o => o.AutomaticSale && o.Mint == launch.Mint).ToList();
            if (attempts.Count >= 3 || attempts.Any(o => DateTimeOffset.UtcNow - o.CreatedUtc < TimeSpan.FromMinutes(5))) continue;
            next = launch;
        }
        if (changed) Persist();
        if (next == null || State.Operations.Any(o => o.Pending)) return false;
        await SellCore(next, rpc, true);
        return true;
    }

    private async Task PublishQueued()
    {
        foreach (var op in State.Operations.Where(o => o.Kind == "Create" && o.Status == "Confirmed"))
        {
            if (op.TelegramStatus == "Queued" && State.Settings.TelegramEnabled) await PublishOne(op, true);
            if (op.XStatus == "Queued" && State.Settings.XEnabled) await PublishOne(op, false);
        }
    }
    private async Task PublishOne(Operation op, bool telegram)
    {
        if (telegram) op.TelegramStatus = "Posting"; else op.XStatus = "Posting";
        Persist();
        try
        {
            string id = telegram ? await _services.Telegram(State.Settings, op.Promotion) : await _services.X(State.Settings, op.Promotion);
            if (telegram) { op.TelegramStatus = "Published"; op.TelegramId = id; }
            else { op.XStatus = "Published"; op.XId = id; }
        }
        catch (Exception ex)
        {
            if (telegram) op.TelegramStatus = "Unknown"; else op.XStatus = "Unknown";
            op.Error = "Promotion delivery needs review: " + ex.Message;
        }
        Persist();
    }
    public async Task Publish(Operation op)
    {
        await _gate.WaitAsync();
        try
        {
            Writable();
            if (op.Kind != "Create" || op.Status != "Confirmed") throw new InvalidOperationException("Promotion requires a finalized launch.");
            if (!State.Settings.TelegramEnabled && !State.Settings.XEnabled) throw new InvalidOperationException("Enable a promotion channel in Settings.");
            State.Settings.Validate();
            if (State.Settings.TelegramEnabled && op.TelegramStatus != "Published") await PublishOne(op, true);
            if (State.Settings.XEnabled && op.XStatus != "Published") await PublishOne(op, false);
        }
        finally { _gate.Release(); Changed?.Invoke(); }
    }
}
