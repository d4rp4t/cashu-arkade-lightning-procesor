using BTCPayServer.Lightning;
using Grpc.Core;
using cdk_arkade_payment_processor.Configuration;
using NArk.Swaps.Models;
using NArk.Swaps.Boltz;
using Proto = global::CdkPaymentProcessor;

namespace cdk_arkade_payment_processor.Services;

public sealed class CdkPaymentProcessorGrpcService : Proto.CdkPaymentProcessor.CdkPaymentProcessorBase
{
    private readonly ProcessorContext _context;
    private readonly ArkSwapLightningService _lightning;
    private readonly ArkSwapOnchainService _onchain;
    private readonly IncomingPaymentEventBus _incomingPaymentEvents;
    private readonly ILogger<CdkPaymentProcessorGrpcService> _logger;
    private readonly NBitcoin.Network _network;

    public CdkPaymentProcessorGrpcService(
        ProcessorContext context,
        ArkSwapLightningService lightning,
        ArkSwapOnchainService onchain,
        IncomingPaymentEventBus incomingPaymentEvents,
        Microsoft.Extensions.Options.IOptions<NbxplorerOptions> nbxplorerOptions,
        ILogger<CdkPaymentProcessorGrpcService> logger)
    {
        _context = context;
        _lightning = lightning;
        _onchain = onchain;
        _incomingPaymentEvents = incomingPaymentEvents;
        _network = NetworkParser.Parse(nbxplorerOptions.Value.Network);
        _logger = logger;
    }

    public override async Task<Proto.SettingsResponse> GetSettings(
        Proto.EmptyRequest request,
        ServerCallContext context)
    {
        var response = new Proto.SettingsResponse
        {
            Unit = _context.Options.Unit,
            Bolt11 = new Proto.Bolt11Settings
            {
                Amountless = false,
                InvoiceDescription = true
            }
        };

        try
        {
            var outgoing = await _lightning.GetOutgoingLimitsAsync(context.CancellationToken);
            var incoming = await _lightning.GetIncomingLimitsAsync(context.CancellationToken);
            if (outgoing is not null)
            {
                response.Custom["melt_min_sat"] = outgoing.MinAmount.ToString();
                response.Custom["melt_max_sat"] = outgoing.MaxAmount.ToString();
            }
            if (incoming is not null)
            {
                response.Custom["mint_min_sat"] = incoming.MinAmount.ToString();
                response.Custom["mint_max_sat"] = incoming.MaxAmount.ToString();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not fetch Boltz limits for GetSettings");
        }

        try
        {
            var inTask = _onchain.GetIncomingLimitsAsync(context.CancellationToken);
            var outTask = _onchain.GetOutgoingLimitsAsync(context.CancellationToken);
            await Task.WhenAll(inTask, outTask);
            var inLimits = inTask.Result;
            var outLimits = outTask.Result;
            if (inLimits is not null || outLimits is not null)
            {
                response.Onchain = new Proto.OnchainSettings
                {
                    Confirmations = 1,
                    MinReceiveAmountSat = (ulong)(inLimits?.MinAmount ?? 0),
                    MinSendAmountSat = (ulong)(outLimits?.MinAmount ?? 0)
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not fetch chain swap limits for GetSettings");
        }

        return response;
    }

    public override async Task<Proto.CreatePaymentResponse> CreatePayment(
        Proto.CreatePaymentRequest request,
        ServerCallContext context)
    {
        try
        {
            if (request.Options?.Onchain is not null)
            {
                var opts = request.Options.Onchain;
                var amount = (long)(opts.Amount?.Value ?? 0);
                if (amount == 0) throw BadRequest("Amount is required for onchain incoming payment");

                var result = await _onchain.CreateIncomingSwap(
                    _context.Options.WalletId, amount, context.CancellationToken);

                return new Proto.CreatePaymentResponse
                {
                    RequestIdentifier = new Proto.PaymentIdentifier
                    {
                        Type = Proto.PaymentIdentifierType.QuoteId,
                        Id = result.SwapId
                    },
                    Request = result.BtcAddress
                };
            }

            var bolt11 = request.Options?.Bolt11 ?? throw BadRequest("Only bolt11 and onchain incoming options are supported");
            var bolt11Amount = bolt11.Amount?.Value ?? 0;
            if (bolt11Amount == 0) throw BadRequest("Amount is required");

            var expirySeconds = bolt11.HasUnixExpiry
                ? Math.Max(60, (long)bolt11.UnixExpiry - DateTimeOffset.UtcNow.ToUnixTimeSeconds())
                : 3600;

            var invoice = await _lightning.CreateInvoice(
                _context.Options.WalletId,
                (long)bolt11Amount,
                bolt11.Description ?? string.Empty,
                TimeSpan.FromSeconds(expirySeconds),
                context.CancellationToken);

            var invoiceHash = invoice.PaymentHash
                ?? throw new InvalidOperationException("Created invoice has no payment hash.");

            return new Proto.CreatePaymentResponse
            {
                RequestIdentifier = new Proto.PaymentIdentifier
                {
                    Type = Proto.PaymentIdentifierType.PaymentHash,
                    Hash = invoiceHash
                },
                Request = invoice.BOLT11,
                Expiry = (ulong)invoice.ExpiresAt.ToUnixTimeSeconds()
            };
        }
        catch (RpcException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (PaymentValidationException ex)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CreatePayment failed for wallet {WalletId}", _context.Options.WalletId);
            throw new RpcException(new Status(StatusCode.Internal, $"CreatePayment failed: {ex.Message}"));
        }
    }

    public override async Task<Proto.PaymentQuoteResponse> GetPaymentQuote(
        Proto.PaymentQuoteRequest request,
        ServerCallContext context)
    {
        try
        {
            if (request.RequestType == Proto.OutgoingPaymentRequestType.Onchain)
            {
                var opts = request.OnchainOptions
                    ?? throw BadRequest("onchain_options is required for onchain quote");
                var amountSats = (long)(opts.Amount?.Value ?? 0);
                if (amountSats == 0) throw BadRequest("Amount is required");

                var limits = await _onchain.GetOutgoingLimitsAsync(context.CancellationToken);
                if (limits is not null)
                {
                    if (amountSats < limits.MinAmount)
                        throw new PaymentValidationException(
                            $"Amount {amountSats} sats is below minimum {limits.MinAmount} sats for onchain send");
                    if (amountSats > limits.MaxAmount)
                        throw new PaymentValidationException(
                            $"Amount {amountSats} sats exceeds maximum {limits.MaxAmount} sats for onchain send");
                }

                var feeSats = limits is not null ? CalculateFeeSats(amountSats, limits) : 0UL;

                var quoteResponse = new Proto.PaymentQuoteResponse
                {
                    RequestIdentifier = new Proto.PaymentIdentifier
                    {
                        Type = Proto.PaymentIdentifierType.QuoteId,
                        Id = request.QuoteId
                    },
                    Amount = new Proto.AmountMessage { Value = (ulong)amountSats, Unit = _context.Options.Unit },
                    Fee = new Proto.AmountMessage { Value = feeSats, Unit = _context.Options.Unit },
                    State = Proto.QuoteState.Issued
                };
                quoteResponse.FeeOptions.Add(new Proto.OnchainFeeOption
                {
                    FeeReserve = feeSats,
                    EstimatedBlocks = 2,
                    FeeIndex = 0
                });
                return quoteResponse;
            }

            if (request.RequestType != Proto.OutgoingPaymentRequestType.Bolt11Invoice)
                throw BadRequest("Only bolt11 and onchain outgoing quote is supported");

            var pr = BOLT11PaymentRequest.Parse(request.Request, _network);
            var bolt11AmountSats = (long)(pr.MinimumAmount?.ToUnit(LightMoneyUnit.Satoshi) ?? 0);
            var paymentHash = pr.PaymentHash?.ToString()
                ?? throw BadRequest("Invoice has no payment hash.");

            var bolt11Limits = await _lightning.GetOutgoingLimitsAsync(context.CancellationToken);
            if (bolt11Limits is not null)
            {
                if (bolt11AmountSats < bolt11Limits.MinAmount)
                    throw new PaymentValidationException($"Amount {bolt11AmountSats} sats is below minimum {bolt11Limits.MinAmount} sats for melt");
                if (bolt11AmountSats > bolt11Limits.MaxAmount)
                    throw new PaymentValidationException($"Amount {bolt11AmountSats} sats exceeds maximum {bolt11Limits.MaxAmount} sats for melt");
            }

            var bolt11FeeSats = bolt11Limits is not null ? CalculateFeeSats(bolt11AmountSats, bolt11Limits) : 0UL;

            return new Proto.PaymentQuoteResponse
            {
                RequestIdentifier = new Proto.PaymentIdentifier
                {
                    Type = Proto.PaymentIdentifierType.PaymentHash,
                    Hash = paymentHash
                },
                Amount = new Proto.AmountMessage { Value = (ulong)bolt11AmountSats, Unit = _context.Options.Unit },
                Fee = new Proto.AmountMessage { Value = bolt11FeeSats, Unit = _context.Options.Unit },
                State = Proto.QuoteState.Issued
            };
        }
        catch (RpcException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (PaymentValidationException ex)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetPaymentQuote failed");
            throw new RpcException(new Status(StatusCode.Internal, $"GetPaymentQuote failed: {ex.Message}"));
        }
    }

    public override async Task<Proto.MakePaymentResponse> MakePayment(
        Proto.MakePaymentRequest request,
        ServerCallContext context)
    {
        try
        {
            if (request.PaymentOptions?.Onchain is not null)
            {
                var opts = request.PaymentOptions.Onchain;
                var amountSats = (long)(opts.Amount?.Value ?? 0);
                if (string.IsNullOrWhiteSpace(opts.Address)) throw BadRequest("Address is required for onchain payment");
                if (amountSats == 0) throw BadRequest("Amount is required for onchain payment");

                var payment = await _onchain.InitiateOutgoingSwap(
                    _context.Options.WalletId, amountSats, opts.Address, context.CancellationToken);
                return MapOnchainOutgoing(payment);
            }

            var bolt11 = request.PaymentOptions?.Bolt11?.Bolt11;
            if (string.IsNullOrWhiteSpace(bolt11)) throw BadRequest("Only bolt11 and onchain outgoing payment is supported");

            var lightningPayment = await _lightning.PayInvoice(_context.Options.WalletId, bolt11, context.CancellationToken);
            return MapLightningOutgoing(lightningPayment);
        }
        catch (RpcException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (PaymentValidationException ex)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MakePayment failed for wallet {WalletId}", _context.Options.WalletId);
            throw new RpcException(new Status(StatusCode.Internal, $"MakePayment failed: {ex.Message}"));
        }
    }

    public override async Task<Proto.CheckIncomingPaymentResponse> CheckIncomingPayment(
        Proto.CheckIncomingPaymentRequest request,
        ServerCallContext context)
    {
        try
        {
            var response = new Proto.CheckIncomingPaymentResponse();

            if (request.RequestIdentifier.Type == Proto.PaymentIdentifierType.QuoteId)
            {
                var payment = await _onchain.GetIncomingBySwapId(
                    _context.Options.WalletId, request.RequestIdentifier.Id, context.CancellationToken);
                if (payment?.Status == ArkSwapStatus.Settled)
                    response.Payments.Add(MapOnchainIncoming(payment));
            }
            else
            {
                var invoice = await ResolveIncoming(request.RequestIdentifier, context.CancellationToken);
                if (invoice?.Status == LightningInvoiceStatus.Paid)
                    response.Payments.Add(MapIncomingPaymentFromInvoice(invoice));
            }

            return response;
        }
        catch (RpcException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CheckIncomingPayment failed for wallet {WalletId}", _context.Options.WalletId);
            throw new RpcException(new Status(StatusCode.Internal, $"CheckIncomingPayment failed: {ex.Message}"));
        }
    }

    public override async Task<Proto.MakePaymentResponse> CheckOutgoingPayment(
        Proto.CheckOutgoingPaymentRequest request,
        ServerCallContext context)
    {
        try
        {
            if (request.RequestIdentifier.Type == Proto.PaymentIdentifierType.QuoteId)
            {
                var payment = await _onchain.GetOutgoingBySwapId(
                    _context.Options.WalletId, request.RequestIdentifier.Id, context.CancellationToken);
                return payment is null ? UnknownOutgoingResponse() : MapOnchainOutgoing(payment);
            }

            var lightningPayment = await ResolveOutgoing(request.RequestIdentifier, context.CancellationToken);
            return lightningPayment is null ? UnknownOutgoingResponse() : MapLightningOutgoing(lightningPayment);
        }
        catch (RpcException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CheckOutgoingPayment failed for wallet {WalletId}", _context.Options.WalletId);
            throw new RpcException(new Status(StatusCode.Internal, $"CheckOutgoingPayment failed: {ex.Message}"));
        }
    }

    public override async Task WaitPaymentEvent(
        Proto.EmptyRequest request,
        IServerStreamWriter<Proto.PaymentEventResponse> responseStream,
        ServerCallContext context)
    {
        await StreamWalletSwapEvents(
            responseStream,
            swap => new Proto.PaymentEventResponse { PaymentReceived = MapAnyIncomingSwap(swap) },
            context.CancellationToken);
    }

    public override async Task WaitIncomingPayment(
        Proto.EmptyRequest request,
        IServerStreamWriter<Proto.WaitIncomingPaymentResponse> responseStream,
        ServerCallContext context)
    {
        await StreamWalletSwapEvents(responseStream, MapAnyIncomingSwap, context.CancellationToken);
    }

    private async Task StreamWalletSwapEvents<T>(
        IServerStreamWriter<T> stream,
        Func<ArkSwap, T> map,
        CancellationToken ct)
    {
        await foreach (var swap in _incomingPaymentEvents.Subscribe(ct))
        {
            if (!string.Equals(swap.WalletId, _context.Options.WalletId, StringComparison.Ordinal))
                continue;

            T message;
            try
            {
                message = map(swap);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to map swap {SwapId} — skipping", swap.SwapId);
                continue;
            }

            await stream.WriteAsync(message, ct);
        }
    }

    private Proto.WaitIncomingPaymentResponse MapAnyIncomingSwap(ArkSwap swap) =>
        swap.SwapType == ArkSwapType.ChainBtcToArk
            ? MapOnchainIncoming(new OnchainIncomingPayment(
                swap.SwapId, swap.Address, swap.ExpectedAmount, swap.Status, swap.CreatedAt, swap.UpdatedAt))
            : MapIncomingPayment(swap);

    private Proto.WaitIncomingPaymentResponse MapIncomingPayment(ArkSwap swap)
    {
        var invoice = BOLT11PaymentRequest.Parse(swap.Invoice, _network);
        var paymentHash = invoice.PaymentHash?.ToString() ?? swap.Hash;

        Proto.PaymentIdentifier identifier;
        string paymentId;
        if (!string.IsNullOrWhiteSpace(paymentHash))
        {
            identifier = new Proto.PaymentIdentifier { Type = Proto.PaymentIdentifierType.PaymentHash, Hash = paymentHash };
            paymentId = paymentHash;
        }
        else
        {
            identifier = new Proto.PaymentIdentifier { Type = Proto.PaymentIdentifierType.CustomId, Id = swap.SwapId };
            paymentId = swap.SwapId;
        }

        return new Proto.WaitIncomingPaymentResponse
        {
            PaymentIdentifier = identifier,
            PaymentAmount = new Proto.AmountMessage
            {
                Value = (ulong)(invoice.MinimumAmount?.ToUnit(LightMoneyUnit.Satoshi) ?? 0),
                Unit = _context.Options.Unit
            },
            PaymentId = paymentId
        };
    }

    private Proto.WaitIncomingPaymentResponse MapIncomingPaymentFromInvoice(LightningInvoice invoice)
    {
        var paymentHash = invoice.PaymentHash;
        Proto.PaymentIdentifier identifier;
        string paymentId;
        if (!string.IsNullOrWhiteSpace(paymentHash))
        {
            identifier = new Proto.PaymentIdentifier { Type = Proto.PaymentIdentifierType.PaymentHash, Hash = paymentHash };
            paymentId = paymentHash;
        }
        else
        {
            identifier = new Proto.PaymentIdentifier { Type = Proto.PaymentIdentifierType.CustomId, Id = invoice.Id };
            paymentId = invoice.Id;
        }

        return new Proto.WaitIncomingPaymentResponse
        {
            PaymentIdentifier = identifier,
            PaymentAmount = new Proto.AmountMessage
            {
                Value = (ulong)(invoice.Amount?.ToUnit(LightMoneyUnit.Satoshi) ?? 0),
                Unit = _context.Options.Unit
            },
            PaymentId = paymentId
        };
    }

    private Proto.WaitIncomingPaymentResponse MapOnchainIncoming(OnchainIncomingPayment payment) =>
        new()
        {
            PaymentIdentifier = new Proto.PaymentIdentifier
            {
                Type = Proto.PaymentIdentifierType.QuoteId,
                Id = payment.SwapId
            },
            PaymentAmount = new Proto.AmountMessage
            {
                Value = (ulong)payment.ExpectedAmountSats,
                Unit = _context.Options.Unit
            },
            PaymentId = payment.SwapId
        };

    private Proto.MakePaymentResponse MapOnchainOutgoing(OnchainOutgoingPayment payment)
    {
        var state = payment.Status switch
        {
            ArkSwapStatus.Settled => Proto.QuoteState.Paid,
            ArkSwapStatus.Failed or ArkSwapStatus.Refunded => Proto.QuoteState.Failed,
            ArkSwapStatus.Pending or ArkSwapStatus.Unknown => Proto.QuoteState.Pending,
            _ => Proto.QuoteState.Unknown
        };

        return new Proto.MakePaymentResponse
        {
            PaymentIdentifier = new Proto.PaymentIdentifier
            {
                Type = Proto.PaymentIdentifierType.QuoteId,
                Id = payment.SwapId
            },
            Status = state,
            TotalSpent = new Proto.AmountMessage { Value = (ulong)payment.AmountSats, Unit = _context.Options.Unit }
        };
    }

    private async Task<LightningInvoice?> ResolveIncoming(Proto.PaymentIdentifier id, CancellationToken ct)
    {
        return id.Type switch
        {
            Proto.PaymentIdentifierType.PaymentHash or
            Proto.PaymentIdentifierType.Bolt12PaymentHash
                => await _lightning.GetIncomingByHash(_context.Options.WalletId, id.Hash, ct),
            _ => await _lightning.GetIncomingById(_context.Options.WalletId, id.Id, ct)
        };
    }

    private async Task<LightningPayment?> ResolveOutgoing(Proto.PaymentIdentifier id, CancellationToken ct)
    {
        return id.Type == Proto.PaymentIdentifierType.PaymentHash
            ? await _lightning.GetOutgoingByHash(_context.Options.WalletId, id.Hash, ct)
            : await _lightning.GetOutgoingById(_context.Options.WalletId, id.Id, ct);
    }

    private Proto.MakePaymentResponse MapLightningOutgoing(LightningPayment payment)
    {
        var state = payment.Status switch
        {
            LightningPaymentStatus.Complete => Proto.QuoteState.Paid,
            LightningPaymentStatus.Pending => Proto.QuoteState.Pending,
            LightningPaymentStatus.Failed => Proto.QuoteState.Failed,
            _ => Proto.QuoteState.Unknown
        };

        var paymentHash = payment.PaymentHash;
        Proto.PaymentIdentifier identifier;
        if (!string.IsNullOrWhiteSpace(paymentHash))
            identifier = new Proto.PaymentIdentifier { Type = Proto.PaymentIdentifierType.PaymentHash, Hash = paymentHash };
        else
            identifier = new Proto.PaymentIdentifier { Type = Proto.PaymentIdentifierType.CustomId, Id = payment.Id };

        return new Proto.MakePaymentResponse
        {
            PaymentIdentifier = identifier,
            PaymentProof = payment.Preimage ?? string.Empty,
            Status = state,
            TotalSpent = new Proto.AmountMessage
            {
                Value = (ulong)(payment.AmountSent?.ToUnit(LightMoneyUnit.Satoshi) ?? 0),
                Unit = _context.Options.Unit
            }
        };
    }

    private Proto.MakePaymentResponse UnknownOutgoingResponse() =>
        new() { Status = Proto.QuoteState.Unknown, TotalSpent = new Proto.AmountMessage { Value = 0, Unit = _context.Options.Unit } };

    private static ulong CalculateFeeSats(long amountSats, BoltzLimits limits)
        => (ulong)Math.Ceiling(amountSats * (double)limits.FeePercentage / 100.0) + (ulong)limits.MinerFee;

    private static RpcException BadRequest(string message)
        => new(new Status(StatusCode.InvalidArgument, message));
}
