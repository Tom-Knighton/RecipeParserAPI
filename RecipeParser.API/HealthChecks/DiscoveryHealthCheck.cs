using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using RecipeParser.Infrastructure.Discovery;

namespace RecipeParser.HealthChecks;

internal sealed class DiscoveryHealthCheck(IServiceScopeFactory scopeFactory) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var database = scope.ServiceProvider.GetRequiredService<DiscoveryDbContext>();
            await database.DiscoveryCandidates.Select(candidate => candidate.Id)
                .Take(1).ToListAsync(cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception exception)
        {
            return HealthCheckResult.Unhealthy("Discovery database is unavailable.", exception);
        }
    }
}
