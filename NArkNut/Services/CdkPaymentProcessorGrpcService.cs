using BTCPayServer.Lightning;
using Grpc.Core;
using cdk_arkade_payment_processor.Configuration;
using NArk.Swaps.Models;
using Proto = global::CdkPaymentProcessor;

namespace cdk_arkade_payment_processor.Services;

public sealed class CdkPaymentProcessorGrpcService : Proto.CdkPaymentProcessor.CdkPaymentProcessorBase
{
    private readonly ProcessorContext _context;
    private readonly ArkSwapLightningService _lightning;
    private readonly IncomingPaymentEventBus _incomingPaymentEvents;
    private readonly ILogger<CdkPaymentProcessorGrpcService> _logger;
    private readonly NBitcoin.Network _network;

    public CdkPaymentProcessorGrpcService(
        ProcessorContext context,
        ArkSwapLightningService lightning,
        IncomingPaymentEventBus incomingPaymentEvents,
        Microsoft.Extensions.Options.IOptions<NbxplorerOptions> nbxplorerOptions,
        ILogger<CdkPaymentProcessorGrpcService> logger)
    {
        _context = context;
        _lightning = lightning;
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

        return response;
    }

    public override async Task<Proto.CreatePaymentResponse> CreatePayment(
        Proto.CreatePaymentRequest request,
        ServerCallContext context)
    {
        try
        {
            if (request.Options?.Onchain is not null)
                throw NotImplemented("Onchain incoming payments are not implemented");

            var bolt11 = request.Options?.Bolt11 ?? throw BadRequest("Only bolt11 incoming options are supported");
            var amount = bolt11.Amount?.Value ?? 0;
            if (amount == 0) throw BadRequest("Amount is required");

            var expirySeconds = bolt11.HasUnixExpiry
                ? Math.Max(60, (long)bolt11.UnixExpiry - DateTimeOffset.UtcNow.ToUnixTimeSeconds())
                : 3600;

            var invoice = await _lightning.CreateInvoice(
                _context.Options.WalletId,
                (long)amount,
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
                throw NotImplemented("Onchain payment quotes are not implemented");

            if (request.RequestType != Proto.OutgoingPaymentRequestType.Bolt11Invoice)
                throw BadRequest("Only bolt11 outgoing quote is supported");

            var pr = BOLT11PaymentRequest.Parse(request.Request, _network);
            var amountSats = (long)(pr.MinimumAmount?.ToUnit(LightMoneyUnit.Satoshi) ?? 0);
            var paymentHash = pr.PaymentHash?.ToString()
                ?? throw BadRequest("Invoice has no payment hash.");

            var limits = await _lightning.GetOutgoingLimitsAsync(context.CancellationToken);
            if (limits is not null)
            {
                if (amountSats < limits.MinAmount)
                    throw new PaymentValidationException($"Amount {amountSats} sats is below minimum {limits.MinAmount} sats for melt");
                if (amountSats > limits.MaxAmount)
                    throw new PaymentValidationException($"Amount {amountSats} sats exceeds maximum {limits.MaxAmount} sats for melt");
            }

            var feeSats = limits is not null
                ? (ulong)Math.Ceiling(amountSats * (double)limits.FeePercentage / 100.0) + (ulong)limits.MinerFee
                : 0UL;

            return new Proto.PaymentQuoteResponse
            {
                RequestIdentifier = new Proto.PaymentIdentifier
                {
                    Type = Proto.PaymentIdentifierType.PaymentHash,
                    Hash = paymentHash
                },
                Amount = new Proto.AmountMessage { Value = (ulong)amountSats, Unit = _context.Options.Unit },
                Fee = new Proto.AmountMessage { Value = feeSats, Unit = _context.Options.Unit },
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
                throw NotImplemented("Onchain payments are not implemented");

            var bolt11 = request.PaymentOptions?.Bolt11?.Bolt11;
            if (string.IsNullOrWhiteSpace(bolt11)) throw BadRequest("Only bolt11 outgoing payment is supported");

            var payment = await _lightning.PayInvoice(_context.Options.WalletId, bolt11, context.CancellationToken);
            return MapOutgoing(payment);
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
            var invoice = await ResolveIncoming(request.RequestIdentifier, context.CancellationToken);
            var response = new Proto.CheckIncomingPaymentResponse();

            if (invoice?.Status == LightningInvoiceStatus.Paid)
                response.Payments.Add(MapIncomingPaymentFromInvoice(invoice));

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
            var payment = await ResolveOutgoing(request.RequestIdentifier, context.CancellationToken);
            if (payment is null)
                return new Proto.MakePaymentResponse
                {
                    Status = Proto.QuoteState.Unknown,
                    TotalSpent = new Proto.AmountMessage { Value = 0, Unit = _context.Options.Unit }
                };

            return MapOutgoing(payment);
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
            swap => new Proto.PaymentEventResponse { PaymentReceived = MapIncomingPayment(swap) },
            context.CancellationToken);
    }

    public override async Task WaitIncomingPayment(
        Proto.EmptyRequest request,
        IServerStreamWriter<Proto.WaitIncomingPaymentResponse> responseStream,
        ServerCallContext context)
    {
        await StreamWalletSwapEvents(responseStream, MapIncomingPayment, context.CancellationToken);
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

    private Proto.MakePaymentResponse MapOutgoing(LightningPayment payment)
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

    private static RpcException BadRequest(string message)
        => new(new Status(StatusCode.InvalidArgument, message));

    private static RpcException NotImplemented(string message)
        => new(new Status(StatusCode.Unimplemented, message));
}
