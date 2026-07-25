using System.Reflection;
using Microsoft.AspNetCore.SignalR;
using RemotePointer.Contracts.Messages;
using RemotePointer.Contracts.Serialization;
using RemotePointer.Server.Health;
using RemotePointer.Server.Hubs;
using RemotePointer.Server.RateLimiting;
using RemotePointer.Server.Security;
using RemotePointer.Server.Sessions;
using PointerSessionOptions = RemotePointer.Server.Sessions.SessionOptions;

var builder = WebApplication.CreateBuilder(args);
var behindHttpsProxy = builder.Configuration.GetValue<bool>("Deployment:BehindHttpsProxy");

builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole();

builder.Services
    .AddOptions<PointerSessionOptions>()
    .Bind(builder.Configuration.GetSection(PointerSessionOptions.SectionName))
    .Validate(
        options => options.AbandonedSessionLifetimeMinutes > 0
            && options.MaximumSessionHours is > 0 and <= 8
            && options.SequenceWindowSize > 0,
        "Session options must use positive values and a maximum lifetime of eight hours.")
    .ValidateOnStart();
builder.Services
    .AddOptions<PointerRateLimitOptions>()
    .Bind(builder.Configuration.GetSection(PointerRateLimitOptions.SectionName))
    .Validate(
        options => options.EventsPerSecond > 0 && options.BurstSize > 0,
        "Pointer rate-limit options must be positive.")
    .ValidateOnStart();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ISessionSecretGenerator, SessionSecretGenerator>();
builder.Services.AddSingleton<ISessionManager, SessionManager>();
builder.Services.AddSingleton<PointerHubAuditFilter>();
builder.Services.AddHostedService<SessionExpirationService>();
builder.Services.AddProblemDetails();
builder.Services
    .AddSignalR(options =>
    {
        options.EnableDetailedErrors = false;
        options.MaximumParallelInvocationsPerClient = 1;
        options.MaximumReceiveMessageSize = 32 * 1024;
        options.AddFilter<PointerHubAuditFilter>();
    })
    .AddJsonProtocol(options => RemotePointerJson.Configure(options.PayloadSerializerOptions));
builder.Services
    .AddHealthChecks()
    .AddCheck<SessionHealthCheck>("sessions");

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler();
    app.UseHsts();
    if (!behindHttpsProxy)
    {
        app.Use(
            async (context, next) =>
            {
                if (!context.Request.IsHttps)
                {
                    var logger = context.RequestServices
                        .GetRequiredService<ILoggerFactory>()
                        .CreateLogger("RemotePointer.Server.TransportSecurity");
                    logger.LogWarning(
                        AuditEventIds.PlaintextRejected,
                        "Plaintext request rejected. Path={Path}",
                        context.Request.Path);
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    await context.Response.WriteAsJsonAsync(
                            new
                            {
                                type = "https-required",
                                title = "HTTPS is required.",
                                status = StatusCodes.Status400BadRequest,
                            })
                        .ConfigureAwait(false);
                    return;
                }

                await next(context).ConfigureAwait(false);
            });
    }
}

app.MapHealthChecks("/health");
app.MapGet(
    "/version",
    () => new ServerVersionResponse(
        ServerVersionResponse.RelayProductId,
        ServerVersion.Current));
app.MapHub<PointerHub>("/hubs/pointer");

app.Run();

/// <summary>
/// The build version this relay advertises. Clients read it to show which server they are
/// configured against.
/// </summary>
internal static class ServerVersion
{
    public static string Current { get; } = Read();

    private static string Read()
    {
        var informationalVersion = typeof(Program).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(informationalVersion))
        {
            return typeof(Program).Assembly.GetName().Version?.ToString() ?? "unknown";
        }

        // Nerdbank.GitVersioning appends "+<commit>"; the commit adds noise for a client label.
        var metadataIndex = informationalVersion.IndexOf('+', StringComparison.Ordinal);
        return metadataIndex < 0
            ? informationalVersion
            : informationalVersion[..metadataIndex];
    }
}

public partial class Program;
