namespace MoiCalendar.Sync;

public sealed record SyncProviderConfiguration
{
    public static SyncProviderConfiguration Disabled { get; } = new(SyncProviderType.None);

    public SyncProviderConfiguration(SyncProviderType providerType)
    {
        if (!Enum.IsDefined(providerType))
        {
            throw new ArgumentOutOfRangeException(nameof(providerType));
        }

        ProviderType = providerType;
    }

    public SyncProviderType ProviderType { get; }
}

public interface ISyncProviderSelection
{
    Task<SyncProviderConfiguration> GetAsync(CancellationToken cancellationToken = default);

    Task SelectAsync(
        SyncProviderType providerType,
        CancellationToken cancellationToken = default);
}

public sealed class InMemorySyncProviderSelection : ISyncProviderSelection
{
    private readonly object gate = new();
    private SyncProviderConfiguration configuration;

    public InMemorySyncProviderSelection()
        : this(SyncProviderConfiguration.Disabled)
    {
    }

    public InMemorySyncProviderSelection(SyncProviderConfiguration initialConfiguration)
    {
        configuration = initialConfiguration;
    }

    public Task<SyncProviderConfiguration> GetAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (gate)
        {
            return Task.FromResult(configuration);
        }
    }

    public Task SelectAsync(
        SyncProviderType providerType,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var selectedConfiguration = new SyncProviderConfiguration(providerType);

        lock (gate)
        {
            configuration = selectedConfiguration;
        }

        return Task.CompletedTask;
    }
}
