using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls.Primitives;

namespace blink_o_mat
{
    // Per-filter entry shown as a toggleable chip.
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

        // Display text shown on the chip button: "Ha (12)"
        public string Label => $"{Key}  ({RejectCount})";

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value) return;
                _isSelected = value;
                OnPropertyChanged();
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    public partial class RejectConfirmDialog : Window
    {
        private readonly List<RejectFilterChip> _chips;
        private readonly int _totalRejectedFrameCount;

        /// <summary>
        /// Keys of filters the user left selected when they clicked Proceed.
        /// Null means "all" (no filter chips were shown).
        /// </summary>
        public IReadOnlyCollection<string>? SelectedFilterKeys { get; private set; }

        /// <param name="filterRejectCounts">
        /// Dictionary of filterKey → rejected-frame-count.
        /// Pass an empty dictionary (or null) when there is only one filter / no filter info.
        /// </param>
        public RejectConfirmDialog(
            int totalRejectedFrameCount,
            string destination,
            IReadOnlyDictionary<string, int>? filterRejectCounts = null)
        {
            InitializeComponent();

            _totalRejectedFrameCount = totalRejectedFrameCount;
            DestinationText.Text = destination;

            // Build chips only when there are ≥2 filters.
            _chips = filterRejectCounts != null && filterRejectCounts.Count >= 2
                ? filterRejectCounts
                    .OrderBy(kv => kv.Key)
                    .Select(kv => new RejectFilterChip(kv.Key, kv.Value))
                    .ToList()
                : [];

            if (_chips.Count >= 2)
            {
                FilterSection.Visibility = Visibility.Visible;
                FilterChipsList.ItemsSource = _chips;
                foreach (var chip in _chips)
                    chip.PropertyChanged += Chip_PropertyChanged;
            }

            UpdateFrameCountText();
        }

        // Recompute and refresh the "Frames to move" label whenever a chip is toggled.
        private void Chip_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(RejectFilterChip.IsSelected))
                UpdateFrameCountText();
        }

        private void UpdateFrameCountText()
        {
            int count = _chips.Count >= 2
                ? _chips.Where(c => c.IsSelected).Sum(c => c.RejectCount)
                : _totalRejectedFrameCount;

            FrameCountText.Text = $"{count} frame{(count == 1 ? "" : "s")}";
            ProceedButton.IsEnabled = count > 0;
        }

        private void Proceed_Click(object sender, RoutedEventArgs e)
        {
            SelectedFilterKeys = _chips.Count >= 2
                ? _chips.Where(c => c.IsSelected).Select(c => c.Key).ToHashSet()
                : null;

            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
