using System.Net;
using Jering.Javascript.NodeJS;
using NSubstitute;
using NUnit.Framework;
using RecipeParser.Application.Services;
using RecipeParser.Domain.Interfaces;
using RecipeParser.Domain.Models;
using Shouldly;

namespace RecipeParser.Services.UnitTests;

public class RecipeParserServiceFallbackTests
{
    [Test]
    public async Task ParseRecipeByUrl_WhenTasteOfHomeRecipe_UsesWordPressRecipeApiFirst()
    {
        var node = CreateNodeParser();
        var fetcher = new TasteOfHomeApiRecipeFetcher();
        var sut = new RecipeParserService(node, fetcher);

        var result = await sut.ParseRecipeByUrl("https://www.tasteofhome.com/recipes/kimchi-cauliflower-fried-rice/");

        result.Title.ShouldBe("Kimchi Cauliflower Fried Rice");
        result.MinutesToCook.ShouldBe(25);
        result.RawIngredients.ShouldContain("1 cup kimchi, chopped");
        result.Ratings.TotalRatings.ShouldBe(2);
        fetcher.StaticCalls.ShouldBe(1);
        fetcher.LastStaticUrl.ShouldBe("https://www.tasteofhome.com/wp-json/wp/v2/recipe?slug=kimchi-cauliflower-fried-rice");
        fetcher.RenderedCalls.ShouldBe(0);
    }

    [Test]
    public async Task ParseRecipeByUrl_WhenStaticFetchIsForbidden_TriesRenderedJsonLd()
    {
        var node = CreateNodeParser();
        var fetcher = new StaticForbiddenRenderedRecipeFetcher();
        var sut = new RecipeParserService(node, fetcher);

        var result = await sut.ParseRecipeByUrl("https://www.example.com/recipes/kimchi-cauliflower-fried-rice/");

        result.Title.ShouldBe("Kimchi Cauliflower Fried Rice");
        result.RawIngredients.ShouldContain("1 cup kimchi");
        fetcher.RenderedCalls.ShouldBe(1);
    }

    [Test]
    public async Task ParseRecipeByUrl_WhenStaticRecipeHasNoCommentsOrRatings_ParsesWithoutRenderedFallback()
    {
        var node = CreateNodeParser();
        var fetcher = new StaticRecipeWithoutSocialProofFetcher();
        var sut = new RecipeParserService(node, fetcher);

        var result = await sut.ParseRecipeByUrl("https://foodieholly.com/2026/05/25/charcuterie-pesto-pasta-salad/");

        result.Title.ShouldBe("Charcuterie Pesto Pasta Salad");
        result.RawIngredients.ShouldContain("200 g pasta");
        result.StepSections.Single().Steps.ShouldNotBeNull();
        result.StepSections.Single().Steps!.Count.ShouldBe(1);
        result.Ratings.TotalRatings.ShouldBe(0);
        fetcher.RenderedCalls.ShouldBe(0);
    }

    private static INodeJSService CreateNodeParser()
    {
        var node = Substitute.For<INodeJSService>();
        node.InvokeFromFileAsync<IngredientResult>(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<object[]>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var args = callInfo.ArgAt<object[]>(2);
                var line = args.FirstOrDefault()?.ToString() ?? "";
                return Task.FromResult(new IngredientResult(null, null, null, null, null, null, line, null, line, []));
            });
        node.InvokeFromFileAsync<InstructionResult>(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<object[]>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new InstructionResult(null, [], null, null, null, null, null)));

        node.InvokeFromFileAsync<RecipeEnrichmentResult>(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<object[]>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var args = call.ArgAt<object[]>(2);
                var ingredients = (string[])args[0];
                var instructions = (string[])args[1];
                return Task.FromResult<RecipeEnrichmentResult?>(new RecipeEnrichmentResult(
                    ingredients.Select(line => new IngredientResult(null, null, null, null, null, null, line, null, line, [])).ToArray(),
                    instructions.Select(_ => new InstructionResult(null, [], null, null, null, null, null)).ToArray()));
            });
        return node;
    }

    private sealed class TasteOfHomeApiRecipeFetcher : IPageFetcher
    {
        public int StaticCalls { get; private set; }
        public int RenderedCalls { get; private set; }
        public string? LastStaticUrl { get; private set; }

        public Task<string> GetStaticHtmlAsync(string url, CancellationToken ct = default)
        {
            StaticCalls++;
            LastStaticUrl = url;
            url.ShouldBe("https://www.tasteofhome.com/wp-json/wp/v2/recipe?slug=kimchi-cauliflower-fried-rice");

            return Task.FromResult(
                """
                [
                  {
                    "recipe_schema": {
                      "@context": "https://schema.org",
                      "@type": "Recipe",
                      "@id": "https://www.tasteofhome.com/recipes/kimchi-cauliflower-fried-rice/",
                      "name": "Kimchi Cauliflower Fried Rice",
                      "cookTime": "PT25M",
                      "recipeIngredient": ["1 cup kimchi, chopped"],
                      "recipeInstructions": ["Stir-fry everything until hot."],
                      "aggregateRating": {
                        "@type": "AggregateRating",
                        "ratingValue": 5,
                        "reviewCount": 2
                      }
                    }
                  }
                ]
                """);
        }

        public Task<IReadOnlyList<string>> GetRenderedJsonLdAsync(string url, TimeSpan timeout, CancellationToken ct = default)
        {
            RenderedCalls++;
            return Task.FromResult<IReadOnlyList<string>>([]);
        }
    }

    private sealed class StaticForbiddenRenderedRecipeFetcher : IPageFetcher
    {
        public int RenderedCalls { get; private set; }

        public Task<string> GetStaticHtmlAsync(string url, CancellationToken ct = default)
        {
            throw new HttpRequestException("Forbidden", null, HttpStatusCode.Forbidden);
        }

        public Task<IReadOnlyList<string>> GetRenderedJsonLdAsync(string url, TimeSpan timeout, CancellationToken ct = default)
        {
            RenderedCalls++;
            return Task.FromResult<IReadOnlyList<string>>(
            [
                """
                {
                  "@context": "https://schema.org",
                  "@type": "Recipe",
                  "name": "Kimchi Cauliflower Fried Rice",
                  "recipeIngredient": ["1 cup kimchi"],
                  "recipeInstructions": ["Stir-fry everything until hot."]
                }
                """
            ]);
        }
    }

    private sealed class StaticRecipeWithoutSocialProofFetcher : IPageFetcher
    {
        public int RenderedCalls { get; private set; }

        public Task<string> GetStaticHtmlAsync(string url, CancellationToken ct = default)
        {
            return Task.FromResult(
                """
                <html>
                <head>
                  <script type="application/ld+json">
                  {
                    "@context": "https://schema.org",
                    "@type": "Recipe",
                    "@id": "https://foodieholly.com/2026/05/25/charcuterie-pesto-pasta-salad/#recipe",
                    "name": "Charcuterie Pesto Pasta Salad",
                    "description": "A pesto pasta salad with charcuterie board flavours.",
                    "recipeYield": ["4"],
                    "prepTime": "PT5M",
                    "cookTime": "PT10M",
                    "recipeIngredient": ["200 g pasta", "50 g salami"],
                    "recipeInstructions": [
                      {
                        "@type": "HowToStep",
                        "text": "Cook the pasta, drain, then toss with the remaining ingredients."
                      }
                    ]
                  }
                  </script>
                </head>
                <body></body>
                </html>
                """);
        }

        public Task<IReadOnlyList<string>> GetRenderedJsonLdAsync(string url, TimeSpan timeout, CancellationToken ct = default)
        {
            RenderedCalls++;
            return Task.FromResult<IReadOnlyList<string>>([]);
        }
    }
}
