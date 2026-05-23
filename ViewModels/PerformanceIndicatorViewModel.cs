using System.ComponentModel;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace blink_o_mat.ViewModels;

/// <summary>
/// Polls CPU, RAM, Network, and Disk throughput every second
/// and exposes them as bindable string properties for the status bar.
/// </summary>
public sealed class PerformanceIndicatorViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly PerformanceCounter _cpuCounter;
    private readonly PerformanceCounter _diskCounter;
    private readonly CancellationTokenSource _cts = new();

    private string _cpu  = "CPU   0%";
    private string _ram  = "RAM   0.0 MB";
    private string _net  = "NET   0.0 KB/s";
    private string _disk = "DSK   0.0 KB/s";

    private long _lastBytesReceived;
    private long _lastBytesSent;

    public string Cpu
    {
        get => _cpu;
        private set { if (_cpu == value) return; _cpu = value; OnPropertyChanged(); }
    }

    public string Ram
    {
        get => _ram;
        private set { if (_ram == value) return; _ram = value; OnPropertyChanged(); }
    }

    public string Net
    {
        get => _net;
        private set { if (_net == value) return; _net = value; OnPropertyChanged(); }
    }

    public string Disk
    {
        get => _disk;
        private set { if (_disk == value) return; _disk = value; OnPropertyChanged(); }
    }

    public PerformanceIndicatorViewModel()
    {
        _cpuCounter  = new PerformanceCounter("Processor",     "% Processor Time", "_Total");
        _diskCounter = new PerformanceCounter("PhysicalDisk",  "Disk Bytes/sec",   "_Total");
        // Warm up — first NextValue() call always returns 0.
        _ = _cpuCounter.NextValue();
        _ = _diskCounter.NextValue();
        _ = Task.Run(() => PollAsync(_cts.Token));
    }

    private async Task PollAsync(CancellationToken ct)
    {
        GetNetworkBytes(out _lastBytesReceived, out _lastBytesSent);

        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(1000, ct).ConfigureAwait(false);
            if (ct.IsCancellationRequested) break;

            try
            {
                // CPU — right-align percentage so width stays constant
                float cpu = _cpuCounter.NextValue();
                Cpu = $"CPU {cpu,3:F0}%";

                // RAM — process working set
                long ramBytes = Environment.WorkingSet;
                Ram = $"RAM {FormatBytes(ramBytes),8}";

                // Network — bytes in+out per second across all NICs
                GetNetworkBytes(out long rx, out long tx);
                long rxDelta = Math.Max(0, rx - _lastBytesReceived);
                long txDelta = Math.Max(0, tx - _lastBytesSent);
                _lastBytesReceived = rx;
                _lastBytesSent = tx;
                Net = $"NET {FormatBytes(rxDelta + txDelta),8}/s";

                // Disk — bytes/sec across all physical disks
                float diskBps = _diskCounter.NextValue();
                Disk = $"DSK {FormatBytes((long)diskBps),8}/s";
            }
            catch
            {
                // Swallow — indicators are non-critical.
            }
        }
    }

    private static void GetNetworkBytes(out long received, out long sent)
    {
        received = 0;
        sent = 0;
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            var stats = nic.GetIPv4Statistics();
            received += stats.BytesReceived;
            sent     += stats.BytesSent;
        }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0)             return "0 B";
        if (bytes < 1024)           return $"{bytes} B";
        if (bytes < 1024 * 1024)    return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F1} GB";
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cpuCounter.Dispose();
        _diskCounter.Dispose();
        _cts.Dispose();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
