using System.Net.NetworkInformation;
using System.Security.Principal;
using LibreHardwareMonitor.Hardware;
using PCEssentials.Core;

namespace PCEssentials.Services;

public sealed record HardwareSnapshot
{
    public double? CpuLoad { get; init; }
    public double? CpuTemp { get; init; }
    public double[] CoreLoads { get; init; } = [];
    public double? CpuPower { get; init; }
    public double? GpuLoad { get; init; }
    public double? GpuTemp { get; init; }
    public double? GpuMemUsedMb { get; init; }
    public double? GpuMemTotalMb { get; init; }
    public double RamUsedGb { get; init; }
    public double RamTotalGb { get; init; }
    public double RamLoad => RamTotalGb > 0 ? RamUsedGb / RamTotalGb * 100 : 0;
    /// Bytes per second across all active physical adapters.
    public double NetUpBps { get; init; }
    public double NetDownBps { get; init; }
    public long SessionUpBytes { get; init; }
    public long SessionDownBytes { get; init; }
    public string? CpuName { get; init; }
    public string? GpuName { get; init; }
    public List<(string Name, double FreeGb, double TotalGb)> Disks { get; init; } = [];
    public bool CpuTempNeedsAdmin { get; init; }
}

/// Polls hardware once a second on a background thread and publishes a snapshot.
public sealed class HardwareMonitor : IDisposable
{
    private readonly Computer _computer;
    private readonly Thread _thread;
    private volatile bool _running = true;
    private readonly Dictionary<string, (long Rx, long Tx)> _lastNet = new();
    /// The adapters counted for network speed. Listing them takes ~5 ms of CPU, so it is done again only
    /// when Windows reports a network change, and every 30 seconds in case a change went unreported.
    private NetworkInterface[] _adapters = [];
    private DateTime _adaptersAt = DateTime.MinValue;
    private volatile bool _adaptersStale = true;
    /// The NVIDIA GPU through NVML when there is one; otherwise LibreHardwareMonitor reads the GPU.
    private NvmlGpu? _nvml;
    private DateTime _lastNetAt = DateTime.UtcNow;
    private long _sessionRx, _sessionTx;
    private readonly List<double> _cpuHistory = new(), _netDownHistory = new(), _netUpHistory = new(), _ramHistory = new();

    public HardwareSnapshot Current { get; private set; } = new();
    public event Action<HardwareSnapshot>? Updated;
    public static bool IsAdmin { get; } = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    public IReadOnlyList<double> CpuHistory { get { lock (_cpuHistory) return _cpuHistory.ToArray(); } }
    public IReadOnlyList<double> RamHistory { get { lock (_cpuHistory) return _ramHistory.ToArray(); } }
    public IReadOnlyList<double> NetDownHistory { get { lock (_cpuHistory) return _netDownHistory.ToArray(); } }
    public IReadOnlyList<double> NetUpHistory { get { lock (_cpuHistory) return _netUpHistory.ToArray(); } }

    public HardwareMonitor()
    {
        // GPUs are added in Loop, once it is known whether NVML can read the GPU instead.
        _computer = new Computer { IsCpuEnabled = true, IsGpuEnabled = false, IsMemoryEnabled = true, IsMotherboardEnabled = false };
        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkChanged;
        _thread = new Thread(Loop) { IsBackground = true, Name = "HardwareMonitor", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    private void OnNetworkChanged(object? sender, EventArgs e) => _adaptersStale = true;

    private void Loop()
    {
        _nvml = NvmlGpu.TryOpen();
        _computer.IsGpuEnabled = _nvml is null;
        Log.Info(_nvml is null ? "gpu: LibreHardwareMonitor" : $"gpu: NVML ({_nvml.Name})");
        try { _computer.Open(); }
        catch (Exception ex) { Log.Error("hardware open", ex); }

        var nextDisk = DateTime.MinValue;
        List<(string, double, double)> disks = [];
        LogSensorsOnce();
        while (_running)
        {
            try
            {
                if (DateTime.UtcNow >= nextDisk)
                {
                    disks = ReadDisks();
                    nextDisk = DateTime.UtcNow.AddSeconds(30);
                }
                var snap = Read() with { Disks = disks };
                Current = snap;
                lock (_cpuHistory)
                {
                    Push(_cpuHistory, snap.CpuLoad ?? 0);
                    Push(_ramHistory, snap.RamLoad);
                    Push(_netDownHistory, snap.NetDownBps);
                    Push(_netUpHistory, snap.NetUpBps);
                }
                Updated?.Invoke(snap);
            }
            catch (Exception ex) { Log.Error("hardware read", ex); }
            Thread.Sleep(1000);
        }
        // Shut NVML down here rather than in Dispose, so it never closes under a read in progress.
        _nvml?.Dispose();
    }

    /// Records which CPU and GPU sensors exist, to explain a missing temperature.
    private void LogSensorsOnce()
    {
        try
        {
            foreach (var hw in _computer.Hardware.Where(h => h.HardwareType is HardwareType.Cpu or HardwareType.GpuNvidia or HardwareType.GpuAmd))
            {
                hw.Update();
                var temps = hw.Sensors.Where(s => s.SensorType == SensorType.Temperature).Select(s => $"{s.Name}={s.Value?.ToString("0.0") ?? "null"}");
                Log.Info($"sensors {hw.HardwareType} '{hw.Name}' (admin {IsAdmin}): {string.Join(", ", temps)}");
            }
        }
        catch (Exception ex) { Log.Error("sensor log", ex); }
    }

    private static void Push(List<double> list, double value)
    {
        list.Add(value);
        if (list.Count > 60) list.RemoveAt(0);
    }

    private HardwareSnapshot Read()
    {
        double? cpuLoad = null, cpuTemp = null, cpuPower = null, gpuLoad = null, gpuTemp = null, gpuMemUsed = null, gpuMemTotal = null;
        double ramUsed = 0, ramAvail = 0;
        string? cpuName = null, gpuName = null;
        var cores = new SortedDictionary<int, double>();

        foreach (var hw in _computer.Hardware)
        {
            hw.Update();
            switch (hw.HardwareType)
            {
                case HardwareType.Cpu:
                    cpuName = hw.Name;
                    foreach (var s in hw.Sensors)
                    {
                        if (s.Value is not float v) continue;
                        if (s.SensorType == SensorType.Load && s.Name == "CPU Total") cpuLoad = v;
                        else if (s.SensorType == SensorType.Load && s.Name.StartsWith("CPU Core #") && !s.Name.Contains("Thread"))
                        {
                            if (int.TryParse(s.Name.AsSpan("CPU Core #".Length), out var idx)) cores[idx] = v;
                        }
                        else if (s.SensorType == SensorType.Temperature && v > 0)
                        {
                            // AMD reports Tctl/Tdie, Intel reports Package; prefer those over per-core readings.
                            if (s.Name.Contains("Tctl") || s.Name.Contains("Tdie") || s.Name.Contains("Package")) cpuTemp = v;
                            else if (cpuTemp is null && s.Name.Contains("Core")) cpuTemp = v;
                        }
                        else if (s.SensorType == SensorType.Power && s.Name.Contains("Package")) cpuPower = v;
                    }
                    break;
                case HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel:
                    // The first discrete GPU wins; integrated GPUs are only used when nothing else exists.
                    if (gpuName is not null && hw.HardwareType == HardwareType.GpuIntel) break;
                    gpuName ??= hw.Name;
                    foreach (var s in hw.Sensors)
                    {
                        if (s.Value is not float v) continue;
                        if (s.SensorType == SensorType.Load && s.Name == "GPU Core") gpuLoad = v;
                        else if (s.SensorType == SensorType.Temperature && s.Name == "GPU Core") gpuTemp = v;
                        else if (s.SensorType == SensorType.SmallData && s.Name == "GPU Memory Used") gpuMemUsed = v;
                        else if (s.SensorType == SensorType.SmallData && s.Name == "GPU Memory Total") gpuMemTotal = v;
                    }
                    break;
                case HardwareType.Memory:
                    if (hw.Name.Contains("Virtual")) break;
                    foreach (var s in hw.Sensors)
                    {
                        if (s.Value is not float v) continue;
                        if (s.SensorType == SensorType.Data && s.Name == "Memory Used") ramUsed = v;
                        else if (s.SensorType == SensorType.Data && s.Name == "Memory Available") ramAvail = v;
                    }
                    break;
            }
        }

        if (_nvml is not null)
        {
            gpuName = _nvml.Name;
            (gpuLoad, gpuTemp, gpuMemUsed, gpuMemTotal) = _nvml.Read();
        }

        var (up, down) = ReadNetwork();
        return new HardwareSnapshot
        {
            CpuLoad = cpuLoad,
            CpuTemp = cpuTemp,
            CoreLoads = cores.Values.ToArray(),
            CpuPower = cpuPower,
            GpuLoad = gpuLoad,
            GpuTemp = gpuTemp,
            GpuMemUsedMb = gpuMemUsed,
            GpuMemTotalMb = gpuMemTotal,
            RamUsedGb = ramUsed,
            RamTotalGb = ramUsed + ramAvail,
            NetUpBps = up,
            NetDownBps = down,
            SessionUpBytes = _sessionTx,
            SessionDownBytes = _sessionRx,
            CpuName = cpuName,
            GpuName = gpuName,
            CpuTempNeedsAdmin = cpuTemp is null,
        };
    }

    private (double Up, double Down) ReadNetwork()
    {
        var now = DateTime.UtcNow;
        var seconds = Math.Max(0.2, (now - _lastNetAt).TotalSeconds);
        _lastNetAt = now;
        long dRx = 0, dTx = 0;
        foreach (var nic in Adapters())
        {
            IPv4InterfaceStatistics stats;
            try { stats = nic.GetIPv4Statistics(); } catch { continue; }
            var rx = stats.BytesReceived;
            var tx = stats.BytesSent;
            if (_lastNet.TryGetValue(nic.Id, out var last))
            {
                if (rx >= last.Rx) dRx += rx - last.Rx;
                if (tx >= last.Tx) dTx += tx - last.Tx;
            }
            _lastNet[nic.Id] = (rx, tx);
        }
        _sessionRx += dRx;
        _sessionTx += dTx;
        return (dTx / seconds, dRx / seconds);
    }

    private NetworkInterface[] Adapters()
    {
        if (!_adaptersStale && DateTime.UtcNow - _adaptersAt < TimeSpan.FromSeconds(30)) return _adapters;
        _adaptersStale = false;
        _adaptersAt = DateTime.UtcNow;
        try
        {
            _adapters = NetworkInterface.GetAllNetworkInterfaces().Where(nic =>
            {
                if (nic.OperationalStatus != OperationalStatus.Up) return false;
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) return false;
                var desc = nic.Description;
                // Skip virtual adapters (Hyper-V, WSL, VPN filters) so traffic is not counted twice.
                return !(desc.Contains("Hyper-V") || desc.Contains("Virtual") || desc.Contains("Filter") || desc.Contains("WFP"));
            }).ToArray();
        }
        catch (NetworkInformationException ex) { Log.Error("network adapters", ex); }
        return _adapters;
    }

    private static List<(string, double, double)> ReadDisks()
    {
        var list = new List<(string, double, double)>();
        foreach (var d in System.IO.DriveInfo.GetDrives())
        {
            try
            {
                if (d.DriveType != System.IO.DriveType.Fixed || !d.IsReady) continue;
                list.Add((d.Name.TrimEnd('\\'), d.AvailableFreeSpace / 1e9, d.TotalSize / 1e9));
            }
            catch { }
        }
        return list;
    }

    public void Dispose()
    {
        _running = false;
        NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkChanged;
        try { _computer.Close(); } catch { }
    }
}

public static class Format
{
    public static string Speed(double bytesPerSecond)
    {
        if (bytesPerSecond < 1024) return $"{bytesPerSecond:0} B/s";
        if (bytesPerSecond < 1024 * 1024) return $"{bytesPerSecond / 1024:0} KB/s";
        if (bytesPerSecond < 1024L * 1024 * 1024) return $"{bytesPerSecond / 1024 / 1024:0.0} MB/s";
        return $"{bytesPerSecond / 1024 / 1024 / 1024:0.00} GB/s";
    }

    public static string Bytes(double bytes)
    {
        if (bytes < 1024 * 1024) return $"{bytes / 1024:0} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / 1024 / 1024:0.0} MB";
        return $"{bytes / 1024 / 1024 / 1024:0.00} GB";
    }

    public static string Temp(double? celsius, bool fahrenheit)
    {
        if (celsius is not double c) return "--°";
        return fahrenheit ? $"{c * 9 / 5 + 32:0}°F" : $"{c:0}°C";
    }

    public static string Percent(double? value) => value is double v ? $"{v:0}%" : "--%";
}
