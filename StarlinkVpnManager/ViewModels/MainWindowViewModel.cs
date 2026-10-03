using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StarlinkVpnManager.Services;
using System.Collections.ObjectModel;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace StarlinkVpnManager.ViewModels;

internal sealed partial class MainWindowViewModel : ObservableObject
{
    private static readonly LatencyEndpoint[] LatencyTargets =
    [
        new("OpenAI", "api.openai.com"),
        new("Anthropic", "api.anthropic.com"),
        new("Google Gemini", "generativelanguage.googleapis.com")
    ];

    private readonly LatencyService _latencyService;
    private readonly AiRoutingService _aiRoutingService;
    private readonly TunnelManagerService _tunnelManager;
    private readonly KillSwitchService _killSwitchService;
    private readonly Func<CancellationToken, Task<bool>> _startWireGuardAsync;
    private readonly Func<Task<bool>> _stopWireGuardAsync;
    private readonly Func<bool> _isWireGuardConnected;
    private readonly Func<
        CancellationToken,
        Task<(IReadOnlyCollection<IPAddress> Addresses, string InterfaceAlias)>> _getTrustedEndpointsAsync;
    private readonly Func<bool> _confirmKillSwitchEnable;
    private string? _generatedConfigPath;

    [ObservableProperty]
    private bool _isConnected;

    [ObservableProperty]
    private long _currentLatencyMs = -1;

    [ObservableProperty]
    private string _statusMessage = "در حال آماده‌سازی برنامه...";

    [ObservableProperty]
    private bool _enableAiBypass;

    [ObservableProperty]
    private string _aiRoutingRulesJson = string.Empty;

    [ObservableProperty]
    private string _latencySummary = "تأخیر هنوز اندازه‌گیری نشده است.";

    [ObservableProperty]
    private string? _singBoxConfigPath;

    [ObservableProperty]
    private bool _hasError;

    [ObservableProperty]
    private bool _isKillSwitchEnabled;

    [ObservableProperty]
    private bool _isBusy;

    public MainWindowViewModel(
        LatencyService latencyService,
        AiRoutingService aiRoutingService,
        TunnelManagerService tunnelManager,
        KillSwitchService killSwitchService,
        Func<CancellationToken, Task<bool>> startWireGuardAsync,
        Func<Task<bool>> stopWireGuardAsync,
        Func<bool> isWireGuardConnected,
        Func<
            CancellationToken,
            Task<(IReadOnlyCollection<IPAddress> Addresses, string InterfaceAlias)>> getTrustedEndpointsAsync,
        Func<bool> confirmKillSwitchEnable)
    {
        _latencyService = latencyService;
        _aiRoutingService = aiRoutingService;
        _tunnelManager = tunnelManager;
        _killSwitchService = killSwitchService;
        _startWireGuardAsync = startWireGuardAsync;
        _stopWireGuardAsync = stopWireGuardAsync;
        _isWireGuardConnected = isWireGuardConnected;
        _getTrustedEndpointsAsync = getTrustedEndpointsAsync;
        _confirmKillSwitchEnable = confirmKillSwitchEnable;
        ConnectionLogs = [];
        _isKillSwitchEnabled = _killSwitchService.IsEnabled;
        RefreshAiRules();
        AddLog("برنامه آماده شد.");
        if (_isKillSwitchEnabled)
        {
            AddLog("System-wide firewall kill switch is still active from a previous run.");
        }
    }

    public ObservableCollection<string> ConnectionLogs { get; }

    public string ConnectionButtonText => IsConnected ? "قطع اتصال" : "اتصال";

    public string ConnectionStateText => IsConnected ? "CONNECTED" : "DISCONNECTED";

    public bool CanToggleKillSwitch => !IsBusy && (IsConnected || _killSwitchService.IsEnabled);

    public string CurrentLatencyText =>
        CurrentLatencyMs < 0 ? "— ms" : $"{CurrentLatencyMs} ms";

    public bool HasGeneratedRuntimeConfig => _generatedConfigPath is not null;

    public void SetSingBoxConfiguration(string configPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configPath);
        var path = Path.GetFullPath(configPath);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("فایل پیکربندی sing-box پیدا نشد.", path);
        }

        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
        {
            throw new InvalidDataException("پیکربندی sing-box باید یک شیء JSON باشد.");
        }

        SingBoxConfigPath = path;
        StatusMessage = "پیکربندی sing-box انتخاب شد؛ اتصال بعدی از همین فایل استفاده می‌کند.";
        HasError = false;
        AddLog($"فایل پیکربندی sing-box انتخاب شد: {path}");
    }

    public void UpdateConnectionState()
    {
        var wasConnected = IsConnected;
        IsConnected = _tunnelManager.IsRunning || _isWireGuardConnected();
        OnPropertyChanged(nameof(ConnectionButtonText));
        if (_generatedConfigPath is not null && !_tunnelManager.IsRunning)
        {
            try
            {
                if (File.Exists(_generatedConfigPath))
                {
                    File.Delete(_generatedConfigPath);
                }

                _generatedConfigPath = null;
                OnPropertyChanged(nameof(HasGeneratedRuntimeConfig));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                HasError = true;
                StatusMessage = $"Could not remove the stopped tunnel's temporary configuration: {ex.Message}";
                AddLog(StatusMessage);
            }
        }

        if (wasConnected && !IsConnected)
        {
            StatusMessage = _killSwitchService.IsEnabled
                ? "VPN tunnel stopped; system-wide kill switch protection remains active."
                : "VPN tunnel disconnected.";
            AddLog(StatusMessage);
        }
    }

    public void AddLog(string message)
    {
        ConnectionLogs.Add($"{DateTime.Now:HH:mm:ss}  {message}");
        while (ConnectionLogs.Count > 300)
        {
            ConnectionLogs.RemoveAt(0);
        }
    }

    public async Task StopManagedTunnelAsync()
    {
        if (_tunnelManager.IsRunning)
        {
            await _tunnelManager.StopTunnelAsync();
        }

        await DeleteGeneratedConfigAsync();
        UpdateConnectionState();
        AddLog("تونل هنگام بستن برنامه متوقف شد.");
    }

    public async Task SetKillSwitchEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        HasError = false;
        if (enabled == _killSwitchService.IsEnabled)
        {
            IsKillSwitchEnabled = enabled;
            return;
        }

        if (enabled && !_confirmKillSwitchEnable())
        {
            IsKillSwitchEnabled = false;
            AddLog("فعال‌سازی kill switch توسط کاربر لغو شد.");
            return;
        }

        IsBusy = true;
        try
        {
            if (enabled)
            {
                if (!IsConnected)
                {
                    throw new InvalidOperationException(
                        "برای فعال‌کردن kill switch ابتدا یک تونل VPN را متصل کنید.");
                }

                var endpoints = await _getTrustedEndpointsAsync(cancellationToken);
                await _killSwitchService.EnableAsync(
                    endpoints.Addresses,
                    endpoints.InterfaceAlias,
                    cancellationToken);
                StatusMessage = "محافظت kill switch فعال است؛ خروجی همهٔ برنامه‌ها مسدود و فقط IP سرور VPN مجاز است.";
            }
            else
            {
                await _killSwitchService.DisableAsync(cancellationToken);
                StatusMessage = "تنظیم قبلی Windows Firewall بازیابی شد.";
            }

            AddLog(StatusMessage);
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or InvalidOperationException
                                   or ArgumentException
                                   or System.ComponentModel.Win32Exception
                                   or System.Text.Json.JsonException)
        {
            HasError = true;
            StatusMessage = ex is System.ComponentModel.Win32Exception { NativeErrorCode: 1223 }
                ? "درخواست دسترسی مدیریتی برای تغییر Windows Firewall لغو شد."
                : ex.Message;
            AddLog($"خطای kill switch: {StatusMessage}");
        }
        finally
        {
            IsKillSwitchEnabled = _killSwitchService.IsEnabled;
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ToggleConnectionAsync(CancellationToken cancellationToken)
    {
        IsBusy = true;
        HasError = false;
        try
        {
            if (IsConnected)
            {
                if (_tunnelManager.IsRunning)
                {
                    await _tunnelManager.StopTunnelAsync();
                    await DeleteGeneratedConfigAsync();
                    AddLog("sing-box tunnel stopped.");
                }
                else
                {
                    await _stopWireGuardAsync();
                    AddLog("WireGuard tunnel stopped.");
                }

                StatusMessage = "تونل متوقف شد.";
            }
            else if (!string.IsNullOrWhiteSpace(SingBoxConfigPath))
            {
                await StartSingBoxAsync(cancellationToken);
                StatusMessage = "تونل sing-box فعال شد.";
            }
            else
            {
                var started = await _startWireGuardAsync(cancellationToken);
                if (!started)
                {
                    return;
                }

                StatusMessage = "تونل WireGuard فعال شد.";
                AddLog(StatusMessage);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            StatusMessage = "عملیات لغو شد.";
            AddLog(StatusMessage);
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or InvalidOperationException
                                   or ArgumentException
                                   or System.ComponentModel.Win32Exception
                                   or System.Security.Cryptography.CryptographicException
                                   or System.Text.Json.JsonException
                                   or FormatException)
        {
            HasError = true;
            StatusMessage = ex.Message;
            AddLog($"خطا: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
            UpdateConnectionState();
        }
    }

    [RelayCommand]
    private async Task RefreshLatencyAsync(CancellationToken cancellationToken)
    {
        HasError = false;
        try
        {
            var measurements = await _latencyService.MeasureEndpointsAsync(
                LatencyTargets,
                timeoutMs: 3000,
                cancellationToken);
            var successful = measurements.Where(measurement => measurement.LatencyMs.HasValue).ToArray();
            CurrentLatencyMs = successful.Length == 0
                ? -1
                : (long)Math.Round(successful.Average(measurement => measurement.LatencyMs!.Value));
            LatencySummary = string.Join(
                " · ",
                measurements.Select(measurement => measurement.LatencyMs is { } latency
                    ? $"{measurement.Name}: {latency} ms"
                    : $"{measurement.Name}: ناموفق"));
            AddLog($"آزمایش تأخیر کامل شد؛ {successful.Length}/{measurements.Count} مقصد پاسخ داد.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            LatencySummary = "آزمایش تأخیر لغو شد.";
            AddLog(LatencySummary);
        }
        catch (Exception ex) when (ex is SocketException or HttpRequestException or IOException)
        {
            HasError = true;
            LatencySummary = $"آزمایش تأخیر ناموفق بود: {ex.Message}";
            AddLog(LatencySummary);
        }
    }

    partial void OnEnableAiBypassChanged(bool value)
    {
        RefreshAiRules();
        var message = value
            ? "قواعد دورزدن مسیر AI برای اتصال بعدی sing-box فعال شدند."
            : "قواعد دورزدن مسیر AI برای اتصال بعدی sing-box غیرفعال شدند.";
        StatusMessage = message;
        AddLog(message);
    }

    partial void OnIsConnectedChanged(bool value)
    {
        OnPropertyChanged(nameof(ConnectionButtonText));
        OnPropertyChanged(nameof(ConnectionStateText));
        OnPropertyChanged(nameof(CanToggleKillSwitch));
    }

    partial void OnIsKillSwitchEnabledChanged(bool value) =>
        OnPropertyChanged(nameof(CanToggleKillSwitch));

    partial void OnIsBusyChanged(bool value) =>
        OnPropertyChanged(nameof(CanToggleKillSwitch));

    partial void OnCurrentLatencyMsChanged(long value) =>
        OnPropertyChanged(nameof(CurrentLatencyText));

    private async Task StartSingBoxAsync(CancellationToken cancellationToken)
    {
        var configurationPath = SingBoxConfigPath
                                ?? throw new InvalidOperationException("ابتدا یک فایل پیکربندی sing-box انتخاب کنید.");
        var configuration = await File.ReadAllTextAsync(configurationPath, cancellationToken);
        var mergedConfiguration = EnableAiBypass
            ? _aiRoutingService.MergeRoutingRules(configuration, enableAiBypass: true)
            : configuration;
        var runtimeDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "StarlinkVpnManager",
            "runtime-configs");
        Directory.CreateDirectory(runtimeDirectory);
        var generatedPath = Path.Combine(
            runtimeDirectory,
            $".{Path.GetFileNameWithoutExtension(configurationPath)}.{Guid.NewGuid():N}.runtime.json");

        try
        {
            await File.WriteAllTextAsync(generatedPath, mergedConfiguration, cancellationToken);
            await _tunnelManager.StartTunnelAsync(
                generatedPath,
                Path.GetDirectoryName(configurationPath)!,
                cancellationToken);
            _generatedConfigPath = generatedPath;
            OnPropertyChanged(nameof(HasGeneratedRuntimeConfig));
            AddLog($"sing-box tunnel started with AI bypass {(EnableAiBypass ? "enabled" : "disabled")}.");
        }
        catch
        {
            if (File.Exists(generatedPath))
            {
                File.Delete(generatedPath);
            }

            throw;
        }
    }

    private async Task DeleteGeneratedConfigAsync()
    {
        if (_generatedConfigPath is not { } path)
        {
            return;
        }

        if (File.Exists(path))
        {
            await Task.Run(() => File.Delete(path));
        }

        _generatedConfigPath = null;
        OnPropertyChanged(nameof(HasGeneratedRuntimeConfig));
    }

    private void RefreshAiRules()
    {
        AiRoutingRulesJson = _aiRoutingService
            .GenerateRoutingRules(EnableAiBypass)
            .ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    }
}
