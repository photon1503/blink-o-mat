using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace blink_o_mat.ViewModels;

public sealed class FilterChipViewModel : INotifyPropertyChanged
{
    private bool _isSelected;

    public FilterChipViewModel(string key, string displayName, bool isSelected)
    {
        Key = key;
        DisplayName = displayName;
        _isSelected = isSelected;
    }

    public string Key { get; }

    public string DisplayName { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
            {
                return;
            }

            _isSelected = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
