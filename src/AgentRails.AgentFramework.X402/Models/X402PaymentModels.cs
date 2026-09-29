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

    [JsonPropertyName("resource")]
    public X402Resource? Resource { get; set; }

    [JsonPropertyName("extensions")]
    public Dictionary<string, System.Text.Json.JsonElement>? Extensions { get; set; }
}

/// <summary>Resource binding in x402 V2 challenge.</summary>
public sealed class X402Resource
{
    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? OtherFields { get; set; }
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

    // x402 V2 uses `amount`; V1 used `maxAmountRequired`.
    // Do not default a missing amount to zero: fail closed before signing.
    [JsonPropertyName("amount")]
    public string? Amount { get; set; }

    [JsonPropertyName("maxAmountRequired")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MaxAmountRequired { get; set; }

    [JsonIgnore]
    public string? PaymentAmount => Amount ?? MaxAmountRequired;

    [JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? OtherFields { get; set; }

    [JsonPropertyName("resource")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Resource { get; set; }

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }

    [JsonPropertyName("payTo")]
    public string PayTo { get; set; } = string.Empty;

    [JsonPropertyName("asset")]
    public string Asset { get; set; } = string.Empty;

    [JsonPropertyName("extra")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public X402PaymentExtra? Extra { get; set; }
}

/// <summary>
/// Extra metadata in payment option.
/// </summary>
public sealed class X402PaymentExtra
{
    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; set; }

    [JsonPropertyName("version")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Version { get; set; }

    [JsonPropertyName("expiresAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? ExpiresAt { get; set; }

    [JsonPropertyName("assetTransferMethod")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AssetTransferMethod { get; set; }

    [JsonPropertyName("paymentFlow")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PaymentFlow { get; set; }

    [JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? OtherFields { get; set; }
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

    [JsonPropertyName("resource")]
    public X402Resource? Resource { get; set; }

    [JsonPropertyName("accepted")]
    public X402PaymentOption? Accepted { get; set; }

    [JsonPropertyName("extensions")]
    public Dictionary<string, System.Text.Json.JsonElement>? Extensions { get; set; }

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
