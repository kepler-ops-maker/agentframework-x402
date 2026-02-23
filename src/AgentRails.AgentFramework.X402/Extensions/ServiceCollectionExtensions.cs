using AgentRails.AgentFramework.X402.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AgentRails.AgentFramework.X402.Extensions;

/// <summary>
/// Extension methods for registering x402 tools services.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register the x402 tools with lambda configuration.
    /// </summary>
    /// <example>
    /// <code>
    /// services.AddX402Tools(options =>
    /// {
    ///     options.PrivateKey = "your-private-key";
    ///     options.Network = "eip155:84532";
    ///     options.BudgetUsd = 5.00m;
    /// });
    ///
    /// var x402 = serviceProvider.GetRequiredService&lt;X402Tools&gt;();
    /// var tools = x402.GetTools(); // IList&lt;AIFunction&gt;
    /// </code>
    /// </example>
    public static IServiceCollection AddX402Tools(
        this IServiceCollection services,
        Action<X402PluginOptions> configure)
    {
        var options = new X402PluginOptions();
        configure(options);

        services.Configure<X402PluginOptions>(o =>
        {
            o.PrivateKey = options.PrivateKey;
            o.Network = options.Network;
            o.BudgetUsd = options.BudgetUsd;
            o.DefaultMaxPriceUsd = options.DefaultMaxPriceUsd;
            o.AutoPay = options.AutoPay;
            o.TimeoutSeconds = options.TimeoutSeconds;
        });

        services.AddSingleton(new X402Wallet(options));
        services.AddHttpClient("X402");
        services.AddTransient<X402Tools>();

        return services;
    }

    /// <summary>
    /// Register the x402 tools from an IConfiguration section.
    /// </summary>
    /// <example>
    /// <code>
    /// services.AddX402Tools(configuration.GetSection("X402"));
    ///
    /// var x402 = serviceProvider.GetRequiredService&lt;X402Tools&gt;();
    /// var tools = x402.GetTools(); // IList&lt;AIFunction&gt;
    /// </code>
    /// </example>
    public static IServiceCollection AddX402Tools(
        this IServiceCollection services,
        IConfigurationSection section)
    {
        services.Configure<X402PluginOptions>(section);

        var options = new X402PluginOptions();
        section.Bind(options);

        services.AddSingleton(new X402Wallet(options));
        services.AddHttpClient("X402");
        services.AddTransient<X402Tools>();

        return services;
    }
}
