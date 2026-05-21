using cdk_arkade_payment_processor.Configuration;
using cdk_arkade_payment_processor.Services;
using Grpc.Core;
using Microsoft.Extensions.Options;
using NArk.Abstractions.Wallets;
using NArk.Core.Transport;
using NArk.Core.Wallet;

var builder = WebApplication.CreateBuilder(args);

LoadDotEnv(builder.Environment.ContentRootPath);

builder.Services.AddArkadePaymentProcessor(builder.Configuration);

var app = builder.Build();

await app.Services.GetRequiredService<MigrationRunner>().ExecuteAsync();
await EnsureProcessorWalletAsync(app.Services);

app.Use(async (ctx, next) =>
{
    if (!ctx.ValidateVersion())
    {
        throw new RpcException(new Status(StatusCode.FailedPrecondition,
                $"Invalid protocol version! Expected: {VersionValidatoor.ProtocolVersion}"));
    }
    await next();
});

app.MapGrpcService<CdkPaymentProcessorGrpcService>();
app.MapGet("/", () => "CDK Arkade payment processor gRPC server");

await app.RunAsync();

static void LoadDotEnv(string rootPath)
{
    var envPath = Path.Combine(rootPath, ".env");
    if (!File.Exists(envPath))
    {
        return;
    }

    foreach (var rawLine in File.ReadAllLines(envPath))
    {
        var line = rawLine.Trim();
        if (line.Length == 0 || line.StartsWith('#'))
        {
            continue;
        }

        var separatorIndex = line.IndexOf('=');
        if (separatorIndex <= 0)
        {
            continue;
        }

        var key = line[..separatorIndex].Trim();
        var value = line[(separatorIndex + 1)..].Trim();
        if ((value.StartsWith('"') && value.EndsWith('"')) || (value.StartsWith('\'') && value.EndsWith('\'')))
        {
            value = value[1..^1];
        }

        if (Environment.GetEnvironmentVariable(key) is null)
            Environment.SetEnvironmentVariable(key, value);
    }
}

static async Task EnsureProcessorWalletAsync(IServiceProvider services)
{
    using var scope = services.CreateScope();
    var options = scope.ServiceProvider.GetRequiredService<IOptions<ProcessorOptions>>().Value;
    var walletStorage = scope.ServiceProvider.GetRequiredService<IWalletStorage>();

    var existing = await walletStorage.GetWalletById(options.WalletId);
    if (existing is not null)
    {
        return;
    }

    if (string.IsNullOrWhiteSpace(options.WalletSecret))
    {
        throw new InvalidOperationException(
            $"Wallet '{options.WalletId}' not found and Processor:WalletSecret is not configured.");
    }

    var transport = scope.ServiceProvider.GetRequiredService<IClientTransport>();
    var serverInfo = await transport.GetServerInfoAsync();
    var destination = string.IsNullOrWhiteSpace(options.FundingAddress) ? null : options.FundingAddress;
    var wallet = await WalletFactory.CreateWallet(options.WalletSecret, destination, serverInfo);
    wallet = wallet with { Id = options.WalletId };
    await walletStorage.UpsertWallet(wallet, updateIfExists: false);
}
