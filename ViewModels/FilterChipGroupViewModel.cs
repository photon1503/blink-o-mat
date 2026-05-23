using System.Collections.ObjectModel;
using blink_o_mat.Services;

namespace blink_o_mat.ViewModels;

/// <summary>Groups filter chips by <see cref="FilterGroup"/> (Narrowband, LRGB, Other)
/// for display in the rejection scope dropdown.</summary>
public sealed class FilterChipGroupViewModel
{
    public FilterChipGroupViewModel(FilterGroup group, string displayName)
    {
        Group = group;
        DisplayName = displayName;
    }

    public FilterGroup Group { get; }

    public string DisplayName { get; }

    public ObservableCollection<FilterChipViewModel> Chips { get; } = [];

    public int SortOrder => Group switch
    {
        FilterGroup.Narrowband => 0,
        FilterGroup.Lrgb => 1,
        _ => 2,
    };
}
