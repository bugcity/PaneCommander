using PaneCommander.Core;

namespace PaneCommander.Infrastructure;

public sealed class JsonSettingsStore(string? filePath = null) : ISettingsStore
{
    private readonly AtomicJsonState<AppearanceSettings> store = new(filePath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PaneCommander", "settings.json"), value =>
    {
        var errors = value.Validate();
        if (errors.Count > 0) throw new InvalidDataException(string.Join("、", errors));
    }, 65536, false, (value,root) => value with
    {
        FilterMatchForeground = root.TryGetProperty(nameof(AppearanceSettings.FilterMatchForeground),out _) ? value.FilterMatchForeground : AppearanceSettings.DefaultFilterMatchColor(value.Background),
        CustomSchemes = value.CustomSchemes?.Select(scheme => scheme is null ? scheme! : ColorSchemes.Upgrade(scheme)).ToArray()
    });
    public async Task<SettingsLoadResult> LoadAsync(CancellationToken cancellationToken = default)
    {
        var result = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
        return new(result.Value ?? new(), result.Error is null ? null : "設定を読み込めないため既定値を使用します: " + result.Error);
    }
    public async Task SaveAsync(AppearanceSettings settings, CancellationToken cancellationToken = default)
    {
        var errors = settings.Validate();
        if (errors.Count > 0) throw new ArgumentException(string.Join("、", errors));
        await store.SaveAsync(settings, cancellationToken).ConfigureAwait(false);
    }
}
