using AgentRails.AgentFramework.X402;
using FluentAssertions;
using Xunit;

namespace AgentRails.AgentFramework.X402.Tests;

public class Eip3009SignerTests
{
    // Well-known test private key (Hardhat account #0) — NEVER use in production
    private const string TestPrivateKey = "ac0974bec39a17e36ba4a6b4d238ff944bacb478cbed5efcae784d7bf4f2ff80";
    private const string TestAddress = "0xf39Fd6e51aad88F6F4ce6aB8827279cffFb92266";

    [Fact]
    public void Address_DerivedFromKnownTestKey_MatchesExpected()
    {
        var signer = new Eip3009Signer(TestPrivateKey);
        signer.Address.Should().BeEquivalentTo(TestAddress);
    }

    [Fact]
    public void Address_WithOxPrefix_MatchesExpected()
    {
        var signer = new Eip3009Signer("0x" + TestPrivateKey);
        signer.Address.Should().BeEquivalentTo(TestAddress);
    }

    [Fact]
    public void SignTransferAuthorization_ProducesValid65ByteSignature()
    {
        var signer = new Eip3009Signer(TestPrivateKey);
        var nonce = Eip3009Signer.GenerateNonce();
        var validBefore = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();

        var signature = signer.SignTransferAuthorization(
            toAddress: "0x70997970C51812dc3A010C7d01b50e0d17dc79C8",
            value: "1000000",
            validAfter: 0,
            validBefore: validBefore,
            nonce: nonce,
            network: "eip155:84532");

        // Verify format: 0x + 130 hex chars (65 bytes)
        signature.Should().StartWith("0x");
        signature.Length.Should().Be(132);

        // V byte should be 27 or 28
        var vByte = Convert.ToByte(signature.Substring(130, 2), 16);
        vByte.Should().BeOneOf((byte)27, (byte)28);
    }

    [Fact]
    public void SignTransferAuthorization_SameInputs_ProduceSameSignature()
    {
        var signer = new Eip3009Signer(TestPrivateKey);
        var nonce = Eip3009Signer.GenerateNonce();
        var validBefore = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();

        var sig1 = signer.SignTransferAuthorization(
            "0x70997970C51812dc3A010C7d01b50e0d17dc79C8",
            "1000000", 0, validBefore, nonce, "eip155:84532");

        var sig2 = signer.SignTransferAuthorization(
            "0x70997970C51812dc3A010C7d01b50e0d17dc79C8",
            "1000000", 0, validBefore, nonce, "eip155:84532");

        sig1.Should().Be(sig2);
    }

    [Fact]
    public void SignTransferAuthorization_DifferentNetworks_ProduceDifferentSignatures()
    {
        var signer = new Eip3009Signer(TestPrivateKey);
        var nonce = Eip3009Signer.GenerateNonce();
        var validBefore = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();

        var sigBaseSepolia = signer.SignTransferAuthorization(
            "0x70997970C51812dc3A010C7d01b50e0d17dc79C8",
            "1000000", 0, validBefore, nonce, "eip155:84532");

        var sigArcTestnet = signer.SignTransferAuthorization(
            "0x70997970C51812dc3A010C7d01b50e0d17dc79C8",
            "1000000", 0, validBefore, nonce, "eip155:5042002");

        sigBaseSepolia.Should().NotBe(sigArcTestnet);
    }

    [Fact]
    public void SignTransferAuthorization_InvalidNetwork_Throws()
    {
        var signer = new Eip3009Signer(TestPrivateKey);
        var nonce = Eip3009Signer.GenerateNonce();

        var act = () => signer.SignTransferAuthorization(
            "0x70997970C51812dc3A010C7d01b50e0d17dc79C8",
            "1000000", 0, 9999999999, nonce, "eip155:999999");

        act.Should().Throw<ArgumentException>().WithMessage("*Unsupported network*");
    }

    [Fact]
    public void SignTransferAuthorization_LegacyNetworkName_Works()
    {
        var signer = new Eip3009Signer(TestPrivateKey);
        var nonce = Eip3009Signer.GenerateNonce();
        var validBefore = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();

        var sig = signer.SignTransferAuthorization(
            "0x70997970C51812dc3A010C7d01b50e0d17dc79C8",
            "1000000", 0, validBefore, nonce, "base-sepolia");

        sig.Should().StartWith("0x");
        sig.Length.Should().Be(132);
    }

    [Fact]
    public void GenerateNonce_Returns32Bytes()
    {
        var nonce = Eip3009Signer.GenerateNonce();
        nonce.Length.Should().Be(32);
    }

    [Fact]
    public void GenerateNonce_IsRandom()
    {
        var nonce1 = Eip3009Signer.GenerateNonce();
        var nonce2 = Eip3009Signer.GenerateNonce();
        nonce1.Should().NotEqual(nonce2);
    }
}
