using Microsoft.Win32;
using StarlinkVpnManager.Models;
using StarlinkVpnManager.Services;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace StarlinkVpnManager;

public partial class MainWindow : Window
{
    private readonly ProfileStore _profileStore = new();
    private readonly WireGuardServiceManager _serviceManager = new();
    private readonly NetworkDiagnosticsService _diagnosticsService = new();
    private readonly ObservableCollection<WireGuardProfile> _profiles = [];
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(8) };
    private bool _isBusy;
    private bool _isRefreshingStatuses;

    public MainWindow()
    {
        InitializeComponent();
        ProfilesList.ItemsSource = _profiles;
        _statusTimer.Tick += StatusTimer_Tick;
        Closed += (_, _) => _statusTimer.Stop();
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
            StatusText.Text = _serviceManager.ExecutablePath is null
                ? "برای اتصال، ابتدا WireGuard for Windows را نصب کنید. پروفایل‌ها را می‌توانید از همین حالا وارد کنید."
                : "آماده؛ یک پروفایل انتخاب کنید یا فایل WireGuard را وارد کنید.";
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or CryptographicException
                                   or InvalidOperationException
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
            StatusText.Text = $"پروفایل «{profile.Name}» به‌صورت رمزگذاری‌شده ذخیره شد.";
            return Task.CompletedTask;
        });
    }

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (ProfilesList.SelectedItem is not WireGuardProfile profile)
        {
            StatusText.Text = "ابتدا یک پروفایل را انتخاب کنید.";
            return;
        }

        await RunActionAsync(async () =>
        {
            var config = _profileStore.ReadConfiguration(profile);
            try
            {
                await _serviceManager.ConnectAsync(profile, config);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(config);
            }

            var tunnelStatus = await _serviceManager.GetTunnelStatusAsync(profile);
            profile.Status = tunnelStatus.Status;
            profile.StatusDetails = tunnelStatus.Details;
            StatusText.Text = $"سرویس تونل «{profile.Name}» فعال شد.";
        });
    }

    private async void DisconnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (ProfilesList.SelectedItem is not WireGuardProfile profile)
        {
            StatusText.Text = "ابتدا یک پروفایل را انتخاب کنید.";
            return;
        }

        await RunActionAsync(async () =>
        {
            await _serviceManager.DisconnectAsync(profile);
            var tunnelStatus = await _serviceManager.GetTunnelStatusAsync(profile);
            profile.Status = tunnelStatus.Status;
            profile.StatusDetails = tunnelStatus.Details;
            StatusText.Text = $"سرویس تونل «{profile.Name}» متوقف شد.";
        });
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
            StatusText.Text = "آزمایش شبکه کامل شد؛ هیچ تنظیمی تغییر نکرد.";
        });
    }

    private async void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (ProfilesList.SelectedItem is not WireGuardProfile profile)
        {
            StatusText.Text = "ابتدا یک پروفایل را انتخاب کنید.";
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
            StatusText.Text = $"پروفایل «{profile.Name}» حذف شد.";
        });
    }

    private async void ProfilesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateActionButtons();
        if (ProfilesList.SelectedItem is WireGuardProfile profile)
        {
            try
            {
                var tunnelStatus = await _serviceManager.GetTunnelStatusAsync(profile);
                profile.Status = tunnelStatus.Status;
                profile.StatusDetails = tunnelStatus.Details;
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
        var hasSelection = !_isBusy && ProfilesList.SelectedItem is WireGuardProfile;
        var isActive = ProfilesList.SelectedItem is WireGuardProfile { Status: "فعال" };
        ConnectButton.IsEnabled = hasSelection && !isActive;
        DisconnectButton.IsEnabled = hasSelection && isActive;
        DeleteButton.IsEnabled = hasSelection;
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
            UpdateActionButtons();
        }
    }

    private async void StatusTimer_Tick(object? sender, EventArgs e)
    {
        if (_isBusy || _isRefreshingStatuses || _profiles.Count == 0)
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
            StatusText.Foreground = (Brush)FindResource("AccentBrush");
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
        UpdateActionButtons();
    }

    private void ShowError(Exception exception)
    {
        StatusText.Text = exception is Win32Exception { NativeErrorCode: 1223 }
            ? "درخواست دسترسی مدیریتی لغو شد؛ هیچ تغییری اعمال نشد."
            : exception.Message;
        StatusText.Foreground = Brushes.Salmon;
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

        DiagnosticsSummaryText.Text = $"نتیجهٔ بررسی شبکه · {report.CheckedAt:HH:mm}";
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
