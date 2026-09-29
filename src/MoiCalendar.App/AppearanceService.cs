using Microsoft.JSInterop;
using MoiCalendar.Core;

namespace MoiCalendar.App;

public sealed class AppearanceService(
    IAppearancePreferenceStore preferenceStore,
    IJSRuntime jsRuntime)
{
    private readonly SemaphoreSlim initializationGate = new(1, 1);
    private bool initialized;

    public AppearancePreference Current { get; private set; } = AppearancePreference.Default;

    public string? PersistenceError { get; private set; }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (initialized)
        {
            return;
        }

        await initializationGate.WaitAsync(cancellationToken);
        try
        {
            if (initialized)
            {
                return;
            }

            try
            {
                Current = await preferenceStore.GetAsync(cancellationToken) ??
                    AppearancePreference.Default;
                PersistenceError = null;
            }
            catch (EventRepositoryException exception)
            {
                Current = AppearancePreference.Default;
                PersistenceError = exception.Message;
            }

            await ApplyAsync(cancellationToken);
            initialized = true;
        }
        finally
        {
            initializationGate.Release();
        }
    }

    public Task SetModeAsync(
        AppearanceMode mode,
        CancellationToken cancellationToken = default) =>
        UpdateAsync(Current with { Mode = mode }, cancellationToken);

    private async Task UpdateAsync(
        AppearancePreference preference,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(
            Enum.IsDefined(preference.Mode),
            true,
            nameof(preference.Mode));

        Current = preference;
        await ApplyAsync(cancellationToken);

        try
        {
            await preferenceStore.SaveAsync(Current, cancellationToken);
            PersistenceError = null;
        }
        catch (EventRepositoryException exception)
        {
            PersistenceError = exception.Message;
            throw;
        }
    }

    private ValueTask ApplyAsync(CancellationToken cancellationToken)
    {
        return jsRuntime.InvokeVoidAsync(
            "moicalendarUi.applyAppearance",
            cancellationToken,
            Current.Mode.ToString().ToLowerInvariant());
    }
}
