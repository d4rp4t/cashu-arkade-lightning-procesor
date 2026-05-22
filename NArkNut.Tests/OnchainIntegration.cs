using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DotNut;
using DotNut.Abstractions;
using DotNut.Api;
using DotNut.ApiModels;

namespace cdk_arkade_payment_processor.Tests;

public class OnchainIntegration
{
    private const string MintUrl = "http://localhost:3338";
    private const string FulmineUrl = "http://localhost:7003";
    private const ulong MintAmountSats = 50_000;
    private const ulong MeltAmountSats = 40_000;

    [Fact]
    public async Task GetSettings_IncludesOnchainMethod()
    {
        using var http = new HttpClient { BaseAddress = new Uri(MintUrl), Timeout = TimeSpan.FromSeconds(15) };
        var info = JsonNode.Parse(await http.GetStringAsync("/v1/info"));

        var mintMethods = info?["nuts"]?["4"]?["methods"]?.AsArray();
        Assert.NotNull(mintMethods);
        Assert.Contains(mintMethods, m =>
            string.Equals(m?["method"]?.GetValue<string>(), "btconchain", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task CanMintOnchain_BtcToArk()
    {
        await EnsureFulmineLiquidity();

        using var http = new HttpClient { BaseAddress = new Uri(MintUrl), Timeout = TimeSpan.FromSeconds(30) };

        var quoteResp = await http.PostAsync(
            "/v1/mint/quote/btconchain",
            Json(new { amount = MintAmountSats, unit = "sat" }));
        quoteResp.EnsureSuccessStatusCode();

        var quoteJson = JsonNode.Parse(await quoteResp.Content.ReadAsStringAsync());
        var quoteId = quoteJson?["quote"]?.GetValue<string>()
            ?? throw new InvalidOperationException("Missing quote id in mint quote response");
        var btcAddress = quoteJson?["request"]?.GetValue<string>()
            ?? throw new InvalidOperationException("Missing BTC address in mint quote response");

        // Add buffer for Boltz chain swap fees on top of the requested mint amount
        var lockupBtc = (decimal)(MintAmountSats + 5_000) / 100_000_000m;
        await DockerHelper.SendBitcoinToAddress(btcAddress, lockupBtc);
        await DockerHelper.MineBlocks(6);

        var paid = await WaitForMintQuotePaid(http, quoteId, TimeSpan.FromSeconds(120));
        Assert.True(paid, $"Mint quote {quoteId} did not reach PAID state within timeout");
    }

    [Fact]
    public async Task CanMeltOnchain_ArkToBtc()
    {
        await EnsureFulmineLiquidity();

        // Fund the processor wallet by minting tokens via lightning
        var proofs = await MintTokensViaLightning(MintAmountSats);

        var destinationAddress = await DockerHelper.GetNewBitcoinAddress();

        using var http = new HttpClient { BaseAddress = new Uri(MintUrl), Timeout = TimeSpan.FromSeconds(30) };

        var meltQuoteResp = await http.PostAsync(
            "/v1/melt/quote/btconchain",
            Json(new { request = destinationAddress, unit = "sat", amount = MeltAmountSats }));
        meltQuoteResp.EnsureSuccessStatusCode();

        var meltQuoteJson = JsonNode.Parse(await meltQuoteResp.Content.ReadAsStringAsync());
        var meltQuoteId = meltQuoteJson?["quote"]?.GetValue<string>()
            ?? throw new InvalidOperationException("Missing quote id in melt quote response");

        var meltResp = await http.PostAsync(
            "/v1/melt/btconchain",
            Json(new { quote = meltQuoteId, inputs = proofs }));
        meltResp.EnsureSuccessStatusCode();

        await DockerHelper.MineBlocks(6);

        var received = await WaitForBtcReceived(destinationAddress, TimeSpan.FromSeconds(180));
        Assert.True(received > 0m, $"No BTC received at {destinationAddress} within timeout");
    }

    private static async Task<bool> WaitForMintQuotePaid(HttpClient http, string quoteId, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                var body = await http.GetStringAsync($"/v1/mint/quote/btconchain/{quoteId}");
                var state = JsonNode.Parse(body)?["state"]?.GetValue<string>();
                if (string.Equals(state, "PAID", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            catch { }
            await Task.Delay(2000);
        }
        return false;
    }

    private static async Task<decimal> WaitForBtcReceived(string address, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var received = await DockerHelper.GetReceivedByAddress(address);
            if (received > 0m) return received;
            await DockerHelper.MineBlocks(1);
            await Task.Delay(2000);
        }
        return 0m;
    }

    private static async Task<IReadOnlyList<Proof>> MintTokensViaLightning(ulong amountSats)
    {
        var wallet = Wallet.Create().WithMint(MintUrl);
        IMintHandler<PostMintQuoteBolt11Response, List<Proof>>? mintHandler = null;
        Exception? lastError = null;

        for (var i = 0; i < 5; i++)
        {
            try
            {
                mintHandler = await wallet.CreateMintQuote().WithAmount(amountSats).ProcessAsyncBolt11();
                break;
            }
            catch (Exception ex)
            {
                lastError = ex;
                await Task.Delay(1500);
            }
        }

        if (mintHandler is null)
            throw new InvalidOperationException("Could not create lightning mint quote.", lastError);

        await DockerHelper.PayLndInvoice(mintHandler.GetQuote().Request);

        for (var i = 0; i < 240; i++)
        {
            try
            {
                return await mintHandler.Mint();
            }
            catch (CashuProtocolException ex) when (ex.Message.Contains("Quote not paid", StringComparison.OrdinalIgnoreCase))
            {
                await Task.Delay(500);
            }
        }

        throw new TimeoutException("Lightning mint quote did not transition to paid state in time.");
    }

    private static async Task EnsureFulmineLiquidity(long minBalanceSats = 200_000, int maxAttempts = 10)
    {
        using var http = new HttpClient { BaseAddress = new Uri(FulmineUrl), Timeout = TimeSpan.FromSeconds(15) };

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            try
            {
                var json = JsonNode.Parse(await http.GetStringAsync("/api/v1/balance"));
                var value = json?["offchain"] ?? json?["amount"];
                if (long.TryParse(value?.ToString(), out var balance) && balance >= minBalanceSats)
                    return;
            }
            catch { }

            await FundFulmine(http);
            for (var i = 0; i < 6; i++) await DockerHelper.MineBlocks();
            await Task.Delay(TimeSpan.FromSeconds(2));
            try { await http.GetAsync("/api/v1/settle"); } catch { }
            await Task.Delay(TimeSpan.FromSeconds(15));
            for (var i = 0; i < 6; i++) await DockerHelper.MineBlocks();
            await Task.Delay(TimeSpan.FromSeconds(3));
        }
    }

    private static async Task FundFulmine(HttpClient http)
    {
        try
        {
            var json = JsonNode.Parse(await http.GetStringAsync("/api/v1/address"));
            var arkAddress = json?["address"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(arkAddress)) return;

            var onchainAddress = Uri.TryCreate(arkAddress, UriKind.Absolute, out var uri)
                ? uri.AbsolutePath.TrimStart('/')
                : arkAddress;

            await DockerHelper.SendBitcoinToAddress(onchainAddress);
        }
        catch { }
    }

    private static StringContent Json(object body) =>
        new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
}
