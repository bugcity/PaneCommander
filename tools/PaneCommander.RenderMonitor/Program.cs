using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;

if (args.Length == 7 && args[0] == "product") return ProductionMeasurement.Run(args[1], args[2], args[3], int.Parse(args[4]), args[5], args[6] == "visit-tabs");
if (args.Length != 5) return 2;
var driver = args[0]; var scenario = args[1]; var fixture = args[2]; var output = args[3]; var mode = args[4];
Directory.CreateDirectory(output);
using var displayRequest = new DisplayRequest();
using var gpu = new GpuQuery();
var start = new ProcessStartInfo(driver) { UseShellExecute = false, CreateNoWindow = true };
foreach (var arg in new[] { "render", scenario, fixture, output, mode }) start.ArgumentList.Add(arg);
using var target = Process.Start(start) ?? throw new InvalidOperationException("Driver did not start");
using var cleanup = new OwnedTargetCleanup(target);
var startup = Stopwatch.StartNew();
while (!File.Exists(Path.Combine(output, "ready.json")))
{
    if (target.HasExited || startup.Elapsed.TotalSeconds > 120) throw new InvalidOperationException("Driver startup failed");
    Thread.Sleep(100); gpu.Read(target.Id);
}
using var csv = new StreamWriter(Path.Combine(output, "samples.csv"));
csv.WriteLine("Seconds,Phase,PrivateBytes,WorkingSet,CpuSeconds,CpuPercentAllLogical,CpuPercentOneCore,GpuDedicatedBytes,GpuSharedBytes,GpuEngineMaxPercent,Handles");
var watch = Stopwatch.StartNew();
double lastTime = 0, lastCpu = 0;
var samples = new List<Sample>();
while (true)
{
    target.Refresh();
    if (target.HasExited) throw new InvalidOperationException("Driver exited before final sample");
    var t = watch.Elapsed.TotalSeconds; var cpu = target.TotalProcessorTime.TotalSeconds;
    var percent = samples.Count == 0 ? 0 : 100 * (cpu - lastCpu) / (t - lastTime);
    lastTime = t; lastCpu = cpu;
    var phaseFile = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "phase.json")));
    var phase = phaseFile.RootElement.GetProperty("phase").GetString()!;
    phaseFile.Dispose();
    var graphics = gpu.Read(target.Id);
    var sample = new Sample(t, phase, target.PrivateMemorySize64, target.WorkingSet64, cpu,
        percent / Environment.ProcessorCount, percent, graphics.Dedicated, graphics.Shared, graphics.Engine, target.HandleCount);
    samples.Add(sample);
    csv.WriteLine(string.Join(',', new object?[] { t, phase, sample.PrivateBytes, sample.WorkingSet, cpu,
        sample.CpuPercentAllLogical, percent, graphics.Dedicated, graphics.Shared, graphics.Engine, sample.Handles }.Select(v => Convert.ToString(v, CultureInfo.InvariantCulture))));
    csv.Flush();
    if (File.Exists(Path.Combine(output, "completed.json"))) break;
    if (watch.Elapsed.TotalSeconds > (scenario == "GUI" ? 1900 : 450)) throw new TimeoutException("Run timeout");
    Thread.Sleep(250);
}
if (!target.WaitForExit(10000)) throw new TimeoutException("Driver exit timeout");
if (target.ExitCode != 0) throw new InvalidOperationException("Driver failed");
var first = samples[0]; var final = samples[^1];
File.WriteAllText(Path.Combine(output, "monitor-summary.json"), JsonSerializer.Serialize(new
{
    scenario, mode, pid = target.Id, logicalProcessors = Environment.ProcessorCount, durationSeconds = final.Seconds,
    privateMeanMB = samples.Average(s => s.PrivateBytes) / 1e6, privateMaxMB = samples.Max(s => s.PrivateBytes) / 1e6, privateFinalMB = final.PrivateBytes / 1e6,
    workingSetMeanMB = samples.Average(s => s.WorkingSet) / 1e6, workingSetMaxMB = samples.Max(s => s.WorkingSet) / 1e6,
    handlesMean = samples.Average(s => s.Handles), handlesMax = samples.Max(s => s.Handles), handlesFinal = final.Handles, handlesDelta = final.Handles - first.Handles,
    cpuSeconds = final.CpuSeconds - first.CpuSeconds,
    cpuTotalIncludingWarmupSeconds = final.CpuSeconds,
    cpuMeanAllLogical = 100 * (final.CpuSeconds - first.CpuSeconds) / final.Seconds / Environment.ProcessorCount,
    cpuPeakSampleAllLogical = samples.Skip(1).Max(s => s.CpuPercentAllLogical),
    gpuAvailable = samples.Any(s => s.GpuDedicatedBytes != null), gpuCounterErrors = gpu.Errors,
    gpuDedicatedMaxMB = samples.Max(s => s.GpuDedicatedBytes) / 1e6, gpuSharedMaxMB = samples.Max(s => s.GpuSharedBytes) / 1e6,
    gpuEnginePeakPercent = samples.Max(s => s.GpuEngineMaxPercent),
    phases = samples.GroupBy(s => s.Phase).Select(g => new { phase = g.Key, count = g.Count(),
        privateMeanMB = g.Average(s => s.PrivateBytes) / 1e6, privateMaxMB = g.Max(s => s.PrivateBytes) / 1e6,
        privateFinalMB = g.Last().PrivateBytes / 1e6, durationSeconds = g.Last().Seconds - g.First().Seconds,
        handlesMean = g.Average(s => s.Handles), handlesMax = g.Max(s => s.Handles), handlesFinal = g.Last().Handles,
        cpuSeconds = g.Last().CpuSeconds - g.First().CpuSeconds,
        cpuMeanAllLogical = g.Count() < 2 ? (double?)null : 100 * (g.Last().CpuSeconds - g.First().CpuSeconds) / (g.Last().Seconds - g.First().Seconds) / Environment.ProcessorCount,
        cpuMeanSampleAllLogical = g.Skip(1).Select(s => s.CpuPercentAllLogical).DefaultIfEmpty().Average(),
        cpuPeakSampleAllLogical = g.Skip(1).Select(s => s.CpuPercentAllLogical).DefaultIfEmpty().Max() }).ToArray(),
    samplingMilliseconds = 250, forcedGc = false, monitorSeparateProcess = true
    , displayAndSystemRequested = true
}, new JsonSerializerOptions { WriteIndented = true }));
return 0;

internal sealed record Sample(double Seconds, string Phase, long PrivateBytes, long WorkingSet, double CpuSeconds,
    double CpuPercentAllLogical, double CpuPercentOneCore, double? GpuDedicatedBytes, double? GpuSharedBytes, double? GpuEngineMaxPercent, int Handles);

internal sealed class DisplayRequest : IDisposable
{
    public DisplayRequest()
    {
        if (SetThreadExecutionState(0x80000003) == 0) throw new InvalidOperationException("Display/system execution request failed");
    }
    public void Dispose() => SetThreadExecutionState(0x80000000);
    [DllImport("kernel32.dll")] private static extern uint SetThreadExecutionState(uint flags);
}

internal sealed class GpuQuery : IDisposable
{
    private nint query;
    private readonly nint[] counters = new nint[3];
    public List<string> Errors { get; } = [];
    public GpuQuery()
    {
        var status = PdhOpenQuery(null, 0, out query);
        if (status != 0) { Errors.Add($"OpenQuery:{status:x}"); return; }
        string[] paths = [@"\GPU Process Memory(*)\Dedicated Usage", @"\GPU Process Memory(*)\Shared Usage", @"\GPU Engine(*)\Utilization Percentage"];
        for (var i = 0; i < 3; i++)
        { status = PdhAddEnglishCounter(query, paths[i], 0, out counters[i]); if (status != 0) Errors.Add($"{paths[i]}:{status:x}"); }
        PdhCollectQueryData(query);
    }
    public (double? Dedicated, double? Shared, double? Engine) Read(int pid)
    {
        if (query == 0) return (null, null, null);
        PdhCollectQueryData(query);
        double? ReadCounter(int index)
        {
            if (counters[index] == 0) return null;
            uint bytes = 0, count = 0;
            const uint format = 0x200; // PDH_FMT_DOUBLE
            var status = PdhGetFormattedCounterArray(counters[index], format, ref bytes, ref count, 0);
            if (bytes == 0) return null;
            var buffer = Marshal.AllocHGlobal((int)bytes);
            try
            {
                status = PdhGetFormattedCounterArray(counters[index], format, ref bytes, ref count, buffer);
                if (status != 0) return null;
                var values = new List<double>();
                for (var i = 0; i < count; i++)
                {
                    var item = Marshal.PtrToStructure<CounterItem>(buffer + i * Marshal.SizeOf<CounterItem>());
                    var name = Marshal.PtrToStringUni(item.Name);
                    if (name?.StartsWith($"pid_{pid}_", StringComparison.OrdinalIgnoreCase) == true && item.Status <= 1) values.Add(item.Value);
                }
                return values.Count == 0 ? null : index == 2 ? values.Max() : values.Sum();
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        return (ReadCounter(0), ReadCounter(1), ReadCounter(2));
    }
    public void Dispose() { if (query != 0) PdhCloseQuery(query); }
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct CounterItem { [FieldOffset(0)] public nint Name; [FieldOffset(8)] public uint Status; [FieldOffset(16)] public double Value; }
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhOpenQuery(string? source, nint data, out nint query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhAddEnglishCounterW")] private static extern uint PdhAddEnglishCounter(nint query, string path, nint data, out nint counter);
    [DllImport("pdh.dll")] private static extern uint PdhCollectQueryData(nint query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhGetFormattedCounterArrayW")] private static extern uint PdhGetFormattedCounterArray(nint counter, uint format, ref uint size, ref uint count, nint buffer);
    [DllImport("pdh.dll")] private static extern uint PdhCloseQuery(nint query);
}
