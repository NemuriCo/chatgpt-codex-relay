using System.Net;
using System.Net.Sockets;
using System.Text.Json.Serialization;
using BlueRelay.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BlueRelay.Services.Bridges;

/// <summary>
/// Small Kestrel host bound explicitly to loopback. The browser extension is the
/// only intended client; normal web pages do not receive permissive CORS headers.
/// </summary>
public sealed class BrowserBridgeServer : IBrowserBridgeServer
{
    private readonly BrowserBridgeService _bridge;
    private readonly int _port;
    private WebApplication? _application;

    public BrowserBridgeServer(BrowserBridgeService bridge, int port = BrowserBridgeService.DefaultPort)
    {
        _bridge = bridge;
        _port = port;
    }

    public int Port => _port;

    public bool IsRunning => _application is not null;

    public async Task<BridgeOperationResult> StartAsync(CancellationToken cancellationToken = default)
    {
        if (_application is not null)
        {
            return new BridgeOperationResult(true);
        }

        try
        {
            var options = new WebApplicationOptions
            {
                ApplicationName = typeof(BrowserBridgeServer).Assembly.GetName().Name
            };
            var builder = WebApplication.CreateSlimBuilder(options);
            builder.Logging.ClearProviders();
            builder.Services.ConfigureHttpJsonOptions(jsonOptions =>
            {
                jsonOptions.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
            });
            builder.WebHost.ConfigureKestrel(serverOptions =>
            {
                serverOptions.Listen(IPAddress.Loopback, _port, listenOptions =>
                {
                    listenOptions.Protocols = HttpProtocols.Http1;
                });
            });

            var application = builder.Build();
            ConfigurePipeline(application);
            await application.StartAsync(cancellationToken).ConfigureAwait(false);
            _application = application;
            StartupDiagnostics.Write($"BrowserBridge started address=127.0.0.1 port={_port}");
            return new BridgeOperationResult(true);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or SocketException)
        {
            StartupDiagnostics.Write($"BrowserBridge unavailable port={_port} reason={exception.Message}");
            return new BridgeOperationResult(false, "bridge_unavailable", $"Browser Bridge is unavailable on 127.0.0.1:{_port}. {exception.Message}");
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_application is null)
        {
            return;
        }

        var application = _application;
        _application = null;
        try
        {
            await application.StopAsync(cancellationToken).ConfigureAwait(false);
            await application.DisposeAsync().ConfigureAwait(false);
            StartupDiagnostics.Write($"BrowserBridge stopped port={_port}");
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or OperationCanceledException)
        {
            StartupDiagnostics.Write($"BrowserBridge stop failed port={_port} reason={exception.Message}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
    }

    private void ConfigurePipeline(WebApplication application)
    {
        application.Use(async (context, next) =>
        {
            var origin = context.Request.Headers.Origin.ToString();
            if (IsExtensionOrigin(origin))
            {
                context.Response.Headers.AccessControlAllowOrigin = origin;
                context.Response.Headers.AccessControlAllowHeaders = "Content-Type, Authorization, X-BlueRelay-Token";
                context.Response.Headers.AccessControlAllowMethods = "GET, POST, OPTIONS";
                context.Response.Headers.AccessControlMaxAge = "600";
            }

            if (HttpMethods.IsOptions(context.Request.Method))
            {
                context.Response.StatusCode = IsExtensionOrigin(origin) ? StatusCodes.Status204NoContent : StatusCodes.Status403Forbidden;
                return;
            }

            await next().ConfigureAwait(false);
        });

        application.MapGet("/v1/health", () => Ok(new BridgeHealthDto(
            "ok",
            _bridge.IsPaired,
            "127.0.0.1",
            _port,
            typeof(BrowserBridgeServer).Assembly.GetName().Version?.ToString())));

        application.MapPost("/v1/pair", async (PairRequest request, CancellationToken cancellationToken) =>
            await PairAsync(request, cancellationToken).ConfigureAwait(false));

        application.MapGet("/v1/workstreams", (HttpContext context) =>
            Authorized(context) ? Ok(_bridge.ListWorkstreams()) : Unauthorized());

        application.MapPost("/v1/tabs/register", async (HttpContext context, RegisterTabRequest request, CancellationToken cancellationToken) =>
            await AuthorizedOperationAsync(context, () => _bridge.RegisterTabAsync(request, cancellationToken)).ConfigureAwait(false));

        application.MapPost("/v1/tabs/heartbeat", async (HttpContext context, RegisterTabRequest request, CancellationToken cancellationToken) =>
            await AuthorizedOperationAsync(context, () => _bridge.HeartbeatAsync(request.InstallationId, request.TabId, request.ChatGPTUrl, request.ChatGPTConversationId, request.PageTitle, cancellationToken)).ConfigureAwait(false));

        application.MapPost("/v1/tabs/bind", async (HttpContext context, BindTabRequest request, CancellationToken cancellationToken) =>
            await AuthorizedOperationAsync(context, () => _bridge.BindTabAsync(request, cancellationToken)).ConfigureAwait(false));

        application.MapPost("/v1/tabs/unbind/{workstreamId:guid}", async (HttpContext context, Guid workstreamId, CancellationToken cancellationToken) =>
            await AuthorizedOperationAsync(context, () => _bridge.UnbindWorkstreamAsync(workstreamId, cancellationToken)).ConfigureAwait(false));

        application.MapPost("/v1/tasks/capture", async (HttpContext context, CaptureTaskRequest request, CancellationToken cancellationToken) =>
            await AuthorizedOperationAsync(context, () => _bridge.CaptureTaskAsync(request, cancellationToken)).ConfigureAwait(false));

        application.MapPost("/v1/tasks/{taskId:guid}/confirm", async (HttpContext context, Guid taskId, CancellationToken cancellationToken) =>
            await AuthorizedOperationAsync(context, () => _bridge.ConfirmTaskAsync(taskId, cancellationToken)).ConfigureAwait(false));

        application.MapPost("/v1/tasks/{taskId:guid}/simulate-result", async (HttpContext context, Guid taskId, SimulatedResultRequest request, CancellationToken cancellationToken) =>
            await AuthorizedOperationAsync(context, () => _bridge.SimulateResultAsync(taskId, request.Result, cancellationToken)).ConfigureAwait(false));

        application.MapPost("/v1/tasks/{taskId:guid}/handoff", async (HttpContext context, Guid taskId, CancellationToken cancellationToken) =>
            await AuthorizedOperationAsync(context, () => _bridge.QueueHandoffAsync(taskId, cancellationToken)).ConfigureAwait(false));

        application.MapPost("/v1/tasks/{taskId:guid}/complete", async (HttpContext context, Guid taskId, CancellationToken cancellationToken) =>
            await AuthorizedOperationAsync(context, () => _bridge.CompleteTaskAsync(taskId, cancellationToken)).ConfigureAwait(false));

        application.MapGet("/v1/commands/next", async (HttpContext context, string installationId, string tabId, CancellationToken cancellationToken) =>
            await AuthorizedOperationAsync(context, () => _bridge.GetNextCommandAsync(installationId, tabId, cancellationToken)).ConfigureAwait(false));

        application.MapPost("/v1/commands/{commandId:guid}/ack", async (HttpContext context, Guid commandId, CommandAcknowledgement request, CancellationToken cancellationToken) =>
            await AuthorizedOperationAsync(context, () => _bridge.AcknowledgeHandoffAsync(commandId, request.Success, request.Code, cancellationToken)).ConfigureAwait(false));
    }

    private async Task<IResult> PairAsync(PairRequest request, CancellationToken cancellationToken)
    {
        var result = await _bridge.PairAsync(request.PairingCode, request.InstallationId, cancellationToken).ConfigureAwait(false);
        return result.Success ? Ok(result.Value) : Error(result.ErrorCode, result.Error, StatusCodes.Status400BadRequest);
    }

    private async Task<IResult> AuthorizedOperationAsync(HttpContext context, Func<Task<BridgeOperationResult>> action)
    {
        if (!Authorized(context))
        {
            return Unauthorized();
        }

        var result = await action().ConfigureAwait(false);
        return result.Success ? Ok(new { success = true }) : Error(result.ErrorCode, result.Error, StatusCodeFor(result.ErrorCode));
    }

    private async Task<IResult> AuthorizedOperationAsync<T>(HttpContext context, Func<Task<BridgeOperationResult<T>>> action)
    {
        if (!Authorized(context))
        {
            return Unauthorized();
        }

        var result = await action().ConfigureAwait(false);
        return result.Success ? Ok(result.Value) : Error(result.ErrorCode, result.Error, StatusCodeFor(result.ErrorCode));
    }

    private bool Authorized(HttpContext context)
    {
        var token = context.Request.Headers["X-BlueRelay-Token"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(token))
        {
            var authorization = context.Request.Headers.Authorization.FirstOrDefault();
            if (authorization?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) == true)
            {
                token = authorization[7..].Trim();
            }
        }

        return _bridge.IsAuthorized(token);
    }

    private static bool IsExtensionOrigin(string? origin)
    {
        return !string.IsNullOrWhiteSpace(origin)
            && (origin.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase)
                || origin.StartsWith("edge-extension://", StringComparison.OrdinalIgnoreCase));
    }

    private static int StatusCodeFor(string? errorCode)
    {
        return errorCode switch
        {
            "tab_not_bound" or "tab_disconnected" or "result_missing" => StatusCodes.Status409Conflict,
            "workstream_not_found" or "task_not_found" or "command_not_found" => StatusCodes.Status404NotFound,
            "not_paired" or "unauthorized" => StatusCodes.Status401Unauthorized,
            _ => StatusCodes.Status400BadRequest
        };
    }

    private static IResult Ok(object? data) => Results.Json(new { data });

    private static IResult Unauthorized() => Error("unauthorized", "A valid BlueRelay pairing token is required.", StatusCodes.Status401Unauthorized);

    private static IResult Error(string code, string message, int statusCode) =>
        Results.Json(new { error = new { code, message } }, statusCode: statusCode);

}
