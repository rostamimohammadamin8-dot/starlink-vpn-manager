using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using StarlinkVpnManager.Models;
using StarlinkVpnManager.Services;
using StarlinkVpnManager.ViewModels;

namespace StarlinkVpnManager;

public partial class MainWindow : Window
{
    private readonly ProfileStore _profileStore = new();
    private readonly WireGuardServiceManager _serviceManager = new();
    private readonly NetworkDiagnosticsService _diagnosticsService = new();
    private readonly LatencyService _latencyService = new();
    private readonly AiRoutingService _aiRoutingService = new();
    private readonly TunnelManagerService _tunnelManager = new();
    private readonly KillSwitchService _killSwitchService = new();
    private readonly MainWindowViewModel _viewModel;
    private readonly ObservableCollection<WireGuardProfile> _profiles = [];
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(8) };
    private bool _isBusy;
    private bool _isRefreshingStatuses;
    private bool _isClosingAfterManagedTunnelStop;

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new MainWindowViewModel(
            _latencyService,
            _aiRoutingService,
            _tunnelManager,
            _killSwitchService,
            StartSelectedProfileAsync,
            StopSelectedProfileAsync,
            () => ProfilesList.SelectedItem is WireGuardProfile { Status: "فعال" },
            GetTrustedTunnelEndpointsAsync,
            ConfirmKillSwitchEnable);
        DataContext = _viewModel;
        _viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(MainWindowViewModel.IsBusy)
                or nameof(MainWindowViewModel.SingBoxConfigPath)
                or nameof(MainWindowViewModel.IsConnected))
            {
                UpdateActionButtons();
            }
        };
        ProfilesList.ItemsSource = _profiles;
        _viewModel.ConnectionLogs.CollectionChanged += (_, _) =>
        {
            if (_viewModel.ConnectionLogs.Count > 0)
            {
                Dispatcher.InvokeAsync(() =>
                    ConnectionLogList.ScrollIntoView(_viewModel.ConnectionLogs[^1]));
            }
        };
        _tunnelManager.LogReceived += (_, message) =>
            Dispatcher.InvokeAsync(() => _viewModel.AddLog(message));
        _statusTimer.Tick += StatusTimer_Tick;
        Closed += (_, _) => _statusTimer.Stop();
        Closing += MainWindow_Closing;
        UpdateActionButtons();
        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            foreach (var profile in _profileStore.Load())
            {
                _profiles.Add(profile);
            }

            EmptyState.Visibility = _profiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            _statusTimer.Start();
            await RefreshStatusesAsync();
            _viewModel.StatusMessage = _killSwitchService.IsEnabled
                ? "محافظت سراسری Windows Firewall از نشست قبلی فعال است؛ خاموش‌کردن آن تنظیمات قبلی را بازیابی می‌کند."
                : _serviceManager.ExecutablePath is null
                    ? "برای اتصال، ابتدا WireGuard for Windows را نصب کنید. پروفایل‌ها را می‌توانید از همین حالا وارد کنید."
                    : "آماده؛ یک پروفایل انتخاب کنید یا فایل WireGuard را وارد کنید.";
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or CryptographicException
                                   or InvalidDataException
                                   or JsonException
                                   or Win32Exception)
        {
            ShowError(ex);
        }
    }

    private async void ImportButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "انتخاب فایل پیکربندی WireGuard",
            Filter = "WireGuard configuration (*.conf)|*.conf",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        await RunActionAsync(() =>
        {
            var profile = _profileStore.Import(dialog.FileName);
            _profiles.Add(profile);
            EmptyState.Visibility = Visibility.Collapsed;
            ProfilesList.SelectedItem = profile;
            _viewModel.StatusMessage = $"پروفایل «{profile.Name}» به‌صورت رمزگذاری‌شده ذخیره شد.";
            return Task.CompletedTask;
        });
    }

    private void SelectSingBoxConfigButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "انتخاب پیکربندی sing-box",
            Filter = "sing-box JSON configuration (*.json)|*.json|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            _viewModel.SetSingBoxConfiguration(dialog.FileName);
            UpdateActionButtons();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            ShowError(ex);
        }
    }

    private async void KillSwitchToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { IsChecked: { } enabled })
        {
            await _viewModel.SetKillSwitchEnabledAsync(enabled);
        }
    }

    private async void DiagnosticsButton_Click(object sender, RoutedEventArgs e)
    {
        await RunActionAsync(async () =>
        {
            WireGuardProfileHints? hints = null;
            if (ProfilesList.SelectedItem is WireGuardProfile profile)
            {
                var configuration = _profileStore.ReadConfiguration(profile);
                try
                {
                    hints = WireGuardConfiguration.GetProfileHints(
                        System.Text.Encoding.UTF8.GetString(configuration));
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(configuration);
                }
            }

            var report = await _diagnosticsService.DiagnoseAsync(
                hints?.EndpointHosts ?? [],
                hints?.PersistentKeepaliveSeconds,
                hints?.Mtu);
            DisplayDiagnostics(report);
            _viewModel.StatusMessage = "آزمایش شبکه کامل شد؛ هیچ تنظیمی تغییر نکرد.";
        });
    }

    private async void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (ProfilesList.SelectedItem is not WireGuardProfile profile)
        {
            _viewModel.StatusMessage = "ابتدا یک پروفایل را انتخاب کنید.";
            return;
        }

        var result = MessageBox.Show(
            this,
            $"پروفایل «{profile.Name}» و تونل نصب‌شدهٔ آن حذف شوند؟",
            "حذف پروفایل",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        await RunActionAsync(async () =>
        {
            await _serviceManager.RemoveTunnelAsync(profile);
            _profileStore.Remove(profile);
            _profiles.Remove(profile);
            EmptyState.Visibility = _profiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            _viewModel.StatusMessage = $"پروفایل «{profile.Name}» حذف شد.";
        });
    }

    private async void ProfilesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _viewModel.UpdateConnectionState();
        UpdateActionButtons();
        if (ProfilesList.SelectedItem is WireGuardProfile profile)
        {
            try
            {
                var tunnelStatus = await _serviceManager.GetTunnelStatusAsync(profile);
                profile.Status = tunnelStatus.Status;
                profile.StatusDetails = tunnelStatus.Details;
                _viewModel.UpdateConnectionState();
                UpdateActionButtons();
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or Win32Exception)
            {
                ShowError(ex);
            }
        }
    }

    private void UpdateActionButtons()
    {
        var isBusy = _isBusy || _viewModel.IsBusy;
        var canToggle = !_isBusy
                        && !_viewModel.IsBusy
                        && (ProfilesList.SelectedItem is WireGuardProfile
                            || !string.IsNullOrWhiteSpace(_viewModel.SingBoxConfigPath));
        ConnectButton.IsEnabled = canToggle;
        ImportButton.IsEnabled = !isBusy;
        SelectSingBoxConfigButton.IsEnabled = !isBusy;
        DiagnosticsButton.IsEnabled = !isBusy;
        DeleteButton.IsEnabled = !isBusy && ProfilesList.SelectedItem is WireGuardProfile;
    }

    private async Task RefreshStatusesAsync()
    {
        if (_isRefreshingStatuses)
        {
            return;
        }

        _isRefreshingStatuses = true;
        try
        {
            using var concurrency = new SemaphoreSlim(4);
            var refreshTasks = _profiles.Select(async profile =>
            {
                await concurrency.WaitAsync();
                try
                {
                    var tunnelStatus = await _serviceManager.GetTunnelStatusAsync(profile);
                    profile.Status = tunnelStatus.Status;
                    profile.StatusDetails = tunnelStatus.Details;
                }
                finally
                {
                    concurrency.Release();
                }
            });
            await Task.WhenAll(refreshTasks);
        }
        finally
        {
            _isRefreshingStatuses = false;
            _viewModel.UpdateConnectionState();
            UpdateActionButtons();
        }
    }

    private async void StatusTimer_Tick(object? sender, EventArgs e)
    {
        if (_isBusy || _isRefreshingStatuses)
        {
            return;
        }

        try
        {
            await RefreshStatusesAsync();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or Win32Exception)
        {
            ShowError(ex);
        }
    }

    private async Task RunActionAsync(Func<Task> action)
    {
        SetBusy(true);
        try
        {
            await action();
            _viewModel.HasError = false;
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or CryptographicException
                                   or InvalidOperationException
                                   or Win32Exception
                                   or FormatException
                                   or InvalidDataException
                                   or JsonException
                                   or NetworkInformationException)
        {
            ShowError(ex);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool isBusy)
    {
        _isBusy = isBusy;
        ImportButton.IsEnabled = !isBusy;
        DiagnosticsButton.IsEnabled = !isBusy;
        SelectSingBoxConfigButton.IsEnabled = !isBusy;
        UpdateActionButtons();
    }

    private void ShowError(Exception exception)
    {
        _viewModel.StatusMessage = exception is Win32Exception { NativeErrorCode: 1223 }
            ? "درخواست دسترسی مدیریتی لغو شد؛ هیچ تغییری اعمال نشد."
            : exception.Message;
        _viewModel.HasError = true;
        _viewModel.AddLog($"خطا: {_viewModel.StatusMessage}");
    }

    private async Task<bool> StartSelectedProfileAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (ProfilesList.SelectedItem is not WireGuardProfile profile)
        {
            _viewModel.StatusMessage = "ابتدا یک پروفایل WireGuard انتخاب یا پیکربندی sing-box را بارگذاری کنید.";
            return false;
        }

        var configuration = _profileStore.ReadConfiguration(profile);
        try
        {
            await _serviceManager.ConnectAsync(profile, configuration);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(configuration);
        }

        var status = await _serviceManager.GetTunnelStatusAsync(profile);
        profile.Status = status.Status;
        profile.StatusDetails = status.Details;
        _viewModel.AddLog($"WireGuard tunnel started: {profile.Name}");
        return true;
    }

    private async Task<bool> StopSelectedProfileAsync()
    {
        if (ProfilesList.SelectedItem is not WireGuardProfile profile)
        {
            _viewModel.StatusMessage = "ابتدا یک پروفایل WireGuard انتخاب کنید.";
            return false;
        }

        await _serviceManager.DisconnectAsync(profile);
        var status = await _serviceManager.GetTunnelStatusAsync(profile);
        profile.Status = status.Status;
        profile.StatusDetails = status.Details;
        return true;
    }

    private bool ConfirmKillSwitchEnable()
    {
        var result = MessageBox.Show(
            this,
            "این محافظت تغییر سراسری در Windows Firewall اعمال می‌کند و خروجی شبکهٔ همهٔ برنامه‌ها را مسدود می‌کند؛ فقط IP سرور VPN در فهرست مجاز می‌ماند. برای اعمال تغییر دسترسی مدیر لازم است. اگر برنامه یا تونل به‌طور غیرمنتظره متوقف شود، سیاست مسدودسازی باقی می‌ماند تا آن را خاموش کنید و تنظیم قبلی بازیابی شود. ادامه می‌دهید؟",
            "فعال‌سازی Kill Switch سراسری",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        return result == MessageBoxResult.Yes;
    }

    private async Task<(IReadOnlyCollection<IPAddress> Addresses, string InterfaceAlias)> GetTrustedTunnelEndpointsAsync(
        CancellationToken cancellationToken)
    {
        if (!_viewModel.IsConnected)
        {
            throw new InvalidOperationException("برای فعال‌کردن kill switch ابتدا تونل را متصل کنید.");
        }

        var hosts = new List<string>();
        string? interfaceAlias = null;
        if (_tunnelManager.IsRunning && _viewModel.SingBoxConfigPath is { } singBoxConfig)
        {
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(singBoxConfig, cancellationToken));
            if (document.RootElement.TryGetProperty("inbounds", out var inbounds)
                && inbounds.ValueKind == JsonValueKind.Array)
            {
                foreach (var inbound in inbounds.EnumerateArray())
                {
                    if (inbound.ValueKind == JsonValueKind.Object
                        && inbound.TryGetProperty("type", out var type)
                        && type.GetString() == "tun"
                        && inbound.TryGetProperty("interface_name", out var interfaceName)
                        && interfaceName.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(interfaceName.GetString()))
                    {
                        interfaceAlias = interfaceName.GetString();
                        break;
                    }
                }
            }

            if (document.RootElement.TryGetProperty("outbounds", out var outbounds)
                && outbounds.ValueKind == JsonValueKind.Array)
            {
                foreach (var outbound in outbounds.EnumerateArray())
                {
                    if (outbound.ValueKind == JsonValueKind.Object
                        && outbound.TryGetProperty("server", out var server)
                        && server.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(server.GetString()))
                    {
                        hosts.Add(server.GetString()!);
                    }
                }
            }
        }
        else if (ProfilesList.SelectedItem is WireGuardProfile profile)
        {
            interfaceAlias = FindActiveTunnelInterfaceAlias();
            var configuration = _profileStore.ReadConfiguration(profile);
            try
            {
                hosts.AddRange(WireGuardConfiguration.GetProfileHints(
                    System.Text.Encoding.UTF8.GetString(configuration)).EndpointHosts);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(configuration);
            }
        }

        if (string.IsNullOrWhiteSpace(interfaceAlias))
        {
            interfaceAlias = FindActiveTunnelInterfaceAlias();
        }

        if (string.IsNullOrWhiteSpace(interfaceAlias))
        {
            throw new InvalidOperationException(
                "A trusted WireGuard or sing-box TUN interface could not be identified; the kill switch was not enabled.");
        }

        var addresses = new HashSet<IPAddress>();
        foreach (var host in hosts.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IPAddress.TryParse(host, out var address))
            {
                addresses.Add(address);
                continue;
            }

            try
            {
                foreach (var resolvedAddress in await Dns.GetHostAddressesAsync(host, cancellationToken))
                {
                    addresses.Add(resolvedAddress);
                }
            }
            catch (System.Net.Sockets.SocketException ex)
            {
                throw new InvalidOperationException(
                    $"VPN server '{host}' could not be resolved; the kill switch was not enabled.",
                    ex);
            }
        }

        return (addresses, interfaceAlias);
    }

    private static string? FindActiveTunnelInterfaceAlias()
    {
        try
        {
            using var socket = new System.Net.Sockets.Socket(
                System.Net.Sockets.AddressFamily.InterNetwork,
                System.Net.Sockets.SocketType.Dgram,
                System.Net.Sockets.ProtocolType.Udp);
            socket.Connect(new IPEndPoint(IPAddress.Parse("1.1.1.1"), 443));
            if (socket.LocalEndPoint is not IPEndPoint { Address: var localAddress })
            {
                return null;
            }

            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(network => network.OperationalStatus == OperationalStatus.Up)
                .FirstOrDefault(network =>
                    (network.Description.Contains("WireGuard", StringComparison.OrdinalIgnoreCase)
                     || network.Description.Contains("Wintun", StringComparison.OrdinalIgnoreCase)
                     || network.Description.Contains("sing-box", StringComparison.OrdinalIgnoreCase))
                    && network.GetIPProperties().UnicastAddresses
                        .Any(address => address.Address.Equals(localAddress)))
                ?.Name;
        }
        catch (System.Net.Sockets.SocketException)
        {
            return null;
        }
        catch (NetworkInformationException)
        {
            return null;
        }
    }

    private async void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_isClosingAfterManagedTunnelStop
            || (!_tunnelManager.IsRunning && !_viewModel.HasGeneratedRuntimeConfig))
        {
            return;
        }

        e.Cancel = true;
        SetBusy(true);
        try
        {
            await _viewModel.StopManagedTunnelAsync();
            _isClosingAfterManagedTunnelStop = true;
            Close();
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or InvalidOperationException
                                   or Win32Exception)
        {
            ShowError(ex);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void DisplayDiagnostics(NetworkDiagnosticsReport report)
    {
        var results = new List<string>();
        if (report.LocalGateway is { } gateway)
        {
            results.Add(FormatProbe(gateway));
        }
        else
        {
            results.Add("دروازهٔ محلی: پیدا نشد");
        }

        results.AddRange(report.PublicTargets.Select(FormatProbe));
        results.AddRange(report.TransportLatencies.Select(result => result.LatencyMs is { } latency
            ? $"{result.Name}: {latency} ms"
            : $"{result.Name}: {result.Error ?? "بدون پاسخ"}"));

        if (report.Endpoints.Count > 0)
        {
            results.AddRange(report.Endpoints.Select(endpoint => endpoint.Error is null
                ? $"سرور VPN {endpoint.Host}: {string.Join(", ", endpoint.Addresses)}"
                : $"سرور VPN {endpoint.Host}: {endpoint.Error}"));
        }

        if (report.ConfiguredMtu is not null)
        {
            results.Add($"MTU پروفایل: {report.ConfiguredMtu}");
        }

        if (report.PersistentKeepaliveSeconds is not null)
        {
            results.Add($"Keepalive: {report.PersistentKeepaliveSeconds} ثانیه");
        }

        var stability = report.StabilityScorePercentage is { } score
            ? $" · پایداری TCP/HTTP {score}%"
            : " · امتیاز پایداری نامشخص";
        DiagnosticsSummaryText.Text = $"نتیجهٔ بررسی شبکه · {report.CheckedAt:HH:mm}{stability}";
        ActiveRouteText.Text = report.ActiveRoute is { } route
            ? $"مسیر واقعی سیستم: {route.InterfaceName} · IP محلی {route.LocalAddress}"
              + (route.GatewayAddress is null ? " · بدون دروازهٔ محلی" : $" · دروازه {route.GatewayAddress}")
            : "مسیر فعال اینترنت شناسایی نشد.";
        DiagnosticsDetailsText.Text = string.Join("   |   ", results);
        RecommendationsList.ItemsSource = report.Recommendations;
        DiagnosticsPanel.Visibility = Visibility.Visible;
    }

    private static string FormatProbe(NetworkProbeResult probe)
    {
        var latency = probe.AverageLatencyMs is { } average
            ? $"{average:F0} ms میانگین، {probe.JitterMs ?? 0:F0} ms نوسان"
            : "بدون پاسخ";
        var packetLoss = probe.LossPercentage is { } loss ? $"{loss}% اتلاف" : "اتلاف نامشخص";
        var probeErrors = probe.ProbeErrors > 0 ? $"، {probe.ProbeErrors} خطای محلی" : string.Empty;
        return $"{probe.Name} ({probe.Address}): {latency}، {packetLoss}{probeErrors}";
    }
}
