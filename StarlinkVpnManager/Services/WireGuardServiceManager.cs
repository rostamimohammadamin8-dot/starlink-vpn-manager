using StarlinkVpnManager.Models;
using System.ComponentModel;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.NetworkInformation;
using System.Text;

namespace StarlinkVpnManager.Services;

internal sealed class WireGuardServiceManager
{
    private static readonly TimeSpan StatusTimeout = TimeSpan.FromSeconds(20);
    private readonly ConcurrentDictionary<string, TransferSample> _previousTransfers = new(StringComparer.Ordinal);
    private readonly string _serviceControlPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System),
        "sc.exe");
    private readonly string? _wireGuardCliPath;

    public WireGuardServiceManager()
    {
        ExecutablePath = FindWireGuardExecutable();
        _wireGuardCliPath = ExecutablePath is null
            ? null
            : Path.Combine(Path.GetDirectoryName(ExecutablePath)!, "wg.exe");
    }

    public string? ExecutablePath { get; }

    public async Task<TunnelStatusSnapshot> GetTunnelStatusAsync(WireGuardProfile profile)
    {
        var state = await QueryStateAsync(profile.ServiceName);
        var displayStatus = state switch
        {
            TunnelState.Running => "فعال",
            TunnelState.Stopped => "متوقف",
            TunnelState.Transitioning => "در حال تغییر",
            _ => "نصب‌نشده"
        };

        if (state != TunnelState.Running)
        {
            return new TunnelStatusSnapshot(displayStatus, string.Empty);
        }

        return new TunnelStatusSnapshot(displayStatus, await GetPeerDetailsAsync(profile.ServiceName));
    }

    public async Task ConnectAsync(WireGuardProfile profile, byte[] configuration)
    {
        var state = await WaitForStableStateAsync(profile.ServiceName);
        if (state == TunnelState.Running)
        {
            return;
        }

        var executable = RequireWireGuard();
        var temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            "StarlinkVpnManager",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        var temporaryPath = Path.Combine(temporaryDirectory, $"{profile.ServiceName}.conf");
        try
        {
            await File.WriteAllBytesAsync(temporaryPath, configuration);
            await RunElevatedAsync(executable, $"/installtunnelservice \"{temporaryPath}\"");
            await WaitForStateAsync(profile.ServiceName, TunnelState.Running);
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    public async Task DisconnectAsync(WireGuardProfile profile)
    {
        var state = await WaitForStableStateAsync(profile.ServiceName);
        if (state is TunnelState.NotInstalled or TunnelState.Stopped)
        {
            return;
        }

        if (state == TunnelState.Running)
        {
            await RunElevatedAsync(_serviceControlPath, $"stop \"WireGuardTunnel${profile.ServiceName}\"");
        }

        await WaitForStateAsync(profile.ServiceName, TunnelState.Stopped);
    }

    public async Task RemoveTunnelAsync(WireGuardProfile profile)
    {
        if (await WaitForStableStateAsync(profile.ServiceName) == TunnelState.NotInstalled)
        {
            return;
        }

        await RunElevatedAsync(
            RequireWireGuard(),
            $"/uninstalltunnelservice \"{profile.ServiceName}\"");
        await WaitForStateAsync(profile.ServiceName, TunnelState.NotInstalled);
    }

    private async Task<TunnelState> WaitForStableStateAsync(string serviceName)
    {
        var deadline = DateTime.UtcNow + StatusTimeout;
        while (DateTime.UtcNow < deadline)
        {
            var state = await QueryStateAsync(serviceName);
            if (state != TunnelState.Transitioning)
            {
                return state;
            }

            await Task.Delay(500);
        }

        throw new InvalidOperationException("وضعیت سرویس WireGuard پایدار نشد.");
    }

    private async Task<TunnelState> QueryStateAsync(string serviceName)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = _serviceControlPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        process.StartInfo.ArgumentList.Add("query");
        process.StartInfo.ArgumentList.Add($"WireGuardTunnel${serviceName}");

        if (!process.Start())
        {
            throw new InvalidOperationException("وضعیت سرویس WireGuard قابل بررسی نیست.");
        }

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = await outputTask;
        var error = await errorTask;

        if (process.ExitCode == 1060)
        {
            return TunnelState.NotInstalled;
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"بررسی سرویس WireGuard ناموفق بود: {error.Trim()}");
        }

        if (output.Contains("RUNNING", StringComparison.OrdinalIgnoreCase))
        {
            return TunnelState.Running;
        }

        if (output.Contains("STOPPED", StringComparison.OrdinalIgnoreCase))
        {
            return TunnelState.Stopped;
        }

        return TunnelState.Transitioning;
    }

    private async Task<string> GetPeerDetailsAsync(string interfaceName)
    {
        if (_wireGuardCliPath is null || !File.Exists(_wireGuardCliPath))
        {
            return "برای نمایش handshake، ابزار رسمی wg.exe پیدا نشد.";
        }

        try
        {
            var handshakes = await RunWireGuardCommandAsync(interfaceName, "latest-handshakes");
            var transfers = await RunWireGuardCommandAsync(interfaceName, "transfer");
            var timestamps = ParseLatestHandshakeTimestamps(handshakes);
            var totals = ParseTransferTotals(transfers);
            var trafficSummary = FormatTransferSummary(interfaceName, totals);
            if (timestamps.Count == 0)
            {
                return "تونل فعال است؛ peer قابل خواندن نیست.";
            }

            var newestHandshake = timestamps.Max();
            if (newestHandshake == 0)
            {
                return $"فعال · {timestamps.Count} peer · هنوز handshake موفقی ثبت نشده · {trafficSummary}";
            }

            var age = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(newestHandshake);
            var handshakeSummary = age > TimeSpan.FromMinutes(3)
                ? $"آخرین handshake {FormatElapsed(age)} (ممکن است peer بی‌کار باشد)"
                : $"آخرین handshake {FormatElapsed(age)}";
            return $"فعال · {timestamps.Count} peer · {handshakeSummary} · {trafficSummary}";
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or InvalidOperationException
                                   or Win32Exception
                                   or FormatException
                                   or NetworkInformationException)
        {
            return $"تونل فعال است؛ جزئیات peer قابل دریافت نیست ({ex.Message}).";
        }
        catch (ArgumentOutOfRangeException)
        {
            return "تونل فعال است؛ زمان handshake دریافتی معتبر نیست.";
        }
    }

    private async Task<string> RunWireGuardCommandAsync(string interfaceName, string subcommand)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = _wireGuardCliPath!,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            }
        };
        process.StartInfo.ArgumentList.Add("show");
        process.StartInfo.ArgumentList.Add(interfaceName);
        process.StartInfo.ArgumentList.Add(subcommand);

        if (!process.Start())
        {
            throw new InvalidOperationException("اجرای بررسی وضعیت WireGuard ممکن نشد.");
        }

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        var exitTask = process.WaitForExitAsync();
        if (await Task.WhenAny(exitTask, Task.Delay(TimeSpan.FromSeconds(3))) != exitTask)
        {
            process.Kill(entireProcessTree: true);
            await exitTask;
            await outputTask;
            await errorTask;
            throw new InvalidOperationException("بررسی وضعیت peer بیش از حد طول کشید.");
        }

        await exitTask;
        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"خواندن وضعیت peer ناموفق بود: {error.Trim()}");
        }

        return output;
    }

    private static IReadOnlyList<long> ParseLatestHandshakeTimestamps(string output)
    {
        var timestamps = new List<long>();
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.Split('\t');
            if (fields.Length < 2
                || !long.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var timestamp)
                || timestamp < 0)
            {
                throw new FormatException("قالب زمان handshake از wg.exe معتبر نیست.");
            }

            timestamps.Add(timestamp);
        }

        return timestamps;
    }

    private static TransferTotals ParseTransferTotals(string output)
    {
        ulong received = 0;
        ulong sent = 0;
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.Split('\t');
            if (fields.Length < 3
                || !ulong.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var peerReceived)
                || !ulong.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var peerSent))
            {
                throw new FormatException("قالب شمارندهٔ ترافیک از wg.exe معتبر نیست.");
            }

            received = SaturatingAdd(received, peerReceived);
            sent = SaturatingAdd(sent, peerSent);
        }

        return new TransferTotals(received, sent);
    }

    private string FormatTransferSummary(string interfaceName, TransferTotals totals)
    {
        var now = DateTimeOffset.UtcNow;
        var currentSample = new TransferSample(totals, now);
        while (true)
        {
            if (!_previousTransfers.TryGetValue(interfaceName, out var previousSample))
            {
                if (_previousTransfers.TryAdd(interfaceName, currentSample))
                {
                    return $"دریافت {FormatBytes(totals.Received)} · ارسال {FormatBytes(totals.Sent)}";
                }

                continue;
            }

            if (_previousTransfers.TryUpdate(interfaceName, currentSample, previousSample))
            {
                return FormatTransferChange(totals, previousSample, now);
            }
        }
    }

    private static string FormatTransferChange(
        TransferTotals totals,
        TransferSample previousSample,
        DateTimeOffset now)
    {
        if (totals.Received < previousSample.Totals.Received
            || totals.Sent < previousSample.Totals.Sent)
        {
            return $"شمارندهٔ ترافیک بازنشانی شد · دریافت {FormatBytes(totals.Received)} · ارسال {FormatBytes(totals.Sent)}";
        }

        var elapsedSeconds = (now - previousSample.SampledAt).TotalSeconds;
        if (elapsedSeconds <= 0)
        {
            return $"دریافت {FormatBytes(totals.Received)} · ارسال {FormatBytes(totals.Sent)}";
        }

        var receivedDelta = totals.Received - previousSample.Totals.Received;
        var sentDelta = totals.Sent - previousSample.Totals.Sent;
        if (receivedDelta == 0 && sentDelta == 0)
        {
            return $"دریافت {FormatBytes(totals.Received)} · ارسال {FormatBytes(totals.Sent)}"
                   + " · ترافیک جدیدی در این نمونه ثبت نشد";
        }

        var receiveRate = (ulong)(receivedDelta / elapsedSeconds);
        var sendRate = (ulong)(sentDelta / elapsedSeconds);
        return $"دریافت {FormatBytes(totals.Received)} (+{FormatBytes(receiveRate)}/s)"
               + $" · ارسال {FormatBytes(totals.Sent)} (+{FormatBytes(sendRate)}/s)";
    }

    private static ulong SaturatingAdd(ulong left, ulong right) =>
        ulong.MaxValue - left < right ? ulong.MaxValue : left + right;

    private sealed record TransferTotals(ulong Received, ulong Sent);
    private sealed record TransferSample(TransferTotals Totals, DateTimeOffset SampledAt);

    private static string FormatBytes(ulong bytes)
    {
        var amount = (double)bytes;
        var units = new[] { "B", "KB", "MB", "GB", "TB" };
        var unit = 0;
        while (amount >= 1024 && unit < units.Length - 1)
        {
            amount /= 1024;
            unit++;
        }

        return $"{amount:F1} {units[unit]}";
    }

    private static string FormatElapsed(TimeSpan age)
    {
        if (age < TimeSpan.Zero)
        {
            return "همین حالا";
        }

        if (age.TotalMinutes < 1)
        {
            return "کمتر از یک دقیقه پیش";
        }

        if (age.TotalHours < 1)
        {
            return $"{(int)age.TotalMinutes} دقیقه پیش";
        }

        if (age.TotalDays < 1)
        {
            return $"{(int)age.TotalHours} ساعت پیش";
        }

        return $"{(int)age.TotalDays} روز پیش";
    }

    private async Task WaitForStateAsync(string serviceName, TunnelState expectedState)
    {
        var deadline = DateTime.UtcNow + StatusTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await QueryStateAsync(serviceName) == expectedState)
            {
                return;
            }

            await Task.Delay(500);
        }

        throw new InvalidOperationException("تغییر وضعیت تونل بیش از حد طول کشید.");
    }

    private static async Task RunElevatedAsync(string executable, string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = executable,
            Arguments = arguments,
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden
        }) ?? throw new InvalidOperationException("اجرای عملیات مدیریتی WireGuard ممکن نشد.");

        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"عملیات WireGuard با کد {process.ExitCode} ناموفق بود.");
        }
    }

    private string RequireWireGuard() =>
        ExecutablePath ?? throw new InvalidOperationException(
            "WireGuard for Windows نصب نیست. آن را نصب کنید و دوباره تلاش کنید.");

    private static string? FindWireGuardExecutable()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WireGuard", "wireguard.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "WireGuard", "wireguard.exe")
        };

        var installedPath = candidates.FirstOrDefault(File.Exists);
        if (installedPath is not null)
        {
            return installedPath;
        }

        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var path = Path.Combine(directory.Trim('"'), "wireguard.exe");
            if (File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

    private enum TunnelState
    {
        NotInstalled,
        Stopped,
        Running,
        Transitioning
    }
}

internal sealed record TunnelStatusSnapshot(string Status, string Details);
