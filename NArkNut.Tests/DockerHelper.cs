using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using CliWrap;
using CliWrap.Buffered;

namespace cdk_arkade_payment_processor.Tests;

public static class DockerHelper
{
    private static async Task<string> Exec(string container, string[] args, CancellationToken ct = default)
    {
        var result = await Cli.Wrap("docker")
            .WithArguments(["exec", container, .. args])
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(ct);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"docker exec failed (container={container}, exitCode={result.ExitCode}). " +
                $"stderr: {result.StandardError.Trim()} stdout: {result.StandardOutput.Trim()}");
        }

        return result.StandardOutput;
    }

    public static async Task MineBlocks(int count = 6, CancellationToken ct = default)
        => await Exec("bitcoin",
            ["bitcoin-cli", "-rpcwallet=", "-generate", count.ToString(CultureInfo.InvariantCulture)], ct);

    public static async Task<string> SendBitcoinToAddress(string address, decimal amountBtc = 1m, CancellationToken ct = default)
        => (await Exec("bitcoin",
            ["bitcoin-cli", "-rpcwallet=", "sendtoaddress", address, amountBtc.ToString("F8", CultureInfo.InvariantCulture)], ct)).Trim();

    public static async Task<string> GetNewBitcoinAddress(CancellationToken ct = default)
        => (await Exec("bitcoin", ["bitcoin-cli", "-rpcwallet=", "getnewaddress"], ct)).Trim();

    public static async Task<decimal> GetReceivedByAddress(string address, int minConf = 0, CancellationToken ct = default)
    {
        var output = await Exec("bitcoin",
            ["bitcoin-cli", "-rpcwallet=", "getreceivedbyaddress", address, minConf.ToString(CultureInfo.InvariantCulture)], ct);
        return decimal.TryParse(output.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0m;
    }
    public static async Task<string> CreateLndInvoice(long amtSats = 10000, int expirySecs = 30,
        CancellationToken ct = default)
    {
        var args = new List<string>
        {
            "lncli", "--network=regtest", "addinvoice", "--amt", amtSats.ToString()
        };
        if (expirySecs > 0)
        {
            args.AddRange(["--expiry", expirySecs.ToString(CultureInfo.InvariantCulture)]);
        }

        var output = await Exec("lnd", args.ToArray(), ct);
        var invoice = JsonSerializer.Deserialize<JsonObject>(output)?["payment_request"]
                          ?.GetValue<string>()
                      ?? throw new InvalidOperationException($"Invoice creation on LND failed. Output: {output}");
        return invoice.Trim();
    }
    
    public static async Task PayLndInvoice(
        string invoice,
        CancellationToken ct = default)
    {
        var payerContainer = await ResolvePayerContainer(invoice, ct);

        try
        {
            await Exec(
                payerContainer,
                ["lncli", "--network=regtest", "payinvoice", "--force", invoice],
                ct);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("self-payments not allowed", StringComparison.OrdinalIgnoreCase))
        {
            await Exec(
                payerContainer,
                ["lncli", "--network=regtest", "payinvoice", "--force", "--allow_self_payment", invoice],
                ct);
        }
    }
    
    public static async Task<bool> IsLndInvoicePaid(
        string paymentHashHex,
        string lookupContainer = "lnd",
        CancellationToken ct = default)
    {
        string output;
        try
        {
            output = await Exec(lookupContainer, new[]
            {
                "lncli", "--network=regtest", "lookupinvoice", paymentHashHex
            }, ct);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("unable to locate invoice", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var json = JsonSerializer.Deserialize<JsonObject>(output)
                   ?? throw new InvalidOperationException($"Invalid lookupinvoice output: {output}");

        return json["state"]?.GetValue<string>()?.Equals("SETTLED", StringComparison.OrdinalIgnoreCase) == true;
    }

    public static async Task<bool> IsLndInvoicePaidByBolt11(string bolt11, CancellationToken ct = default)
    {
        var decoded = await Exec("lnd", ["lncli", "--network=regtest", "decodepayreq", bolt11], ct);
        var paymentHash = JsonSerializer.Deserialize<JsonObject>(decoded)?["payment_hash"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(paymentHash))
        {
            throw new InvalidOperationException($"Could not decode payment_hash from invoice. Output: {decoded}");
        }

        var destination = JsonSerializer.Deserialize<JsonObject>(decoded)?["destination"]?.GetValue<string>();
        var lookupContainer = await ResolveInvoiceContainer(destination, ct);
        return await IsLndInvoicePaid(paymentHash, lookupContainer, ct);
    }

    public static async Task WaitForLndInvoicePaid(
        string paymentHashHex,
        TimeSpan? timeout = null,
        TimeSpan? pollInterval = null,
        CancellationToken ct = default)
    {
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(45);
        var effectivePoll = pollInterval ?? TimeSpan.FromMilliseconds(500);
        var deadline = DateTimeOffset.UtcNow + effectiveTimeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            if (await IsLndInvoicePaid(paymentHashHex, "lnd", ct))
            {
                return;
            }

            await Task.Delay(effectivePoll, ct);
        }

        throw new TimeoutException($"Invoice was not settled within {effectiveTimeout.TotalSeconds:F0}s. payment_hash={paymentHashHex}");
    }

    public static async Task WaitForLndInvoicePaidByBolt11(
        string bolt11,
        TimeSpan? timeout = null,
        TimeSpan? pollInterval = null,
        CancellationToken ct = default)
    {
        var decoded = await Exec("lnd", ["lncli", "--network=regtest", "decodepayreq", bolt11], ct);
        var paymentHash = JsonSerializer.Deserialize<JsonObject>(decoded)?["payment_hash"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(paymentHash))
        {
            throw new InvalidOperationException($"Could not decode payment_hash from invoice. Output: {decoded}");
        }

        var destination = JsonSerializer.Deserialize<JsonObject>(decoded)?["destination"]?.GetValue<string>();
        var lookupContainer = await ResolveInvoiceContainer(destination, ct);

        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(45);
        var effectivePoll = pollInterval ?? TimeSpan.FromMilliseconds(500);
        var deadline = DateTimeOffset.UtcNow + effectiveTimeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            if (await IsLndInvoicePaid(paymentHash, lookupContainer, ct))
            {
                return;
            }

            await Task.Delay(effectivePoll, ct);
        }

        throw new TimeoutException(
            $"Invoice was not settled within {effectiveTimeout.TotalSeconds:F0}s. payment_hash={paymentHash}, lookup_container={lookupContainer}");
    }

    private static async Task<string> ResolveInvoiceContainer(string? destinationPubkey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(destinationPubkey))
        {
            return "lnd";
        }

        var lndInfo = await Exec("lnd", ["lncli", "--network=regtest", "getinfo"], ct);
        var lndPubkey = JsonSerializer.Deserialize<JsonObject>(lndInfo)?["identity_pubkey"]?.GetValue<string>();
        if (string.Equals(destinationPubkey, lndPubkey, StringComparison.OrdinalIgnoreCase))
        {
            return "lnd";
        }

        var boltzInfo = await Exec("boltz-lnd", ["lncli", "--network=regtest", "getinfo"], ct);
        var boltzPubkey = JsonSerializer.Deserialize<JsonObject>(boltzInfo)?["identity_pubkey"]?.GetValue<string>();
        if (string.Equals(destinationPubkey, boltzPubkey, StringComparison.OrdinalIgnoreCase))
        {
            return "boltz-lnd";
        }

        return "lnd";
    }

    private static async Task<string> ResolvePayerContainer(string bolt11, CancellationToken ct)
    {
        var decoded = await Exec("lnd", ["lncli", "--network=regtest", "decodepayreq", bolt11], ct);
        var destination = JsonSerializer.Deserialize<JsonObject>(decoded)?["destination"]?.GetValue<string>();
        var invoiceContainer = await ResolveInvoiceContainer(destination, ct);
        return string.Equals(invoiceContainer, "lnd", StringComparison.OrdinalIgnoreCase) ? "boltz-lnd" : "lnd";
    }
}
