namespace RecipeParser.Domain.Interfaces;

using Microsoft.Playwright;

public interface IPlaywrightBrowser : IAsyncDisposable
{
    Task<IBrowserContext> NewContextAsync(BrowserNewContextOptions? options = null, CancellationToken ct = default);
}
