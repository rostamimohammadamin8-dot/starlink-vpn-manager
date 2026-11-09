using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;

namespace StarlinkVpnManager.Services;

internal sealed class KillSwitchService
{
    private const string RuleGroup = "StarlinkVpnManager Kill Switch";
    private static readonly string PowerShellScript = """
        param(
            [Parameter(Mandatory = $true)][ValidateSet('Enable', 'Disable')][string]$Action,
            [Parameter(Mandatory = $true)][string]$StatePath,
            [Parameter(Mandatory = $true)][string]$ResultPath,
            [string]$TrustedInterfaceAlias,
            [string[]]$RemoteAddresses = @()
        )

        $ErrorActionPreference = 'Stop'
        $ruleGroup = 'StarlinkVpnManager Kill Switch'

        function Restore-FirewallState {
            if (Test-Path -LiteralPath $StatePath) {
                $savedProfiles = Get-Content -LiteralPath $StatePath -Raw | ConvertFrom-Json
                foreach ($profile in @($savedProfiles.Profiles)) {
                    Set-NetFirewallProfile -Profile $profile.Name -DefaultOutboundAction $profile.DefaultOutboundAction
                }

                foreach ($ruleName in @($savedProfiles.EnabledOutboundRuleNames)) {
                    Enable-NetFirewallRule -Name $ruleName -ErrorAction SilentlyContinue
                }
            }

            Remove-NetFirewallRule -Group $ruleGroup -ErrorAction SilentlyContinue
        }

        try {
            if ($Action -eq 'Enable') {
                $profiles = @(Get-NetFirewallProfile -Profile Domain, Private, Public |
                    Select-Object Name, DefaultOutboundAction)
                $enabledRuleNames = @(Get-NetFirewallRule -PolicyStore PersistentStore `
                        -Direction Outbound -Action Allow |
                    Where-Object { $_.Enabled -eq 'True' } |
                    Select-Object -ExpandProperty Name)
                [pscustomobject]@{
                    Profiles = $profiles
                    EnabledOutboundRuleNames = $enabledRuleNames
                } | ConvertTo-Json -Depth 5 -Compress |
                    Set-Content -LiteralPath $StatePath -Encoding UTF8

                try {
                    Remove-NetFirewallRule -Group $ruleGroup -ErrorAction SilentlyContinue
                    Get-NetFirewallRule -PolicyStore PersistentStore `
                            -Direction Outbound -Action Allow |
                        Where-Object { $_.Enabled -eq 'True' } |
                        Disable-NetFirewallRule -ErrorAction Stop

                    New-NetFirewallRule `
                        -DisplayName "Starlink VPN interface $TrustedInterfaceAlias" `
                        -Group $ruleGroup `
                        -Direction Outbound `
                        -Action Allow `
                        -InterfaceAlias $TrustedInterfaceAlias `
                        -Protocol Any `
                        -Profile Any | Out-Null

                    foreach ($address in $RemoteAddresses) {
                        New-NetFirewallRule `
                            -DisplayName "Starlink VPN endpoint $address" `
                            -Group $ruleGroup `
                            -Direction Outbound `
                            -Action Allow `
                            -RemoteAddress $address `
                            -Protocol Any `
                            -Profile Any | Out-Null
                    }

                    Set-NetFirewallProfile `
                        -Profile Domain, Private, Public `
                        -DefaultOutboundAction Block

                    $activeProfiles = @(Get-NetFirewallProfile -PolicyStore ActiveStore `
                            -Profile Domain, Private, Public)
                    if ($activeProfiles.Where({ $_.DefaultOutboundAction -ne 'Block' }).Count -gt 0) {
                        throw "An enforced firewall policy did not apply the outbound block."
                    }

                    $activeExceptions = @(Get-NetFirewallRule -PolicyStore ActiveStore `
                            -Group $ruleGroup -Direction Outbound -Action Allow |
                        Where-Object { $_.Enabled -eq 'True' })
                    if ($activeExceptions.Count -lt ($RemoteAddresses.Count + 1)) {
                        throw "The VPN interface or server allow rules are not active in the effective firewall policy."
                    }

                    $unexpectedRules = @(Get-NetFirewallRule -PolicyStore ActiveStore `
                            -Direction Outbound -Action Allow |
                        Where-Object { $_.Enabled -eq 'True' -and $_.Group -ne $ruleGroup })
                    if ($unexpectedRules.Count -gt 0) {
                        throw "An enforced firewall policy still allows outbound traffic outside the VPN. Kill switch activation was cancelled."
                    }
                }
                catch {
                    $applyError = $_.Exception.Message
                    try {
                        Restore-FirewallState
                        Remove-Item -LiteralPath $StatePath -Force -ErrorAction SilentlyContinue
                        $message = "Enable failed; previous firewall policy was restored. $applyError"
                    }
                    catch {
                        $message = "Enable failed and rollback was incomplete. Protection state was retained. Apply error: $applyError; rollback error: $($_.Exception.Message)"
                    }

                    Set-Content -LiteralPath $ResultPath -Value $message -Encoding UTF8
                    exit 1
                }

                Set-Content -LiteralPath $ResultPath -Value 'Kill switch enabled.' -Encoding UTF8
                exit 0
            }

            Restore-FirewallState
            Remove-Item -LiteralPath $StatePath -Force -ErrorAction SilentlyContinue
            Set-Content -LiteralPath $ResultPath -Value 'Previous firewall policy restored.' -Encoding UTF8
            exit 0
        }
        catch {
            Set-Content -LiteralPath $ResultPath -Value $_.Exception.Message -Encoding UTF8
            exit 1
        }
        """;

    private readonly string _statePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "StarlinkVpnManager",
        "kill-switch-state.json");

    public bool IsEnabled => File.Exists(_statePath);

    public Task EnableAsync(
        IEnumerable<IPAddress> trustedRemoteAddresses,
        string trustedInterfaceAlias,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(trustedInterfaceAlias);
        ArgumentNullException.ThrowIfNull(trustedRemoteAddresses);
        var addresses = trustedRemoteAddresses
            .Where(IsValidRemoteAddress)
            .Select(address => address.ToString())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (addresses.Length == 0)
        {
            throw new InvalidOperationException(
                "No valid VPN server IPs were found. The kill switch was not enabled.");
        }

        return RunElevatedScriptAsync("Enable", addresses, trustedInterfaceAlias, ct);
    }

    public Task DisableAsync(CancellationToken ct = default) =>
        RunElevatedScriptAsync("Disable", [], null, ct);

    private async Task RunElevatedScriptAsync(
        string action,
        IReadOnlyList<string> remoteAddresses,
        string? trustedInterfaceAlias,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var stateDirectory = Path.GetDirectoryName(_statePath)!;
        Directory.CreateDirectory(stateDirectory);
        var scriptPath = Path.Combine(Path.GetTempPath(), $"starlink-vpn-killswitch-{Guid.NewGuid():N}.ps1");
        var resultPath = Path.Combine(Path.GetTempPath(), $"starlink-vpn-killswitch-{Guid.NewGuid():N}.txt");

        try
        {
            await File.WriteAllTextAsync(scriptPath, PowerShellScript, new UTF8Encoding(false), ct);
            var startInfo = new ProcessStartInfo
            {
                FileName = GetPowerShellPath(),
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(scriptPath);
            startInfo.ArgumentList.Add("-Action");
            startInfo.ArgumentList.Add(action);
            startInfo.ArgumentList.Add("-StatePath");
            startInfo.ArgumentList.Add(_statePath);
            startInfo.ArgumentList.Add("-ResultPath");
            startInfo.ArgumentList.Add(resultPath);
            if (trustedInterfaceAlias is not null)
            {
                startInfo.ArgumentList.Add("-TrustedInterfaceAlias");
                startInfo.ArgumentList.Add(trustedInterfaceAlias);
            }

            if (remoteAddresses.Count > 0)
            {
                startInfo.ArgumentList.Add("-RemoteAddresses");
                foreach (var address in remoteAddresses)
                {
                    startInfo.ArgumentList.Add(address);
                }
            }

            using var process = Process.Start(startInfo)
                                ?? throw new InvalidOperationException("The elevated firewall operation did not start.");
            await process.WaitForExitAsync();
            var result = File.Exists(resultPath)
                ? await File.ReadAllTextAsync(resultPath)
                : "The elevated firewall operation returned no status.";
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(result.Trim());
            }
        }
        finally
        {
            DeleteTemporaryFile(scriptPath);
            DeleteTemporaryFile(resultPath);
        }
    }

    private static string GetPowerShellPath()
    {
        var systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var path = Path.Combine(systemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        return File.Exists(path)
            ? path
            : throw new FileNotFoundException("Windows PowerShell could not be located.", path);
    }

    private static void DeleteTemporaryFile(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static bool IsValidRemoteAddress(IPAddress address) =>
        !IPAddress.IsLoopback(address)
        && !address.Equals(IPAddress.Any)
        && !address.Equals(IPAddress.IPv6Any)
        && !address.IsIPv6Multicast
        && !(address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
             && address.GetAddressBytes()[0] >= 224);
}
