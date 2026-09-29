# AgentRails.AgentFramework.X402

x402 payment tools for [Microsoft Agent Framework](https://learn.microsoft.com/en-us/dotnet/ai/) (via `Microsoft.Extensions.AI`). Enables .NET AI agents to make HTTP requests with automatic [x402](https://www.x402.org/) payment handling. When an API returns HTTP 402 Payment Required, the tools automatically sign a USDC payment authorization (EIP-3009) and retry the request.

Built by [AgentRails](https://www.agentrails.io) — the first x402 integration for the Microsoft Agent Framework ecosystem.

> **Note:** If you're using Semantic Kernel, see the companion package [AgentRails.SemanticKernel.X402](https://www.nuget.org/packages/AgentRails.SemanticKernel.X402).

## Installation

```bash
dotnet add package AgentRails.AgentFramework.X402
```

## Quick Start

```csharp
using AgentRails.AgentFramework.X402;
using AgentRails.AgentFramework.X402.Extensions;
using AgentRails.AgentFramework.X402.Models;
using Microsoft.Extensions.AI;

// Create x402 tools
var options = new X402PluginOptions
{
    PrivateKey = Environment.GetEnvironmentVariable("WALLET_PRIVATE_KEY")!,
    Network = "eip155:84532",  // Base Sepolia testnet
    BudgetUsd = 5.00m,
};
var wallet = new X402Wallet(options);
var httpFactory = /* your IHttpClientFactory */;
var x402 = new X402Tools(wallet, httpFactory, options);

// Register as tools on any IChatClient-based agent
IChatClient chatClient = /* your chat client (OpenAI, Azure, Ollama, etc.) */;
var response = await chatClient.GetResponseAsync(
    "Get the analysis from https://sandbox.agentrails.io/api/x402/protected/analysis",
    new ChatOptions { Tools = x402.GetTools() });
Console.WriteLine(response);
```

### With Dependency Injection

```csharp
using AgentRails.AgentFramework.X402.Extensions;

// In your service registration:
builder.Services.AddX402Tools(options =>
{
    options.PrivateKey = Environment.GetEnvironmentVariable("WALLET_PRIVATE_KEY")!;
    options.Network = "eip155:84532";
    options.BudgetUsd = 5.00m;
});

// Later, resolve and use:
var x402 = serviceProvider.GetRequiredService<X402Tools>();
var tools = x402.GetTools(); // IList<AIFunction>
```

### appsettings.json Configuration

```json
{
  "X402": {
    "PrivateKey": "your-hex-private-key",
    "Network": "eip155:84532",
    "BudgetUsd": 10.00,
    "DefaultMaxPriceUsd": 1.00,
    "AutoPay": true,
    "TimeoutSeconds": 30
  }
}
```

```csharp
builder.Services.AddX402Tools(configuration.GetSection("X402"));
```

## Features

- **Automatic 402 handling** — Detects HTTP 402 responses, signs EIP-3009 payment authorizations, retries transparently
- **Budget management** — Per-session USD budget with real-time tracking
- **Thread-safe** — Safe for concurrent use across multiple agent invocations
- **x402 V2 compliant** — Supports V2 protocol; legacy V1 challenges are rejected without signing
- **Multi-network** — Base Sepolia, Ethereum Sepolia, Arc Testnet, Base Mainnet, Ethereum Mainnet
- **Local signing** — Uses Nethereum for local EIP-712 signing, no external services required
- **Agent Framework native** — Exposes tools as `IList<AIFunction>` via `AIFunctionFactory`

## Tools

The package exposes three AI functions:

| Function | Description |
|----------|-------------|
| `MakePaidRequestAsync` | Make an HTTP request to any URL. Automatically handles x402 payment if required. |
| `CheckBudget` | Check wallet address, network, budget, spending, and remaining balance. |
| `GetPaymentHistory` | Get the history of all x402 payments made during this session. |

### MakePaidRequestAsync Parameters

| Parameter | Type | Default | Description |
|-----------|------|---------|-------------|
| `url` | string | required | The full URL to request |
| `method` | string | "GET" | HTTP method: GET, POST, PUT, DELETE, PATCH |
| `body` | string? | null | Request body as JSON string |
| `maxPriceUsd` | double? | null | Maximum USD willing to pay (overrides default) |

## Configuration Options

| Option | Default | Description |
|--------|---------|-------------|
| `PrivateKey` | required | Hex-encoded Ethereum private key (with or without 0x prefix) |
| `Network` | `"eip155:8453"` | CAIP-2 network identifier or legacy name |
| `BudgetUsd` | `10.00` | Total USD budget for the session |
| `DefaultMaxPriceUsd` | `1.00` | Default per-request price cap |
| `AutoPay` | `true` | Automatically sign and pay, or return requirements for approval |
| `TimeoutSeconds` | `30` | HTTP request timeout |

### Supported Networks

| CAIP-2 ID | Legacy Name | Network |
|-----------|-------------|---------|
| `eip155:84532` | `base-sepolia` | Base Sepolia (testnet) |
| `eip155:11155111` | `ethereum-sepolia` | Ethereum Sepolia (testnet) |
| `eip155:5042002` | `arc-testnet` | Arc Testnet |
| `eip155:8453` | `base-mainnet` | Base (mainnet) |
| `eip155:1` | `ethereum-mainnet` | Ethereum (mainnet) |

## How It Works

1. Your AI agent calls `MakePaidRequestAsync` with a URL
2. The tool makes the HTTP request
3. If the server returns **200 OK**, the response is returned directly
4. If the server returns **402 Payment Required**:
   - Parses the `PAYMENT-REQUIRED` header (V2); rejects legacy V1 challenges without signing
   - Finds a compatible payment option matching the wallet's network
   - Checks the price against the per-request limit and session budget
   - Signs an EIP-3009 `TransferWithAuthorization` message locally
   - Retries the request with the `PAYMENT-SIGNATURE` header
   - Returns the response prefixed with payment details

## Security

- **Private keys stay local** — All signing happens in-process using Nethereum. No keys are ever sent over the network.
- **Budget enforcement** — Hard USD cap prevents runaway spending. Thread-safe budget tracking.
- **Per-request limits** — Each request has a configurable max price. The agent can also set limits per-call.
- **AutoPay toggle** — Set `AutoPay = false` to require explicit approval before any payment.

## Requirements

- .NET 8.0+
- Microsoft.Extensions.AI 9.3.0+

## License

MIT — see [LICENSE](LICENSE)

## Related Packages

- [AgentRails.SemanticKernel.X402](https://www.nuget.org/packages/AgentRails.SemanticKernel.X402) - x402 for Semantic Kernel (.NET)
- [langchain-x402](https://pypi.org/project/langchain-x402/) - x402 integration for LangChain (Python)
- [crewai-x402](https://pypi.org/project/crewai-x402/) - x402 integration for CrewAI (Python)

## Links

- [AgentRails](https://www.agentrails.io) - AI agent payment infrastructure
- [AgentRails Documentation](https://www.agentrails.io/docs)
- [x402 Protocol](https://www.x402.org/)
- [Microsoft.Extensions.AI](https://www.nuget.org/packages/Microsoft.Extensions.AI)
- [Semantic Kernel version](https://www.nuget.org/packages/AgentRails.SemanticKernel.X402)
