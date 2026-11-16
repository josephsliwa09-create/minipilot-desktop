using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using NSec.Cryptography;

namespace Mintpilot;

public static class Sol
{
    public const string SystemProgram = "11111111111111111111111111111111";
    public const string Pump = "6EF8rrecthR5Dkzon8Nwu78hRvfCKubJ14M5uBEwF6P";
    public const string PortalRouter = "FAdo9NCw1ssek6Z6yeWzWjhLVsr8uiCwcWNUnKgzTnHe";
    public const string PumpAmm = "pAMMBay6oceH9fJKBRHGP5D4bD4sWpmSwMn52FMfXEA";
    public const string Token = "TokenkegQfeZyiNwAJbNbGKPFXCWuBvf9Ss623VQ5DA";
    public const string Token2022 = "TokenzQdBNbLqP5VEhdkAS6EPFLC1PHnBqCXEpPxuEb";
    public const string Associated = "ATokenGPvbdGVxr1b2hvZbsiqW5xWH25efTNsLJA8knL";
    public const string Compute = "ComputeBudget111111111111111111111111111111";
    public const string LookupProgram = "AddressLookupTab1e1111111111111111111111111";
    public static NSec.Cryptography.Key Key(byte[] seed) => NSec.Cryptography.Key.Import(SignatureAlgorithm.Ed25519, seed, KeyBlobFormat.RawPrivateKey);
    public static string Address(byte[] seed) { using var k = Key(seed); return B58.Encode(k.PublicKey.Export(KeyBlobFormat.RawPublicKey)); }
    public static long Lamports(decimal sol)
    {
        var n = sol * 1_000_000_000m;
        if (n != decimal.Truncate(n)) throw new InvalidOperationException("SOL amounts support up to nine decimal places.");
        return checked((long)n);
    }
    public static string Display(long n) => (n / 1_000_000_000m).ToString("0.#########") + " SOL";
    public static byte[] AddressBytes(string address)
    {
        var bytes = B58.Decode(address);
        if (bytes.Length != 32) throw new InvalidOperationException("Enter a valid Solana public address.");
        return bytes;
    }
    public static byte[] Discriminator(string name) => SHA256.HashData(Encoding.UTF8.GetBytes("global:" + name))[..8];
    public static bool IsInstruction(byte[] data, string name) => data.Length >= 8 && data.AsSpan(0, 8).SequenceEqual(Discriminator(name));
    public static WireTransaction Transfer(string sender, string receiver, long amount, string blockhash)
    {
        if (amount <= 0) throw new InvalidOperationException("Withdrawal amount must be positive.");
        if (sender == receiver) throw new InvalidOperationException("Choose a different destination wallet.");
        var msg = new List<byte> { 1, 0, 1, 3 };
        msg.AddRange(AddressBytes(sender)); msg.AddRange(AddressBytes(receiver)); msg.AddRange(AddressBytes(SystemProgram));
        msg.AddRange(AddressBytes(blockhash));
        msg.AddRange(new byte[] { 1, 2, 2, 0, 1, 12 });
        msg.AddRange(BitConverter.GetBytes((uint)2)); msg.AddRange(BitConverter.GetBytes(amount));
        var raw = new List<byte> { 1 }; raw.AddRange(new byte[64]); raw.AddRange(msg);
        return WireTransaction.Parse(raw.ToArray());
    }
}
public static class B58
{
    private const string Alphabet = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";
    public static string Encode(byte[] bytes)
    {
        var n = new BigInteger(bytes, isUnsigned: true, isBigEndian: true);
        var result = "";
        while (n > 0) { n = BigInteger.DivRem(n, 58, out var mod); result = Alphabet[(int)mod] + result; }
        return new string('1', bytes.TakeWhile(b => b == 0).Count()) + result;
    }
    public static byte[] Decode(string text)
    {
        if (text.Length is < 1 or > 200) throw new FormatException("Invalid Base58 value.");
        BigInteger n = 0;
        foreach (char c in text)
        {
            int digit = Alphabet.IndexOf(c);
            if (digit < 0) throw new FormatException("Invalid Base58 character.");
            n = n * 58 + digit;
        }
        byte[] body = n == 0 ? [] : n.ToByteArray(isUnsigned: true, isBigEndian: true);
        return new byte[text.TakeWhile(c => c == '1').Count()].Concat(body).ToArray();
    }
}

public sealed record CompiledInstruction(int Program, byte[] Accounts, byte[] Data);
public sealed record Lookup(string Address, byte[] Writable, byte[] Readonly);
public sealed class Cursor(byte[] source)
{
    public int Position { get; private set; }
    public int Remaining => source.Length - Position;
    public byte Byte() => Take(1)[0];
    public byte[] Take(int count)
    {
        if (count < 0 || count > Remaining) throw new InvalidDataException("Truncated transaction.");
        var value = source.AsSpan(Position, count).ToArray(); Position += count; return value;
    }
    public int Compact()
    {
        int value = 0;
        for (int i = 0; i < 3; i++)
        {
            int b = Byte(); value |= (b & 127) << (7 * i);
            if ((b & 128) == 0)
            {
                if (value > 65535 || (i > 0 && b == 0)) throw new InvalidDataException("Invalid compact length.");
                return value;
            }
        }
        throw new InvalidDataException("Invalid compact length.");
    }
    public string Text()
    {
        var length = BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
        if (length > 4096) throw new InvalidDataException("Invalid instruction string.");
        return new UTF8Encoding(false, true).GetString(Take((int)length));
    }
}

public sealed class WireTransaction
{
    public byte[] Raw { get; private set; } = [];
    public byte[] Message { get; private set; } = [];
    public int SignatureOffset { get; private set; }
    public int RequiredSigners { get; private set; }
    public List<string> Keys { get; } = [];
    public List<CompiledInstruction> Instructions { get; } = [];
    public List<Lookup> Lookups { get; } = [];
    public string Blockhash { get; private set; } = "";

    public static WireTransaction Parse(byte[] raw)
    {
        if (raw.Length is < 69 or > 1232) throw new InvalidDataException("Invalid transaction size.");
        var c = new Cursor(raw); var tx = new WireTransaction { Raw = raw.ToArray() };
        int signatures = c.Compact(); tx.SignatureOffset = c.Position;
        if (signatures is < 1 or > 12) throw new InvalidDataException("Invalid signer count.");
        c.Take(checked(signatures * 64)); int messageStart = c.Position;
        int header = c.Byte(); bool v0 = (header & 128) != 0;
        if (v0 && header != 128) throw new InvalidDataException("Unsupported Solana message version.");
        tx.RequiredSigners = v0 ? c.Byte() : header;
        int readonlySigned = c.Byte(), readonlyUnsigned = c.Byte(), keyCount = c.Compact();
        if (signatures != tx.RequiredSigners || keyCount < signatures || keyCount > 256 || readonlySigned > signatures || readonlyUnsigned > keyCount - signatures)
            throw new InvalidDataException("Invalid message header.");
        for (int i = 0; i < keyCount; i++) tx.Keys.Add(B58.Encode(c.Take(32)));
        tx.Blockhash = B58.Encode(c.Take(32));
        int instructionCount = c.Compact();
        if (instructionCount is < 1 or > 64) throw new InvalidDataException("Invalid instruction count.");
        for (int i = 0; i < instructionCount; i++)
        {
            var program = c.Byte(); var accounts = c.Take(c.Compact()); var data = c.Take(c.Compact());
            tx.Instructions.Add(new CompiledInstruction(program, accounts, data));
        }
        if (v0)
        {
            int lookupCount = c.Compact();
            if (lookupCount > 16) throw new InvalidDataException("Too many lookup tables.");
            for (int i = 0; i < lookupCount; i++) tx.Lookups.Add(new Lookup(B58.Encode(c.Take(32)), c.Take(c.Compact()), c.Take(c.Compact())));
        }
        if (c.Remaining != 0) throw new InvalidDataException("Unexpected transaction data.");
        tx.Message = raw[messageStart..];
        return tx;
    }

    public async Task<List<string>> ResolveKeys(Rpc rpc)
    {
        var writable = new List<string>(); var readOnly = new List<string>();
        foreach (var table in Lookups)
        {
            var r = await rpc.Call("getAccountInfo", [table.Address, new { encoding = "base64", commitment = "confirmed" }]);
            var value = r.GetProperty("value");
            if (value.ValueKind == System.Text.Json.JsonValueKind.Null || value.GetProperty("owner").GetString() != Sol.LookupProgram)
                throw new InvalidDataException("Invalid address lookup table.");
            var bytes = Convert.FromBase64String(value.GetProperty("data")[0].GetString()!);
            if (bytes.Length < 56 || (bytes.Length - 56) % 32 != 0 || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != 1)
                throw new InvalidDataException("Invalid lookup table data.");
            string Resolve(byte index)
            {
                int offset = checked(56 + index * 32);
                if (offset + 32 > bytes.Length) throw new InvalidDataException("Lookup index is out of range.");
                return B58.Encode(bytes.AsSpan(offset, 32).ToArray());
            }
            writable.AddRange(table.Writable.Select(Resolve)); readOnly.AddRange(table.Readonly.Select(Resolve));
        }
        return Keys.Concat(writable).Concat(readOnly).ToList();
    }

    public void Audit(List<string> keys, string wallet, Operation operation, CoinIdea? idea, long maxBuy)
    {
        if (Keys[0] != wallet || RequiredSigners != (operation.Kind == "Create" ? 2 : 1))
            throw new InvalidDataException("Unexpected fee payer or signers.");
        var expected = operation.Kind == "Create" ? new[] { wallet, operation.Mint } : new[] { wallet };
        if (!Keys.Take(RequiredSigners).Order().SequenceEqual(expected.Order()))
            throw new InvalidDataException("Unexpected signing authority.");
        bool hasCreate = false, hasTrade = false;
        long directDebit = 0;
        foreach (var ix in Instructions)
        {
            if (ix.Program >= keys.Count || ix.Accounts.Any(i => i >= keys.Count)) throw new InvalidDataException("Invalid account index.");
            string program = keys[ix.Program];
            if (program == Sol.Compute) continue;
            if (program == Sol.SystemProgram)
            {
                if (ix.Data.Length < 4) throw new InvalidDataException("Invalid system instruction.");
                uint kind = BinaryPrimitives.ReadUInt32LittleEndian(ix.Data);
                if ((kind != 0 && kind != 2) || ix.Accounts.Length < 2 || keys[ix.Accounts[0]] != wallet || ix.Data.Length < 12)
                    throw new InvalidDataException("Unexpected system authority operation.");
                long debit = checked((long)BinaryPrimitives.ReadUInt64LittleEndian(ix.Data.AsSpan(4)));
                directDebit = checked(directDebit + debit);
                if (operation.Kind == "Withdraw" && (kind != 2 || keys[ix.Accounts[1]] != operation.Recipient || debit != operation.LimitLamports))
                    throw new InvalidDataException("Withdrawal destination or amount changed.");
                continue;
            }
            if (operation.Kind == "Withdraw") throw new InvalidDataException("Unexpected withdrawal instruction.");
            if (program == Sol.Associated)
            {
                if (ix.Data.Length > 1 || (ix.Data.Length == 1 && ix.Data[0] > 1) || ix.Accounts.Length < 4 || keys[ix.Accounts[0]] != wallet || keys[ix.Accounts[2]] != wallet)
                    throw new InvalidDataException("Unexpected associated token account instruction.");
                continue;
            }
            if (program is Sol.Token or Sol.Token2022)
            {
                if (ix.Data.Length == 0 || ix.Data[0] is not (0 or 1 or 9 or 16 or 17 or 18 or 20))
                    throw new InvalidDataException("Unexpected token authority instruction.");
                if (ix.Data[0] == 9 && (ix.Accounts.Length < 3 || keys[ix.Accounts[1]] != wallet || keys[ix.Accounts[2]] != wallet))
                    throw new InvalidDataException("Unexpected token-account close destination.");
                continue;
            }
            if (program == Sol.PortalRouter)
            {
                // PumpPortal's current routing envelope retains the Pump argument
                // order and includes its 50-basis-point local API fee.
                string action = operation.Kind == "Create" ? "buy" : "sell";
                var tag = Sol.Discriminator(action); tag[1] = 50;
                if (ix.Data.Length != 26 || !ix.Data.AsSpan(0, 8).SequenceEqual(tag) ||
                    BinaryPrimitives.ReadUInt16LittleEndian(ix.Data.AsSpan(24)) != 50 ||
                    ix.Accounts.Length < 16 || keys[ix.Accounts[2]] != operation.Mint ||
                    keys[ix.Accounts[6]] != wallet || keys[ix.Accounts[7]] != Sol.SystemProgram ||
                    (keys[ix.Accounts[8]] != Sol.Token && keys[ix.Accounts[8]] != Sol.Token2022) ||
                    keys[ix.Accounts[11]] != Sol.Pump || hasTrade)
                    throw new InvalidDataException("Unexpected PumpPortal routing instruction.");
                if (operation.Kind == "Create" && BinaryPrimitives.ReadUInt64LittleEndian(ix.Data.AsSpan(16)) > (ulong)operation.LimitLamports)
                    throw new InvalidDataException("PumpPortal buy quote exceeds the total launch limit.");
                if (operation.Kind is not ("Create" or "Sell")) throw new InvalidDataException("Unexpected routed operation.");
                hasTrade = true; continue;
            }
            if (program != Sol.Pump && program != Sol.PumpAmm) throw new InvalidDataException("Unexpected transaction program: " + program);
            if (!ix.Accounts.Any(index => keys[index] == operation.Mint)) throw new InvalidDataException("Unexpected trade mint.");
            if (operation.Kind == "Create" && program == Sol.Pump && (Sol.IsInstruction(ix.Data, "create") || Sol.IsInstruction(ix.Data, "create_v2")))
            {
                var c = new Cursor(ix.Data[8..]);
                if (idea is null || c.Text() != idea.Name || c.Text() != idea.Symbol || c.Text() != operation.Uri)
                    throw new InvalidDataException("Token metadata changed.");
                if (B58.Encode(c.Take(32)) != wallet) throw new InvalidDataException("Unexpected token creator.");
                if (hasCreate) throw new InvalidDataException("Duplicate create instruction.");
                hasCreate = true; continue;
            }
            if (operation.Kind == "Create" && program == Sol.Pump)
            {
                bool exactIn = Sol.IsInstruction(ix.Data, "buy_exact_sol_in") || Sol.IsInstruction(ix.Data, "buy_exact_quote_in_v2");
                bool exactOut = Sol.IsInstruction(ix.Data, "buy") || Sol.IsInstruction(ix.Data, "buy_v2");
                if ((!exactIn && !exactOut) || ix.Data.Length < 24) throw new InvalidDataException("Unexpected Pump buy instruction.");
                ulong cap = BinaryPrimitives.ReadUInt64LittleEndian(ix.Data.AsSpan(exactIn ? 8 : 16));
                if (cap > (ulong)maxBuy || hasTrade) throw new InvalidDataException("Buy amount exceeds the configured slippage limit.");
                hasTrade = true; continue;
            }
            if (operation.Kind == "Sell" && (Sol.IsInstruction(ix.Data, "sell") || Sol.IsInstruction(ix.Data, "sell_v2")))
            {
                if (hasTrade) throw new InvalidDataException("Duplicate sell instruction.");
                hasTrade = true; continue;
            }
            throw new InvalidDataException("Unexpected Pump operation.");
        }
        if (directDebit > operation.LimitLamports || (operation.Kind == "Create" && (!hasCreate || !hasTrade)) || (operation.Kind == "Sell" && !hasTrade))
            throw new InvalidDataException("Transaction does not match the requested operation.");
        if (operation.Kind == "Withdraw" && (Instructions.Count != 1 || directDebit != operation.LimitLamports))
            throw new InvalidDataException("Invalid withdrawal transfer.");
    }
    public string Sign(params byte[][] seeds)
    {
        foreach (var seed in seeds)
        {
            using var key = Sol.Key(seed);
            var address = B58.Encode(key.PublicKey.Export(KeyBlobFormat.RawPublicKey));
            int index = Keys.IndexOf(address);
            if (index < 0 || index >= RequiredSigners) throw new InvalidDataException("Key is not a required signer.");
            var signature = SignatureAlgorithm.Ed25519.Sign(key, Message);
            signature.CopyTo(Raw, SignatureOffset + 64 * index);
        }
        for (int i = 0; i < RequiredSigners; i++)
        {
            var pub = PublicKey.Import(SignatureAlgorithm.Ed25519, Sol.AddressBytes(Keys[i]), KeyBlobFormat.RawPublicKey);
            if (!SignatureAlgorithm.Ed25519.Verify(pub, Message, Raw.AsSpan(SignatureOffset + 64 * i, 64)))
                throw new InvalidDataException("Missing or invalid transaction signature.");
        }
        return B58.Encode(Raw.AsSpan(SignatureOffset, 64).ToArray());
    }
}
