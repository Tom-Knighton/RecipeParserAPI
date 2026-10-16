namespace RecipeParser.Domain.Models;

public sealed record IngredientResult(
    decimal? Quantity,
    string? QuantityText,
    decimal? MinQuantity,
    decimal? MaxQuantity,
    string? Unit,
    string? UnitText,
    string? Ingredient,
    string? Extra,
    string FullIngredient,
    AlternativeQuantity[] AlternativeQuantities
);
public sealed record AlternativeQuantity(
    decimal Quantity,
    string Unit,
    string UnitText,
    decimal MinQuantity,
    decimal MaxQuantity
);

public sealed record InstructionResult(
    int? TotalTimeInSeconds,
    TimeItem[] TimeItems,
    decimal? Temperature,
    string? TemperatureText,
    string? TemperatureUnit,
    string? TemperatureUnitText,
    AlternativeTemp[]? AlternativeTemperatures
);
public sealed record TimeItem(int TimeInSeconds, string TimeUnitText, string TimeText);
public sealed record AlternativeTemp(decimal Quantity, string Unit, decimal MinQuantity, decimal MaxQuantity);
public sealed record RecipeEnrichmentResult(IngredientResult[] Ingredients, InstructionResult[] Instructions);
