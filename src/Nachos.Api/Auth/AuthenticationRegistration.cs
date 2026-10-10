using System.Runtime.ExceptionServices;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.Identity.Web;
using Microsoft.IdentityModel.Tokens;
using Nachos.Abstractions;
using Nachos.Abstractions.Stores;
using Nachos.Api.Errors;

namespace Nachos.Api.Auth;

internal static class AuthenticationRegistration
{
    internal static void AddNachosAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        var entra = configuration.GetSection("Nachos:Auth:Entra");
        var auth = services.AddAuthentication("Bearer").AddPolicyScheme("Bearer", "Bearer", options =>
        {
            options.ForwardDefaultSelector = context =>
                configuration.GetValue("Nachos:Auth:Enabled", true) && entra.Exists() &&
                BearerCredential.HasIssuer(BearerCredential.Read(context.Request)) ? "Entra" : "NachosKey";
        }).AddScheme<AuthenticationSchemeOptions, NachosKeyAuthenticationHandler>("NachosKey", _ => { });
        auth.AddMicrosoftIdentityWebApi(entra, jwtBearerScheme: "Entra");
        services.Configure<JwtBearerOptions>("Entra", options =>
            {
                options.MapInboundClaims = false;
                options.TokenValidationParameters.ValidateIssuer = true;
                options.TokenValidationParameters.ValidateAudience = true;
                options.TokenValidationParameters.ValidateLifetime = true;
                options.TokenValidationParameters.RequireExpirationTime = true;
                options.TokenValidationParameters.RequireSignedTokens = true;
                options.TokenValidationParameters.ValidateIssuerSigningKey = true;
                options.TokenValidationParameters.TryAllIssuerSigningKeys = false;
                options.TokenValidationParameters.ValidAlgorithms = [SecurityAlgorithms.RsaSha256];
                options.TokenValidationParameters.ClockSkew = TimeSpan.Zero;
                options.TokenValidationParameters.IncludeTokenOnFailedValidation = false;
                var validated = options.Events.OnTokenValidated;
                options.Events.OnTokenValidated = async context =>
                {
                    await validated(context);
                    if (context.Result?.Failure is not null) return;
                    try
                    {
                        context.Principal = await EntraPrincipalMapper.MapAsync(context.Principal!,
                            context.HttpContext.RequestServices.GetRequiredService<IMemoryStore>().Grants,
                            context.HttpContext.RequestAborted);
                    }
                    catch (AuthException) { context.Fail("Invalid Entra authority."); }
                };
                options.Events.OnAuthenticationFailed = context =>
                {
                    if (Operational(context.Exception))
                        ExceptionDispatchInfo.Capture(context.Exception).Throw();
                    return Task.CompletedTask;
                };
            });
        services.AddAuthorization(options => options.AddPolicy("Nachos", policy => policy.AddRequirements(new RouteRequirements())));
        services.AddScoped<IAuthorizationHandler, NachosAuthorizationHandler>();
        services.AddSingleton<IAuthorizationMiddlewareResultHandler, NachosAuthorizationResultHandler>();
    }

    private static bool Operational(Exception error)
    {
        if (error is AggregateException aggregate) return aggregate.InnerExceptions.Any(Operational);
        return error.InnerException is not null && Operational(error.InnerException) ||
            error is not (SecurityTokenException or AuthException or ArgumentException or FormatException);
    }

    private sealed class NachosAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
    {
        public Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult result)
        {
            if (result.Succeeded) return next(context);
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return NachosExceptionHandler.WriteStatusAsync(context, StatusCodes.Status401Unauthorized);
        }
    }
}
