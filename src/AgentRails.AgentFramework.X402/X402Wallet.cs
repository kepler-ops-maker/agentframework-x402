using AgentRails.AgentFramework.X402.Models;

namespace AgentRails.AgentFramework.X402;

/// <summary>
/// Thread-safe wallet for x402 payments with budget management.
/// Manages a local private key, tracks spending against a USD budget,
/// and signs EIP-3009 TransferWithAuthorization messages.
/// </summary>
public sealed class X402Wallet
{
    private readonly Eip3009Signer _signer;
    private readonly string _network;
    private readonly object _syncLock = new();

    private decimal _budgetUsd;
    private decimal _spentUsd;
    private readonly List<PaymentRecord> _payments = new();

    private const long UsdcMultiplier = 1_000_000;

    public X402Wallet(X402PluginOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.PrivateKey))
            throw new ArgumentException("PrivateKey is required", nameof(options));

        _signer = new Eip3009Signer(options.PrivateKey);
        _network = X402NetworkConfig.NormalizeNetwork(options.Network);
        _budgetUsd = options.BudgetUsd;
        _spentUsd = 0m;

        if (X402NetworkConfig.GetNetwork(_network) == null)
            throw new ArgumentException(
                $"Unsupported network: {options.Network}. " +
                $"Supported: {string.Join(", ", X402NetworkConfig.Networks.Keys)}");
    }

    /// <summary>Wallet address derived from the private key.</summary>
    public string Address => _signer.Address;

    /// <summary>CAIP-2 network identifier.</summary>
    public string Network => _network;

    /// <summary>Total budget in USD.</summary>
    public decimal BudgetUsd
    {
        get { lock (_syncLock) return _budgetUsd; }
    }

    /// <summary>Total USD spent so far.</summary>
    public decimal SpentUsd
    {
        get { lock (_syncLock) return _spentUsd; }
    }

    /// <summary>Remaining budget in USD.</summary>
    public decimal RemainingUsd
    {
        get { lock (_syncLock) return Math.Max(0m, _budgetUsd - _spentUsd); }
    }

    /// <summary>Number of payments made.</summary>
    public int PaymentCount
    {
        get { lock (_syncLock) return _payments.Count; }
    }

    /// <summary>Check if the wallet can afford a payment.</summary>
    public bool CanAfford(decimal amountUsd)
    {
        lock (_syncLock) return amountUsd <= (_budgetUsd - _spentUsd);
    }

    /// <summary>Convert USDC smallest units to USD.</summary>
    public static decimal UnitsToUsd(long units) => (decimal)units / UsdcMultiplier;

    /// <summary>Convert USD to USDC smallest units.</summary>
    public static long UsdToUnits(decimal usd) => (long)(usd * UsdcMultiplier);

    /// <summary>
    /// Sign an EIP-3009 payment authorization and record it.
    /// Thread-safe.
    /// </summary>
    /// <returns>(signature hex, nonce hex)</returns>
    /// <exception cref="InvalidOperationException">If budget would be exceeded.</exception>
    public (string Signature, string NonceHex) SignPayment(
        string toAddress, long amountUnits, long validBefore, string resourceUrl = "")
    {
        var amountUsd = UnitsToUsd(amountUnits);

        lock (_syncLock)
        {
            if (amountUsd > (_budgetUsd - _spentUsd))
                throw new InvalidOperationException(
                    $"Budget exceeded: need ${amountUsd:F4}, " +
                    $"have ${(_budgetUsd - _spentUsd):F4} remaining");

            var nonce = Eip3009Signer.GenerateNonce();
            var nonceHex = "0x" + Convert.ToHexString(nonce).ToLowerInvariant();

            var signature = _signer.SignTransferAuthorization(
                toAddress: toAddress,
                value: amountUnits.ToString(),
                validAfter: 0,
                validBefore: validBefore,
                nonce: nonce,
                network: _network);

            _spentUsd += amountUsd;
            _payments.Add(new PaymentRecord
            {
                Timestamp = DateTimeOffset.UtcNow,
                ResourceUrl = resourceUrl,
                ToAddress = toAddress,
                AmountUsd = amountUsd,
                AmountUnits = amountUnits,
                Network = _network,
                Nonce = nonceHex,
                Signature = signature,
            });

            return (signature, nonceHex);
        }
    }

    /// <summary>Get a copy of the payment history.</summary>
    public IReadOnlyList<PaymentRecord> GetPayments()
    {
        lock (_syncLock) return _payments.ToList().AsReadOnly();
    }

    /// <summary>Reset budget and clear payment history.</summary>
    public void ResetBudget(decimal? newBudgetUsd = null)
    {
        lock (_syncLock)
        {
            if (newBudgetUsd.HasValue) _budgetUsd = newBudgetUsd.Value;
            _spentUsd = 0m;
            _payments.Clear();
        }
    }
}
