namespace RecipeParser.Domain.Models;

public sealed class ParserPerformanceOptions
{
    public int TotalTimeoutSeconds { get; set; } = 25;
    public int StaticTimeoutSeconds { get; set; } = 8;
    public int RenderedTimeoutSeconds { get; set; } = 8;
    public int LinkedPageTimeoutSeconds { get; set; } = 4;
    public int MaxLinkedPages { get; set; } = 2;
    public string[] RenderedFirstHosts { get; set; } = [];
}
