using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using blink_o_mat.Services;
using Brush = System.Windows.Media.Brush;

namespace blink_o_mat.ViewModels;

public sealed class FilterChipViewModel : INotifyPropertyChanged
{
    private bool _isSelected;

    public FilterChipViewModel(string key, string displayName, bool isSelected)
        : this(key, displayName, isSelected, FilterCategory.Unknown)
    {
    }

    public FilterChipViewModel(string key, string displayName, bool isSelected, FilterCategory category)
    {
        Key = key;
        DisplayName = displayName;
        _isSelected = isSelected;
        Category = category;
        Group = FilterClassifier.GetGroup(category);
        GroupDisplay = FilterClassifier.GetGroupDisplay(Group);
        SortOrder = FilterClassifier.GetSortOrder(category);

        var (bg, br, fg) = FilterClassifier.GetColors(category);
        BackgroundBrush = Freeze((Brush)new BrushConverter().ConvertFromString(bg)!);
        BorderBrush = Freeze((Brush)new BrushConverter().ConvertFromString(br)!);
        ForegroundBrush = Freeze((Brush)new BrushConverter().ConvertFromString(fg)!);
    }

    public string Key { get; }

    public string DisplayName { get; }

    public FilterCategory Category { get; }

    public FilterGroup Group { get; }

    public string GroupDisplay { get; }

    public int SortOrder { get; }

    public Brush BackgroundBrush { get; }

    public Brush BorderBrush { get; }

    public Brush ForegroundBrush { get; }

    private static Brush Freeze(Brush b)
    {
        if (b.CanFreeze && !b.IsFrozen) b.Freeze();
        return b;
    }

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
