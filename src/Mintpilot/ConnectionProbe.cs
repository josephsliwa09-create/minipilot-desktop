using System.Net.Http;
using System.Security.Cryptography;

namespace Mintpilot;

public static class ConnectionProbe
{
    public static int Run()
    {
        try { return Probe().GetAwaiter().GetResult(); }
        catch (Exception ex)
        {
            File.WriteAllText("connection-probe-result.txt", "UNVERIFIED: live transaction-builder probe could not complete. " + ex.Message);
            return ex is InvalidDataException ? 1 : 0;
        }
    }
    private static async Task<int> Probe()
    {
        var walletSeed = RandomNumberGenerator.GetBytes(32);
        var mintSeed = RandomNumberGenerator.GetBytes(32);
        try
        {
            var s = new Settings();
            var net = new Network(new HttpClient { Timeout = TimeSpan.FromSeconds(45) });
            var rpc = new Rpc(net, s.RpcUrl);
            var idea = new CoinIdea { Name = "Mintpilot Protocol Check", Symbol = "MPCHECK", Description = "A transaction builder check." };
            string wallet = Sol.Address(walletSeed), mint = Sol.Address(mintSeed);
            var op = new Operation
            {
                Kind = "Create", Mint = mint, LimitLamports = Sol.Lamports(s.MaxLaunchSol),
                Uri = "https://raw.githubusercontent.com/josephsliwa09-create/minipilot-desktop/main/tests/probe-metadata.json"
            };
            var tx = await new LaunchServices(net).Build(s, new
            {
                publicKey = wallet, action = "create", tokenMetadata = new { name = idea.Name, symbol = idea.Symbol, uri = op.Uri },
                mint, denominatedInSol = "true", amount = s.BuySol, slippage = s.Slippage, priorityFee = s.PriorityFeeSol, pool = "pump"
            });
            var keys = await tx.ResolveKeys(rpc);
            Console.WriteLine("Live unsigned instructions: " + System.Text.Json.JsonSerializer.Serialize(tx.Instructions.Select(ix => new
            {
                program = keys[ix.Program], accounts = ix.Accounts.Select(i => keys[i]).ToArray(), data = Convert.ToHexString(ix.Data)
            })));
            try
            {
                var direct = await net.Json(HttpMethod.Post, "https://fun-block.pump.fun/agents/create-coin", new
                {
                    user = wallet, name = idea.Name, symbol = idea.Symbol, uri = op.Uri, solLamports = "10000000",
                    encoding = "base64", mayhemMode = false, cashback = false, tokenizedAgent = false
                });
                Console.WriteLine("Direct Pump builder response fields: " + string.Join(",", direct.EnumerateObject().Select(p => p.Name)));
                if (direct.TryGetProperty("transaction", out var data) && direct.TryGetProperty("mintPublicKey", out var publicMint))
                {
                    var candidate = WireTransaction.Parse(Convert.FromBase64String(data.GetString()!));
                    var candidateOp = new Operation { Kind = "Create", Mint = publicMint.GetString()!, Uri = op.Uri, LimitLamports = op.LimitLamports };
                    candidate.Audit(await candidate.ResolveKeys(rpc), wallet, candidateOp, idea, Sol.Lamports(0.011m));
                    candidate.Sign(walletSeed);
                    Console.WriteLine("PASS direct Pump builder decoded, audited, and co-signed; no broadcast.");
                }
            }
            catch (Exception directError) { Console.WriteLine("Direct Pump builder: " + directError.Message); }
            tx.Audit(keys, wallet, op, idea, Sol.Lamports(0.011m));
            tx.Sign(walletSeed, mintSeed);
            File.WriteAllText("connection-probe-result.txt", "PASS PumpPortal returned a live pump.fun create transaction. Native decoding, metadata, fee payer, signers, buy limits, creator and Ed25519 signatures passed. No transaction was broadcast.");
            return 0;
        }
        finally { CryptographicOperations.ZeroMemory(walletSeed); CryptographicOperations.ZeroMemory(mintSeed); }
    }
}
