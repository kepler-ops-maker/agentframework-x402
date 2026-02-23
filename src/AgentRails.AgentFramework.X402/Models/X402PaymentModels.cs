using System.Text.Json.Serialization;

namespace AgentRails.AgentFramework.X402.Models;

/// <summary>
/// x402 V2 Payment Required response (decoded from PAYMENT-REQUIRED header).
/// </summary>
public sealed class X402PaymentRequired
{
    [JsonPropertyName("x402Version")]
    public int X402Version { get; set; } = 2;

    [JsonPropertyName("accepts")]
    public List<X402PaymentOption> Accepts { get; set; } = new();
}

/// <summary>
/// Individual payment option from the server's accepts array.
/// </summary>
public sealed class X402PaymentOption
{
    [JsonPropertyName("scheme")]
    public string Scheme { get; set; } = "exact";

    [JsonPropertyName("network")]
    public string Network { get; set; } = string.Empty;

    [JsonPropertyName("maxAmountRequired")]
    public string MaxAmountRequired { get; set; } = "0";

    [JsonPropertyName("resource")]
    public string Resource { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("payTo")]
    public string PayTo { get; set; } = string.Empty;

    [JsonPropertyName("asset")]
    public string Asset { get; set; } = string.Empty;

    [JsonPropertyName("extra")]
    public X402PaymentExtra? Extra { get; set; }
}

/// <summary>
/// Extra metadata in payment option.
/// </summary>
public sealed class X402PaymentExtra
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("expiresAt")]
    public long? ExpiresAt { get; set; }
}

/// <summary>
/// Payment payload sent by client in PAYMENT-SIGNATURE header.
/// </summary>
public sealed class X402PaymentPayload
{
    [JsonPropertyName("x402Version")]
    public int X402Version { get; set; } = 2;

    [JsonPropertyName("scheme")]
    public string Scheme { get; set; } = "exact";

    [JsonPropertyName("network")]
    public string Network { get; set; } = string.Empty;

    [JsonPropertyName("payload")]
    public X402EvmPayload? Payload { get; set; }
}

/// <summary>
/// EVM-specific payload containing signature and authorization.
/// </summary>
public sealed class X402EvmPayload
{
    [JsonPropertyName("signature")]
    public string Signature { get; set; } = string.Empty;

    [JsonPropertyName("authorization")]
    public X402Authorization? Authorization { get; set; }
}

/// <summary>
/// EIP-3009 TransferWithAuthorization parameters.
/// </summary>
public sealed class X402Authorization
{
    [JsonPropertyName("from")]
    public string From { get; set; } = string.Empty;

    [JsonPropertyName("to")]
    public string To { get; set; } = string.Empty;

    [JsonPropertyName("value")]
    public string Value { get; set; } = "0";

    [JsonPropertyName("validAfter")]
    public string ValidAfter { get; set; } = "0";

    [JsonPropertyName("validBefore")]
    public string ValidBefore { get; set; } = "0";

    [JsonPropertyName("nonce")]
    public string Nonce { get; set; } = string.Empty;
}

/// <summary>
/// x402 V2 header name constants.
/// </summary>
internal static class X402Headers
{
    public const string PaymentRequired = "PAYMENT-REQUIRED";
    public const string Payment = "PAYMENT-SIGNATURE";
    public const string PaymentResponse = "PAYMENT-RESPONSE";

    // V1 legacy (for reading only)
    public const string LegacyPaymentRequired = "X-PAYMENT-REQUIRED";
    public const string LegacyPaymentResponse = "X-PAYMENT-RESPONSE";
}
