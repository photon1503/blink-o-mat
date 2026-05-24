using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace blink_o_mat.ViewModels;

public sealed class FilterSummaryViewModel : INotifyPropertyChanged
{
    private string _filterName = string.Empty;
    private int _total;
    private int _accepted;
    private int _rejected;
    private double _acceptedRatio;
    private string _ratioText = string.Empty;
    private string _integrationTimeText = string.Empty;

    public string FilterName
    {
        get => _filterName;
        set { if (_filterName == value) return; _filterName = value; OnPropertyChanged(); }
    }

    public int Total
    {
        get => _total;
        set { if (_total == value) return; _total = value; OnPropertyChanged(); }
    }

    public int Accepted
    {
        get => _accepted;
        set { if (_accepted == value) return; _accepted = value; OnPropertyChanged(); }
    }

    public int Rejected
    {
        get => _rejected;
        set { if (_rejected == value) return; _rejected = value; OnPropertyChanged(); }
    }

    /// <summary>Ratio of accepted frames (0.0 – 1.0), used for bar width.</summary>
    public double AcceptedRatio
    {
        get => _acceptedRatio;
        set { if (Math.Abs(_acceptedRatio - value) < 0.001) return; _acceptedRatio = value; OnPropertyChanged(); }
    }

    /// <summary>e.g. "75.0% accepted"</summary>
    public string RatioText
    {
        get => _ratioText;
        set { if (_ratioText == value) return; _ratioText = value; OnPropertyChanged(); }
    }

    /// <summary>e.g. "2.3 h"</summary>
    public string IntegrationTimeText
    {
        get => _integrationTimeText;
        set { if (_integrationTimeText == value) return; _integrationTimeText = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
