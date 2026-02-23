namespace AgentRails.AgentFramework.X402.Models;

/// <summary>
/// Network configuration for EIP-3009 signing.
/// Contains chain IDs and USDC contract addresses for supported EVM networks.
/// </summary>
public static class X402NetworkConfig
{
    /// <summary>
    /// Network metadata keyed by CAIP-2 network identifier.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, NetworkInfo> Networks =
        new Dictionary<string, NetworkInfo>
        {
            // Testnets
            ["eip155:84532"] = new("USD Coin", "2", 84532,
                "0x036CbD53842c5426634e7929541eC2318f3dCF7e"),   // Base Sepolia
            ["eip155:11155111"] = new("USD Coin", "2", 11155111,
                "0x1c7D4B196Cb0C7B01d743Fbc6116a902379C7238"),   // Ethereum Sepolia
            ["eip155:5042002"] = new("USD Coin", "2", 5042002,
                "0x3600000000000000000000000000000000000000"),     // Arc Testnet

            // Mainnets
            ["eip155:8453"] = new("USD Coin", "2", 8453,
                "0x833589fCD6eDb6E08f4c7C32D4f71b54bdA02913"),   // Base
            ["eip155:1"] = new("USD Coin", "2", 1,
                "0xA0b86991c6218b36c1d19D4a2e9Eb0cE3606eB48"),   // Ethereum
        };

    /// <summary>
    /// Legacy name to CAIP-2 mapping for backwards compatibility.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> LegacyNames =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["base-mainnet"] = "eip155:8453",
            ["base-sepolia"] = "eip155:84532",
            ["ethereum-mainnet"] = "eip155:1",
            ["ethereum-sepolia"] = "eip155:11155111",
            ["arc-testnet"] = "eip155:5042002",
        };

    /// <summary>
    /// Normalize a network identifier (accept both legacy and CAIP-2).
    /// </summary>
    public static string NormalizeNetwork(string network) =>
        LegacyNames.TryGetValue(network, out var caip2) ? caip2 : network;

    /// <summary>
    /// Get network info, normalizing legacy names.
    /// </summary>
    public static NetworkInfo? GetNetwork(string network)
    {
        var normalized = NormalizeNetwork(network);
        return Networks.TryGetValue(normalized, out var info) ? info : null;
    }
}

/// <summary>
/// Configuration for a specific EVM network.
/// </summary>
public sealed record NetworkInfo(
    string TokenName,
    string TokenVersion,
    int ChainId,
    string UsdcContract);
