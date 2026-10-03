using StarlinkVpnManager.Models;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace StarlinkVpnManager.Services;

internal sealed class TunnelManagerService : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly WireGuardServiceManager _wireGuardManager = new();
    private readonly string? _singBoxExecutablePath;
    private Process? _process;
    private WireGuardProfile? _wireGuardProfile;
    private string? _activeConfigPath;
    private bool _disposed;

    public TunnelManagerService(string? singBoxExecutablePath = null)
    {
        _singBoxExecutablePath = singBoxExecutablePath ?? FindExecutable("sing-box.exe");
    }

    public event EventHandler<string>? LogReceived;

    public bool IsRunning
    {
        get
        {
            if (_wireGuardProfile is not null)
            {
                return _wireGuardProfile.Status == "فعال";
            }

            try
            {
                return _process is { HasExited: false };
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
    }

    public async Task<bool> StartTunnelAsync(string configPath, CancellationToken ct)
    {
        var fullConfigPath = Path.GetFullPath(configPath);
        return await StartTunnelAsync(
            fullConfigPath,
            Path.GetDirectoryName(fullConfigPath)!,
            ct);
    }

    public async Task<bool> StartTunnelAsync(
        string configPath,
        string workingDirectory,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        var fullConfigPath = Path.GetFullPath(configPath);
        var fullWorkingDirectory = Path.GetFullPath(workingDirectory);
        if (!File.Exists(fullConfigPath))
        {
            throw new FileNotFoundException("فایل پیکربندی تونل پیدا نشد.", fullConfigPath);
        }

        if (!Directory.Exists(fullWorkingDirectory))
        {
            throw new DirectoryNotFoundException($"Tunnel working directory was not found: {fullWorkingDirectory}");
        }

        await _gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (IsRunning)
            {
                if (string.Equals(_activeConfigPath, fullConfigPath, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                throw new InvalidOperationException("A different tunnel is already running.");
            }

            await DisposeExitedProcessAsync();
            ct.ThrowIfCancellationRequested();

            if (Path.GetExtension(fullConfigPath).Equals(".conf", StringComparison.OrdinalIgnoreCase))
            {
                var started = await StartWireGuardAsync(fullConfigPath, ct);
                _activeConfigPath = fullConfigPath;
                return started;
            }

            var singBoxStarted = await StartSingBoxAsync(fullConfigPath, fullWorkingDirectory, ct);
            _activeConfigPath = fullConfigPath;
            return singBoxStarted;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> StopTunnelAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_wireGuardProfile is { } profile)
            {
                await _wireGuardManager.DisconnectAsync(profile);
                profile.Status = "متوقف";
                _wireGuardProfile = null;
                _activeConfigPath = null;
                Log("WireGuard tunnel stopped.");
                return true;
            }

            if (_process is not { } process)
            {
                return true;
            }

            if (!process.HasExited)
            {
                if (!process.CloseMainWindow())
                {
                    await TerminateProcessAsync(process);
                }
                else
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    try
                    {
                        await process.WaitForExitAsync(timeout.Token);
                    }
                    catch (OperationCanceledException) when (timeout.IsCancellationRequested)
                    {
                        await TerminateProcessAsync(process);
                    }
                }
            }

            Log("Tunnel process stopped.");
            await DisposeExitedProcessAsync();
            _activeConfigPath = null;
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await StopTunnelAsync();
        _disposed = true;
        _gate.Dispose();
    }

    private async Task<bool> StartWireGuardAsync(string configPath, CancellationToken ct)
    {
        var configuration = await File.ReadAllBytesAsync(configPath, ct);
        try
        {
            WireGuardConfiguration.Validate(Encoding.UTF8.GetString(configuration));
            var profile = new WireGuardProfile
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = Path.GetFileNameWithoutExtension(configPath),
                ServiceName = $"slvpn-{Guid.NewGuid():N}"[..14]
            };

            await _wireGuardManager.ConnectAsync(profile, configuration);
            profile.Status = "فعال";
            _wireGuardProfile = profile;
            Log($"WireGuard tunnel started from {configPath}.");
            return true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(configuration);
        }
    }

    private async Task<bool> StartSingBoxAsync(
        string configPath,
        string workingDirectory,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_singBoxExecutablePath is null)
        {
            throw new FileNotFoundException(
                "sing-box.exe پیدا نشد. آن را کنار برنامه قرار دهید یا مسیر آن را هنگام ساخت TunnelManagerService مشخص کنید.");
        }

        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = _singBoxExecutablePath,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            },
            EnableRaisingEvents = true
        };
        process.StartInfo.ArgumentList.Add("run");
        process.StartInfo.ArgumentList.Add("-c");
        process.StartInfo.ArgumentList.Add(configPath);
        process.OutputDataReceived += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.Data))
            {
                Log(args.Data);
            }
        };
        process.ErrorDataReceived += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.Data))
            {
                Log($"[stderr] {args.Data}");
            }
        };
        process.Exited += (_, _) => Log($"Tunnel process exited with code {process.ExitCode}.");

        var started = false;
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("sing-box process could not be started.");
            }

            started = true;
            _process = process;
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            Log($"sing-box started with configuration {configPath}.");
            await Task.Delay(TimeSpan.FromMilliseconds(300), ct);
            if (process.HasExited)
            {
                await process.WaitForExitAsync();
                throw new InvalidOperationException(
                    $"sing-box exited during startup with code {process.ExitCode}; check the connection log for details.");
            }

            return true;
        }
        catch
        {
            if (started && !process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }

            if (ReferenceEquals(_process, process))
            {
                _process = null;
            }

            process.Dispose();
            throw;
        }
    }

    private static async Task TerminateProcessAsync(Process process)
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }
    }

    private async Task DisposeExitedProcessAsync()
    {
        if (_process is not { } process)
        {
            return;
        }

        if (!process.HasExited)
        {
            await TerminateProcessAsync(process);
        }

        process.Dispose();
        _process = null;
    }

    private void Log(string message) => LogReceived?.Invoke(this, message);

    private static string? FindExecutable(string executableName)
    {
        var adjacentPath = Path.Combine(AppContext.BaseDirectory, executableName);
        if (File.Exists(adjacentPath))
        {
            return adjacentPath;
        }

        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory.Trim('"'), executableName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
