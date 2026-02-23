namespace AgentRails.AgentFramework.X402.Models;

/// <summary>
/// Immutable record of a payment made by the wallet.
/// </summary>
public sealed record PaymentRecord
{
    public required DateTimeOffset Timestamp { get; init; }
    public required string ResourceUrl { get; init; }
    public required string ToAddress { get; init; }
    public required decimal AmountUsd { get; init; }
    public required long AmountUnits { get; init; }
    public required string Network { get; init; }
    public required string Nonce { get; init; }
    public required string Signature { get; init; }
    public string? TransactionHash { get; init; }
}
