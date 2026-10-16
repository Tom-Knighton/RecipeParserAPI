using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Playwright;
using RecipeParser.Domain.Interfaces;

namespace RecipeParser.Infrastructure.Services;

public sealed class PlaywrightPageFetcher(IPlaywrightBrowser browser, HttpClient httpClient) : IPageFetcher
{
    public async Task<string> GetStaticHtmlAsync(string url, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadAsStringAsync(ct);
    }

    public async Task<IReadOnlyList<string>> GetRenderedJsonLdAsync(string url, TimeSpan timeout, CancellationToken ct = default)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(timeout);
        var ctx = await browser.NewContextAsync(new()
        {
            UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/120 Safari/537.36",
        }, budget.Token);
        try
        {
            budget.Token.ThrowIfCancellationRequested();
            await ctx.RouteAsync("**/*", route =>
                route.Request.ResourceType is "image" or "media" or "font"
                    ? route.AbortAsync()
                    : route.ContinueAsync());
            var page = await ctx.NewPageAsync();
            page.SetDefaultTimeout((float)timeout.TotalMilliseconds);
            await page.GotoAsync(url, new()
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = (float)timeout.TotalMilliseconds
            }).WaitAsync(budget.Token);

            // JSON-LD may be injected after DOMContentLoaded.
            try
            {
                await page.WaitForFunctionAsync("""
                    () => [...document.querySelectorAll('script[type="application/ld+json"]')]
                        .some(node => /"(?:https?:\/\/schema\.org\/)?Recipe"/i.test(node.textContent || ''))
                    """, options: new() { Timeout = 1500 }).WaitAsync(budget.Token);
            }
            catch (System.TimeoutException) { }

            var jsons = await page.EvalOnSelectorAllAsync<string[]>(
                "script[type='application/ld+json']",
                "nodes => nodes.map(n => n.textContent ?? '')").WaitAsync(budget.Token);
            return jsons.Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();
        }
        finally
        {
            await ctx.CloseAsync();
        }
    }
}
