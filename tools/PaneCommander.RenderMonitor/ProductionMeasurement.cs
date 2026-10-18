using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

internal static class ProductionMeasurement
{
    public static int Run(string executable, string profile, string output, int seconds, string expectedMode, bool visitTabs)
    {
        Directory.CreateDirectory(output);
        using var display = new DisplayRequest();
        using var gpu = new GpuQuery();
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "--profile-directory", profile, "--diagnostics-directory", output }) start.ArgumentList.Add(arg);
        if (visitTabs) { start.ArgumentList.Add("--diagnostics-workload"); start.ArgumentList.Add("visit-tabs"); }
        using var target = Process.Start(start) ?? throw new InvalidOperationException("Product did not start");
        using var cleanup = new OwnedTargetCleanup(target);
        var startup = Stopwatch.StartNew();
        var startupSamples = new List<(double Seconds, long Private, long WorkingSet, double Cpu, int Handles)>();
        using var startupCsv = new StreamWriter(Path.Combine(output, "startup.csv"));
        startupCsv.WriteLine("Seconds,PrivateBytes,WorkingSet,CpuSeconds,Handles");
        while (!File.Exists(Path.Combine(output, "ready.json")))
        {
            if (target.HasExited || startup.Elapsed.TotalSeconds > 120) throw new InvalidOperationException("Product did not become ready");
            target.Refresh();
            var point = (Seconds: startup.Elapsed.TotalSeconds, Private: target.PrivateMemorySize64, WorkingSet: target.WorkingSet64, Cpu: target.TotalProcessorTime.TotalSeconds, Handles: target.HandleCount);
            startupSamples.Add(point);
            startupCsv.WriteLine(FormattableString.Invariant($"{point.Seconds},{point.Private},{point.WorkingSet},{point.Cpu},{point.Handles}")); startupCsv.Flush();
            Thread.Sleep(100); gpu.Read(target.Id);
        }
        var ready = JsonNode.Parse(File.ReadAllText(Path.Combine(output, "ready.json")))!;
        Dictionary<string, long?> ProfileTimes() => new[] { "settings.json", "session.json", "favorites.json" }
            .ToDictionary(name => name, name => File.Exists(Path.Combine(profile, name)) ? (long?)File.GetLastWriteTimeUtc(Path.Combine(profile, name)).Ticks : null);
        var beforeTimes = ProfileTimes();
        if (ready["modeBeforeWindow"]!.GetValue<string>() != expectedMode || ready["effectiveRenderMode"]!.GetValue<string>() != expectedMode)
            throw new InvalidOperationException("Rendering preference was not applied before window creation");
        using var csv = new StreamWriter(Path.Combine(output, "samples.csv"));
        csv.WriteLine("Seconds,PrivateBytes,WorkingSet,CpuSeconds,CpuPercentAllLogical,GpuDedicatedBytes,GpuSharedBytes,GpuEngineMaxPercent,Handles");
        var samples = new List<Sample>(); var watch = Stopwatch.StartNew(); double previousCpu = 0, previousTime = 0;
        while (true)
        {
            target.Refresh(); if (target.HasExited) throw new InvalidOperationException("Product exited during measurement");
            var elapsed = watch.Elapsed.TotalSeconds; var cpu = target.TotalProcessorTime.TotalSeconds;
            var percent = samples.Count == 0 ? 0 : 100 * (cpu - previousCpu) / (elapsed - previousTime);
            previousCpu = cpu; previousTime = elapsed;
            var graphics = gpu.Read(target.Id);
            var sample = new Sample(elapsed, "observation", target.PrivateMemorySize64, target.WorkingSet64, cpu,
                percent / Environment.ProcessorCount, percent, graphics.Dedicated, graphics.Shared, graphics.Engine, target.HandleCount);
            samples.Add(sample);
            csv.WriteLine(string.Join(',', new object?[] { elapsed, sample.PrivateBytes, sample.WorkingSet, cpu, sample.CpuPercentAllLogical,
                graphics.Dedicated, graphics.Shared, graphics.Engine, sample.Handles }.Select(v => Convert.ToString(v, CultureInfo.InvariantCulture)))); csv.Flush();
            if (elapsed >= seconds) break;
            Thread.Sleep(250);
        }
        var afterTimes = ProfileTimes();
        var noProfileWrites = beforeTimes.All(pair => afterTimes[pair.Key] == pair.Value);
        using (var stop = EventWaitHandle.OpenExisting(ready["eventName"]!.GetValue<string>())) stop.Set();
        if (!target.WaitForExit(20000) || target.ExitCode != 0) throw new InvalidOperationException("Product did not close normally");
        var saved = JsonNode.Parse(File.ReadAllText(Path.Combine(profile, "session.json")))!;
        var restored = ready["session"]!;
        // JSON number text can differ slightly after WPF rounding; compare layout separately.
        var tabsMatch = JsonNode.DeepEquals(saved["Left"], restored["Left"]) && JsonNode.DeepEquals(saved["Right"], restored["Right"])
            && JsonNode.DeepEquals(saved["ActiveSide"], restored["ActiveSide"]);
        var first = samples[0]; var final = samples[^1];
        File.WriteAllText(Path.Combine(output, "summary.json"), JsonSerializer.Serialize(new
        {
            mode = expectedMode, productionExecutable = true, target.Id, seconds, observedSeconds = final.Seconds,
            startupDurationSeconds = startup.Elapsed.TotalSeconds - watch.Elapsed.TotalSeconds,
            startupPrivateMaxMB = Math.Max(startupSamples.Max(s => s.Private), first.PrivateBytes) / 1e6,
            startupWorkingSetMaxMB = Math.Max(startupSamples.Max(s => s.WorkingSet), first.WorkingSet) / 1e6,
            privateMeanMB = samples.Average(s => s.PrivateBytes) / 1e6, privateMaxMB = samples.Max(s => s.PrivateBytes) / 1e6, privateFinalMB = final.PrivateBytes / 1e6,
            privateDeltaMB = (final.PrivateBytes - first.PrivateBytes) / 1e6,
            handlesMean = samples.Average(s => s.Handles), handlesMax = samples.Max(s => s.Handles), handlesFinal = final.Handles, handlesDelta = final.Handles - first.Handles,
            workingSetMeanMB = samples.Average(s => s.WorkingSet) / 1e6, workingSetMaxMB = samples.Max(s => s.WorkingSet) / 1e6,
            cpuSeconds = final.CpuSeconds - first.CpuSeconds, cpuTotalIncludingWarmupSeconds = final.CpuSeconds,
            cpuMeanAllLogical = 100 * (final.CpuSeconds - first.CpuSeconds) / (final.Seconds - first.Seconds) / Environment.ProcessorCount,
            cpuPeakSampleAllLogical = samples.Skip(1).Max(s => s.CpuPercentAllLogical), logicalProcessors = Environment.ProcessorCount,
            gpuDedicatedMaxMB = samples.Max(s => s.GpuDedicatedBytes) / 1e6, gpuSharedMaxMB = samples.Max(s => s.GpuSharedBytes) / 1e6,
            gpuEnginePeakPercent = samples.Max(s => s.GpuEngineMaxPercent), gpuCounterErrors = gpu.Errors,
            savedTabStateMatchesReady = tabsMatch, savedLayout = saved["Layout"]!.DeepClone(), forcedGc = false,
            profileFilesUnchangedDuringObservation = noProfileWrites,
            samplingMilliseconds = 250, monitorSeparateProcess = true, visitTabs, displayAndSystemRequested = true
        }, new JsonSerializerOptions { WriteIndented = true }));
        if (!tabsMatch) throw new InvalidOperationException("Session tab state changed unexpectedly on close");
        return 0;
    }
}

internal sealed class OwnedTargetCleanup(Process process) : IDisposable
{
    public void Dispose()
    {
        if (!process.HasExited) { process.Kill(entireProcessTree: true); process.WaitForExit(10000); }
    }
}
