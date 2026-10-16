using Microsoft.Extensions.DependencyInjection;
using RecipeParser.Application.Services;
using RecipeParser.Domain.Discovery;
using RecipeParser.Domain.Interfaces;

namespace RecipeParser.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddRecipeParserApplication(this IServiceCollection services)
    {
        services.AddTransient<RecipeParserService>();
        services.AddSingleton<IRecipeParserService, CachedRecipeParserService>();
        services.AddScoped<IDiscoveryProfileService, DiscoveryProfileService>();
        services.AddScoped<IDiscoveryRankingService, DiscoveryRankingService>();
        services.AddScoped<IDiscoveryFeedService, DiscoveryFeedService>();
        return services;
    }
}
