using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace StarlinkVpnManager.Models;

public sealed class WireGuardProfile : INotifyPropertyChanged
{
    private string _status = "بررسی‌نشده";
    private string _statusDetails = string.Empty;

    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string ServiceName { get; init; } = string.Empty;

    [JsonIgnore]
    public string Status
    {
        get => _status;
        set
        {
            if (_status == value)
            {
                return;
            }

            _status = value;
            OnPropertyChanged();
        }
    }

    [JsonIgnore]
    public string StatusDetails
    {
        get => _statusDetails;
        set
        {
        if (_statusDetails == value)
        {
            return;
            }

        _statusDetails = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
