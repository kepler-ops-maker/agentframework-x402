using System.Net;
using System.Text;
using System.Text.Json;
using AgentRails.AgentFramework.X402;
using AgentRails.AgentFramework.X402.Models;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Moq;
using Moq.Protected;
using Xunit;

namespace AgentRails.AgentFramework.X402.Tests;

public class X402ToolsTests
{
    private const string TestPrivateKey = "ac0974bec39a17e36ba4a6b4d238ff944bacb478cbed5efcae784d7bf4f2ff80";
    private const string TestUrl = "https://api.example.com/data";
    private const string TestPayTo = "0x70997970C51812dc3A010C7d01b50e0d17dc79C8";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private static X402PluginOptions CreateOptions(
        decimal budget = 10.0m,
        decimal maxPrice = 1.0m,
        bool autoPay = true) => new()
    {
        PrivateKey = TestPrivateKey,
        Network = "eip155:84532",
        BudgetUsd = budget,
        DefaultMaxPriceUsd = maxPrice,
        AutoPay = autoPay,
    };

    private static (X402Tools Tools, Mock<HttpMessageHandler> Handler) CreateTools(
        X402PluginOptions? options = null)
    {
        options ??= CreateOptions();
        var wallet = new X402Wallet(options);
        var handler = new Mock<HttpMessageHandler>();

        var httpClient = new HttpClient(handler.Object);
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient("X402")).Returns(httpClient);

        var tools = new X402Tools(wallet, factory.Object, options);
        return (tools, handler);
    }

    private static string BuildPaymentRequiredHeader(
        string network = "eip155:84532",
        string amount = "10000",   // $0.01
        string payTo = TestPayTo)
    {
        var required = new X402PaymentRequired
        {
            X402Version = 2,
            Resource = new X402Resource { Url = TestUrl },
            Accepts =
            [
                new X402PaymentOption
                {
                    Scheme = "exact",
                    Network = network,
                    Amount = amount,
                    PayTo = payTo,
                    Resource = TestUrl,
                    Asset = "0x036CbD53842c5426634e7929541eC2318f3dCF7e",
                    Extra = new X402PaymentExtra
                    {
                        Name = "USD Coin",
                        Version = "2",
                        ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds(),
                    },
                },
            ],
        };

        var json = JsonSerializer.Serialize(required, JsonOptions);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
    }

    [Fact]
    public void ProofRandomLiveChallenge_UsesV2AmountAndBindsResource()
    {
        // Snapshot captured by an unsigned GET against the Base Sepolia Worker.
        var fixture = System.IO.File.ReadAllText(
            System.IO.Path.Combine(AppContext.BaseDirectory, "live-challenge-fixture.json"));
        using var fixtureJson = JsonDocument.Parse(fixture);
        var challenge = fixtureJson.RootElement.GetProperty("challenge").GetRawText();
        var parsed = JsonSerializer.Deserialize<X402PaymentRequired>(challenge, JsonOptions)!;
        parsed.X402Version.Should().Be(2);
        parsed.Resource!.Url.Should().Be(
            "https://proof-random-x402-sepolia.pn-26f.workers.dev/v1/random?nonce=integration-fixture-readonly");
        var option = parsed.Accepts.Single();
        option.Network.Should().Be("eip155:84532");
        option.Amount.Should().Be("1000");
        option.MaxAmountRequired.Should().BeNull();
        option.PaymentAmount.Should().Be("1000");
        option.Asset.Should().Be("0x036CbD53842c5426634e7929541eC2318f3dCF7e");
    }

    [Theory]
    [InlineData("missingAmount", "Missing V2 payment amount")]
    [InlineData("zeroAmount", "Missing or invalid payment amount")]
    [InlineData("conflictingAmounts", "Conflicting payment amounts")]
    [InlineData("wrongAsset", "Payment asset does not match")]
    [InlineData("wrongResource", "challenge version or resource URL mismatch")]
    [InlineData("wrongScheme", "Unsupported payment scheme")]
    [InlineData("permit2", "Unsupported asset transfer method")]
    [InlineData("upfront", "Unsupported payment flow")]
    public async Task MalformedChallenge_NeverRetriesOrSigns(string mutation, string expected)
    {
        var (tools, handler) = CreateTools();
        using var document = JsonDocument.Parse(System.IO.File.ReadAllText(
            System.IO.Path.Combine(AppContext.BaseDirectory, "live-challenge-fixture.json")));
        var challenge = JsonSerializer.Deserialize<X402PaymentRequired>(
            document.RootElement.GetProperty("challenge").GetRawText(), JsonOptions)!;
        // Bind fixture to the mocked request URL to isolate the challenged field.
        challenge.Resource!.Url = TestUrl;
        var option = challenge.Accepts.Single();
        switch (mutation)
        {
            case "missingAmount": option.Amount = null; break;
            case "zeroAmount": option.Amount = "0"; break;
            case "conflictingAmounts": option.MaxAmountRequired = "2000"; break;
            case "wrongAsset": option.Asset = "0x0000000000000000000000000000000000000001"; break;
            case "wrongResource": challenge.Resource.Url = "https://attacker.invalid/other"; break;
            case "wrongScheme": option.Scheme = "not-exact"; break;
            case "permit2": option.Extra!.AssetTransferMethod = "permit2"; break;
            case "upfront": option.Extra!.PaymentFlow = "upfront"; break;
        }
        var header = Convert.ToBase64String(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(challenge, JsonOptions)));
        var calls = 0;
        handler.Protected().Setup<Task<HttpResponseMessage>>(
            "SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() =>
            {
                calls++;
                var response = new HttpResponseMessage((HttpStatusCode)402)
                    { Content = new StringContent("{}") };
                response.Headers.TryAddWithoutValidation("PAYMENT-REQUIRED", header);
                return response;
            });
        var result = await tools.MakePaidRequestAsync(TestUrl);
        result.Should().Contain(expected);
        calls.Should().Be(1);
        tools.GetPaymentHistory().Should().Contain("No payments");
    }

    [Fact]
    public async Task ProofRandomChallenge_MockPaymentEchoesAcceptedAndResource()
    {
        var (tools, handler) = CreateTools();
        using var fixtureJson = JsonDocument.Parse(System.IO.File.ReadAllText(
            System.IO.Path.Combine(AppContext.BaseDirectory, "live-challenge-fixture.json")));
        var challenge = JsonSerializer.Deserialize<X402PaymentRequired>(
            fixtureJson.RootElement.GetProperty("challenge").GetRawText(), JsonOptions)!;
        var requestUrl = challenge.Resource!.Url!;
        var header = Convert.ToBase64String(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(challenge, JsonOptions)));
        var calls = 0;
        handler.Protected().Setup<Task<HttpResponseMessage>>(
            "SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync((HttpRequestMessage request, CancellationToken _) =>
            {
                calls++;
                if (calls == 1)
                {
                    var response = new HttpResponseMessage((HttpStatusCode)402)
                        { Content = new StringContent("{}") };
                    response.Headers.TryAddWithoutValidation("PAYMENT-REQUIRED", header);
                    return response;
                }
                var signatureHeader = request.Headers.GetValues("PAYMENT-SIGNATURE").Single();
                using var payload = JsonDocument.Parse(Convert.FromBase64String(signatureHeader));
                payload.RootElement.GetProperty("resource").GetProperty("url").GetString()
                    .Should().Be(requestUrl);
                var accepted = payload.RootElement.GetProperty("accepted");
                accepted.GetProperty("amount").GetString().Should().Be("1000");
                accepted.GetProperty("asset").GetString().Should().Be(challenge.Accepts[0].Asset);
                accepted.TryGetProperty("maxAmountRequired", out var ignored).Should().BeFalse();
                accepted.TryGetProperty("resource", out var ignored2).Should().BeFalse();
                accepted.TryGetProperty("description", out var ignored3).Should().BeFalse();
                accepted.GetProperty("maxTimeoutSeconds").GetInt32().Should().Be(300);
                payload.RootElement.GetProperty("extensions").GetProperty("bazaar")
                    .ValueKind.Should().Be(JsonValueKind.Object);
                payload.RootElement.GetProperty("payload").GetProperty("authorization")
                    .GetProperty("value").GetString().Should().Be("1000");
                return new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new StringContent("{\"value\":3}") };
            });
        var result = await tools.MakePaidRequestAsync(requestUrl);
        calls.Should().Be(2);
        result.Should().Contain("{\"value\":3}");
        tools.GetPaymentHistory().Should().Contain("$0.0010");
    }

    // --- Tests ---

    [Fact]
    public async Task NonPaymentResponse_ReturnsContentDirectly()
    {
        var (tools, handler) = CreateTools();

        handler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"result\":\"hello\"}"),
            });

        var result = await tools.MakePaidRequestAsync(TestUrl);

        result.Should().Be("{\"result\":\"hello\"}");
    }

    [Fact]
    public async Task NonPayment_ErrorResponse_ReturnsErrorString()
    {
        var (tools, handler) = CreateTools();

        handler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("not found"),
            });

        var result = await tools.MakePaidRequestAsync(TestUrl);

        result.Should().Contain("Error 404");
        result.Should().Contain("not found");
    }

    [Fact]
    public async Task Payment402_FullFlow_SignsAndRetries()
    {
        var (tools, handler) = CreateTools();
        var headerValue = BuildPaymentRequiredHeader();
        var callCount = 0;

        handler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync((HttpRequestMessage req, CancellationToken _) =>
            {
                callCount++;
                if (callCount == 1)
                {
                    // First call: return 402
                    var resp = new HttpResponseMessage((HttpStatusCode)402)
                    {
                        Content = new StringContent(""),
                    };
                    resp.Headers.TryAddWithoutValidation("PAYMENT-REQUIRED", headerValue);
                    return resp;
                }
                else
                {
                    // Second call: return success with payment response
                    var resp = new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{\"analysis\":\"data\"}"),
                    };
                    var txJson = JsonSerializer.Serialize(new { transactionHash = "0xabc123" });
                    var txBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(txJson));
                    resp.Headers.TryAddWithoutValidation("PAYMENT-RESPONSE", txBase64);
                    return resp;
                }
            });

        var result = await tools.MakePaidRequestAsync(TestUrl);

        callCount.Should().Be(2);
        result.Should().Contain("Paid $0.01");
        result.Should().Contain("USDC");
        result.Should().Contain("tx: 0xabc123");
        result.Should().Contain("{\"analysis\":\"data\"}");
    }

    [Fact]
    public async Task Payment402_NetworkMismatch_ReturnsError()
    {
        var (tools, handler) = CreateTools();
        // Server requires Ethereum mainnet, wallet is on Base Sepolia
        var headerValue = BuildPaymentRequiredHeader(network: "eip155:1");

        handler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() =>
            {
                var resp = new HttpResponseMessage((HttpStatusCode)402)
                {
                    Content = new StringContent(""),
                };
                resp.Headers.TryAddWithoutValidation("PAYMENT-REQUIRED", headerValue);
                return resp;
            });

        var result = await tools.MakePaidRequestAsync(TestUrl);

        result.Should().Contain("Network mismatch");
        result.Should().Contain("eip155:1");
    }

    [Fact]
    public async Task Payment402_PriceExceedsLimit_ReturnsError()
    {
        var options = CreateOptions(budget: 10.0m, maxPrice: 0.001m);
        var wallet = new X402Wallet(options);
        var handlerMock = new Mock<HttpMessageHandler>();

        var httpClient = new HttpClient(handlerMock.Object);
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient("X402")).Returns(httpClient);
        var tools = new X402Tools(wallet, factory.Object, options);

        // Price is $0.01, limit is $0.001
        var headerValue = BuildPaymentRequiredHeader(amount: "10000");

        handlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() =>
            {
                var resp = new HttpResponseMessage((HttpStatusCode)402)
                {
                    Content = new StringContent(""),
                };
                resp.Headers.TryAddWithoutValidation("PAYMENT-REQUIRED", headerValue);
                return resp;
            });

        var result = await tools.MakePaidRequestAsync(TestUrl);

        result.Should().Contain("exceeds limit");
    }

    [Fact]
    public async Task Payment402_BudgetExceeded_ReturnsError()
    {
        var options = CreateOptions(budget: 0.005m);
        var wallet = new X402Wallet(options);
        var handlerMock = new Mock<HttpMessageHandler>();

        var httpClient = new HttpClient(handlerMock.Object);
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient("X402")).Returns(httpClient);
        var tools = new X402Tools(wallet, factory.Object, options);

        // Price is $0.01, budget is $0.005
        var headerValue = BuildPaymentRequiredHeader(amount: "10000");

        handlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() =>
            {
                var resp = new HttpResponseMessage((HttpStatusCode)402)
                {
                    Content = new StringContent(""),
                };
                resp.Headers.TryAddWithoutValidation("PAYMENT-REQUIRED", headerValue);
                return resp;
            });

        var result = await tools.MakePaidRequestAsync(TestUrl);

        result.Should().Contain("exceeds remaining budget");
    }

    [Fact]
    public async Task Payment402_AutoPayDisabled_ReturnsRequirements()
    {
        var options = CreateOptions(autoPay: false);
        var wallet = new X402Wallet(options);
        var handlerMock = new Mock<HttpMessageHandler>();

        var httpClient = new HttpClient(handlerMock.Object);
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient("X402")).Returns(httpClient);
        var tools = new X402Tools(wallet, factory.Object, options);

        var headerValue = BuildPaymentRequiredHeader();

        handlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() =>
            {
                var resp = new HttpResponseMessage((HttpStatusCode)402)
                {
                    Content = new StringContent(""),
                };
                resp.Headers.TryAddWithoutValidation("PAYMENT-REQUIRED", headerValue);
                return resp;
            });

        var result = await tools.MakePaidRequestAsync(TestUrl);

        result.Should().Contain("Auto-pay is disabled");
        result.Should().Contain(TestPayTo);
    }

    [Fact]
    public async Task Payment402_V1LegacyHeader_RejectsWithoutSigning()
    {
        var (tools, handler) = CreateTools();
        var headerValue = BuildPaymentRequiredHeader();
        var calls = 0;
        handler.Protected().Setup<Task<HttpResponseMessage>>(
            "SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() =>
            {
                calls++;
                var response = new HttpResponseMessage((HttpStatusCode)402)
                    { Content = new StringContent("{}") };
                response.Headers.TryAddWithoutValidation("X-PAYMENT-REQUIRED", headerValue);
                return response;
            });
        var result = await tools.MakePaidRequestAsync(TestUrl);
        result.Should().Contain("V1 payment challenge is not supported");
        calls.Should().Be(1);
        tools.GetPaymentHistory().Should().Contain("No payments");
    }

    [Fact]
    public void CheckBudget_ReturnsFormattedString()
    {
        var (tools, _) = CreateTools();

        var result = tools.CheckBudget();

        result.Should().Contain("Wallet: 0x");
        result.Should().Contain("Network: eip155:84532");
        result.Should().Contain("Budget: $10.00");
        result.Should().Contain("Spent: $0.0000");
        result.Should().Contain("Remaining: $10.0000");
    }

    [Fact]
    public void GetPaymentHistory_Empty_ReturnsMessage()
    {
        var (tools, _) = CreateTools();

        var result = tools.GetPaymentHistory();

        result.Should().Be("No payments have been made yet.");
    }

    [Fact]
    public async Task Payment402_FullFlow_PaymentAppearsInHistory()
    {
        var options = CreateOptions();
        var wallet = new X402Wallet(options);
        var handlerMock = new Mock<HttpMessageHandler>();

        var httpClient = new HttpClient(handlerMock.Object);
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient("X402")).Returns(httpClient);
        var tools = new X402Tools(wallet, factory.Object, options);

        var headerValue = BuildPaymentRequiredHeader();
        var callCount = 0;

        handlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync((HttpRequestMessage req, CancellationToken _) =>
            {
                callCount++;
                if (callCount == 1)
                {
                    var resp = new HttpResponseMessage((HttpStatusCode)402)
                    {
                        Content = new StringContent(""),
                    };
                    resp.Headers.TryAddWithoutValidation("PAYMENT-REQUIRED", headerValue);
                    return resp;
                }
                else
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("result"),
                    };
                }
            });

        await tools.MakePaidRequestAsync(TestUrl);

        var history = tools.GetPaymentHistory();
        history.Should().Contain("1 payments");
        history.Should().Contain("$0.01");
        history.Should().Contain(TestUrl);
    }

    [Fact]
    public void GetTools_ReturnsThreeAIFunctions()
    {
        var (tools, _) = CreateTools();

        var aiFunctions = tools.GetTools();

        aiFunctions.Should().HaveCount(3);
        aiFunctions.Select(f => f.Name).Should().Contain("MakePaidRequestAsync");
        aiFunctions.Select(f => f.Name).Should().Contain("CheckBudget");
        aiFunctions.Select(f => f.Name).Should().Contain("GetPaymentHistory");
    }

    [Fact]
    public void GetTools_FunctionsHaveDescriptions()
    {
        var (tools, _) = CreateTools();

        var aiFunctions = tools.GetTools();

        foreach (var fn in aiFunctions)
        {
            fn.Description.Should().NotBeNullOrEmpty(
                $"Function '{fn.Name}' should have a description");
        }
    }
}
