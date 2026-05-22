using NArk.Abstractions.Safety;
using NArk.Core.Transport;
using NArk.Swaps.Abstractions;
using NArk.Swaps.Boltz;
using NArk.Swaps.Models;
using NArk.Swaps.Services;
using NBitcoin;

namespace cdk_arkade_payment_processor.Services;

public sealed record OnchainIncomingPayment(
    string SwapId,
    string BtcAddress,
    long ExpectedAmountSats,
    ArkSwapStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record OnchainOutgoingPayment(
    string SwapId,
    string BtcDestination,
    long AmountSats,
    ArkSwapStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed class ArkSwapOnchainService(
    IClientTransport clientTransport,
    SwapsManagementService swapsManagementService,
    BoltzLimitsValidator boltzLimitsValidator,
    ISwapStorage swapStorage,
    ISafetyService safetyService,
    ILogger<ArkSwapOnchainService> logger)
{
    public async Task<OnchainIncomingPayment> CreateIncomingSwap(string walletId, long amountSats, CancellationToken ct)
    {
        var limits = await boltzLimitsValidator.GetChainLimitsAsync(isBtcToArk: true, ct);
        ValidateAmount(amountSats, limits, "incoming chain");

        var (btcAddress, swapId, expectedLockupSats) =
            await swapsManagementService.InitiateBtcToArkChainSwap(walletId, amountSats, ct);

        return new OnchainIncomingPayment(
            SwapId: swapId,
            BtcAddress: btcAddress,
            ExpectedAmountSats: expectedLockupSats,
            Status: ArkSwapStatus.Pending,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow);
    }

    public async Task<OnchainOutgoingPayment> InitiateOutgoingSwap(
        string walletId,
        long amountSats,
        string btcAddress,
        CancellationToken ct)
    {
        var serverInfo = await clientTransport.GetServerInfoAsync(ct);
        var destination = BitcoinAddress.Create(btcAddress, serverInfo.Network);

        var limits = await boltzLimitsValidator.GetChainLimitsAsync(isBtcToArk: false, ct);
        ValidateAmount(amountSats, limits, "outgoing chain");

        var lockKey = $"onchain-pay:{walletId}:{btcAddress}:{amountSats}";
        await using var _ = await safetyService.LockKeyAsync(lockKey, ct);

        var swapId = await swapsManagementService.InitiateArkToBtcChainSwap(walletId, amountSats, destination, ct);
        var now = DateTimeOffset.UtcNow;

        return new OnchainOutgoingPayment(
            SwapId: swapId,
            BtcDestination: btcAddress,
            AmountSats: amountSats,
            Status: ArkSwapStatus.Pending,
            CreatedAt: now,
            UpdatedAt: now);
    }

    public async Task<OnchainIncomingPayment?> GetIncomingBySwapId(string walletId, string swapId, CancellationToken ct)
    {
        var swaps = await swapStorage.GetSwaps(
            walletIds: [walletId],
            swapIds: [swapId],
            swapTypes: [ArkSwapType.ChainBtcToArk],
            cancellationToken: ct);
        var swap = swaps.FirstOrDefault();
        return swap is null ? null : MapIncoming(swap);
    }

    public async Task<OnchainOutgoingPayment?> GetOutgoingBySwapId(string walletId, string swapId, CancellationToken ct)
    {
        var swaps = await swapStorage.GetSwaps(
            walletIds: [walletId],
            swapIds: [swapId],
            swapTypes: [ArkSwapType.ChainArkToBtc],
            cancellationToken: ct);
        var swap = swaps.FirstOrDefault();
        return swap is null ? null : MapOutgoing(swap);
    }

    public Task<BoltzLimits?> GetIncomingLimitsAsync(CancellationToken ct)
        => boltzLimitsValidator.GetChainLimitsAsync(isBtcToArk: true, ct);

    public Task<BoltzLimits?> GetOutgoingLimitsAsync(CancellationToken ct)
        => boltzLimitsValidator.GetChainLimitsAsync(isBtcToArk: false, ct);

    private static OnchainIncomingPayment MapIncoming(ArkSwap swap) =>
        new(swap.SwapId, swap.Address, swap.ExpectedAmount, swap.Status, swap.CreatedAt, swap.UpdatedAt);

    private static OnchainOutgoingPayment MapOutgoing(ArkSwap swap) =>
        new(
            swap.SwapId,
            swap.Get(SwapMetadata.BtcAddress) ?? string.Empty,
            swap.ExpectedAmount,
            swap.Status,
            swap.CreatedAt,
            swap.UpdatedAt);

    private static void ValidateAmount(long amountSats, BoltzLimits? limits, string swapType)
    {
        if (limits is null) return;
        if (amountSats < limits.MinAmount)
            throw new PaymentValidationException(
                $"Amount {amountSats} sats is below minimum {limits.MinAmount} sats for {swapType} swap");
        if (amountSats > limits.MaxAmount)
            throw new PaymentValidationException(
                $"Amount {amountSats} sats exceeds maximum {limits.MaxAmount} sats for {swapType} swap");
    }
}
