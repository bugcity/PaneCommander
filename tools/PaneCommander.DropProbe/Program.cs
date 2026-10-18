using System.Text.Json;

// Harmless test-only launch detector. No child process, registry or user file access.
var directory = AppContext.BaseDirectory;
var marker = args.Contains("--calibrate") ? "calibration.marker" : "exe-launched.marker";
File.WriteAllText(Path.Combine(directory, marker), JsonSerializer.Serialize(new { pid = Environment.ProcessId, args, time = DateTimeOffset.UtcNow }));
