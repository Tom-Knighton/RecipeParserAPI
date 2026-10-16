using System;
using System.Net;
using RecipeParser.Domain.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;
using RecipeParser.Domain.Discovery;
using RecipeParser.Domain.Interfaces;
using RecipeParser.Infrastructure.Discovery;
using RecipeParser.Infrastructure.Services;

namespace RecipeParser.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddRecipeParserInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<ParserPerformanceOptions>()
            .Bind(configuration.GetSection("ParserPerformance"))
            .Validate(o => o.TotalTimeoutSeconds is >= 1 and <= 120
                && o.StaticTimeoutSeconds is >= 1 and <= 30
                && o.RenderedTimeoutSeconds is >= 1 and <= 30
                && o.LinkedPageTimeoutSeconds is >= 1 and <= 15
                && o.MaxLinkedPages is >= 0 and <= 5, "Invalid parser performance limits")
            .ValidateOnStart();
        services.AddSingleton<IPlaywrightBrowser, PlaywrightBrowser>();
        services.AddHttpClient<IPageFetcher, PlaywrightPageFetcher>((provider, client) =>
        {
            client.Timeout = TimeSpan.FromSeconds(provider.GetRequiredService<IOptions<ParserPerformanceOptions>>().Value.StaticTimeoutSeconds);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Mobile/15E148 Safari/604.1");
            client.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        }).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            MaxConnectionsPerServer = 16,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        });

        services.Configure<OpenAiRecipeParserOptions>(configuration.GetSection("OpenAI"));
        services.Configure<DiscoveryOptions>(configuration.GetSection("Discovery"));
        services.AddDbContext<DiscoveryDbContext>(options =>
            options.UseNpgsql(configuration.GetConnectionString("Discovery")));

        services.AddScoped<IDiscoveryStore, EfDiscoveryStore>();
        services.AddScoped<IDiscoveryCandidateProvider, ConfiguredDiscoveryCandidateProvider>();
        services.AddHttpClient<IDiscoveryCandidateIngestionService, DiscoveryCandidateIngestionService>((serviceProvider, client) =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<DiscoveryOptions>>().Value;
            client.Timeout = TimeSpan.FromSeconds(Math.Clamp(options.SourceRequestTimeoutSeconds, 3, 60));
            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "SporkastRecipeDiscovery/1.0 (+https://sporkast.app; recipe metadata discovery)");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/rss+xml,application/atom+xml,application/xml;q=0.9,text/html;q=0.8,*/*;q=0.5");
        });
        services.AddHostedService<DiscoveryStartupSyncService>();
        services.AddHostedService<DiscoveryCandidateRefreshService>();

        services.AddHttpClient<IRecipeDescriptionParser, OpenAiRecipeDescriptionParser>((serviceProvider, client) =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<OpenAiRecipeParserOptions>>().Value;
            client.BaseAddress = new Uri("https://api.openai.com/v1/");
            client.Timeout = TimeSpan.FromSeconds(30);

            if (!string.IsNullOrWhiteSpace(options.ApiKey))
                client.DefaultRequestHeaders.Authorization = new("Bearer", options.ApiKey);
        });
        services.AddHttpClient<IDiscoveryReasonService, DiscoveryReasonService>((serviceProvider, client) =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<OpenAiRecipeParserOptions>>().Value;
            client.BaseAddress = new Uri("https://api.openai.com/v1/");
            client.Timeout = TimeSpan.FromSeconds(15);

            if (!string.IsNullOrWhiteSpace(options.ApiKey))
                client.DefaultRequestHeaders.Authorization = new("Bearer", options.ApiKey);
        });

        return services;
    }
}
