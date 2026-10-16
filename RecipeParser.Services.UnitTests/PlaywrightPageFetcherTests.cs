using Microsoft.Playwright;
using NSubstitute;
using NUnit.Framework;
using RecipeParser.Domain.Interfaces;
using RecipeParser.Infrastructure.Services;
using Shouldly;

namespace RecipeParser.Services.UnitTests;

public class PlaywrightPageFetcherTests
{
    [Test]
    public async Task NavigationFailureClosesBrowserContext()
    {
        var context = Substitute.For<IBrowserContext>();
        var page = Substitute.For<IPage>();
        var browser = Substitute.For<IPlaywrightBrowser>();
        browser.NewContextAsync(Arg.Any<BrowserNewContextOptions>(), Arg.Any<CancellationToken>()).Returns(context);
        context.NewPageAsync().Returns(page);
        page.GotoAsync(Arg.Any<string>(), Arg.Any<PageGotoOptions>())
            .Returns(Task.FromException<IResponse?>(new InvalidOperationException("Navigation failed")));
        using var client = new HttpClient();
        var sut = new PlaywrightPageFetcher(browser, client);
        await Should.ThrowAsync<InvalidOperationException>(() => sut.GetRenderedJsonLdAsync("https://example.com", TimeSpan.FromSeconds(2)));
        await context.Received(1).CloseAsync();
    }

    [Test]
    public async Task CancelledNavigationClosesContextWithinBudget()
    {
        var context = Substitute.For<IBrowserContext>();
        var page = Substitute.For<IPage>();
        var browser = Substitute.For<IPlaywrightBrowser>();
        browser.NewContextAsync(Arg.Any<BrowserNewContextOptions>(), Arg.Any<CancellationToken>()).Returns(context);
        context.NewPageAsync().Returns(page);
        var navigation = new TaskCompletionSource<IResponse?>(TaskCreationOptions.RunContinuationsAsynchronously);
        page.GotoAsync(Arg.Any<string>(), Arg.Any<PageGotoOptions>()).Returns(navigation.Task);
        using var client = new HttpClient();
        var sut = new PlaywrightPageFetcher(browser, client);
        await Should.ThrowAsync<OperationCanceledException>(() => sut.GetRenderedJsonLdAsync("https://example.com", TimeSpan.FromMilliseconds(30)));
        await context.Received(1).CloseAsync();
        navigation.TrySetResult(null);
    }
}
