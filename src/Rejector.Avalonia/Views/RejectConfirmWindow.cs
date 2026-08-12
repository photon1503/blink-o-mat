using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Rejector.Avalonia.Views;

public sealed class RejectFilterChip : INotifyPropertyChanged
{
    private bool _isSelected = true;

    public RejectFilterChip(string key, int rejectCount)
    {
        Key = key;
        RejectCount = rejectCount;
    }

    public string Key { get; }
    public int RejectCount { get; }
    public string Label => $"{Key} ({RejectCount})";

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

public partial class RejectConfirmWindow : Window
{
    private readonly List<RejectFilterChip> _chips = [];
    private int _totalRejectedFrameCount;

    public IReadOnlyCollection<string>? SelectedFilterKeys { get; private set; }

    public RejectConfirmWindow()
        : this(0, string.Empty, null)
    {
    }

    public RejectConfirmWindow(
        int totalRejectedFrameCount,
        string destination,
        IReadOnlyDictionary<string, int>? filterRejectCounts = null)
    {
        InitializeComponent();
        ConfigureWindow(totalRejectedFrameCount, destination, filterRejectCounts);
    }

    private void ConfigureWindow(int totalRejectedFrameCount, string destination, IReadOnlyDictionary<string, int>? filterRejectCounts)
    {
        _totalRejectedFrameCount = totalRejectedFrameCount;
        DestinationText.Text = destination;

        _chips.Clear();
        foreach (var chip in filterRejectCounts is not null && filterRejectCounts.Count >= 2
                     ? filterRejectCounts
                         .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                         .Select(kv => new RejectFilterChip(kv.Key, kv.Value))
                     : [])
        {
            _chips.Add(chip);
            chip.PropertyChanged += Chip_PropertyChanged;
        }

        if (_chips.Count >= 2)
        {
            FilterSection.IsVisible = true;
            FilterChipsList.ItemsSource = _chips;
        }

        UpdateFrameCountText();
    }

    private void Chip_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.Equals(e.PropertyName, nameof(RejectFilterChip.IsSelected), StringComparison.Ordinal))
        {
            UpdateFrameCountText();
        }
    }

    private void UpdateFrameCountText()
    {
        var count = _chips.Count >= 2
            ? _chips.Where(chip => chip.IsSelected).Sum(chip => chip.RejectCount)
            : _totalRejectedFrameCount;

        FrameCountText.Text = $"{count} frame{(count == 1 ? "" : "s")}";
        ProceedButton.IsEnabled = count > 0;
    }

    private void Proceed_Click(object? sender, RoutedEventArgs e)
    {
        SelectedFilterKeys = _chips.Count >= 2
            ? _chips.Where(chip => chip.IsSelected).Select(chip => chip.Key).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : null;

        Close(this);
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e)
    {
        SelectedFilterKeys = null;
        Close(null);
    }
}
