using MoiCalendar.Sync;

namespace MoiCalendar.Tests;

public sealed class SyncArchitectureTests
{
    [Fact]
    public void StorageProvider_ExposesRequiredProviderIndependentCapabilities()
    {
        var methodNames = typeof(ISyncStorageProvider)
            .GetMethods()
            .Select(method => method.Name)
            .Order()
            .ToArray();

        Assert.Equal(
            new[]
            {
                "DeleteAsync",
                "DownloadTextAsync",
                "EnsureDirectoryAsync",
                "ExistsAsync",
                "ListFilesAsync",
                "TestConnectionAsync",
                "UploadTextAsync"
            },
            methodNames);
    }

    [Fact]
    public void ProviderTypes_AreProviderSelectionValues()
    {
        Assert.Equal(
            [SyncProviderType.None, SyncProviderType.OneDrive, SyncProviderType.WebDav],
            Enum.GetValues<SyncProviderType>());
    }

    [Theory]
    [InlineData(SyncProviderType.None)]
    [InlineData(SyncProviderType.OneDrive)]
    [InlineData(SyncProviderType.WebDav)]
    public async Task ProviderSelection_StoresSelectedConfiguration(SyncProviderType providerType)
    {
        ISyncProviderSelection selection = new InMemorySyncProviderSelection();

        await selection.SelectAsync(providerType);

        var configuration = await selection.GetAsync();
        Assert.Equal(providerType, configuration.ProviderType);
    }

    [Fact]
    public async Task ProviderSelection_DefaultsToNone()
    {
        ISyncProviderSelection selection = new InMemorySyncProviderSelection();

        var configuration = await selection.GetAsync();

        Assert.Equal(SyncProviderType.None, configuration.ProviderType);
    }

    [Fact]
    public void SyncAssembly_HasNoHostingOrCloudProviderDependency()
    {
        var dependencyNames = typeof(ISyncStorageProvider).Assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .ToArray();

        Assert.DoesNotContain(dependencyNames, name =>
            name.Contains("Azure", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Graph", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("WebDav", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RemoteFormat_IsSingleProviderIndependentContract()
    {
        Assert.Equal(1, RemoteSyncFormat.CurrentVersion);
        Assert.Equal("moicalendar.sync.json", RemoteSyncFormat.FileName);
        Assert.Equal("application/json", RemoteSyncFormat.MediaType);
        Assert.Equal("MyCalendar/operations", RemoteSyncFormat.OperationsDirectory);
    }

    [Fact]
    public void SyncService_DependsOnProviderInterfaceInsteadOfConcreteProvider()
    {
        var constructorDependencies = typeof(SyncService)
            .GetConstructors()
            .Single()
            .GetParameters()
            .Select(parameter => parameter.ParameterType)
            .ToArray();

        Assert.Contains(typeof(ISyncStorageProvider), constructorDependencies);
        Assert.DoesNotContain(constructorDependencies, dependency =>
            dependency.Name.Contains("OneDrive", StringComparison.OrdinalIgnoreCase) ||
            dependency.Name.Contains("WebDav", StringComparison.OrdinalIgnoreCase) ||
            dependency.Name.Contains("Azure", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SyncStatus_ContainsOnlyProviderIndependentFields()
    {
        var propertyNames = typeof(MoiCalendar.Core.SyncStatus)
            .GetProperties()
            .Select(property => property.Name)
            .OrderBy(name => name)
            .ToArray();

        Assert.Equal(
            new[]
            {
                "ActiveProvider",
                "FailedOperationCount",
                "IsSyncing",
                "LastErrorSummary",
                "LastFailedSyncAtUtc",
                "LastSuccessfulSyncAtUtc",
                "LastSyncStartedAtUtc",
                "PendingOperationCount"
            },
            propertyNames);
    }
}
