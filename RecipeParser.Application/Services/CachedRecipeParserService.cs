using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RecipeParser.Domain.Interfaces;
using RecipeParser.Domain.Models;

namespace RecipeParser.Application.Services;

public sealed class CachedRecipeParserService(IServiceScopeFactory scopes, ILogger<CachedRecipeParserService> logger)
    : IRecipeParserService, IDisposable
{
    private const string ParserVersion = "v2";
    private readonly MemoryCache cache = new(new MemoryCacheOptions { SizeLimit = 32 * 1024 * 1024 });
    private readonly ConcurrentDictionary<string, Lazy<Task<CachedRecipe>>> flights = new();
    private static readonly Meter Meter = new("Sporkast.RecipeParser.Cache");
    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>("recipe.parse.request.duration", "ms");

    public async Task<Recipe> ParseRecipeByUrl(string url, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var key = ParserVersion + ":" + NormalizeUrl(url);
        var started = Stopwatch.GetTimestamp();
        var outcome = "hit";
        try
        {
            if (!cache.TryGetValue(key, out CachedRecipe? cached))
            {
                var proposed = new Lazy<Task<CachedRecipe>>(() => ParseAndCache(key, url));
                var flight = flights.GetOrAdd(key, proposed);
                outcome = ReferenceEquals(proposed, flight) ? "miss" : "joined";
                // A cancelled client must not cancel an import being awaited by other clients.
                cached = await flight.Value.WaitAsync(ct);
            }
            var recipe = JsonSerializer.Deserialize<Recipe>(cached!.Json)!;
            recipe.RawIngredients = cached.RawIngredients.ToList();
            return recipe;
        }
        catch { outcome = "failure"; throw; }
        finally
        {
            var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            Duration.Record(elapsed, new KeyValuePair<string, object?>("cache", outcome));
            logger.LogInformation("Recipe import cache {CacheOutcome}: {DurationMs}ms", outcome, elapsed);
        }
    }

    private async Task<CachedRecipe> ParseAndCache(string key, string url)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var parser = scope.ServiceProvider.GetRequiredService<RecipeParserService>();
            var recipe = await parser.ParseRecipeByUrl(url);
            var result = new CachedRecipe(JsonSerializer.SerializeToUtf8Bytes(recipe), recipe.RawIngredients.ToArray());
            if (!string.IsNullOrWhiteSpace(recipe.Title) && recipe.Ingredients.Count > 0
                && recipe.StepSections.Any(section => section.Steps?.Count > 0))
                cache.Set(key, result, new MemoryCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(6),
                    Size = result.Json.Length + result.RawIngredients.Sum(line => line.Length * 2L)
                });
            return result;
        }
        finally { flights.TryRemove(key, out _); }
    }

    public static string NormalizeUrl(string url)
    {
        var uri = new Uri(url, UriKind.Absolute);
        if (uri.Scheme is not ("http" or "https")) throw new ArgumentException("A web URL is required.", nameof(url));
        var builder = new UriBuilder(uri) { Fragment = "" };
        // Query order can be significant for signed URLs.
        builder.Query = string.Join("&", uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(part => !IsTrackingParameter(part.Split('=')[0])));
        return builder.Uri.AbsoluteUri;
    }

    private static bool IsTrackingParameter(string name)
    {
        name = Uri.UnescapeDataString(name);
        return name.StartsWith("utm_", StringComparison.OrdinalIgnoreCase)
            || name.Equals("fbclid", StringComparison.OrdinalIgnoreCase)
            || name.Equals("gclid", StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose() => cache.Dispose();
    private sealed record CachedRecipe(byte[] Json, string[] RawIngredients);
}
