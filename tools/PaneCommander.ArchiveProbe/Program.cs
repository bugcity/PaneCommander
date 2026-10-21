using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using PaneCommander.Core;
using PaneCommander.Infrastructure;

if (!OperatingSystem.IsWindows() || args.Length != 3) throw new ArgumentException("Usage: ArchiveProbe <existing-output-directory> <zip|7z> <sizeMiB>");
var parent = Path.GetFullPath(args[0]); if (!Directory.Exists(parent)) throw new DirectoryNotFoundException(parent);
var format = args[1] switch { "zip" => ArchiveFormat.Zip, "7z" => ArchiveFormat.SevenZip, _ => throw new ArgumentException("zip|7z") };
var size = checked(long.Parse(args[2]) * 1024 * 1024);
if (new DriveInfo(Path.GetPathRoot(parent)!).AvailableFreeSpace < size + 2L * 1024 * 1024 * 1024) throw new IOException("Insufficient space for probe plus reserve");
var id = Guid.NewGuid().ToString("N"); var root = Directory.CreateDirectory(Path.Combine(parent, id)).FullName;
var source = Path.Combine(root, "large.bin");
try
{
    using (var file = new FileStream(source, FileMode.CreateNew, FileAccess.Write))
    {
        if (!DeviceIoControl(file.SafeFileHandle, 0x900c4, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        file.SetLength(size);
    }
    var output = Directory.CreateDirectory(Path.Combine(root, "extracted")).FullName;
    using var process = Process.GetCurrentProcess(); GC.Collect(); GC.WaitForPendingFinalizers(); process.Refresh();
    var baselineWorking = process.WorkingSet64; var baselinePrivate = process.PrivateMemorySize64;
    var meter = new Meter(process); var watch = Stopwatch.StartNew(); var service = new ArchiveService(new ErrorLogger(Path.Combine(parent, "Logs")));
    var created = await service.CompressAsync(new([source], root, "large", format, format == ArchiveFormat.SevenZip ? ArchiveLevel.Maximum : ArchiveLevel.Fast), new Choices(), meter, default);
    if (created.Succeeded != 1) throw new IOException(string.Join("\n", created.Errors));
    var archiveBytes = new FileInfo(created.Outputs.Single()).Length; var compressionMs = watch.ElapsedMilliseconds; watch.Restart();
    var extracted = await service.ExtractAsync(created.Outputs.ToArray(), output, 1_073_741_824, new Choices(), meter, default);
    if (extracted.Succeeded != 1) throw new IOException(string.Join("\n", extracted.Errors));
    var actual = Directory.GetFiles(output, "large.bin", SearchOption.AllDirectories).Single();
    if (new FileInfo(actual).Length != size) throw new InvalidDataException("Extracted size mismatch");
    using (var read = File.OpenRead(actual)) { if (read.ReadByte() != 0) throw new InvalidDataException(); read.Position = size - 1; if (read.ReadByte() != 0) throw new InvalidDataException(); }
    meter.Sample();
    var report = new { format = args[1], sourceBytes = size, archiveBytes, compressionMs, extractionMs = watch.ElapsedMilliseconds, baselineWorking, baselinePrivate, peakWorking = process.PeakWorkingSet64, peakPrivate = meter.PeakPrivate, extraWorking = process.PeakWorkingSet64 - baselineWorking, extraPrivate = meter.PeakPrivate - baselinePrivate, meter.ProgressEvents, verifiedSize = new FileInfo(actual).Length, success = true };
    var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
    File.WriteAllText(Path.Combine(parent, $"{args[1]}-{args[2]}MiB.json"), json); Console.WriteLine(json);
}
finally
{
    if (Path.GetDirectoryName(root) != parent || Path.GetFileName(root) != id || !Guid.TryParse(id, out _)) throw new InvalidOperationException("Unsafe cleanup target");
    Directory.Delete(root, true);
}
[DllImport("kernel32.dll", SetLastError = true)]
static extern bool DeviceIoControl(SafeFileHandle handle, uint control, IntPtr input, uint inputSize, IntPtr output, uint outputSize, out uint returned, IntPtr overlapped);

sealed class Choices : IArchiveInteraction
{
    public Task<ZipNameEncoding?> ChooseEncodingAsync(ArchiveNamePreview preview, CancellationToken token) => Task.FromResult<ZipNameEncoding?>(ZipNameEncoding.Utf8);
    public Task<ArchiveConflictChoice> ResolveConflictAsync(ArchiveConflict conflict, CancellationToken token) => throw new InvalidOperationException("Unexpected conflict");
}
sealed class Meter(Process process) : IProgress<ArchiveProgress>
{
    public long PeakPrivate; public int ProgressEvents;
    public void Sample() { process.Refresh(); PeakPrivate = Math.Max(PeakPrivate, process.PrivateMemorySize64); }
    public void Report(ArchiveProgress value) { ProgressEvents++; Sample(); }
}
