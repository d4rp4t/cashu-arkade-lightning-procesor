using System.Collections.Concurrent;
using System.Threading.Channels;
using NArk.Swaps.Abstractions;
using NArk.Swaps.Models;

namespace cdk_arkade_payment_processor.Services;

public sealed class IncomingPaymentEventBus : IDisposable
{
    private readonly ConcurrentDictionary<Guid, Channel<ArkSwap>> _subscribers = new();
    private readonly ISwapStorage _swapStorage;

    public IncomingPaymentEventBus(ISwapStorage swapStorage)
    {
        _swapStorage = swapStorage;
        _swapStorage.SwapsChanged += OnSwapChanged;
    }

    public IAsyncEnumerable<ArkSwap> Subscribe(CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        var channel = Channel.CreateUnbounded<ArkSwap>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        _subscribers[id] = channel;
        return ReadAndCleanup(id, channel, cancellationToken);
    }

    private async IAsyncEnumerable<ArkSwap> ReadAndCleanup(
        Guid id,
        Channel<ArkSwap> channel,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var swap in channel.Reader.ReadAllAsync(cancellationToken))
            {
                yield return swap;
            }
        }
        finally
        {
            _subscribers.TryRemove(id, out _);
        }
    }

    private void OnSwapChanged(object? _, ArkSwap swap)
    {
        if (swap.Status != ArkSwapStatus.Settled)
            return;
        if (swap.SwapType != ArkSwapType.ReverseSubmarine && swap.SwapType != ArkSwapType.ChainBtcToArk)
            return;

        foreach (var subscriber in _subscribers.Values)
        {
            subscriber.Writer.TryWrite(swap);
        }
    }

    public void Dispose()
    {
        _swapStorage.SwapsChanged -= OnSwapChanged;

        foreach (var channel in _subscribers.Values)
        {
            channel.Writer.TryComplete();
        }

        _subscribers.Clear();
    }
}
