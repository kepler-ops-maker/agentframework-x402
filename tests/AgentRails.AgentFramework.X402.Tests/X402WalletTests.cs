using AgentRails.AgentFramework.X402;
using AgentRails.AgentFramework.X402.Models;
using FluentAssertions;
using Xunit;

namespace AgentRails.AgentFramework.X402.Tests;

public class X402WalletTests
{
    private const string TestPrivateKey = "ac0974bec39a17e36ba4a6b4d238ff944bacb478cbed5efcae784d7bf4f2ff80";
    private const string TestAddress = "0xf39Fd6e51aad88F6F4ce6aB8827279cffFb92266";

    private static X402PluginOptions CreateOptions(decimal budget = 10.0m) => new()
    {
        PrivateKey = TestPrivateKey,
        Network = "eip155:84532",
        BudgetUsd = budget,
    };

    [Fact]
    public void Initialization_SetsCorrectProperties()
    {
        var wallet = new X402Wallet(CreateOptions(5.0m));

        wallet.Address.Should().BeEquivalentTo(TestAddress);
        wallet.Network.Should().Be("eip155:84532");
        wallet.BudgetUsd.Should().Be(5.0m);
        wallet.SpentUsd.Should().Be(0m);
        wallet.RemainingUsd.Should().Be(5.0m);
        wallet.PaymentCount.Should().Be(0);
    }

    [Fact]
    public void Initialization_LegacyNetworkName_NormalizesToCaip2()
    {
        var options = new X402PluginOptions
        {
            PrivateKey = TestPrivateKey,
            Network = "base-sepolia",
        };
        var wallet = new X402Wallet(options);

        wallet.Network.Should().Be("eip155:84532");
    }

    [Fact]
    public void Initialization_UnsupportedNetwork_Throws()
    {
        var options = new X402PluginOptions
        {
            PrivateKey = TestPrivateKey,
            Network = "eip155:999999",
        };

        var act = () => new X402Wallet(options);
        act.Should().Throw<ArgumentException>().WithMessage("*Unsupported network*");
    }

    [Fact]
    public void CanAfford_WithinBudget_ReturnsTrue()
    {
        var wallet = new X402Wallet(CreateOptions(10.0m));
        wallet.CanAfford(5.0m).Should().BeTrue();
        wallet.CanAfford(10.0m).Should().BeTrue();
    }

    [Fact]
    public void CanAfford_ExceedsBudget_ReturnsFalse()
    {
        var wallet = new X402Wallet(CreateOptions(1.0m));
        wallet.CanAfford(1.01m).Should().BeFalse();
    }

    [Fact]
    public void UnitsToUsd_CorrectConversion()
    {
        X402Wallet.UnitsToUsd(1_000_000).Should().Be(1.0m);
        X402Wallet.UnitsToUsd(10_000).Should().Be(0.01m);
        X402Wallet.UnitsToUsd(100).Should().Be(0.0001m);
    }

    [Fact]
    public void UsdToUnits_CorrectConversion()
    {
        X402Wallet.UsdToUnits(1.0m).Should().Be(1_000_000);
        X402Wallet.UsdToUnits(0.01m).Should().Be(10_000);
    }

    [Fact]
    public void SignPayment_TracksSpending()
    {
        var wallet = new X402Wallet(CreateOptions(10.0m));
        var validBefore = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds();

        var (sig, nonce) = wallet.SignPayment(
            "0x70997970C51812dc3A010C7d01b50e0d17dc79C8",
            100_000, // $0.10
            validBefore,
            "https://api.example.com/data");

        sig.Should().StartWith("0x").And.HaveLength(132);
        nonce.Should().StartWith("0x");
        wallet.SpentUsd.Should().Be(0.10m);
        wallet.RemainingUsd.Should().Be(9.90m);
        wallet.PaymentCount.Should().Be(1);
    }

    [Fact]
    public void SignPayment_RecordsPaymentHistory()
    {
        var wallet = new X402Wallet(CreateOptions(10.0m));
        var validBefore = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds();

        wallet.SignPayment(
            "0x70997970C51812dc3A010C7d01b50e0d17dc79C8",
            10_000, validBefore, "https://example.com/a");
        wallet.SignPayment(
            "0x3C44CdDdB6a900fa2b585dd299e03d12FA4293BC",
            20_000, validBefore, "https://example.com/b");

        var payments = wallet.GetPayments();
        payments.Should().HaveCount(2);
        payments[0].AmountUsd.Should().Be(0.01m);
        payments[0].ResourceUrl.Should().Be("https://example.com/a");
        payments[1].AmountUsd.Should().Be(0.02m);
        payments[1].ToAddress.Should().Be("0x3C44CdDdB6a900fa2b585dd299e03d12FA4293BC");
    }

    [Fact]
    public void SignPayment_ExceedsBudget_Throws()
    {
        var wallet = new X402Wallet(CreateOptions(0.01m));
        var validBefore = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds();

        var act = () => wallet.SignPayment(
            "0x70997970C51812dc3A010C7d01b50e0d17dc79C8",
            100_000, // $0.10 — exceeds $0.01 budget
            validBefore);

        act.Should().Throw<InvalidOperationException>().WithMessage("*Budget exceeded*");
    }

    [Fact]
    public void ResetBudget_ClearsHistoryAndSpending()
    {
        var wallet = new X402Wallet(CreateOptions(10.0m));
        var validBefore = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds();

        wallet.SignPayment(
            "0x70997970C51812dc3A010C7d01b50e0d17dc79C8",
            1_000_000, validBefore);

        wallet.SpentUsd.Should().Be(1.0m);
        wallet.PaymentCount.Should().Be(1);

        wallet.ResetBudget(20.0m);

        wallet.SpentUsd.Should().Be(0m);
        wallet.RemainingUsd.Should().Be(20.0m);
        wallet.BudgetUsd.Should().Be(20.0m);
        wallet.PaymentCount.Should().Be(0);
    }

    [Fact]
    public async Task ThreadSafety_ConcurrentSignPayments()
    {
        var wallet = new X402Wallet(CreateOptions(100.0m));
        var validBefore = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds();
        var tasks = new Task[10];

        for (int i = 0; i < 10; i++)
        {
            tasks[i] = Task.Run(() =>
            {
                wallet.SignPayment(
                    "0x70997970C51812dc3A010C7d01b50e0d17dc79C8",
                    10_000, // $0.01 each
                    validBefore);
            });
        }

        await Task.WhenAll(tasks);

        wallet.SpentUsd.Should().Be(0.10m);
        wallet.PaymentCount.Should().Be(10);
        wallet.GetPayments().Should().HaveCount(10);
    }
}
