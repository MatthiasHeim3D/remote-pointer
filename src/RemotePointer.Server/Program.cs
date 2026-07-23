using RemotePointer.Contracts.Serialization;
using RemotePointer.Server.Health;
using RemotePointer.Server.Hubs;
using RemotePointer.Server.RateLimiting;
using RemotePointer.Server.Sessions;
using PointerSessionOptions = RemotePointer.Server.Sessions.SessionOptions;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole();

builder.Services
    .AddOptions<PointerSessionOptions>()
    .Bind(builder.Configuration.GetSection(PointerSessionOptions.SectionName))
    .Validate(
        options => options.PairingCodeLifetimeMinutes > 0
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
builder.Services.AddHostedService<SessionExpirationService>();
builder.Services
    .AddSignalR(options =>
    {
        options.EnableDetailedErrors = false;
        options.MaximumParallelInvocationsPerClient = 1;
        options.MaximumReceiveMessageSize = 8 * 1024;
    })
    .AddJsonProtocol(options => RemotePointerJson.Configure(options.PayloadSerializerOptions));
builder.Services
    .AddHealthChecks()
    .AddCheck<SessionHealthCheck>("sessions");

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}

app.MapHealthChecks("/health");
app.MapHub<PointerHub>("/hubs/pointer");

app.Run();

public partial class Program;
