using System.Text.Json;
using PaneCommander.Core;

namespace PaneCommander.Infrastructure;

// Disk flush/replace and serialization stay off the UI thread. One writer per store.
internal sealed class AtomicJsonState<T>(string path, Action<T> validate, int maxBytes = 1_048_576, bool keepBackup = true, Func<T,JsonElement,T>? upgrade = null) where T : class
{
    private readonly SemaphoreSlim gate = new(1, 1);
    public async Task<StateLoadResult<T>> LoadAsync(CancellationToken token)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                try
                {
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    if (stream.Length > maxBytes) throw new InvalidDataException($"JSONが上限{maxBytes:N0}バイトを超えています。");
                    token.ThrowIfCancellationRequested();
                    T value;
                    if (upgrade is null) value = JsonSerializer.Deserialize<T>(stream) ?? throw new InvalidDataException("JSONが空です。");
                    else { using var document = JsonDocument.Parse(stream); value = document.RootElement.Deserialize<T>() ?? throw new InvalidDataException("JSONが空です。"); value = upgrade(value,document.RootElement); }
                    validate(value); return new StateLoadResult<T>(value);
                }
                catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return new StateLoadResult<T>(null); }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException)
                { return new StateLoadResult<T>(null, ex.Message); }
            }, token).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }
    public async Task SaveAsync(T value, CancellationToken token)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await Task.Run(() =>
            {
                validate(value);
                var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
                if (bytes.Length > maxBytes) throw new InvalidDataException($"JSONが上限{maxBytes:N0}バイトを超えています。");
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
                var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    token.ThrowIfCancellationRequested();
                    using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    { stream.Write(bytes); stream.Flush(true); }
                    token.ThrowIfCancellationRequested();
                    if (File.Exists(path)) File.Replace(temporary, path, keepBackup ? path + ".bak" : null);
                    else File.Move(temporary, path);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }, token).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }
}
public sealed class JsonFavoritesStore(string? filePath = null) : IFavoritesStore
{
    private readonly AtomicJsonState<FavoritesDocument> store = new(filePath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PaneCommander", "favorites.json"), value => value.Validate());
    public Task<StateLoadResult<FavoritesDocument>> LoadAsync(CancellationToken token = default) => store.LoadAsync(token);
    public Task SaveAsync(FavoritesDocument value, CancellationToken token = default) => store.SaveAsync(value, token);
}
public sealed class JsonSessionStore(string? filePath = null) : ISessionStore
{
    private readonly AtomicJsonState<SessionDocument> store = new(filePath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PaneCommander", "session.json"), value => value.Validate());
    public Task<StateLoadResult<SessionDocument>> LoadAsync(CancellationToken token = default) => store.LoadAsync(token);
    public Task SaveAsync(SessionDocument value, CancellationToken token = default) => store.SaveAsync(value, token);
}
