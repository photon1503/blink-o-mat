using System.ComponentModel;
using System.Runtime.CompilerServices;
using blink_o_mat.Models;

namespace blink_o_mat.ViewModels;

public sealed record SortFieldOption(FrameSortField Value, string DisplayName)
{
    public override string ToString() => DisplayName;
}

public sealed record SortDirectionOption(ListSortDirection Value, string DisplayName)
{
    public override string ToString() => DisplayName;
}

public sealed class FrameSortRuleViewModel : INotifyPropertyChanged
{
    private SortFieldOption _selectedField;
    private SortDirectionOption _selectedDirection;

    public FrameSortRuleViewModel(SortFieldOption selectedField, SortDirectionOption selectedDirection)
    {
        _selectedField = selectedField;
        _selectedDirection = selectedDirection;
    }

    public SortFieldOption SelectedField
    {
        get => _selectedField;
        set
        {
            if (Equals(_selectedField, value)) return;
            _selectedField = value;
            OnPropertyChanged();
        }
    }

    public SortDirectionOption SelectedDirection
    {
        get => _selectedDirection;
        set
        {
            if (Equals(_selectedDirection, value)) return;
            _selectedDirection = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}