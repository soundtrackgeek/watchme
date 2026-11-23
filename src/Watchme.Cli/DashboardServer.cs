using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
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
/// The page is embedded in watchme.exe and loads nothing from the internet.
/// </summary>
internal static class DashboardServer
{
    public const int DefaultPort = 7317;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static int Run(string[] args)
    {
        var port = DefaultPort;
        var portGiven = false;
        var open = true;
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
            port = FreePort(port);

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
            var limit = int.TryParse(q["limit"], out var l) ? l : 100;
            using var db = Database.Open(Paths.Database);
            var rows = Dashboard.Events(db, Dashboard.DayStart(from), Dashboard.DayStart(to.AddDays(1)),
                q["source"], q["q"], before, limit);
            return Results.Json(rows, Json);
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
        app.WaitForShutdown();
        return 0;
    }

    private static DateOnly? Date(HttpContext ctx, string key) =>
        DateOnly.TryParseExact(ctx.Request.Query[key], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d
            : null;

    /// <summary>The first port from <paramref name="start"/> that nothing listens on, so a second dashboard still starts.</summary>
    private static int FreePort(int start)
    {
        for (var port = start; port < start + 20; port++)
        {
            try
            {
                using var probe = new TcpListener(IPAddress.Loopback, port);
                probe.Start();
                return port;
            }
            catch (SocketException) { }
        }
        return start;
    }
}
