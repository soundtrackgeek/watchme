using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Net.Sockets;
using Watchme.Core;

namespace Watchme.Agent;

/// <summary>
/// The agent's icon in the notification area: it shows whether Watchme records, is paused or has
/// a source that needs a look, and its menu pauses, resumes and opens the dashboard.
/// <para>
/// The agent has no message loop of its own, so the icon gets a thread that runs one. Everything
/// it changes goes through the same files and database as the CLI: a pause from here is the same
/// <c>pause.json</c> and <c>session/pause</c> event as <c>watchme pause</c>, which the trackers
/// pick up within a second.
/// </para>
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private static readonly TimeSpan RefreshEvery = TimeSpan.FromSeconds(20);

    private static readonly (string Label, TimeSpan Duration)[] PauseChoices =
    [
        ("15 minutes", TimeSpan.FromMinutes(15)),
        ("30 minutes", TimeSpan.FromMinutes(30)),
        ("1 hour", TimeSpan.FromHours(1)),
        ("2 hours", TimeSpan.FromHours(2)),
    ];

    private readonly Action _quit;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _started = new();
    private readonly List<ToolStripItem> _problemItems = [];
    private readonly Lock _dashboardGate = new();
    private SynchronizationContext? _ui;
    private NotifyIcon? _notify;
    private ContextMenuStrip? _menu;
    private ToolStripMenuItem _header = null!;
    private ToolStripMenuItem _pauseMenu = null!;
    private ToolStripMenuItem _resume = null!;
    private ToolStripSeparator _statusEnd = null!;
    private readonly Dictionary<(bool Paused, TrayStatus.Alert Alert), Icon> _icons = [];
    private System.Threading.Timer? _timer;
    private TrayStatus _status = TrayStatus.Starting;
    private Process? _dashboard;
    private string? _dashboardUrl;
    private int _refreshing;
    private bool _statusFailed;
    private int _disposed;

    private TrayIcon(Action quit)
    {
        _quit = quit;
        _thread = new Thread(Run) { IsBackground = true, Name = "Tray icon" };
        _thread.SetApartmentState(ApartmentState.STA);
    }

    /// <summary>Shows the icon; <paramref name="quit"/> stops the agent (from the menu's Quit).</summary>
    public static TrayIcon Start(Action quit)
    {
        var tray = new TrayIcon(quit);
        tray._thread.Start();
        tray._started.Wait(TimeSpan.FromSeconds(5));
        return tray;
    }

    private void Run()
    {
        try
        {
            // Crisp icons and menus at any scale. The agent reads no window positions, so being
            // DPI aware changes nothing else it records.
            try { Application.SetHighDpiMode(HighDpiMode.PerMonitorV2); }
            catch (InvalidOperationException) { }
            Application.EnableVisualStyles();
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            _ui = SynchronizationContext.Current;

            var size = SystemInformation.SmallIconSize.Width;
            foreach (var paused in new[] { false, true })
                foreach (var alert in Enum.GetValues<TrayStatus.Alert>())
                    _icons[(paused, alert)] = DrawIcon(size, paused, alert);

            _menu = BuildMenu();
            _notify = new NotifyIcon
            {
                Icon = _icons[(false, TrayStatus.Alert.None)],
                Text = "Watchme",
                ContextMenuStrip = _menu,
                Visible = true,
            };
            _notify.MouseDoubleClick += (_, e) =>
            {
                if (e.Button == MouseButtons.Left)
                    OpenDashboard();
            };
            _timer = new System.Threading.Timer(_ => Refresh(), null, TimeSpan.Zero, RefreshEvery);
            _started.Set();
            Application.Run();
        }
        catch (Exception ex)
        {
            Log.Error("The tray icon failed; the agent keeps recording without it", ex);
        }
        finally
        {
            _started.Set();
            _timer?.Dispose();
            if (_notify is not null)
            {
                // Without this the icon lingers until the pointer passes over it.
                _notify.Visible = false;
                _notify.Dispose();
            }
            _menu?.Dispose();
            foreach (var icon in _icons.Values)
                icon.Dispose();
        }
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip { ShowItemToolTips = true };
        _header = new ToolStripMenuItem("Watchme") { Enabled = false };
        _statusEnd = new ToolStripSeparator();

        _pauseMenu = new ToolStripMenuItem("Pause for");
        foreach (var (label, duration) in PauseChoices)
            _pauseMenu.DropDownItems.Add(label, null, (_, _) => Pause(duration));
        _pauseMenu.ToolTipText = "Nothing is recorded, app history included; it resumes by itself.";
        _resume = new ToolStripMenuItem("Resume recording", null, (_, _) => Resume()) { Visible = false };

        var dashboard = new ToolStripMenuItem("Open dashboard", null, (_, _) => OpenDashboard());
        dashboard.Font = new Font(dashboard.Font, FontStyle.Bold);

        menu.Items.AddRange(
        [
            _header,
            _statusEnd,
            _pauseMenu,
            _resume,
            new ToolStripSeparator(),
            dashboard,
            new ToolStripMenuItem("Copy today's report", null, (_, _) => CopyReport()),
            new ToolStripSeparator(),
            new ToolStripMenuItem("Edit settings (config.json)", null, (_, _) => OpenFile(Paths.Config, notepad: true))
            {
                ToolTipText = "Changes apply the next time the agent starts.",
            },
            new ToolStripMenuItem("Open data folder", null, (_, _) => OpenFile(Paths.DataDir)),
            new ToolStripSeparator(),
            new ToolStripMenuItem("Quit Watchme", null, (_, _) => Quit()),
        ]);

        // The pause may have changed since the last refresh (watchme pause in a terminal); it is
        // one small file, so read it again before the menu shows.
        menu.Opening += (_, _) => Apply(_status.WithPause(PauseState.Active(PauseState.FilePath, Clock.NowMs())));
        return menu;
    }

    /// <summary>Reads the pause and the source problems, off the UI thread.</summary>
    private void Refresh()
    {
        if (Interlocked.Exchange(ref _refreshing, 1) == 1)
            return;
        try
        {
            TrayStatus status;
            using (var db = Database.Open(Paths.Database))
                status = TrayStatus.From(AgentHealth.Build(db, running: true, Clock.NowMs(), PauseState.FilePath));
            _statusFailed = false;
            _ui?.Post(_ => Apply(status), null);
        }
        catch (Exception ex)
        {
            if (!_statusFailed)
                Log.Error("The tray icon could not read the status", ex);
            _statusFailed = true;
        }
        finally
        {
            Volatile.Write(ref _refreshing, 0);
        }
    }

    private void RefreshSoon() => Task.Run(Refresh);

    private void Apply(TrayStatus status)
    {
        if (_notify is null || _menu is null)
            return;
        _status = status;
        var now = Clock.NowMs();
        _notify.Icon = _icons[(status.Paused, status.Badge)];
        _notify.Text = status.Tooltip(now);
        _header.Text = Escape($"Watchme: {status.Headline(now)}");
        _pauseMenu.Text = status.Paused ? "Pause from now for" : "Pause for";
        _resume.Visible = status.Paused;

        foreach (var item in _problemItems)
        {
            _menu.Items.Remove(item);
            item.Dispose();
        }
        _problemItems.Clear();
        if (status.Problems.Count > 0)
        {
            _problemItems.Add(new ToolStripMenuItem(Escape(status.ProblemsLine + ":")) { Enabled = false });
            foreach (var row in status.Problems)
                _problemItems.Add(new ToolStripMenuItem("    " + Escape(TrayStatus.MenuLine(row)), null, (_, _) => OpenFile(Paths.Log, notepad: true))
                {
                    ToolTipText = $"{row.Message}\nClick to open agent.log.",
                });
            var at = _menu.Items.IndexOf(_statusEnd);
            for (var i = 0; i < _problemItems.Count; i++)
                _menu.Items.Insert(at + i, _problemItems[i]);
        }
    }

    /// <summary>A lone &amp; in a menu item underlines the next letter instead of showing.</summary>
    private static string Escape(string text) => text.Replace("&", "&&", StringComparison.Ordinal);

    private void Pause(TimeSpan duration) => Act("pause", () =>
    {
        var now = Clock.NowMs();
        using var db = Database.Open(Paths.Database);
        var pause = PauseState.Begin(db, PauseState.FilePath, now, now + (long)duration.TotalMilliseconds);
        Apply(_status.WithPause(pause));
    });

    private void Resume() => Act("resume", () =>
    {
        using var db = Database.Open(Paths.Database);
        PauseState.End(db, PauseState.FilePath, Clock.NowMs());
        Apply(_status.WithPause(null));
    });

    private void CopyReport() => Task.Run(() =>
    {
        try
        {
            var text = new StringWriter();
            using (var db = Database.Open(Paths.Database))
                DailyReport.Print(db, DateOnly.FromDateTime(DateTime.Now), text);
            // The clipboard belongs to the STA thread.
            _ui?.Post(_ => Act("copy the report", () =>
            {
                Clipboard.SetText(text.ToString());
                Notify("Today's report is on the clipboard.");
            }), null);
        }
        catch (Exception ex)
        {
            Log.Error("The tray icon could not build the report", ex);
            _ui?.Post(_ => Notify($"Could not build the report: {ex.Message}"), null);
        }
    });

    /// <summary>
    /// Opens the dashboard in the browser. The first time, it starts <c>watchme dashboard</c>
    /// without a window; that server stays up for the next time and stops with the agent.
    /// </summary>
    private void OpenDashboard()
    {
        string url;
        Process server;
        lock (_dashboardGate)
        {
            if (_dashboard is { HasExited: false } && _dashboardUrl is not null)
            {
                OpenUrl(_dashboardUrl);
                return;
            }

            var cli = Path.Combine(AppContext.BaseDirectory, AgentIpc.CliExeName);
            if (!File.Exists(cli))
            {
                Notify($"The dashboard needs {AgentIpc.CliExeName} next to the agent ({AppContext.BaseDirectory}).");
                return;
            }

            var port = AgentIpc.FirstFreePort(AgentIpc.DashboardPort);
            var info = new ProcessStartInfo(cli)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = AppContext.BaseDirectory,
            };
            foreach (var arg in new[] { "dashboard", "--port", port.ToString(CultureInfo.InvariantCulture), "--no-open",
                                        "--parent", Environment.ProcessId.ToString(CultureInfo.InvariantCulture) })
                info.ArgumentList.Add(arg);
            try
            {
                _dashboard?.Dispose();
                _dashboard = server = Process.Start(info)!;
            }
            catch (Exception ex)
            {
                Log.Error("Could not start the dashboard", ex);
                Notify($"Could not start the dashboard: {ex.Message}");
                return;
            }
            _dashboardUrl = url = $"http://127.0.0.1:{port}/";
            Log.Info($"Dashboard started on {url} (pid {server.Id}).");
        }

        Task.Run(() =>
        {
            if (WaitUntilListening(server, url, TimeSpan.FromSeconds(15)))
                OpenUrl(url);
            else
                _ui?.Post(_ => Notify("The dashboard did not start; see 'watchme dashboard' in a terminal."), null);
        });
    }

    private static bool WaitUntilListening(Process server, string url, TimeSpan timeout)
    {
        var port = new Uri(url).Port;
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && !server.HasExited)
        {
            try
            {
                using var client = new TcpClient();
                client.Connect("127.0.0.1", port);
                return true;
            }
            catch (SocketException)
            {
                Thread.Sleep(150);
            }
        }
        return false;
    }

    private void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { _ui?.Post(_ => Notify($"Could not open a browser: {ex.Message}"), null); }
    }

    /// <summary>Opens a file or folder with its app; files with no app for them open in Notepad.</summary>
    private void OpenFile(string path, bool notepad = false) => Act("open " + Path.GetFileName(path), () =>
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception) when (notepad && File.Exists(path))
        {
            Process.Start(new ProcessStartInfo("notepad.exe") { ArgumentList = { path }, UseShellExecute = true });
        }
    });

    private void Quit()
    {
        var answer = MessageBox.Show(
            "Stop recording?\n\nWatchme starts again at your next logon, or with 'watchme start'.",
            "Quit Watchme", MessageBoxButtons.OKCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
        if (answer == DialogResult.OK)
            _quit();
    }

    /// <summary>Runs a menu action; a failure becomes a notification and a line in agent.log, never a crash.</summary>
    private void Act(string what, Action action)
    {
        try
        {
            action();
            if (what is "pause" or "resume")
                RefreshSoon();
        }
        catch (Exception ex)
        {
            Log.Error($"The tray icon could not {what}", ex);
            Notify($"Could not {what}: {ex.Message}");
        }
    }

    private void Notify(string text) => _notify?.ShowBalloonTip(4000, "Watchme", text, ToolTipIcon.None);

    /// <summary>
    /// The icon: a blue eye while recording, a grey pause sign while paused, and a badge in the
    /// corner (amber, or red for a failing source) when a source needs a look.
    /// </summary>
    internal static Icon DrawIcon(int size, bool paused, TrayStatus.Alert alert)
    {
        using var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);
            float s = size;
            var pad = s * 0.06f;
            var accent = paused ? Color.FromArgb(0x86, 0x8C, 0x96) : Color.FromArgb(0x2F, 0x7F, 0xE8);
            using var fill = new SolidBrush(accent);
            using var white = new SolidBrush(Color.White);
            g.FillEllipse(fill, pad, pad, s - 2 * pad, s - 2 * pad);

            if (paused)
            {
                var w = s * 0.14f;
                var h = s * 0.44f;
                var gap = s * 0.12f;
                var top = (s - h) / 2;
                g.FillRectangle(white, s / 2 - gap / 2 - w, top, w, h);
                g.FillRectangle(white, s / 2 + gap / 2, top, w, h);
            }
            else
            {
                var iris = s * 0.48f;
                g.FillEllipse(white, (s - iris) / 2, (s - iris) / 2, iris, iris);
                var pupil = s * 0.22f;
                g.FillEllipse(fill, (s - pupil) / 2, (s - pupil) / 2, pupil, pupil);
            }

            if (alert != TrayStatus.Alert.None)
            {
                var d = s * 0.5f;
                var x = s - d;
                var ring = Math.Max(1f, s / 16f);
                using var badge = new SolidBrush(alert == TrayStatus.Alert.Failing
                    ? Color.FromArgb(0xE5, 0x48, 0x4D)
                    : Color.FromArgb(0xF5, 0xA5, 0x24));
                g.FillEllipse(white, x, x, d, d);
                g.FillEllipse(badge, x + ring, x + ring, d - 2 * ring, d - 2 * ring);
            }
        }

        var handle = bmp.GetHicon();
        try
        {
            // Clone copies the icon into one that owns its handle, so the GDI one can go now.
            using var borrowed = Icon.FromHandle(handle);
            return (Icon)borrowed.Clone();
        }
        finally
        {
            Native.DestroyIcon(handle);
        }
    }

    /// <summary>Removes the icon and stops the dashboard it started.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;
        lock (_dashboardGate)
        {
            try
            {
                if (_dashboard is { HasExited: false })
                    _dashboard.Kill();
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            _dashboard?.Dispose();
            _dashboard = null;
        }
        if (_ui is { } ui)
        {
            ui.Post(_ => Application.ExitThread(), null);
            _thread.Join(TimeSpan.FromSeconds(2));
        }
    }
}
