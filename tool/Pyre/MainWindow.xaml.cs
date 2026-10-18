using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Pyre.Cli;
using Pyre.Localization;
using Pyre.Server;
using Pyre.Services;
using Pyre.Ui;

namespace Pyre;

/// <summary>
/// The window. Code-behind rather than MVVM on purpose - this is one screen with one job,
/// and the brief asked for it to read clearly, not to be a framework showcase.
/// </summary>
public partial class MainWindow : Window
{
    private const int MaxLogLines = 200;

    private readonly LocalizationService _loc = LocalizationService.Instance;
    private readonly ObservableCollection<string> _log = new();
    private readonly ObservableCollection<InstructionItem> _instructions = new();
    private readonly ObservableCollection<string> _ps5Steps = new();
    private readonly ObservableCollection<PayloadRow> _payloadRows = new();
    private readonly ObservableCollection<EmuLine> _emuRetro = new();
    private readonly ObservableCollection<string> _emuSystems = new();
    private readonly ObservableCollection<EmuLine> _emuProspero = new();
    private readonly DispatcherTimer _toastTimer;

    /// <summary>--root, when the user asked for a folder deliberately. Null = embedded copy.</summary>
    private readonly string? _overrideRoot;

    private readonly bool _suppressFirewall;

    private AppSettings _settings;
    private PyreWebServer? _server;
    private DnsServer? _dns;
    private TlsProxy? _tls;
    private ElfLocator? _elfs;
    private LanIpResult _lan = LanIpResult.None;
    private SiteResolution _site = new();
    private ElfReport? _payloads;
    private string _phase = "stopped";
    private bool _busy;
    private bool _firewallBusy;

    internal MainWindow(ParsedArgs args)
    {
        _settings = SettingsStore.Load();
        if (args.PortSpecified)
        {
            _settings.Port = args.Port;
        }

        _suppressFirewall = args.NoFirewall;
        _overrideRoot = args.Root;

        InitializeComponent();

        LogList.ItemsSource = _log;
        InstructionsList.ItemsSource = _instructions;
        Ps5Steps.ItemsSource = _ps5Steps;
        PayloadsList.ItemsSource = _payloadRows;
        EmuRetroList.ItemsSource = _emuRetro;
        EmuSystemsList.ItemsSource = _emuSystems;
        EmuProsperoList.ItemsSource = _emuProspero;

        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            ResetCopyLabels();
        };

        LogRequestsCheck.IsChecked = _settings.LogRequests;

        LanguageBox.ItemsSource = _loc.Available;
        LanguageBox.DisplayMemberPath = nameof(LanguageOption.DisplayName);
        LanguageBox.SelectedItem = _loc.Available.FirstOrDefault(l => l.Code == _settings.Language)
                                   ?? _loc.Available.FirstOrDefault();
        _loc.SetLanguage(_settings.Language);

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    // ------------------------------------------------------------------ startup

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyLanguage();
        RefreshLan();

        LogLine($"Pyre {_settings.Port}");

        // Resolve what to serve. Without --root this unpacks and SHA256-verifies the copy
        // embedded in the exe, which is a one-off first-run cost - so keep it off the UI thread.
        // The payloads live beside the exe now, so look for them at the same time.
        var overrideRoot = _overrideRoot;
        var (site, payloads, elfs) = await Task.Run(() =>
        {
            var resolved = EmbeddedSite.Resolve(overrideRoot);
            var report = ElfLocator.InspectManifest(overrideRoot);
            var locator = ElfLocator.Create(overrideRoot);
            return (resolved, report, locator);
        });

        _site = site;
        _payloads = payloads;
        _elfs = elfs;

        LogSite();
        LogPayloads();
        UpdateSiteUi();
        UpdatePayloadUi();
        ApplyState();
    }

    private void LogSite()
    {
        if (_site.Ok && _site.Kind == SiteSourceKind.Embedded)
        {
            LogLine(_loc.T("log.embed.ready", _site.BuildId, _site.FileCount, _site.Root));
        }
        else if (_site.Ok)
        {
            LogLine(_loc.T("log.override.serving", _site.Root));
        }
        else if (_site.ErrorKind == SiteErrorKind.OverrideMissing)
        {
            LogError(_loc.T("log.override.missing", _site.Root));
        }
        else
        {
            LogError(_loc.T("log.embed.failed", _site.BuildId, _site.Error));
        }
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        // Windows 10/11 draws a light title bar by default, which would look wrong on the
        // charcoal window. This is cosmetic and optional: if the OS says no, we move on.
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            var enabled = 1;
            if (DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int)) != 0)
            {
                DwmSetWindowAttribute(handle, 19, ref enabled, sizeof(int));
            }
        }
        catch (Exception)
        {
            // cosmetic only
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        var server = _server;
        _server = null;

        if (server is not null)
        {
            try
            {
                server.StopAsync().Wait(TimeSpan.FromSeconds(3));
            }
            catch (Exception)
            {
                // we are closing either way
            }
        }

        _settings.LogRequests = LogRequestsCheck.IsChecked == true;
        SettingsStore.Save(_settings);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    // ------------------------------------------------------------------ language

    private void OnLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LanguageBox.SelectedItem is not LanguageOption option)
        {
            return;
        }

        if (!_loc.SetLanguage(option.Code))
        {
            return;
        }

        _settings.Language = option.Code;
        SettingsStore.Save(_settings);
        ApplyLanguage();
        LogLine(_loc.T("log.language", option.DisplayName));
    }

    private void ApplyLanguage()
    {
        Title = _loc.T("app.window");
        EyebrowText.Text = _loc.T("app.eyebrow");
        TitleText.Text = TextFx.Spaced(_loc.T("app.title"));
        TaglineText.Text = _loc.T("app.tagline");
        VersionText.Text = _loc.T("app.version");

        HostCardTitle.Text = _loc.T("host.card");
        HostCardHint.Text = _loc.T("host.card.hint");
        IpLabel.Text = _loc.T("host.ip.label");
        IpCopyButton.Content = _loc.T("host.ip.copy");
        IpRefreshButton.Content = _loc.T("host.ip.refresh");
        IpRefreshButton.ToolTip = _loc.T("host.ip.refreshed");
        UrlLabel.Text = _loc.T("host.url.label");
        UrlCopyButton.Content = _loc.T("host.url.copy");

        FirewallCardTitle.Text = _loc.T("firewall.card");
        FirewallHint.Text = _loc.T("firewall.card.hint");
        FirewallAdminNote.Text = _loc.T("firewall.note.admin");

        HowToCardTitle.Text = _loc.T("howto.card");
        Ps5Button.Content = _loc.T("ps5.button");

        LogCardTitle.Text = _loc.T("log.card");
        LogHint.Text = _loc.T("log.card.hint");
        LogRequestsCheck.Content = _loc.T("log.requests");
        LogClearButton.Content = _loc.T("log.clear");

        LangLabel.Text = _loc.T("lang.label");

        // the five tab destinations
        TabHost.Header = _loc.T("tab.host");
        TabJailbreak.Header = _loc.T("tab.jailbreak");
        TabPayloads.Header = _loc.T("tab.payloads");
        TabEmulation.Header = _loc.T("tab.emulation");
        TabLog.Header = _loc.T("tab.log");

        PayloadsCardTitle.Text = _loc.T("payloads.card");
        PayloadsHint.Text = _loc.T("payloads.card.hint");

        EmulationIntro.Text = _loc.T("emu.intro");
        EmuRetroTitle.Text = _loc.T("emu.retro.card");
        EmuRetroIntro.Text = _loc.T("emu.retro.intro");
        EmuSystemsLabel.Text = _loc.T("emu.retro.systems.label");
        EmuProsperoTitle.Text = _loc.T("emu.prospero.card");
        EmuProsperoIntro.Text = _loc.T("emu.prospero.intro");
        EmulationNote.Text = _loc.T("emu.note");

        Ps5Title.Text = _loc.T("ps5.title");
        Ps5Intro.Text = _loc.T("ps5.intro");
        Ps5CloseButton.Content = _loc.T("ps5.close");

        RebuildInstructions();
        RebuildEmulation();
        UpdateBootstrapUi();
        UpdateSiteUi();
        UpdatePayloadUi();
        UpdateIpUi();
        UpdateFirewallUi();
        ApplyState();
    }

    private void RebuildInstructions()
    {
        _instructions.Clear();
        for (var i = 1; i <= 4; i++)
        {
            _instructions.Add(new InstructionItem(i.ToString("00"), _loc.T($"howto.{i}")));
        }
    }

    // ------------------------------------------------------------------ LAN address

    private void RefreshLan()
    {
        _lan = LanAddress.Detect();

        if (_lan.Found)
        {
            var adapter = string.IsNullOrEmpty(_lan.AdapterName)
                ? string.Empty
                : _loc.T("ip.source.adapter", _lan.AdapterName);

            LogLine(_loc.T("log.ip.found", _lan.Ip, adapter));
        }
        else
        {
            LogError(_loc.T("log.ip.failed"));
        }

        UpdateIpUi();
    }

    private string DisplayIp() => _lan.Found ? _lan.Ip : "127.0.0.1";

    private void UpdateIpUi()
    {
        if (_lan.Found)
        {
            IpButton.Content = _lan.Ip;

            var adapter = string.IsNullOrEmpty(_lan.AdapterName)
                ? string.Empty
                : _loc.T("ip.source.adapter", _lan.AdapterName);

            var key = _lan.Source switch
            {
                LanIpSource.DefaultRoute => "ip.source.defaultroute",
                LanIpSource.GatewayInterface => "ip.source.gateway",
                LanIpSource.IpConfigFallback => "ip.source.ipconfig",
                _ => "ip.source.none",
            };

            IpSourceText.Text = _loc.T(key, adapter);
        }
        else
        {
            IpButton.Content = _loc.T("host.ip.unknown");
            IpSourceText.Text = _loc.T("ip.source.none");
        }
    }

    // ------------------------------------------------------------------ site presence

    private void UpdateSiteUi()
    {
        var showError = !_site.Ok && _site.ErrorKind != SiteErrorKind.None;
        SiteErrorBanner.Visibility = showError ? Visibility.Visible : Visibility.Collapsed;

        if (_site.Ok)
        {
            RootText.Text = _site.Kind == SiteSourceKind.Embedded
                ? _loc.T("host.root.embedded", _site.BuildId, _site.FileCount)
                : _loc.T("host.root.override", _site.Root);
            return;
        }

        switch (_site.ErrorKind)
        {
            case SiteErrorKind.EmbeddedFailed:
                SiteErrorTitle.Text = _loc.T("site.embed.failed.title");
                SiteErrorBody.Text = _loc.T("site.embed.failed.body", _site.BuildId, _site.Error);
                RootText.Text = _loc.T("host.root.none");
                break;

            case SiteErrorKind.OverrideMissing:
                SiteErrorTitle.Text = _loc.T("site.override.missing.title");
                SiteErrorBody.Text = _loc.T("site.override.missing.body", _site.Root);
                RootText.Text = _loc.T("host.root.none");
                break;

            default:
                SiteErrorTitle.Text = string.Empty;
                SiteErrorBody.Text = string.Empty;
                RootText.Text = _loc.T("host.root.preparing");
                break;
        }
    }

    // ------------------------------------------------------------------ payloads (beside the exe)

    /// <summary>
    /// The ELF payloads are no longer inside the exe: they ship beside it. Say plainly which
    /// were found and warn about every one that is missing - a missing payload must never be
    /// silent, because the matching button on the console page will 404.
    /// </summary>
    private void LogPayloads()
    {
        var report = _payloads;
        if (report is null)
        {
            return;
        }

        if (report.Bases.Count > 0)
        {
            LogLine(_loc.T("log.payloads.base", string.Join(" ; ", report.Bases)));
        }

        LogLine(_loc.T("log.payloads.count", report.FoundCount, report.Expected));

        foreach (var finding in report.MissingRequiredFinding)
        {
            LogError(_loc.T("log.payloads.missing", finding.Path));
        }

        if (report.MissingOptional > 0)
        {
            LogLine(_loc.T("log.payloads.optional", report.MissingOptional));
        }
    }

    private void UpdatePayloadUi()
    {
        var report = _payloads;
        UpdatePayloadsList();

        if (report is null)
        {
            return;
        }

        if (report.Expected == 0)
        {
            PayloadText.Text = _loc.T("host.payloads.unknown");
            PayloadText.Foreground = (Brush)FindResource("Muted");
            return;
        }

        if (report.MissingRequired > 0)
        {
            var names = string.Join(", ", report.MissingRequiredFinding.Select(f => f.Path).Take(6));
            if (report.MissingRequired > 6)
            {
                names += " …";
            }

            PayloadText.Text = _loc.T("host.payloads.missing", report.MissingRequired, report.Expected, names);
            PayloadText.Foreground = (Brush)FindResource("Warn");
            return;
        }

        if (report.FoundCount == 0)
        {
            PayloadText.Text = _loc.T("host.payloads.none");
            PayloadText.Foreground = (Brush)FindResource("Warn");
            return;
        }

        var text = _loc.T("host.payloads.ok", report.FoundCount, report.Expected);
        if (report.MissingOptional > 0)
        {
            text += " · " + _loc.T("host.payloads.optional", report.MissingOptional);
        }

        PayloadText.Text = text;
        PayloadText.Foreground = (Brush)FindResource("Ok");
    }

    // ------------------------------------------------------------------ hosting

    private void ApplyState()
    {
        var running = _server?.IsRunning == true;

        StatusText.Text = _loc.T("host.status." + _phase);

        var active = _phase == "running" || _phase == "starting";
        StatusText.Foreground = (Brush)FindResource(active ? "Ember" : "Ash");
        StatusPill.BorderBrush = (Brush)FindResource(active ? "Ember" : "Line");
        StatusPill.Background = active ? new SolidColorBrush(Color.FromRgb(0x2A, 0x16, 0x08)) : new SolidColorBrush(Color.FromRgb(0x1A, 0x10, 0x09));

        StartButton.Content = _loc.T(running ? "host.stop" : "host.start");
        StartButton.Style = (Style)FindResource(running ? "SecondaryButton" : "PrimaryButton");
        StartButton.IsEnabled = !_busy && (_site.Ok || running);

        UrlBox.Text = running
            ? PyreWebServer.Url(DisplayIp(), _settings.Port)
            : _loc.T("host.url.empty");
        UrlCopyButton.IsEnabled = running;
    }

    private async void OnStartStop(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        if (_server?.IsRunning == true)
        {
            await StopHostingAsync();
            return;
        }

        await StartHostingAsync();
    }

    private async Task StartHostingAsync()
    {
        _busy = true;
        SetPhase("starting");
        ApplyState();

        try
        {
            if (!_site.Ok)
            {
                LogSite();
                SetPhase("stopped");
                return;
            }

            RefreshLan();

            if (!_suppressFirewall)
            {
                await RunFirewallAsync();
            }

            // The payload locator MUST be passed here. Without it the server's .elf branch is
            // skipped entirely (_elfs is null) and every payload request 404s - which means RUN
            // PYRE can never fetch elfldr or kexp, on every attempt, for every user. The CLI and
            // the selftest both passed a locator, so this only ever broke in the GUI: the one
            // path the tests did not cover.
            var server = new PyreWebServer(_site.Root, _settings.Port, _elfs) { LogRequests = LogRequestsCheck.IsChecked == true };
            server.Notice += OnServerNotice;
            server.Error += OnServerError;
            server.RequestServed += OnServerRequest;

            server.Start();
            _server = server;

            var url = PyreWebServer.Url(DisplayIp(), _settings.Port);
            SetPhase("running");
            LogLine(_loc.T("log.started", url));

            StartBootstrapListeners();
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            LogError(_loc.T("log.portbusy", _settings.Port));
            SetPhase("stopped");
        }
        catch (Exception ex)
        {
            LogError($"{ex.GetType().Name}: {ex.Message}");
            SetPhase("stopped");
        }
        finally
        {
            _busy = false;
            ApplyState();
        }
    }

    /// <summary>
    /// Start the DNS responder (53) and the guide's TLS listener (443).
    ///
    /// Together these are what make "set the console's DNS to this PC" true: 53 answers
    /// manuals.playstation.net with this address, and 443 serves the guide over the HTTPS the
    /// console expects. Neither is fatal on its own - hosting still runs, the log names the
    /// one that failed, and 53 is the usual casualty because Windows normally has Internet
    /// Connection Sharing holding 0.0.0.0:53.
    /// </summary>
    private void StartBootstrapListeners()
    {
        StopBootstrapListeners();

        if (!_lan.Found || !System.Net.IPAddress.TryParse(_lan.Ip, out var self))
        {
            LogLine(_loc.T("log.bootstrap.nolan"));
            UpdateBootstrapUi();
            return;
        }

        try
        {
            var dns = new DnsServer(self, LogLine);
            dns.Start();
            _dns = dns;
        }
        catch (Exception ex)
        {
            LogError(_loc.T("log.bootstrap.dnsfailed", ex.Message));
        }

        try
        {
            var tls = new TlsProxy(self, _settings.Port, LogLine);
            tls.Start();
            _tls = tls;
        }
        catch (Exception ex)
        {
            LogError(_loc.T("log.bootstrap.tlsfailed", ex.Message));
        }

        UpdateBootstrapUi();
    }

    private void StopBootstrapListeners()
    {
        _dns?.Stop();
        _dns?.Dispose();
        _dns = null;

        _tls?.Stop();
        _tls?.Dispose();
        _tls = null;

        UpdateBootstrapUi();
    }

    /// <summary>Say plainly on the Host tab whether the console can actually use this PC as
    /// its DNS. Both listeners being up is what the instructions assume.</summary>
    private void UpdateBootstrapUi()
    {
        if (BootstrapText is null)
        {
            return;
        }

        var dnsUp = _dns?.Running == true;
        var tlsUp = _tls?.Running == true;

        if (dnsUp && tlsUp)
        {
            BootstrapText.Text = _loc.T("host.bootstrap.ok", DisplayIp());
            BootstrapText.Foreground = (Brush)FindResource("Ok");
        }
        else if (dnsUp)
        {
            BootstrapText.Text = _loc.T("host.bootstrap.nohttps", DisplayIp());
            BootstrapText.Foreground = (Brush)FindResource("Warn");
        }
        else
        {
            BootstrapText.Text = _loc.T("host.bootstrap.off");
            BootstrapText.Foreground = (Brush)FindResource("Muted");
        }
    }

    private async Task StopHostingAsync()
    {
        _busy = true;
        SetPhase("stopping");
        ApplyState();

        var server = _server;
        _server = null;

        StopBootstrapListeners();

        try
        {
            if (server is not null)
            {
                await server.StopAsync();
            }

            LogLine(_loc.T("log.stopped"));
        }
        catch (Exception ex)
        {
            LogError($"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _busy = false;
            SetPhase("stopped");
            ApplyState();
        }
    }

    private void SetPhase(string phase)
    {
        _phase = phase;
        ApplyState();
    }

    // Server events arrive on connection threads, so everything hops back to the UI thread.
    private void OnServerNotice(string message) =>
        Dispatcher.InvokeAsync(() => LogLine(message));

    private void OnServerError(string message) =>
        Dispatcher.InvokeAsync(() => LogError(message));

    private void OnServerRequest(string message) =>
        Dispatcher.InvokeAsync(() => LogLine(message));

    // ------------------------------------------------------------------ firewall

    private void OnFirewallButton(object sender, RoutedEventArgs e)
    {
        // A deliberate, user-initiated retry. Still check-then-add, so pressing it twice
        // can never create the rule twice.
        _ = RunFirewallAsync();
    }

    /// <summary>
    /// Only ever called automatically when the outcome is not yet known. Once anything has
    /// been recorded - present, added, declined or failed - Pyre does not check again and
    /// does not ask again. That is Cam's requirement, and it is why the state is persisted.
    /// </summary>
    private async Task RunFirewallAsync()
    {
        if (_firewallBusy)
        {
            return;
        }

        _firewallBusy = true;
        FirewallButton.IsEnabled = false;
        FirewallStatusText.Text = _loc.T("firewall.checking");
        FirewallDot.Fill = (Brush)FindResource("Warn");

        try
        {
            var result = await Task.Run(() => FirewallManager.EnsureRule());
            PersistFirewall(result);
        }
        catch (Exception ex)
        {
            PersistFirewall(new FirewallResult(FirewallOutcome.Failed, $"{ex.GetType().Name}: {ex.Message}"));
        }
        finally
        {
            _firewallBusy = false;
            FirewallButton.IsEnabled = true;
            UpdateFirewallUi();
        }
    }

    private void PersistFirewall(FirewallResult result)
    {
        var state = result.Outcome switch
        {
            FirewallOutcome.Exists => "exists",
            FirewallOutcome.Added => "added",
            FirewallOutcome.Declined => "declined",
            FirewallOutcome.Unavailable => "unavailable",
            _ => "failed",
        };

        _settings.Firewall.Checked = true;
        _settings.Firewall.RuleName = FirewallManager.RuleName;
        _settings.Firewall.State = state;
        _settings.Firewall.Detail = result.Detail;
        _settings.Firewall.LastAttemptUtc = DateTime.UtcNow.ToString("o");
        SettingsStore.Save(_settings);

        switch (result.Outcome)
        {
            case FirewallOutcome.Exists:
                LogLine(_loc.T("log.firewall.exists", FirewallManager.RuleName));
                break;

            case FirewallOutcome.Added:
                LogLine(_loc.T("log.firewall.added", FirewallManager.RuleName));
                break;

            case FirewallOutcome.Declined:
                LogError(_loc.T("log.firewall.declined", FirewallManager.RuleName));
                break;

            case FirewallOutcome.Unavailable:
                LogError(_loc.T("log.firewall.unavailable", result.Detail));
                break;

            default:
                LogError(_loc.T("log.firewall.failed", FirewallManager.RuleName, result.Detail));
                break;
        }
    }

    private void UpdateFirewallUi()
    {
        var firewall = _settings.Firewall;
        var state = firewall.State ?? "unknown";

        var key = state switch
        {
            "exists" => "firewall.state.exists",
            "added" => "firewall.state.added",
            "declined" => "firewall.state.declined",
            "failed" => "firewall.state.failed",
            "unavailable" => "firewall.state.unavailable",
            _ => "firewall.state.unknown",
        };

        var text = _loc.T(key);
        if (state is "failed" or "unavailable" && firewall.Detail.Length > 0)
        {
            text = $"{text} · {firewall.Detail}";
        }

        FirewallStatusText.Text = text;

        FirewallDot.Fill = (Brush)FindResource(state switch
        {
            "exists" or "added" => "Ok",
            "declined" or "failed" or "unavailable" => "Warn",
            _ => "Ash",
        });

        FirewallButton.Content = _loc.T(state is "declined" or "failed" or "unavailable"
            ? "firewall.button.retry"
            : "firewall.button.check");

        var note = state switch
        {
            "declined" => _loc.T("firewall.note.declined"),
            "failed" => _loc.T("firewall.note.failed"),
            _ => string.Empty,
        };

        FirewallNoteText.Text = note;
        FirewallNoteText.Visibility = note.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ------------------------------------------------------------------ copy / log / ps5

    private void OnCopyIp(object sender, RoutedEventArgs e)
    {
        if (!_lan.Found)
        {
            return;
        }

        CopyToClipboard(_lan.Ip, IpCopyButton);
    }

    private void OnCopyUrl(object sender, RoutedEventArgs e)
    {
        if (_server?.IsRunning != true)
        {
            return;
        }

        CopyToClipboard(PyreWebServer.Url(DisplayIp(), _settings.Port), UrlCopyButton);
    }

    private void CopyToClipboard(string text, Button toast)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(text, true);
                toast.Content = _loc.T("host.ip.copied");
                _toastTimer.Stop();
                _toastTimer.Start();
                LogLine(_loc.T("log.copied", text));
                return;
            }
            catch (ExternalException)
            {
                Thread.Sleep(80);
            }
            catch (Exception)
            {
                break;
            }
        }

        LogError(_loc.T("log.copyfailed"));
    }

    private void ResetCopyLabels()
    {
        IpCopyButton.Content = _loc.T("host.ip.copy");
        UrlCopyButton.Content = _loc.T("host.url.copy");
    }

    private void OnRefreshIp(object sender, RoutedEventArgs e) => RefreshLan();

    private void OnLogRequestsToggled(object sender, RoutedEventArgs e)
    {
        var on = LogRequestsCheck.IsChecked == true;
        _settings.LogRequests = on;

        if (_server is not null)
        {
            _server.LogRequests = on;
        }

        SettingsStore.Save(_settings);
    }

    private void OnClearLog(object sender, RoutedEventArgs e) => _log.Clear();

    private void OnShowPs5(object sender, RoutedEventArgs e)
    {
        _ps5Steps.Clear();
        // Stop at the first step the localization table does not define (T returns
        // the key itself when nothing defines it), so adding a step is a JSON edit
        // and never needs this number changed again.
        for (var i = 1; i <= 16; i++)
        {
            var key = $"ps5.step.{i}";
            var text = _loc.T(key);
            if (text == key)
            {
                break;
            }
            _ps5Steps.Add(text);
        }

        Ps5Note.Text = _loc.T("ps5.note");
        Ps5Overlay.Visibility = Visibility.Visible;
    }

    private void OnClosePs5(object sender, RoutedEventArgs e) => Ps5Overlay.Visibility = Visibility.Collapsed;

    /// <summary>
    /// Append a line to the log.
    ///
    /// The DNS responder and both relays log from their own background threads, and an
    /// ObservableCollection bound to a UI control THROWS if it is changed off the UI thread
    /// ("This type of CollectionView does not support changes to its SourceCollection from a
    /// thread different from the Dispatcher thread"). That exception used to escape into the
    /// DNS handler and kill it BEFORE the reply was sent, which presented on the console as
    /// "cannot resolve host name" - a logging bug wearing a networking costume.
    ///
    /// So: hop to the dispatcher when we are not on it, and never let logging throw.
    /// </summary>
    private void LogLine(string text)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => LogLine(text)));
            return;
        }

        try
        {
            _log.Add($"{DateTime.Now:HH:mm:ss}  {text}");
            TrimLog();
        }
        catch (Exception)
        {
            // Logging must never take the caller down with it.
        }
    }

    private void LogError(string text)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => LogError(text)));
            return;
        }

        try
        {
            _log.Add($"{DateTime.Now:HH:mm:ss}  !! {text}");
            TrimLog();
        }
        catch (Exception)
        {
            // as above
        }
    }

    private void TrimLog()
    {
        while (_log.Count > MaxLogLines)
        {
            _log.RemoveAt(0);
        }

        LogScroll.ScrollToEnd();
    }

    // ------------------------------------------------------------------ payloads tab

    /// <summary>
    /// One row per payload the site asks for, so the Payloads tab shows the whole
    /// expected set instead of a single summary line. Present-but-wrong-size is
    /// called out separately from absent: haze-sender verifies the file against the
    /// manifest before pushing, so a size mismatch makes Pyre refuse to send it and
    /// is worth seeing before that happens.
    /// </summary>
    private void UpdatePayloadsList()
    {
        _payloadRows.Clear();

        var report = _payloads;
        if (report is null)
        {
            PayloadsPillText.Text = _loc.T("payloads.pill.unknown");
            return;
        }

        PayloadsPillText.Text = _loc.T("payloads.pill", report.FoundCount, report.Expected);

        var ok = (Brush)FindResource("Ok");
        var warn = (Brush)FindResource("Warn");
        var bad = (Brush)FindResource("Bad");
        var muted = (Brush)FindResource("Muted");
        var ink = (Brush)FindResource("Ink");

        foreach (var f in report.Findings)
        {
            Brush dot;
            Brush tint;
            if (f.Found && f.SizeMatches)
            {
                dot = ok;
                tint = ink;
            }
            else if (f.Found)
            {
                dot = warn;
                tint = warn;
            }
            else if (f.Optional)
            {
                dot = muted;
                tint = muted;
            }
            else
            {
                dot = bad;
                tint = bad;
            }

            _payloadRows.Add(new PayloadRow
            {
                Path = f.Path,
                Size = f.Found ? HumanSize(f.ExpectedSize) : _loc.T("payloads.missing"),
                Dot = dot,
                Tint = tint,
            });
        }
    }

    private static string HumanSize(long bytes)
    {
        if (bytes >= 1024L * 1024L * 1024L)
        {
            return string.Format("{0:0.##} GB", bytes / 1024.0 / 1024.0 / 1024.0);
        }

        if (bytes >= 1024L * 1024L)
        {
            return string.Format("{0:0.#} MB", bytes / 1024.0 / 1024.0);
        }

        if (bytes >= 1024L)
        {
            return string.Format("{0:0.#} KB", bytes / 1024.0);
        }

        return bytes + " B";
    }

    // ----------------------------------------------------------------- emulation tab

    /// <summary>
    /// The Emulation tab. Every path here was read off the console over FTP rather
    /// than assumed: RetroArch lives under /data/homebrew (an older note had it on
    /// /mnt/usb0, which is empty), and ProsperoEden is Switch emulation with
    /// .nsp/.xci files under roms/.
    /// </summary>
    private void RebuildEmulation()
    {
        _emuRetro.Clear();
        _emuSystems.Clear();
        _emuProspero.Clear();

        _emuRetro.Add(new EmuLine(_loc.T("emu.retro.live"), _loc.T("emu.retro.live.v")));
        _emuRetro.Add(new EmuLine(_loc.T("emu.retro.roms"), _loc.T("emu.retro.roms.v")));
        _emuRetro.Add(new EmuLine(_loc.T("emu.retro.bios"), _loc.T("emu.retro.bios.v")));
        _emuRetro.Add(new EmuLine(_loc.T("emu.retro.cores"), _loc.T("emu.retro.cores.v")));
        _emuRetro.Add(new EmuLine(_loc.T("emu.retro.menu"), _loc.T("emu.retro.menu.v")));

        foreach (var part in _loc.T("emu.retro.roms.systems").Split(','))
        {
            var name = part.Trim();
            if (name.Length > 0)
            {
                _emuSystems.Add(name);
            }
        }

        _emuProspero.Add(new EmuLine(_loc.T("emu.prospero.keys"), _loc.T("emu.prospero.keys.v")));
        _emuProspero.Add(new EmuLine(_loc.T("emu.prospero.firmware"), _loc.T("emu.prospero.firmware.v")));
        _emuProspero.Add(new EmuLine(_loc.T("emu.prospero.games"), _loc.T("emu.prospero.games.v")));
        _emuProspero.Add(new EmuLine(_loc.T("emu.prospero.config"), _loc.T("emu.prospero.config.v")));
    }

    /// <summary>One row in the Payloads tab.</summary>
    private sealed class PayloadRow
    {
        public string Path { get; init; } = string.Empty;

        public string Size { get; init; } = string.Empty;

        public Brush Dot { get; init; } = Brushes.Transparent;

        public Brush Tint { get; init; } = Brushes.Transparent;
    }

    /// <summary>A label/value row in the Emulation tab.</summary>
    private sealed record EmuLine(string Label, string Value);
}
