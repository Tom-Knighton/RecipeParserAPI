using Jering.Javascript.NodeJS;
using NSubstitute;
using NUnit.Framework;
using RecipeParser.Application.Services;
using RecipeParser.Domain.Interfaces;
using RecipeParser.Domain.Models;
using RecipeParser.Infrastructure.Services;

namespace RecipeParser.Services.UnitTests;

[TestFixture]
public partial class RecipeParserServiceTests
{
    private INodeJSService _mockNode;
    private IRecipeParserService _sut;
    
    [SetUp]
    public void Setup()
    {
        _mockNode = Substitute.For<INodeJSService>();
        _mockNode.InvokeFromFileAsync<IngredientResult>(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<object[]>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var args = callInfo.ArgAt<object[]>(2);
                var line = args.FirstOrDefault()?.ToString() ?? "";
                return Task.FromResult<IngredientResult>(new IngredientResult(
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    line,
                    null,
                    line,
                    []));
            });
        _mockNode.InvokeFromFileAsync<InstructionResult>(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<object[]>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<InstructionResult>(new InstructionResult(null, [], null, null, null, null, null)));
        _mockNode.InvokeFromFileAsync<RecipeEnrichmentResult>(
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
        _sut = new RecipeParserService(_mockNode, new PlaywrightPageFetcher(new PlaywrightBrowser(), new HttpClient()));
    }
}
