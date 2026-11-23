using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Watchme.Core;

namespace Watchme.Cli;

/// <summary>
/// <c>watchme dashboard</c>: serves charts of the recorded data on http://127.0.0.1 until Ctrl+C.
/// The page is embedded in watchme.exe and loads nothing from the internet. The tray icon runs it
/// without a window, with <c>--parent</c> set to the agent so it stops when the agent does.
/// </summary>
internal static class DashboardServer
{
    public const int DefaultPort = AgentIpc.DashboardPort;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static int Run(string[] args)
    {
        var port = DefaultPort;
        var portGiven = false;
        var open = true;
        int? parent = null;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--port" when i + 1 < args.Length && int.TryParse(args[i + 1], out var p) && p is > 0 and < 65536:
                    port = p;
                    portGiven = true;
                    i++;
                    break;
                case "--no-open":
                    open = false;
                    break;
                case "--parent" when i + 1 < args.Length && int.TryParse(args[i + 1], out var pid):
                    parent = pid;
                    i++;
                    break;
                default:
                    Console.Error.WriteLine($"Unknown option '{args[i]}'. Usage: watchme dashboard [--port N] [--no-open]");
                    return 2;
            }
        }

        if (!File.Exists(Paths.Database))
        {
            Console.WriteLine($"No data yet ({Paths.Database} does not exist). Run 'watchme start' first.");
            return 0;
        }

        if (!portGiven)
            port = AgentIpc.FirstFreePort(port);

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, port));
        var app = builder.Build();

        // A page on any website can point a hostname of its own at 127.0.0.1 (DNS rebinding) and
        // read the API as same-origin. Only answering to the loopback names closes that door.
        app.Use(async (ctx, next) =>
        {
            var host = ctx.Request.Host.Host;
            if (host is not ("127.0.0.1" or "localhost"))
            {
                ctx.Response.StatusCode = StatusCodes.Status421MisdirectedRequest;
                return;
            }
            ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
            ctx.Response.Headers["Cache-Control"] = "no-store";
            await next();
        });

        app.MapGet("/", async ctx =>
        {
            ctx.Response.ContentType = "text/html; charset=utf-8";
            ctx.Response.Headers["Content-Security-Policy"] =
                "default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; connect-src 'self'; img-src data:";
            await using var page = typeof(DashboardServer).Assembly.GetManifestResourceStream("Watchme.Cli.dashboard.html")
                ?? throw new InvalidOperationException("dashboard.html is missing from the build.");
            await page.CopyToAsync(ctx.Response.Body);
        });

        app.MapGet("/api/range", (HttpContext ctx) =>
        {
            var to = Date(ctx, "to") ?? DateOnly.FromDateTime(DateTime.Now);
            var from = Date(ctx, "from") ?? to.AddDays(-6);
            if (Math.Abs(to.DayNumber - from.DayNumber) > 366)
                return Results.BadRequest("Pick a range of at most a year.");
            using var db = Database.Open(Paths.Database);
            return Results.Json(Dashboard.Build(db, from, to), Json);
        });

        app.MapGet("/api/day", (HttpContext ctx) =>
        {
            var day = Date(ctx, "date") ?? DateOnly.FromDateTime(DateTime.Now);
            using var db = Database.Open(Paths.Database);
            return Results.Json(Dashboard.BuildDay(db, day), Json);
        });

        app.MapGet("/api/events", (HttpContext ctx) =>
        {
            var to = Date(ctx, "to") ?? DateOnly.FromDateTime(DateTime.Now);
            var from = Date(ctx, "from") ?? to;
            var q = ctx.Request.Query;
            long? before = long.TryParse(q["before"], out var b) ? b : null;
            long? after = long.TryParse(q["after"], out var a) ? a : null;
            var limit = int.TryParse(q["limit"], out var l) ? l : 100;
            using var db = Database.Open(Paths.Database);
            var rows = Dashboard.Events(db, Dashboard.DayStart(from), Dashboard.DayStart(to.AddDays(1)),
                q["source"], q["q"], before, limit, q["app"], after);
            return Results.Json(rows, Json);
        });

        app.MapGet("/api/status", () =>
        {
            using var db = Database.Open(Paths.Database);
            var report = AgentHealth.Build(db, AgentControl.IsRunning(), Clock.NowMs(), PauseState.FilePath);
            return Results.Json(new
            {
                report.Running,
                report.LastAlive,
                report.Pause,
                Problems = report.Problems.Select(p => new { p.Source, p.Name, Level = p.Level.ToString().ToLowerInvariant(), p.Message }),
            }, Json);
        });

        // Pause and resume change what is recorded, so unlike the GETs they must not be reachable
        // from other sites: a form on any page can POST to 127.0.0.1, but it can't add a custom
        // header without a CORS preflight, which this server never answers.
        app.MapPost("/api/pause", (HttpContext ctx) =>
        {
            if (!FromThisPage(ctx))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (!PauseState.TryParseDuration(ctx.Request.Query["for"], DateTime.Now, out var duration, out var error))
                return Results.BadRequest(error);
            var now = Clock.NowMs();
            using var db = Database.Open(Paths.Database);
            return Results.Json(PauseState.Begin(db, PauseState.FilePath, now, now + (long)duration.TotalMilliseconds), Json);
        });

        app.MapPost("/api/resume", (HttpContext ctx) =>
        {
            if (!FromThisPage(ctx))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            var now = Clock.NowMs();
            var wasPaused = PauseState.Active(PauseState.FilePath, now) is not null;
            using var db = Database.Open(Paths.Database);
            // Also tidies away an ended pause the agent never removed.
            PauseState.End(db, PauseState.FilePath, now);
            return Results.Json(new { resumed = wasPaused }, Json);
        });

        // One event from the log's details: like `watchme forget --match`, a focus event keeps its time.
        app.MapPost("/api/forget", (HttpContext ctx) =>
        {
            if (!FromThisPage(ctx))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (!long.TryParse(ctx.Request.Query["id"], out var id))
                return Results.BadRequest("Which event? Pass id.");
            var redact = ctx.Request.Query["redact"] == "1";
            using var db = Database.Open(Paths.Database);
            var plan = Forgetting.Apply(db, new Forgetting.Selection(null, null, [], EventId: id), redact, Clock.NowMs());
            return Results.Json(new { plan.Removed, Redacted = plan.RedactedCount }, Json);
        });

        var url = $"http://127.0.0.1:{port}/";
        try
        {
            app.Start();
        }
        catch (IOException ex)
        {
            Console.Error.WriteLine($"Could not listen on {url}: {ex.Message}");
            return 1;
        }

        Console.WriteLine($"Watchme dashboard on {url}  (Ctrl+C to stop)");
        if (open)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception ex) { Console.Error.WriteLine($"Could not open a browser: {ex.Message}"); }
        }
        if (parent is { } parentPid)
            StopWithParent(app, parentPid);
        app.WaitForShutdown();
        return 0;
    }

    /// <summary>Stops the server when the process that started it exits (or is already gone).</summary>
    private static void StopWithParent(WebApplication app, int pid)
    {
        Process owner;
        try { owner = Process.GetProcessById(pid); }
        catch (ArgumentException)
        {
            app.Lifetime.StopApplication();
            return;
        }
        _ = owner.WaitForExitAsync().ContinueWith(_ => app.Lifetime.StopApplication(), TaskScheduler.Default);
    }

    /// <summary>A request the dashboard page itself made: it carries our header, and any Origin is ours.</summary>
    internal static bool FromThisPage(HttpContext ctx)
    {
        if (ctx.Request.Headers["X-Watchme"] != "1")
            return false;
        var origin = ctx.Request.Headers.Origin.ToString();
        if (origin.Length == 0)
            return true;
        return Uri.TryCreate(origin, UriKind.Absolute, out var o) && o.Scheme == "http" &&
               o.Host is ("127.0.0.1" or "localhost") && o.Port == ctx.Request.Host.Port;
    }

    private static DateOnly? Date(HttpContext ctx, string key) =>
        DateOnly.TryParseExact(ctx.Request.Query[key], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d
            : null;
}
