using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NSec.Cryptography;

namespace Mintpilot;

public static class SelfTests
{
    private static int _assertions;
    private static void Check(bool condition, string label)
    {
        _assertions++; if (!condition) throw new InvalidOperationException("FAIL " + label);
    }
    private static void Throws(Action action, string label)
    {
        bool threw = false; try { action(); } catch { threw = true; }
        Check(threw, label);
    }
    [STAThread]
    public static int Run()
    {
        try
        {
            var seed = Convert.FromHexString("9d61b19deffd5a60ba844af492ec2cc44449c5697b326919703bac031cae7f60");
            using var key = Sol.Key(seed);
            Check(Convert.ToHexString(key.PublicKey.Export(KeyBlobFormat.RawPublicKey)).ToLowerInvariant() == "d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a", "RFC8032 public key");
            Check(Convert.ToHexString(SignatureAlgorithm.Ed25519.Sign(key, [])).ToLowerInvariant() ==
                "e5564300c360ac729086e2cc806e828a84877f1eb8e5d974d873e065224901555fb8821590a33bacc61e39701cf9b46bd25bf5f0595bbe24655141438e7a100b", "RFC8032 signature");
            Check(B58.Encode(new byte[32]) == Sol.SystemProgram, "zero public key encoding");
            foreach (var size in new[] { 1, 2, 32, 64, 100 })
            {
                var bytes = RandomNumberGenerator.GetBytes(size); bytes[0] = 0;
                Check(B58.Decode(B58.Encode(bytes)).SequenceEqual(bytes), "Base58 roundtrip");
            }
            Throws(() => B58.Decode("0OIl"), "reject invalid Base58");
            Throws(() => Sol.AddressBytes("1111"), "reject short address");
            Throws(() => Sol.Lamports(0.0000000001m), "reject fractional lamport");
            Check(Sol.Lamports(0.000000001m) == 1, "one lamport precision");
            Throws(() => new Cursor([128, 128, 128]).Compact(), "reject invalid compact length");
            Throws(() => new Cursor([128, 0]).Compact(), "reject noncanonical compact length");
            var receiver = Sol.Address(RandomNumberGenerator.GetBytes(32));
            var tx = Sol.Transfer(Sol.Address(seed), receiver, 123456, Sol.SystemProgram);
            var op = new Operation { Kind = "Withdraw", Recipient = receiver, LimitLamports = 123456 };
            tx.Audit(tx.Keys, Sol.Address(seed), op, null, 0);
            string signature = tx.Sign(seed);
            Check(B58.Decode(signature).Length == 64, "transfer signature length");
            Check(WireTransaction.Parse(tx.Raw).Sign() == signature, "serialized signature verification");
            tx.Raw[^1] ^= 1;
            Throws(() => WireTransaction.Parse(tx.Raw).Sign(), "reject mutated signed transaction");
            Throws(() => WireTransaction.Parse(tx.Raw[..^1]), "reject truncated transaction");
            Throws(() => WireTransaction.Parse(tx.Raw.Concat(new byte[] { 0 }).ToArray()), "reject trailing data");
            var valid = Sol.Transfer(Sol.Address(seed), receiver, 123456, Sol.SystemProgram);
            Throws(() => valid.Audit(valid.Keys, receiver, op, null, 0), "reject wrong payer");
            op.Recipient = Sol.Address(RandomNumberGenerator.GetBytes(32));
            Throws(() => valid.Audit(valid.Keys, Sol.Address(seed), op, null, 0), "reject wrong destination");

            valid.Sign(seed);
            var zeroRpc = new Rpc(new Network(new HttpClient(new EmptyAccountSimulation())), "https://unit-test.invalid");
            zeroRpc.Simulate(valid, Sol.Address(seed), 100000).GetAwaiter().GetResult();
            Check(true, "withdraw-all supports a zero-lamport account");
            Throws(() => zeroRpc.Simulate(valid, Sol.Address(seed), 99999).GetAwaiter().GetResult(), "zero-account simulation still enforces the debit cap");
            var directory = Path.Combine(Path.GetTempPath(), "Mintpilot-Checks-" + Guid.NewGuid());
            Directory.CreateDirectory(directory);
            try
            {
                var vault = new Vault(Path.Combine(directory, "one")); string originalAddress = Sol.Address(vault.State.Seed);
                vault.State.Settings.OpenAiKey = "test-only-secret-never-an-api-key"; vault.Save();
                Check(!Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(vault.DirectoryPath, "workspace.dat"))).Contains(vault.State.Settings.OpenAiKey), "secrets encrypted on disk");
                Check(Sol.Address(new Vault(vault.DirectoryPath).State.Seed) == originalAddress, "wallet stable across restart");
                byte[] backup = vault.Export("testing-password-12345");
                var restored = new Vault(Path.Combine(directory, "two")); string before = Sol.Address(restored.State.Seed);
                Throws(() => restored.Restore(backup, "wrong-password"), "reject wrong backup password");
                Check(Sol.Address(restored.State.Seed) == before, "failed restore preserves wallet");
                restored.Restore(backup, "testing-password-12345");
                Check(Sol.Address(restored.State.Seed) == originalAddress, "encrypted backup roundtrip");
                var handler = new FakeChain(vault.DirectoryPath);
                var http = new HttpClient(handler);
                var engine = new Engine(vault, http);
                vault.State.Settings.PinataToken = "test-token"; vault.State.Settings.OpenAiKey = "";
                var idea = new CoinIdea { Name = "Test Orbit", Symbol = "TEST", Description = "Test-only fictional token", Post = "An original fictional meme." };
                var fixture = BuildCreate(engine.Address, receiver, idea, "https://ipfs.io/ipfs/bafytest", true);
                var fixtureOp = new Operation { Kind = "Create", Mint = receiver, Uri = "https://ipfs.io/ipfs/bafytest", LimitLamports = 40000000 };
                fixture.Audit(fixture.Keys, engine.Address, fixtureOp, idea, 11000000);
                Check(fixture.RequiredSigners == 2, "v0 create parsing");
                var changedIdea = new CoinIdea { Name = "Different", Symbol = "TEST", Description = "x" };
                Throws(() => fixture.Audit(fixture.Keys, engine.Address, fixtureOp, changedIdea, 11000000), "reject changed metadata");
                Throws(() => fixture.Audit(fixture.Keys, engine.Address, fixtureOp, idea, 1), "reject excessive initial buy");
                engine.Launch(idea, [1, 2, 3]).GetAwaiter().GetResult();
                var pending = vault.State.Operations.Single();
                Check(handler.Broadcasts == 1 && pending.Pending, "timeout leaves one pending transaction");
                Check(handler.JournalChecked, "signed transaction persisted before broadcast");
                Check(new Vault(vault.DirectoryPath).State.Operations.Single().Signature == pending.Signature, "pending signature survives restart");
                Throws(() => engine.Launch(idea, [1]).GetAwaiter().GetResult(), "pending transaction prevents another launch");
                Check(handler.Broadcasts == 1, "pending operation never replaced or resubmitted");
                handler.Finalize = true;
                var restarted = new Engine(new Vault(vault.DirectoryPath), http);
                restarted.Poll().GetAwaiter().GetResult();
                var confirmed = restarted.State.Operations.Single();
                Check(confirmed.Status == "Confirmed" && confirmed.DebitLamports == 12500000, "restart reconciles finalized cost");
                Check(handler.Broadcasts == 1, "reconciliation does not create a second transaction");
                restarted.State.Settings.DailyCapSol = 0.04m;
                Throws(() => restarted.Launch(idea, [1]).GetAwaiter().GetResult(), "daily cap counts finalized spending");
                Check(handler.Broadcasts == 1, "budget failure never broadcasts");
                var svg = Artwork.Render(idea);
                Check(svg.Length > 100 && svg[0] == 137 && svg[1] == 80, "native PNG artwork");
            }
            finally { Directory.Delete(directory, true); }
            File.WriteAllText("self-test-result.txt", "PASS " + _assertions + " checks: cryptography, transaction validation, encrypted storage, backups, pending recovery, budget enforcement, and artwork.");
            Console.WriteLine("PASS " + _assertions + " checks."); return 0;
        }
        catch (Exception ex) { File.WriteAllText("self-test-result.txt", ex.ToString()); Console.Error.WriteLine(ex); return 1; }
    }
    private static byte[] Compact(int value)
    {
        var bytes = new List<byte>();
        do { byte b = (byte)(value & 127); value >>= 7; bytes.Add((byte)(b | (value > 0 ? 128 : 0))); } while (value > 0);
        return bytes.ToArray();
    }
    private static byte[] BorshText(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text); return BitConverter.GetBytes(bytes.Length).Concat(bytes).ToArray();
    }
    private static WireTransaction BuildCreate(string wallet, string mint, CoinIdea idea, string uri, bool v0 = false)
    {
        var msg = new List<byte>();
        if (v0) msg.Add(128);
        msg.AddRange(new byte[] { 2, 0, 1, 3 });
        foreach (var address in new[] { wallet, mint, Sol.Pump, Sol.SystemProgram }) msg.AddRange(Sol.AddressBytes(address));
        // The fourth address above is the recent blockhash, not an account key.
        msg.Add(2);
        var create = Sol.Discriminator("create_v2").Concat(BorshText(idea.Name)).Concat(BorshText(idea.Symbol)).Concat(BorshText(uri)).Concat(Sol.AddressBytes(wallet)).Concat(new byte[] { 0 }).ToArray();
        msg.AddRange(new byte[] { 2, 2, 0, 1 }); msg.AddRange(Compact(create.Length)); msg.AddRange(create);
        byte[] buy = Sol.Discriminator("buy").Concat(BitConverter.GetBytes((ulong)1000)).Concat(BitConverter.GetBytes((ulong)11000000)).ToArray();
        msg.AddRange(new byte[] { 2, 2, 0, 1 }); msg.AddRange(Compact(buy.Length)); msg.AddRange(buy);
        if (v0) msg.Add(0);
        var raw = new List<byte> { 2 }; raw.AddRange(new byte[128]); raw.AddRange(msg);
        return WireTransaction.Parse(raw.ToArray());
    }
    private sealed class FakeChain(string vaultPath) : HttpMessageHandler
    {
        public int Broadcasts;
        public bool JournalChecked;
        public bool Finalize;
        private long _balance = 2_000_000_000;
        private long _pre = 2_000_000_000;
        private long _post = 2_000_000_000;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            string host = request.RequestUri!.Host;
            if (host == "uploads.pinata.cloud") return Response(new { data = new { cid = "bafytest" } });
            if (host == "api.dexscreener.com") return Response(Array.Empty<object>());
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            var data = body.RootElement;
            if (host == "pumpportal.fun")
            {
                var metadata = data.GetProperty("tokenMetadata");
                var idea = new CoinIdea { Name = metadata.GetProperty("name").GetString()!, Symbol = metadata.GetProperty("symbol").GetString()! };
                var tx = BuildCreate(data.GetProperty("publicKey").GetString()!, data.GetProperty("mint").GetString()!, idea, metadata.GetProperty("uri").GetString()!, true);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(tx.Raw) };
            }
            string method = data.GetProperty("method").GetString()!;
            if (method == "sendTransaction")
            {
                var raw = Convert.FromBase64String(data.GetProperty("params")[0].GetString()!);
                var wire = WireTransaction.Parse(raw); string signature = wire.Sign();
                var saved = new Vault(vaultPath).State.Operations.Last();
                JournalChecked = saved.Status == "Signed" && saved.Signature == signature && saved.Raw == Convert.ToBase64String(raw);
                Broadcasts++; _pre = _balance; _balance -= 12500000; _post = _balance;
                throw new HttpRequestException("Test-only lost response after accepting the transaction.");
            }
            object result = method switch
            {
                "getGenesisHash" => "5eykt4UsFv8P8NJdTREpY1vzqKqZKvdp",
                "getBalance" => new { value = _balance },
                "getSignatureStatuses" => new { value = new object?[] { Finalize ? new { confirmationStatus = "finalized", err = (object?)null } : null } },
                "isBlockhashValid" => new { value = true },
                "getTokenAccountsByOwner" => new { value = Array.Empty<object>() },
                "getTransaction" => new { meta = new { fee = 10000, preBalances = new[] { _pre }, postBalances = new[] { _post }, err = (object?)null } },
                "simulateTransaction" => new { value = new { err = (object?)null, preBalances = new[] { _balance }, postBalances = new[] { _balance - 12500000 } } },
                _ => throw new InvalidOperationException("Unexpected mocked RPC " + method)
            };
            return Response(new { result });
        }
        private static HttpResponseMessage Response(object value) => new(HttpStatusCode.OK)
        { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    }
    private sealed class EmptyAccountSimulation : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            string method = doc.RootElement.GetProperty("method").GetString()!;
            object result = method == "getBalance" ? new { value = 100000 } :
                new { value = new { err = (object?)null, accounts = new object?[] { null } } };
            return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(JsonSerializer.Serialize(new { result }), Encoding.UTF8, "application/json") };
        }
    }

}
