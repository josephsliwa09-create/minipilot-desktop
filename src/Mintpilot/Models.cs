using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NSec.Cryptography;

namespace Mintpilot;

public sealed class Settings
{
    public string RpcUrl { get; set; } = "https://api.mainnet-beta.solana.com";
    public string PinataToken { get; set; } = "";
    public string AiProvider { get; set; } = "Ollama";
    public string AiModel { get; set; } = "llama3.2:3b";
    public string OpenAiKey { get; set; } = "";
    public string Theme { get; set; } = "Original playful space animals";
    public decimal BuySol { get; set; } = 0.01m;
    public decimal MaxLaunchSol { get; set; } = 0.04m;
    public decimal DailyCapSol { get; set; } = 0.12m;
    public decimal ReserveSol { get; set; } = 0.02m;
    public bool AutoExitEnabled { get; set; }
    public decimal ExitDropPercent { get; set; } = 30m;
    public decimal Slippage { get; set; } = 10m;
    public decimal PriorityFeeSol { get; set; } = 0.00001m;
    public int IntervalMinutes { get; set; } = 60;
    public int MaxDailyLaunches { get; set; } = 3;
    public bool TelegramEnabled { get; set; }
    public string TelegramToken { get; set; } = "";
    public string TelegramChat { get; set; } = "";
    public bool XEnabled { get; set; }
    public string XConsumerKey { get; set; } = "";
    public string XConsumerSecret { get; set; } = "";
    public string XAccessToken { get; set; } = "";
    public string XAccessSecret { get; set; } = "";

    public void Validate(bool launching = false, bool agent = false)
    {
        if (!Uri.TryCreate(RpcUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https")
            throw new InvalidOperationException("Enter an HTTPS Solana mainnet RPC URL.");
        if (ExitDropPercent < 1 || ExitDropPercent > 99)
            throw new InvalidOperationException("Choose an automatic exit drop between 1 and 99 percent.");
        if (BuySol <= 0 || BuySol > 100 || Sol.Lamports(BuySol) == 0)
            throw new InvalidOperationException("Initial buy must be greater than zero and at most 100 SOL.");
        if (MaxLaunchSol < BuySol || MaxLaunchSol > 101 || DailyCapSol < MaxLaunchSol)
            throw new InvalidOperationException("Set a launch limit covering the buy and fees, and a daily limit covering one launch.");
        if (ReserveSol < 0 || PriorityFeeSol < 0 || PriorityFeeSol > MaxLaunchSol || Slippage < 0.1m || Slippage > 50)
            throw new InvalidOperationException("Check the SOL reserve, priority fee, and slippage (0.1–50%).");
        if (IntervalMinutes < 1 || IntervalMinutes > 10080 || MaxDailyLaunches < 1 || MaxDailyLaunches > 100)
            throw new InvalidOperationException("Choose an interval of 1–10080 minutes and 1–100 daily launches.");
        foreach (decimal amount in new[] { BuySol, MaxLaunchSol, DailyCapSol, ReserveSol, PriorityFeeSol }) Sol.Lamports(amount);
        if (launching && string.IsNullOrWhiteSpace(PinataToken))
            throw new InvalidOperationException("Add your Pinata JWT in Settings for public image and metadata uploads.");
        if (agent && (string.IsNullOrWhiteSpace(AiModel) || (AiProvider == "OpenAI" && string.IsNullOrWhiteSpace(OpenAiKey))))
            throw new InvalidOperationException("Configure the AI provider and model in Settings.");
        if (TelegramEnabled && (string.IsNullOrWhiteSpace(TelegramToken) || string.IsNullOrWhiteSpace(TelegramChat)))
            throw new InvalidOperationException("Enter the Telegram bot token and destination chat ID.");
        if (XEnabled && new[] { XConsumerKey, XConsumerSecret, XAccessToken, XAccessSecret }.Any(string.IsNullOrWhiteSpace))
            throw new InvalidOperationException("Enter all four X user authentication credentials.");
    }
}

public sealed class CoinIdea
{
    public string Name { get; set; } = "";
    public string Symbol { get; set; } = "";
    public string Description { get; set; } = "";
    public string Post { get; set; } = "";
    public void Validate()
    {
        if (Encoding.UTF8.GetByteCount(Name.Trim()) is < 1 or > 32)
            throw new InvalidOperationException("Coin name must fit in 32 UTF-8 bytes.");
        if (!System.Text.RegularExpressions.Regex.IsMatch(Symbol, "^[A-Z0-9]{2,10}$"))
            throw new InvalidOperationException("Use a 2–10 character uppercase ticker.");
        if (Description.Length is < 1 or > 1500)
            throw new InvalidOperationException("Add a description of up to 1500 characters.");
        if (Post.Length > 150) throw new InvalidOperationException("Promotion text must be at most 150 characters.");
    }
}

public sealed class Operation
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Kind { get; set; } = "";
    public string Name { get; set; } = "";
    public string Symbol { get; set; } = "";
    public string Mint { get; set; } = "";
    public string Recipient { get; set; } = "";
    public string Uri { get; set; } = "";
    public string Signature { get; set; } = "";
    public string Raw { get; set; } = "";
    public string Blockhash { get; set; } = "";
    public string Status { get; set; } = "Preparing";
    public string Error { get; set; } = "";
    public decimal ExitPeakUsd { get; set; }
    public bool ExitTriggered { get; set; }
    public bool AutomaticSale { get; set; }
    public bool ObserveExitPrice(decimal price, decimal percent)
    {
        if (price <= 0 || ExitTriggered) return false;
        if (price > ExitPeakUsd) { ExitPeakUsd = price; return true; }
        ExitTriggered = price <= ExitPeakUsd * ((100m - percent) / 100m);
        return ExitTriggered;
    }
    public string Promotion { get; set; } = "";
    public string TelegramStatus { get; set; } = "Disabled";
    public string XStatus { get; set; } = "Disabled";
    public string TelegramId { get; set; } = "";
    public string XId { get; set; } = "";
    public long LimitLamports { get; set; }
    public long ReservationLamports { get; set; }
    public long? DebitLamports { get; set; }
    public long? FeeLamports { get; set; }
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public bool Pending => Status is "Signed" or "Pending";
    public string Amount => DebitLamports is long n ? Sol.Display(n) : "Awaiting settlement";
}

public sealed class AppState
{
    public int Version { get; set; } = 1;
    public byte[] Seed { get; set; } = [];
    public Settings Settings { get; set; } = new();
    public List<Operation> Operations { get; set; } = [];
    public List<BalancePoint> BalanceHistory { get; set; } = [];
}
public sealed record BalancePoint(DateTimeOffset Time, long Lamports);

public sealed class Vault
{
    public string DirectoryPath { get; }
    public AppState State { get; }
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Mintpilot.Native.V1");
    public Vault(string? directory = null)
    {
        DirectoryPath = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MintpilotNative");
        Directory.CreateDirectory(DirectoryPath);
        var path = Path.Combine(DirectoryPath, "workspace.dat");
        if (File.Exists(path))
        {
            try
            {
                var plain = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);
                try { State = JsonSerializer.Deserialize<AppState>(plain) ?? throw new InvalidDataException(); }
                finally { CryptographicOperations.ZeroMemory(plain); }
                if (State.Version != 1 || State.Seed.Length != 32) throw new InvalidDataException();
                using var key = Sol.Key(State.Seed);
            }
            catch { throw new InvalidOperationException("Unable to unlock the saved workspace. Use the original Windows account or restore your encrypted wallet backup. Your existing wallet file has been preserved."); }
        }
        else
        {
            State = new AppState { Seed = RandomNumberGenerator.GetBytes(32) };
            Save();
        }
    }
    public void Save()
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(State);
        byte[] encrypted;
        try { encrypted = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser); }
        finally { CryptographicOperations.ZeroMemory(plain); }
        AtomicWrite(Path.Combine(DirectoryPath, "workspace.dat"), encrypted);
    }
    public static void AtomicWrite(string path, byte[] content)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { file.Write(content); file.Flush(true); }
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public byte[] Export(string password)
    {
        if (password.Length < 12) throw new InvalidOperationException("Use a backup password of at least 12 characters.");
        var salt = RandomNumberGenerator.GetBytes(16);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var derived = Rfc2898DeriveBytes.Pbkdf2(password, salt, 600000, HashAlgorithmName.SHA256, 32);
        var cipher = new byte[32]; var tag = new byte[16];
        try { using var aes = new AesGcm(derived, 16); aes.Encrypt(nonce, State.Seed, cipher, tag, Entropy); }
        finally { CryptographicOperations.ZeroMemory(derived); }
        return JsonSerializer.SerializeToUtf8Bytes(new WalletBackup { Salt = salt, Nonce = nonce, Cipher = cipher, Tag = tag });
    }
    public void Restore(byte[] backup, string password)
    {
        if (State.Operations.Count > 0) throw new InvalidOperationException("Restore into a fresh workspace to retain a complete transaction history.");
        var data = JsonSerializer.Deserialize<WalletBackup>(backup) ?? throw new InvalidDataException("Invalid backup.");
        if (data.Version != 1 || data.Salt.Length != 16 || data.Nonce.Length != 12 || data.Cipher.Length != 32 || data.Tag.Length != 16)
            throw new InvalidDataException("Invalid backup.");
        var derived = Rfc2898DeriveBytes.Pbkdf2(password, data.Salt, 600000, HashAlgorithmName.SHA256, 32);
        var seed = new byte[32];
        try
        {
            using var aes = new AesGcm(derived, 16); aes.Decrypt(data.Nonce, data.Cipher, data.Tag, seed, Entropy);
            using var key = Sol.Key(seed);
            var previous = State.Seed; State.Seed = seed;
            try { Save(); CryptographicOperations.ZeroMemory(previous); }
            catch { State.Seed = previous; CryptographicOperations.ZeroMemory(seed); throw; }
        }
        catch (CryptographicException) { CryptographicOperations.ZeroMemory(seed); throw new InvalidOperationException("Backup password or backup file is invalid."); }
        finally { CryptographicOperations.ZeroMemory(derived); }
    }
}
public sealed class WalletBackup
{
    public int Version { get; set; } = 1;
    public byte[] Salt { get; set; } = [];
    public byte[] Nonce { get; set; } = [];
    public byte[] Cipher { get; set; } = [];
    public byte[] Tag { get; set; } = [];
}
