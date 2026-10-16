using System.Diagnostics;
using Jering.Javascript.NodeJS;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using NUnit.Framework;
using RecipeParser.Application;
using RecipeParser.Application.Services;
using RecipeParser.Domain.Interfaces;
using RecipeParser.Domain.Models;
using Shouldly;

namespace RecipeParser.Services.UnitTests;

public class ParserPerformanceTests
{
    private const string Url = "https://example.com/soup?id=1";
    private const string Html = """
        <script type="application/ld+json">{"@type":"Recipe","name":"Soup",
        "recipeIngredient":["1 onion"],"recipeInstructions":["Cook for 2 minutes."]}</script>
        """;

    [Test]
    public async Task ConcurrentAndCachedImportsFetchOnceAndReturnIndependentRecipes()
    {
        var fetcher = new ControlledFetcher();
        using var services = CreateServices(fetcher);
        var parser = services.GetRequiredService<IRecipeParserService>();
        var first = parser.ParseRecipeByUrl(Url);
        await fetcher.Started.Task;
        var second = parser.ParseRecipeByUrl(Url + "&utm_source=share#recipe");
        fetcher.Release.SetResult();
        var recipes = await Task.WhenAll(first, second);
        fetcher.StaticCalls.ShouldBe(1);
        recipes[0].Ingredients.Single().Quantity.ShouldBe(1);
        recipes[0].StepSections.Single().Steps!.Single().Times.Single().TimeInSeconds.ShouldBe(120);
        recipes[0].Title = "Locally edited";
        (await parser.ParseRecipeByUrl(Url)).Title.ShouldBe("Soup");
        fetcher.StaticCalls.ShouldBe(1);
        var node = services.GetRequiredService<INodeJSService>();
        await node.Received(1).InvokeFromFileAsync<RecipeEnrichmentResult>(
            "recipeParser.cjs", "parseRecipe", Arg.Any<object[]>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CancelledWaiterDoesNotCancelSharedImport()
    {
        var fetcher = new ControlledFetcher();
        using var services = CreateServices(fetcher);
        var parser = services.GetRequiredService<IRecipeParserService>();
        var first = parser.ParseRecipeByUrl(Url);
        await fetcher.Started.Task;
        using var cancelled = new CancellationTokenSource();
        var waiter = parser.ParseRecipeByUrl(Url, cancelled.Token);
        cancelled.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => waiter);
        fetcher.Release.SetResult();
        (await first).Title.ShouldBe("Soup");
        (await parser.ParseRecipeByUrl(Url)).Title.ShouldBe("Soup");
        fetcher.StaticCalls.ShouldBe(1);
    }

    [Test]
    public async Task FailedImportsAreNotCached()
    {
        var fetcher = new ControlledFetcher { FailuresRemaining = 1 };
        fetcher.Release.SetResult();
        using var services = CreateServices(fetcher);
        var parser = services.GetRequiredService<IRecipeParserService>();
        await Should.ThrowAsync<HttpRequestException>(() => parser.ParseRecipeByUrl(Url));
        (await parser.ParseRecipeByUrl(Url)).Title.ShouldBe("Soup");
        fetcher.StaticCalls.ShouldBe(2);
    }

    [Test]
    public async Task OverallDeadlineCancelsUnderlyingFetch()
    {
        var fetcher = new ControlledFetcher();
        using var services = CreateServices(fetcher);
        var sut = new RecipeParserService(services.GetRequiredService<INodeJSService>(), fetcher,
            performanceOptions: Options.Create(new ParserPerformanceOptions { TotalTimeoutSeconds = 1 }));
        await Should.ThrowAsync<OperationCanceledException>(() => sut.ParseRecipeByUrl(Url));
        fetcher.Cancelled.ShouldBeTrue();
        fetcher.RenderedCalls.ShouldBe(0);
    }

    [Test]
    public async Task RenderedFirstHostSkipsStaticFetch()
    {
        var fetcher = new ControlledFetcher();
        using var services = CreateServices(fetcher);
        var sut = new RecipeParserService(services.GetRequiredService<INodeJSService>(), fetcher,
            performanceOptions: Options.Create(new ParserPerformanceOptions { RenderedFirstHosts = ["example.com"] }));
        (await sut.ParseRecipeByUrl(Url)).Title.ShouldBe("Soup");
        fetcher.StaticCalls.ShouldBe(0);
        fetcher.RenderedCalls.ShouldBe(1);
    }

    [Test]
    public void CacheKeyPreservesRecipeQueryAndPathCase()
    {
        CachedRecipeParserService.NormalizeUrl("https://EXAMPLE.com/Soup?id=1&utm_source=x#recipe").ShouldBe("https://example.com/Soup?id=1");
        CachedRecipeParserService.NormalizeUrl(Url).ShouldNotBe(CachedRecipeParserService.NormalizeUrl("https://example.com/soup?id=2"));
    }

    [Test]
    public async Task SocialLinkAttemptsAreCappedBeforeDescriptionFallback()
    {
        var fetcher = new SocialLinkFetcher();
        using var services = CreateServices(fetcher);
        var description = Substitute.For<IRecipeDescriptionParser>();
        description.ParseRecipeFromDescription(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Recipe?>(new Recipe
            {
                Title = "Caption soup", Url = "https://www.tiktok.com/@cook/video/1",
                RawIngredients = ["1 onion"], StepSections = [new RecipeStepSection { Steps = [new RecipeStep { Step = "Cook for 2 minutes." }] }],
                Ratings = new RecipeRatings()
            }));
        var sut = new RecipeParserService(services.GetRequiredService<INodeJSService>(), fetcher, description,
            performanceOptions: Options.Create(new ParserPerformanceOptions { MaxLinkedPages = 1 }));
        (await sut.ParseRecipeByUrl("https://www.tiktok.com/@cook/video/1")).Title.ShouldBe("Caption soup");
        fetcher.LinkedCalls.ShouldBe(1);
        await description.Received(1).ParseRecipeFromDescription(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    private sealed class SocialLinkFetcher : IPageFetcher
    {
        public int LinkedCalls;
        public Task<string> GetStaticHtmlAsync(string url, CancellationToken ct = default)
        {
            if (new Uri(url).Host != "www.tiktok.com")
            {
                LinkedCalls++;
                throw new HttpRequestException("Gone", null, System.Net.HttpStatusCode.NotFound);
            }
            return Task.FromResult("""
                <meta property="og:description" content="Soup recipe: 1 onion. Cook for 2 minutes.">
                <a href="https://example.com/recipe-one">Recipe one</a>
                <a href="https://example.com/recipe-two">Recipe two</a>
                <a href="https://example.com/recipe-three">Recipe three</a>
                """);
        }
        public Task<IReadOnlyList<string>> GetRenderedJsonLdAsync(string url, TimeSpan timeout, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<string>>([]);
    }

    [Test, Explicit("Requires Node.js and npm ci in the Node directory"), Category("NodeIntegration")]
    public async Task RealNodePipelineAndCacheBenchmark()
    {
        var nodeDirectory = Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory,
            "../../../../RecipeParser.Application/Services/Node"));
        var fetcher = new ControlledFetcher();
        fetcher.Release.SetResult();
        using var services = new ServiceCollection().AddLogging().AddRecipeParserApplication()
            .AddSingleton<IPageFetcher>(fetcher)
            .Configure<NodeJSProcessOptions>(options => options.ProjectPath = nodeDirectory)
            .AddNodeJS().BuildServiceProvider();
        var parser = services.GetRequiredService<IRecipeParserService>();
        var started = Stopwatch.GetTimestamp();
        var recipe = await parser.ParseRecipeByUrl(Url);
        var cold = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        recipe.Ingredients.Single().Quantity.ShouldBe(1);
        recipe.StepSections.Single().Steps!.Single().Times.Single().TimeInSeconds.ShouldBe(120);
        var hits = new List<double>();
        for (var index = 0; index < 30; index++)
        {
            started = Stopwatch.GetTimestamp();
            await parser.ParseRecipeByUrl(Url);
            hits.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
        hits.Sort();
        TestContext.Out.WriteLine($"Fixture import (real Node, no website/network): cold={cold:F2}ms; cache p50={hits[14]:F3}ms p95={hits[28]:F3}ms");
        fetcher.StaticCalls.ShouldBe(1);
    }

    private static ServiceProvider CreateServices(IPageFetcher fetcher)
    {
        var node = Substitute.For<INodeJSService>();
        node.InvokeFromFileAsync<RecipeEnrichmentResult>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<object[]>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var args = call.ArgAt<object[]>(2);
                return Task.FromResult<RecipeEnrichmentResult?>(new RecipeEnrichmentResult(
                    ((string[])args[0]).Select(line => new IngredientResult(1, "1", 1, 1, null, null, "onion", null, line, [])).ToArray(),
                    ((string[])args[1]).Select(_ => new InstructionResult(120, [new TimeItem(120, "minutes", "2")], null, null, null, null, null)).ToArray()));
            });
        return new ServiceCollection().AddLogging().AddRecipeParserApplication()
            .AddSingleton(fetcher).AddSingleton(node).BuildServiceProvider();
    }

    private sealed class ControlledFetcher : IPageFetcher
    {
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int StaticCalls;
        public int RenderedCalls;
        public int FailuresRemaining;
        public bool Cancelled;
        public async Task<string> GetStaticHtmlAsync(string url, CancellationToken ct = default)
        {
            Interlocked.Increment(ref StaticCalls);
            Started.TrySetResult();
            try { await Release.Task.WaitAsync(ct); }
            catch (OperationCanceledException) { Cancelled = true; throw; }
            if (FailuresRemaining-- > 0) throw new HttpRequestException("Temporary error");
            return Html;
        }
        public Task<IReadOnlyList<string>> GetRenderedJsonLdAsync(string url, TimeSpan timeout, CancellationToken ct = default)
        {
            RenderedCalls++;
            return Task.FromResult<IReadOnlyList<string>>([Html.Split('>')[1].Split('<')[0]]);
        }
    }
}
