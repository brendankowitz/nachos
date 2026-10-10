using Nachos.Abstractions;
using Nachos.Api.Auth;
using Nachos.Api.Endpoints;
using Nachos.Api.Errors;
using Nachos.Api.Health;
using Nachos.Api.Json;
using Nachos.Api.Providers;
using Nachos.Core.Configuration;
using Nachos.Core.Keys;
using Nachos.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
builder.Services.AddNachos(nachos =>
{
    ProviderSelection.Use(nachos, builder.Configuration);
    nachos.BindSigningKeys(builder.Configuration.GetSection("Nachos:Auth:NachosKey"));
});
builder.Services.AddNachosAuthentication(builder.Configuration);
builder.Services.AddSigningKeyVault();
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
await SigningKeyVault.LoadAsync(app.Services, app.Lifetime.ApplicationStopping);
_ = app.Services.GetRequiredService<IKeyIssuer>();

app.UseExceptionHandler(_ => { });
app.UseStatusCodePages(async context =>
{
    var response = context.HttpContext.Response;
    await NachosExceptionHandler.WriteStatusAsync(context.HttpContext, response.StatusCode);
});
app.UseRouting();
app.UseAuthentication();
app.Use(async (context, next) =>
{
    var path = new PathString(Uri.UnescapeDataString(context.Request.Path.Value ?? ""));
    if (authEnabled && path.StartsWithSegments("/v3", StringComparison.OrdinalIgnoreCase) &&
        context.User is not NachosPrincipal)
    {
        throw new AuthException("Authentication is required.");
    }
    await next(context);
});
app.UseAuthorization();
app.MapDefaultEndpoints();
var routes = app.MapGroup("").RequireAuthorization("Nachos");
routes.MapWorkspaceEndpoints();
routes.MapPeerEndpoints();
routes.MapSessionEndpoints();
routes.MapMessageEndpoints();
routes.MapKeyEndpoints();
routes.MapGrantEndpoints();
routes.MapNotImplementedEndpoints();
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}
app.Run();

public partial class Program;