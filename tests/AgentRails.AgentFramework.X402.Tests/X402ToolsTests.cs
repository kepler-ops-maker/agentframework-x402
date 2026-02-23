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
            Accepts =
            [
                new X402PaymentOption
                {
                    Scheme = "exact",
                    Network = network,
                    MaxAmountRequired = amount,
                    PayTo = payTo,
                    Resource = TestUrl,
                    Asset = "USDC",
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
    public async Task Payment402_V1LegacyHeader_StillWorks()
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
                    // Use V1 legacy header name
                    var resp = new HttpResponseMessage((HttpStatusCode)402)
                    {
                        Content = new StringContent(""),
                    };
                    resp.Headers.TryAddWithoutValidation("X-PAYMENT-REQUIRED", headerValue);
                    return resp;
                }
                else
                {
                    var resp = new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{\"data\":\"ok\"}"),
                    };
                    return resp;
                }
            });

        var result = await tools.MakePaidRequestAsync(TestUrl);

        callCount.Should().Be(2);
        result.Should().Contain("Paid");
        result.Should().Contain("{\"data\":\"ok\"}");
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
