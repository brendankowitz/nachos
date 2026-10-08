using Nachos.Abstractions;
using Nachos.Api.Endpoints;
using Nachos.Api.Errors;
using Nachos.Api.Health;
using Nachos.Api.Json;
using Nachos.Core.Configuration;
using Nachos.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
builder.Services.AddNachos(nachos => nachos.UseInMemory());
builder.Services.Configure<NachosOptions>(builder.Configuration.GetSection("Nachos"));
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower;
    options.SerializerOptions.DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never;
    options.SerializerOptions.NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.Strict;
    options.SerializerOptions.MaxDepth = int.MaxValue;
    options.SerializerOptions.TypeInfoResolver = NachosJsonContext.Default;
});
builder.Services.AddExceptionHandler<NachosExceptionHandler>();
builder.Services.AddHealthChecks().AddCheck<StoreReadinessCheck>("store");
builder.Services.AddOpenApi(options =>
{
    options.AddSchemaTransformer(NachosOpenApi.DescribeSchemasAsync);
    options.AddOperationTransformer(NachosOpenApi.DescribeErrorsAsync);
});

var app = builder.Build();
var authEnabled = app.Configuration.GetValue("Nachos:Auth:Enabled", true);
if (!authEnabled && !app.Environment.IsDevelopment())
{
    throw new InvalidOperationException("Authentication may be disabled only in Development.");
}

app.UseExceptionHandler(_ => { });
app.UseStatusCodePages(async context =>
{
    var response = context.HttpContext.Response;
    await NachosExceptionHandler.WriteStatusAsync(context.HttpContext, response.StatusCode);
});
// Task 10 replaces this fail-closed gate with authentication and per-route authorization.
app.Use(async (context, next) =>
{
    if (authEnabled && context.Request.Path.StartsWithSegments("/v3"))
    {
        throw new AuthException("Authentication is required.");
    }
    await next(context);
});
app.MapDefaultEndpoints();
app.MapWorkspaceEndpoints();
app.MapPeerEndpoints();
app.MapSessionEndpoints();
app.MapMessageEndpoints();
app.MapNotImplementedEndpoints();
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}
app.Run();

public partial class Program;