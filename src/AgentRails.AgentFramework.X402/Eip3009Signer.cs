using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using AgentRails.AgentFramework.X402.Models;
using Nethereum.Signer;
using Nethereum.Util;

namespace AgentRails.AgentFramework.X402;

/// <summary>
/// Signs EIP-3009 TransferWithAuthorization messages using local private keys.
/// Uses EIP-712 typed structured data hashing with Nethereum.
/// </summary>
public sealed class Eip3009Signer
{
    private readonly EthECKey _key;
    private readonly string _address;

    public Eip3009Signer(string privateKey)
    {
        var normalizedKey = privateKey.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? privateKey[2..]
            : privateKey;

        _key = new EthECKey(normalizedKey);
        _address = _key.GetPublicAddress();
    }

    /// <summary>
    /// The wallet address derived from the private key.
    /// </summary>
    public string Address => _address;

    /// <summary>
    /// Generate a random 32-byte nonce for EIP-3009.
    /// </summary>
    public static byte[] GenerateNonce()
    {
        var nonce = new byte[32];
        RandomNumberGenerator.Fill(nonce);
        return nonce;
    }

    /// <summary>
    /// Sign an EIP-3009 TransferWithAuthorization.
    /// Returns the 65-byte signature as a 0x-prefixed hex string.
    /// </summary>
    public string SignTransferAuthorization(
        string toAddress,
        string value,
        long validAfter,
        long validBefore,
        byte[] nonce,
        string network)
    {
        var networkInfo = X402NetworkConfig.GetNetwork(network)
            ?? throw new ArgumentException($"Unsupported network: {network}");

        var sha3 = new Sha3Keccack();

        // 1. Build domain separator
        var domainSeparator = BuildDomainSeparator(
            sha3, networkInfo.TokenName, networkInfo.TokenVersion,
            networkInfo.ChainId, networkInfo.UsdcContract);

        // 2. Build struct hash
        var structHash = BuildStructHash(
            sha3, _address, toAddress, value,
            validAfter, validBefore, nonce);

        // 3. Build EIP-712 digest: keccak256("\x19\x01" + domainSeparator + structHash)
        var digest = BuildEip712Digest(sha3, domainSeparator, structHash);

        // 4. Sign the digest
        var signature = _key.SignAndCalculateV(digest);

        // 5. Encode as r(32) + s(32) + v(1) = 65 bytes
        var sigBytes = new byte[65];
        var r = signature.R;
        var s = signature.S;
        Array.Copy(r, 0, sigBytes, 32 - r.Length, r.Length);
        Array.Copy(s, 0, sigBytes, 64 - s.Length, s.Length);
        sigBytes[64] = signature.V[0];

        return "0x" + Convert.ToHexString(sigBytes).ToLowerInvariant();
    }

    // --- Private helpers (same pattern as Eip3009SignatureVerifier) ---

    private static byte[] BuildDomainSeparator(
        Sha3Keccack sha3, string name, string version,
        int chainId, string verifyingContract)
    {
        var domainTypeHash = sha3.CalculateHash(
            Encoding.UTF8.GetBytes(
                "EIP712Domain(string name,string version,uint256 chainId,address verifyingContract)"));

        var nameHash = sha3.CalculateHash(Encoding.UTF8.GetBytes(name));
        var versionHash = sha3.CalculateHash(Encoding.UTF8.GetBytes(version));

        var encoded = new byte[32 * 5];
        Array.Copy(domainTypeHash, 0, encoded, 0, 32);
        Array.Copy(nameHash, 0, encoded, 32, 32);
        Array.Copy(versionHash, 0, encoded, 64, 32);

        var chainIdBytes = new BigInteger(chainId).ToByteArray(isUnsigned: true, isBigEndian: true);
        Array.Copy(chainIdBytes, 0, encoded, 96 + (32 - chainIdBytes.Length), chainIdBytes.Length);

        var contractBytes = HexToBytes(verifyingContract);
        Array.Copy(contractBytes, 0, encoded, 128 + (32 - contractBytes.Length), contractBytes.Length);

        return sha3.CalculateHash(encoded);
    }

    private static byte[] BuildStructHash(
        Sha3Keccack sha3, string from, string to, string value,
        long validAfter, long validBefore, byte[] nonce)
    {
        var typeHash = sha3.CalculateHash(
            Encoding.UTF8.GetBytes(
                "TransferWithAuthorization(address from,address to,uint256 value,uint256 validAfter,uint256 validBefore,bytes32 nonce)"));

        var encoded = new byte[32 * 7];
        Array.Copy(typeHash, 0, encoded, 0, 32);

        var fromBytes = HexToBytes(from);
        Array.Copy(fromBytes, 0, encoded, 32 + (32 - fromBytes.Length), fromBytes.Length);

        var toBytes = HexToBytes(to);
        Array.Copy(toBytes, 0, encoded, 64 + (32 - toBytes.Length), toBytes.Length);

        var valueBI = BigInteger.Parse(value);
        if (valueBI > BigInteger.Zero)
        {
            var valueBytes = valueBI.ToByteArray(isUnsigned: true, isBigEndian: true);
            Array.Copy(valueBytes, 0, encoded, 96 + (32 - valueBytes.Length), valueBytes.Length);
        }
        // else: leave as zeros for value=0

        if (validAfter > 0)
        {
            var validAfterBytes = new BigInteger(validAfter).ToByteArray(isUnsigned: true, isBigEndian: true);
            Array.Copy(validAfterBytes, 0, encoded, 128 + (32 - validAfterBytes.Length), validAfterBytes.Length);
        }

        var validBeforeBytes = new BigInteger(validBefore).ToByteArray(isUnsigned: true, isBigEndian: true);
        Array.Copy(validBeforeBytes, 0, encoded, 160 + (32 - validBeforeBytes.Length), validBeforeBytes.Length);

        // nonce is bytes32 — pad/truncate to exactly 32 bytes
        var paddedNonce = new byte[32];
        if (nonce.Length <= 32)
            Array.Copy(nonce, 0, paddedNonce, 32 - nonce.Length, nonce.Length);
        else
            Array.Copy(nonce, 0, paddedNonce, 0, 32);
        Array.Copy(paddedNonce, 0, encoded, 192, 32);

        return sha3.CalculateHash(encoded);
    }

    private static byte[] BuildEip712Digest(
        Sha3Keccack sha3, byte[] domainSeparator, byte[] structHash)
    {
        var message = new byte[66];
        message[0] = 0x19;
        message[1] = 0x01;
        Array.Copy(domainSeparator, 0, message, 2, 32);
        Array.Copy(structHash, 0, message, 34, 32);
        return sha3.CalculateHash(message);
    }

    internal static byte[] HexToBytes(string hex)
    {
        if (string.IsNullOrEmpty(hex)) return Array.Empty<byte>();
        if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) hex = hex[2..];
        if (hex.Length % 2 != 0) hex = "0" + hex;

        var bytes = new byte[hex.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
            bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        return bytes;
    }
}
