# RecipeParser

See [Azure deployment](docs/azure-deployment.md) for development and production CI/CD, registry credential rotation, database configuration, and custom domains.

## Discovery Database Migrations

Discovery uses EF Core with Postgres through `DiscoveryDbContext`. All discovery tables are created in the `sporkast` schema.

Run commands from the repository root:

```bash
cd /Users/tom/Projects/RecipeParser
```

Set the target database connection string before running migrations:

```bash
export ConnectionStrings__Discovery="Host=localhost;Port=5432;Database=sporkast_discovery;Username=postgres;Password=postgres"
```

Apply pending migrations:

```bash
dotnet ef database update \
  --project RecipeParser.Infrastructure/RecipeParser.Infrastructure.csproj \
  --startup-project RecipeParser.API/RecipeParser.API.csproj \
  --context DiscoveryDbContext
```

Create a new migration after changing discovery entities or mappings:

```bash
dotnet ef migrations add <MigrationName> \
  --project RecipeParser.Infrastructure/RecipeParser.Infrastructure.csproj \
  --startup-project RecipeParser.API/RecipeParser.API.csproj \
  --context DiscoveryDbContext \
  --output-dir Discovery/Migrations
```

List migrations:

```bash
dotnet ef migrations list \
  --project RecipeParser.Infrastructure/RecipeParser.Infrastructure.csproj \
  --startup-project RecipeParser.API/RecipeParser.API.csproj \
  --context DiscoveryDbContext
```

Remove the latest migration before it has been applied anywhere shared:

```bash
dotnet ef migrations remove \
  --project RecipeParser.Infrastructure/RecipeParser.Infrastructure.csproj \
  --startup-project RecipeParser.API/RecipeParser.API.csproj \
  --context DiscoveryDbContext
```

Do not remove or rewrite migrations that have already been applied to a shared or production database. Add a new migration instead.

## Discovery Starter Catalog

`RecipeParser.API/DiscoveryCuratedCandidates.starter.json` contains a paste-ready starter catalog for `Discovery:CuratedCandidates`.

For local testing, copy the `CuratedCandidates` array into `RecipeParser.API/appsettings.Development.json` under `Discovery`.

For deployment, store it in environment-specific app configuration using the same `Discovery:CuratedCandidates` shape. The API syncs configured candidates into the discovery database on startup when `Discovery:RunCuratedCandidateSyncOnStartup` is `true`.

## Discovery Source Refresh

Discovery now supports live candidate ingestion from configured sources in addition to the starter catalog. Sources are configured under `Discovery:Sources` and can use:

- `RssFeeds`: RSS or Atom feeds.
- `SitemapUrls`: sitemap indexes or URL sets.
- `IndexPages`: HTML pages where recipe links can be discovered.

Each source is allowlisted by `Domain` and can be narrowed with `UrlMustContain`. The ingestion service stores only card metadata and source URLs. Full recipe extraction still happens through the parser/import path when a user adds a card.

Useful production knobs:

```json
{
  "Discovery": {
    "RunSourceCandidateSyncOnStartup": true,
    "RefreshSourcesWhenFeedIsSparse": true,
    "CandidateRefreshIntervalMinutes": 360,
    "SparseFeedCandidateThreshold": 40,
    "SourceRequestTimeoutSeconds": 15,
    "MaxCandidatesPerSource": 60,
    "MaxDetailFetchesPerSource": 8,
    "EnableUserSourceDiscovery": true,
    "MaxUserSourceDomainsPerRefresh": 3,
    "MaxCandidatesPerUserSource": 20,
    "MaxDetailFetchesPerUserSource": 12,
    "UserSourceFailureCooldownHours": 24,
    "UserSourceSuccessCooldownHours": 6
  }
}
```

Startup sync seeds both curated and live candidates. A hosted refresh service then updates source candidates on `CandidateRefreshIntervalMinutes`. Feed requests also trigger a throttled source refresh when the candidate table is sparse, which keeps early deployments from showing only the starter catalog.

When `EnableUserSourceDiscovery` is enabled, feed requests can also refresh the user's top submitted source domains if those domains are not already configured sources. The service probes only public hosts, tries common feed/sitemap/homepage discovery locations, requires JSON-LD `Recipe` metadata before accepting arbitrary user-domain cards, and applies success/failure cooldowns per domain.
