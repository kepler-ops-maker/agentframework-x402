namespace AgentRails.AgentFramework.X402.Models;

/// <summary>
/// Configuration options for the x402 Agent Framework tools.
/// </summary>
public sealed class X402PluginOptions
{
    /// <summary>
    /// Hex-encoded Ethereum private key (with or without 0x prefix).
    /// </summary>
    public string PrivateKey { get; set; } = string.Empty;

    /// <summary>
    /// Network identifier in CAIP-2 format (e.g., "eip155:8453" for Base).
    /// Legacy names like "base-mainnet" are also accepted.
    /// </summary>
    public string Network { get; set; } = "eip155:8453";

    /// <summary>
    /// Maximum total USD the wallet is allowed to spend.
    /// </summary>
    public decimal BudgetUsd { get; set; } = 10.0m;

    /// <summary>
    /// Default per-request maximum price in USD.
    /// Can be overridden per-call via the max_price_usd parameter.
    /// </summary>
    public decimal DefaultMaxPriceUsd { get; set; } = 1.0m;

    /// <summary>
    /// Whether to automatically pay when within budget.
    /// If false, the plugin returns payment requirements without paying.
    /// </summary>
    public bool AutoPay { get; set; } = true;

    /// <summary>
    /// HTTP request timeout in seconds.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 30;
}
