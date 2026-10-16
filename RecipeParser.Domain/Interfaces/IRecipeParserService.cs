using RecipeParser.Domain.Models;

namespace RecipeParser.Domain.Interfaces;

public interface IRecipeParserService
{
    Task<Recipe> ParseRecipeByUrl(string url, CancellationToken ct = default);
}