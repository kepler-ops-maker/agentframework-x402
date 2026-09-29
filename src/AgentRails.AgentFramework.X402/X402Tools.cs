using System.ComponentModel;
using System.Text;
using System.Text.Json;
using AgentRails.AgentFramework.X402.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AgentRails.AgentFramework.X402;

/// <summary>
/// x402 tools for Microsoft Agent Framework.
/// Enables AI agents to make HTTP requests with automatic x402 payment handling.
/// </summary>
public sealed class X402Tools
{
    private readonly X402Wallet _wallet;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<X402Tools> _logger;
    private readonly X402PluginOptions _options;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Constructor for DI registration (IOptions pattern).</summary>
    public X402Tools(
        X402Wallet wallet,
        IHttpClientFactory httpClientFactory,
        IOptions<X402PluginOptions> options,
        ILogger<X402Tools>? logger = null)
    {
        _wallet = wallet ?? throw new ArgumentNullException(nameof(wallet));
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? NullLogger<X402Tools>.Instance;
    }

    /// <summary>Constructor for manual registration.</summary>
    public X402Tools(
        X402Wallet wallet,
        IHttpClientFactory httpClientFactory,
        X402PluginOptions options,
        ILogger<X402Tools>? logger = null)
    {
        _wallet = wallet ?? throw new ArgumentNullException(nameof(wallet));
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? NullLogger<X402Tools>.Instance;
    }

    /// <summary>
    /// Get all x402 tools as <see cref="AIFunction"/> instances for use with Agent Framework agents.
    /// </summary>
    public IList<AIFunction> GetTools() =>
    [
        AIFunctionFactory.Create(MakePaidRequestAsync, nameof(MakePaidRequestAsync)),
        AIFunctionFactory.Create(CheckBudget, nameof(CheckBudget)),
        AIFunctionFactory.Create(GetPaymentHistory, nameof(GetPaymentHistory)),
    ];

    [Description(
        "Make an HTTP request to any URL. If the API requires payment (HTTP 402), " +
        "automatically signs a USDC payment authorization and retries. " +
        "Use this for accessing any x402-enabled API or paid data source.")]
    public async Task<string> MakePaidRequestAsync(
        [Description("The full URL to request")] string url,
        [Description("HTTP method: GET, POST, PUT, DELETE")] string method = "GET",
        [Description("Request body as JSON string for POST/PUT")] string? body = null,
        [Description("Maximum USD willing to pay for this request")] double? maxPriceUsd = null,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("x402 request: {Method} {Url}", method, url);

        var effectiveMaxPrice = maxPriceUsd.HasValue
            ? (decimal)maxPriceUsd.Value
            : _options.DefaultMaxPriceUsd;

        using var client = _httpClientFactory.CreateClient("X402");
        client.Timeout = TimeSpan.FromSeconds(_options.TimeoutSeconds);

        try
        {
            // --- Initial request ---
            var request = BuildRequest(method, url, body);
            var response = await client.SendAsync(request, cancellationToken);

            if ((int)response.StatusCode != 402)
            {
                var content = await response.Content.ReadAsStringAsync(cancellationToken);
                if (!response.IsSuccessStatusCode)
                    return $"Error {(int)response.StatusCode}: {content}";
                return content;
            }

            // --- Handle 402 Payment Required ---
            _logger.LogInformation("Received 402 from {Url}, processing payment", url);

            var paymentRequired = await ParsePaymentRequiredAsync(response, cancellationToken);
            if (paymentRequired == null || paymentRequired.Accepts.Count == 0)
                return response.Headers.Contains(X402Headers.LegacyPaymentRequired)
                    ? "Error: V1 payment challenge is not supported by this V2 client."
                    : "Error: Received 402 but could not parse payment requirements.";

            var option = FindCompatibleOption(paymentRequired.Accepts);
            if (option == null)
            {
                var availableNetworks = string.Join(", ",
                    paymentRequired.Accepts.Select(a => a.Network));
                return $"Error: Network mismatch. API accepts [{availableNetworks}], " +
                       $"wallet is configured for {_wallet.Network}.";
            }

            if (!string.Equals(option.Scheme, "exact", StringComparison.OrdinalIgnoreCase))
                return "Error: Unsupported payment scheme.";
            if (!string.IsNullOrEmpty(option.Extra?.AssetTransferMethod) &&
                !string.Equals(option.Extra.AssetTransferMethod, "eip3009", StringComparison.Ordinal))
                return "Error: Unsupported asset transfer method.";
            if (!string.IsNullOrEmpty(option.Extra?.PaymentFlow) &&
                !string.Equals(option.Extra.PaymentFlow, "authorization", StringComparison.Ordinal))
                return "Error: Unsupported payment flow.";

            var expectedAsset = X402NetworkConfig.GetNetwork(_wallet.Network)?.UsdcContract;
            if (expectedAsset == null ||
                !string.Equals(option.Asset, expectedAsset, StringComparison.OrdinalIgnoreCase))
                return "Error: Payment asset does not match the configured network USDC contract.";

            if (paymentRequired.X402Version != 2 ||
                !string.Equals(paymentRequired.Resource?.Url, url, StringComparison.Ordinal))
                return "Error: Payment challenge version or resource URL mismatch.";

            if (string.IsNullOrEmpty(option.Amount))
                return "Error: Missing V2 payment amount.";
            if (!long.TryParse(option.PaymentAmount, out var amountUnits) || amountUnits <= 0)
                return "Error: Missing or invalid payment amount.";
            if (option.MaxAmountRequired != null &&
                !string.Equals(option.Amount, option.MaxAmountRequired, StringComparison.Ordinal))
                return "Error: Conflicting payment amounts in challenge.";

            if (!Uri.TryCreate(url, UriKind.Absolute, out var requestUri) ||
                requestUri.Scheme != Uri.UriSchemeHttps)
                return "Error: Paid requests require an HTTPS resource URL.";

            var amountUsd = X402Wallet.UnitsToUsd(amountUnits);

            if (amountUsd > effectiveMaxPrice)
                return $"Error: Price ${amountUsd:F4} exceeds limit of ${effectiveMaxPrice:F4}. " +
                       $"Increase max_price_usd to proceed.";

            if (!_wallet.CanAfford(amountUsd))
                return $"Error: Price ${amountUsd:F4} exceeds remaining budget " +
                       $"${_wallet.RemainingUsd:F4}.";

            if (!_options.AutoPay)
                return $"Payment required: ${amountUsd:F4} USDC to {option.PayTo}. " +
                       $"Auto-pay is disabled.";

            var validBefore = option.Extra?.ExpiresAt
                ?? DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds();

            var (signature, nonceHex) = _wallet.SignPayment(
                toAddress: option.PayTo,
                amountUnits: amountUnits,
                validBefore: validBefore,
                resourceUrl: url);

            _logger.LogInformation("Signed payment: ${Amount:F4} to {PayTo}", amountUsd, option.PayTo);

            var payloadJson = BuildPaymentPayloadJson(paymentRequired, option, amountUnits, signature, nonceHex, validBefore);
            var paymentBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(payloadJson));

            // --- Retry with payment ---
            var retryRequest = BuildRequest(method, url, body);
            retryRequest.Headers.Add(X402Headers.Payment, paymentBase64);

            var paidResponse = await client.SendAsync(retryRequest, cancellationToken);
            var paidContent = await paidResponse.Content.ReadAsStringAsync(cancellationToken);

            if (!paidResponse.IsSuccessStatusCode)
                return $"Error after payment (${amountUsd:F4}): " +
                       $"{(int)paidResponse.StatusCode} - {paidContent}";

            var txHash = ExtractTransactionHash(paidResponse);

            _logger.LogInformation(
                "x402 payment successful: ${Amount:F4}, tx: {TxHash}",
                amountUsd, txHash ?? "none");

            if (txHash != null)
                return $"[Paid ${amountUsd:F4} USDC | tx: {txHash}]\n\n{paidContent}";

            return $"[Paid ${amountUsd:F4} USDC]\n\n{paidContent}";
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "HTTP error requesting {Url}", url);
            return $"Error: HTTP request failed - {ex.Message}";
        }
        catch (TaskCanceledException)
        {
            return "Error: Request timed out.";
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("Budget exceeded"))
        {
            return $"Error: {ex.Message}";
        }
    }

    [Description("Check the current wallet budget, spending, and wallet information.")]
    public string CheckBudget()
    {
        return $"Wallet: {_wallet.Address}\n" +
               $"Network: {_wallet.Network}\n" +
               $"Budget: ${_wallet.BudgetUsd:F2}\n" +
               $"Spent: ${_wallet.SpentUsd:F4}\n" +
               $"Remaining: ${_wallet.RemainingUsd:F4}\n" +
               $"Payments made: {_wallet.PaymentCount}";
    }

    [Description("Get the history of all x402 payments made during this session.")]
    public string GetPaymentHistory()
    {
        var payments = _wallet.GetPayments();

        if (payments.Count == 0)
            return "No payments have been made yet.";

        var sb = new StringBuilder();
        sb.AppendLine($"Payment History ({payments.Count} payments, " +
                       $"${_wallet.SpentUsd:F4} total):");
        sb.AppendLine();

        for (int i = 0; i < payments.Count; i++)
        {
            var p = payments[i];
            sb.AppendLine($"{i + 1}. ${p.AmountUsd:F4} USDC to {p.ToAddress[..10]}...");
            sb.AppendLine($"   URL: {p.ResourceUrl}");
            sb.AppendLine($"   Time: {p.Timestamp:u}");
            if (p.TransactionHash != null)
                sb.AppendLine($"   TX: {p.TransactionHash}");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    // --- Private helpers ---

    private static HttpRequestMessage BuildRequest(string method, string url, string? body)
    {
        var httpMethod = method.ToUpperInvariant() switch
        {
            "GET" => HttpMethod.Get,
            "POST" => HttpMethod.Post,
            "PUT" => HttpMethod.Put,
            "DELETE" => HttpMethod.Delete,
            "PATCH" => HttpMethod.Patch,
            _ => new HttpMethod(method),
        };

        var request = new HttpRequestMessage(httpMethod, url);
        if (body != null && httpMethod != HttpMethod.Get)
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return request;
    }

    private X402PaymentOption? FindCompatibleOption(List<X402PaymentOption> accepts)
    {
        return accepts.FirstOrDefault(a =>
            X402NetworkConfig.NormalizeNetwork(a.Network) == _wallet.Network);
    }

    private async Task<X402PaymentRequired?> ParsePaymentRequiredAsync(
        HttpResponseMessage response, CancellationToken ct)
    {
        string? headerValue = null;
        if (response.Headers.TryGetValues(X402Headers.PaymentRequired, out var v2Values))
            headerValue = v2Values.FirstOrDefault();
        // V1 framing is not safe to auto-pay with a V2 payload. Fail closed.
        else if (response.Headers.Contains(X402Headers.LegacyPaymentRequired))
            return null;

        if (!string.IsNullOrEmpty(headerValue))
        {
            try
            {
                var json = Encoding.UTF8.GetString(Convert.FromBase64String(headerValue));
                var parsed = JsonSerializer.Deserialize<X402PaymentRequired>(json, JsonOptions);
                if (parsed?.Accepts.Count > 0) return parsed;

            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to parse PAYMENT-REQUIRED header");
            }
        }

        // Fallback: response body
        var bodyText = await response.Content.ReadAsStringAsync(ct);
        if (!string.IsNullOrEmpty(bodyText))
        {
            try
            {
                return JsonSerializer.Deserialize<X402PaymentRequired>(bodyText, JsonOptions);
            }
            catch
            {
                _logger.LogWarning("Failed to parse payment requirements from body");
            }
        }

        return null;
    }

    private string BuildPaymentPayloadJson(
        X402PaymentRequired required, X402PaymentOption option, long amountUnits,
        string signature, string nonceHex, long validBefore)
    {
        var payload = new X402PaymentPayload
        {
            X402Version = 2,
            Scheme = option.Scheme,
            Network = _wallet.Network,
            Resource = required.Resource,
            Accepted = option,
            Extensions = required.Extensions,
            Payload = new X402EvmPayload
            {
                Signature = signature,
                Authorization = new X402Authorization
                {
                    From = _wallet.Address,
                    To = option.PayTo,
                    Value = amountUnits.ToString(),
                    ValidAfter = "0",
                    ValidBefore = validBefore.ToString(),
                    Nonce = nonceHex,
                },
            },
        };

        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    private static string? ExtractTransactionHash(HttpResponseMessage response)
    {
        string? headerValue = null;
        if (response.Headers.TryGetValues(X402Headers.PaymentResponse, out var v2))
            headerValue = v2.FirstOrDefault();
        else if (response.Headers.TryGetValues(X402Headers.LegacyPaymentResponse, out var v1))
            headerValue = v1.FirstOrDefault();

        if (string.IsNullOrEmpty(headerValue)) return null;

        try
        {
            // Try base64 first, then raw JSON
            string json;
            try
            {
                json = Encoding.UTF8.GetString(Convert.FromBase64String(headerValue));
            }
            catch
            {
                json = headerValue;
            }

            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("transactionHash", out var txProp)
                ? txProp.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }
}
