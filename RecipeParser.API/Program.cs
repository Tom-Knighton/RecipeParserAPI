using Jering.Javascript.NodeJS;
using Microsoft.OpenApi.Models;
using RecipeParser.Application;
using RecipeParser.Application.Services;
using RecipeParser.Domain.Interfaces;
using RecipeParser.Infrastructure;
using RecipeParser.HealthChecks;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddHealthChecks()
    .AddCheck<DiscoveryHealthCheck>("discovery", tags: ["ready"]);

builder.Services.AddRecipeParserApplication();
builder.Services.AddRecipeParserInfrastructure(builder.Configuration);

builder.Services.Configure<NodeJSProcessOptions>(opts =>
{
    opts.ProjectPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Node");
});
builder.Services.AddNodeJS();
builder.Services.AddEndpointsApiExplorer();

if (builder.Environment.IsDevelopment())
{
    builder.Services.AddSwaggerGen(c =>
    {
        c.SwaggerDoc("v1", new OpenApiInfo
        {
            Version = "v1",
            Title = "Sporkast API",
            Description = "Sporkast's Recipe Parsing API",
            Contact = new OpenApiContact
            {
                Email = "sporkast-dev@tomk.online",
                Name = "Sporkast Developer",
            }
        });
        var filePath = Path.Combine(AppContext.BaseDirectory, "RecipeParser.API.xml");
        c.IncludeXmlComments(filePath);
    });
}

builder.Services.AddOpenApi();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready")
});
app.MapControllers();
app.Run();
