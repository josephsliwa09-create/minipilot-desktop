using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Mintpilot;

public sealed class Network(HttpClient client)
{
    public HttpClient Client { get; } = client;
    public async Task<JsonElement> Json(HttpMethod method, string url, object? body = null, string? bearer = null, CancellationToken token = default)
    {
        using var request = new HttpRequestMessage(method, url);
        if (body != null) request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        if (!string.IsNullOrWhiteSpace(bearer)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        using var response = await Send(request, token);
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: token);
        return doc.RootElement.Clone();
    }
    public async Task<HttpResponseMessage> Send(HttpRequestMessage request, CancellationToken token = default)
    {
        try
        {
            var response = await Client.SendAsync(request, token);
            if (!response.IsSuccessStatusCode)
            {
                int status = (int)response.StatusCode; response.Dispose();
                throw new InvalidOperationException(request.RequestUri!.Host + " returned HTTP " + status + ".");
            }
            return response;
        }
        catch (HttpRequestException) { throw new InvalidOperationException("Connection failed for " + request.RequestUri!.Host + "."); }
        catch (TaskCanceledException) when (!token.IsCancellationRequested) { throw new InvalidOperationException("Request timed out for " + request.RequestUri!.Host + "."); }
    }
}
public sealed class Rpc(Network net, string url)
{
    private long _id;
    public async Task<JsonElement> Call(string method, object?[] parameters)
    {
        var result = await net.Json(HttpMethod.Post, url, new { jsonrpc = "2.0", id = ++_id, method, @params = parameters });
        if (result.TryGetProperty("error", out var error))
        {
            string code = error.TryGetProperty("code", out var c) ? c.ToString() : "unknown";
            throw new InvalidOperationException("Solana RPC rejected " + method + " (code " + code + "). Check your RPC provider.");
        }
        return result.GetProperty("result").Clone();
    }
    public async Task Mainnet()
    {
        if ((await Call("getGenesisHash", [])).GetString() != "5eykt4UsFv8P8NJdTREpY1vzqKqZKvdp")
            throw new InvalidOperationException("This wallet is configured for pump.fun on Solana mainnet. Select a mainnet RPC.");
    }
    public async Task<long> Balance(string wallet) => (await Call("getBalance", [wallet, new { commitment = "confirmed" }])).GetProperty("value").GetInt64();
    public async Task<long> Fee(WireTransaction tx)
    {
        var value = (await Call("getFeeForMessage", [Convert.ToBase64String(tx.Message), new { commitment = "confirmed" }])).GetProperty("value");
        if (value.ValueKind == JsonValueKind.Null) throw new InvalidOperationException("Transaction blockhash expired. Prepare a new transaction.");
        return value.GetInt64();
    }
    public async Task Simulate(WireTransaction tx, string wallet, long limit)
    {
        long before = await Balance(wallet);
        var result = (await Call("simulateTransaction", [Convert.ToBase64String(tx.Raw), new
        {
            encoding = "base64", commitment = "confirmed", sigVerify = true,
            accounts = new { encoding = "base64", addresses = new[] { wallet } }
        }])).GetProperty("value");
        if (result.GetProperty("err").ValueKind != JsonValueKind.Null)
            throw new InvalidOperationException("Transaction simulation failed: " + result.GetProperty("err").GetRawText());
        long? debit = null;
        if (result.TryGetProperty("preBalances", out var pre) && pre.ValueKind == JsonValueKind.Array && pre.GetArrayLength() > 0 &&
            result.TryGetProperty("postBalances", out var post) && post.ValueKind == JsonValueKind.Array && post.GetArrayLength() > 0)
            debit = pre[0].GetInt64() - post[0].GetInt64();
        else if (result.TryGetProperty("accounts", out var accounts) && accounts.ValueKind == JsonValueKind.Array && accounts.GetArrayLength() > 0 && accounts[0].ValueKind != JsonValueKind.Null)
            debit = before - accounts[0].GetProperty("lamports").GetInt64();
        if (debit is null) throw new InvalidOperationException("RPC did not return the wallet simulation balance. Choose an RPC supporting account simulation.");
        if (debit > limit) throw new InvalidOperationException("Simulated cost " + Sol.Display(debit.Value) + " exceeds the operation limit " + Sol.Display(limit) + ".");
    }
}
public sealed class LaunchServices(Network net)
{
    public async Task<CoinIdea> Idea(Settings s, CancellationToken token)
    {
        string prompt = "Create ONE original playful memecoin concept for this theme: " + s.Theme +
            ". Return JSON only with Name, Symbol, Description, Post. Name must be ASCII and at most 32 characters, Symbol 2-10 uppercase A-Z0-9. Description 1-500 characters. Post at most 150 characters. No links. Clearly describe a fictional meme. Avoid real people, brands, investment advice, profit predictions, fabricated partnerships or community statistics. The creator is an automated agent.";
        JsonElement result; string text;
        if (s.AiProvider == "OpenAI")
        {
            result = await net.Json(HttpMethod.Post, "https://api.openai.com/v1/chat/completions", new
            {
                model = s.AiModel, messages = new[] { new { role = "user", content = prompt } },
                response_format = new { type = "json_object" }
            }, s.OpenAiKey, token);
            text = result.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()!;
        }
        else
        {
            result = await net.Json(HttpMethod.Post, "http://127.0.0.1:11434/api/generate", new { model = s.AiModel, prompt, stream = false, format = "json" }, token: token);
            text = result.GetProperty("response").GetString()!;
        }
        var idea = JsonSerializer.Deserialize<CoinIdea>(text, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new InvalidOperationException("AI returned an empty concept.");
        idea.Name = idea.Name.Trim(); idea.Symbol = idea.Symbol.Trim().ToUpperInvariant(); idea.Validate();
        return idea;
    }
    public async Task<string> Upload(Settings s, byte[] bytes, string name, string media)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://uploads.pinata.cloud/v3/files");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", s.PinataToken);
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("public"), "network");
        var file = new ByteArrayContent(bytes); file.Headers.ContentType = new MediaTypeHeaderValue(media);
        form.Add(file, "file", name); request.Content = form;
        using var response = await net.Send(request);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var cid = doc.RootElement.GetProperty("data").GetProperty("cid").GetString();
        if (string.IsNullOrWhiteSpace(cid) || cid.Any(c => !char.IsAsciiLetterOrDigit(c))) throw new InvalidDataException("Invalid IPFS upload response.");
        return "https://ipfs.io/ipfs/" + cid;
    }
    public async Task<WireTransaction> Build(Settings s, object payload)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://pumpportal.fun/api/trade-local")
        { Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json") };
        using var response = await net.Send(request);
        return WireTransaction.Parse(await response.Content.ReadAsByteArrayAsync());
    }
    public async Task<string> Telegram(Settings s, string text)
    {
        var r = await net.Json(HttpMethod.Post, "https://api.telegram.org/bot" + s.TelegramToken + "/sendMessage",
            new { chat_id = s.TelegramChat, text, disable_web_page_preview = false });
        if (!r.GetProperty("ok").GetBoolean()) throw new InvalidOperationException("Telegram rejected the promotion.");
        return r.GetProperty("result").GetProperty("message_id").ToString();
    }
    public static string OAuthHeader(Settings s, string nonce, string timestamp)
    {
        const string url = "https://api.x.com/2/tweets";
        static string E(string value) => Uri.EscapeDataString(value);
        var p = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["oauth_consumer_key"] = s.XConsumerKey, ["oauth_nonce"] = nonce,
            ["oauth_signature_method"] = "HMAC-SHA1", ["oauth_timestamp"] = timestamp,
            ["oauth_token"] = s.XAccessToken, ["oauth_version"] = "1.0"
        };
        string normalized = string.Join("&", p.Select(k => E(k.Key) + "=" + E(k.Value)));
        string signatureBase = "POST&" + E(url) + "&" + E(normalized);
        using var hmac = new HMACSHA1(Encoding.UTF8.GetBytes(E(s.XConsumerSecret) + "&" + E(s.XAccessSecret)));
        p["oauth_signature"] = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(signatureBase)));
        return "OAuth " + string.Join(", ", p.Select(k => E(k.Key) + "=\"" + E(k.Value) + "\""));
    }
    public async Task<string> X(Settings s, string text)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.x.com/2/tweets")
        { Content = new StringContent(JsonSerializer.Serialize(new { text }), Encoding.UTF8, "application/json") };
        request.Headers.TryAddWithoutValidation("Authorization", OAuthHeader(s, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)));
        using var response = await net.Send(request);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("data").GetProperty("id").GetString()!;
    }
    public async Task<Dictionary<string, decimal>> Holdings(Rpc rpc, string wallet)
    {
        var holdings = new Dictionary<string, decimal>();
        foreach (var program in new[] { Sol.Token, Sol.Token2022 })
        {
            var result = await rpc.Call("getTokenAccountsByOwner", [wallet, new { programId = program }, new { encoding = "jsonParsed", commitment = "confirmed" }]);
            foreach (var item in result.GetProperty("value").EnumerateArray())
            {
                var info = item.GetProperty("account").GetProperty("data").GetProperty("parsed").GetProperty("info");
                string mint = info.GetProperty("mint").GetString()!;
                var quantity = info.GetProperty("tokenAmount").GetProperty("uiAmountString").GetString();
                if (decimal.TryParse(quantity, NumberStyles.Float, CultureInfo.InvariantCulture, out var amount))
                    holdings[mint] = holdings.GetValueOrDefault(mint) + amount;
            }
        }
        return holdings;
    }
    public async Task<Dictionary<string, Market>> Markets(IEnumerable<string> mints)
    {
        var markets = new Dictionary<string, Market>();
        foreach (var group in mints.Distinct().Chunk(30))
        {
            foreach (var mint in group) Sol.AddressBytes(mint);
            var result = await net.Json(HttpMethod.Get, "https://api.dexscreener.com/tokens/v1/solana/" + string.Join(",", group));
            foreach (var pair in result.EnumerateArray())
            {
                string mint = pair.GetProperty("baseToken").GetProperty("address").GetString()!;
                if (!group.Contains(mint)) continue;
                decimal Number(JsonElement obj, string property)
                {
                    if (!obj.TryGetProperty(property, out var value)) return 0;
                    return decimal.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : 0;
                }
                decimal liquidity = pair.TryGetProperty("liquidity", out var l) ? Number(l, "usd") : 0;
                if (!markets.TryGetValue(mint, out var prior) || liquidity > prior.Liquidity)
                    markets[mint] = new Market(Number(pair, "priceUsd"), Number(pair, "marketCap"),
                        pair.TryGetProperty("volume", out var volume) ? Number(volume, "h24") : 0, liquidity);
            }
        }
        return markets;
    }
}
public sealed record Market(decimal PriceUsd, decimal MarketCapUsd, decimal Volume24Usd, decimal Liquidity);
