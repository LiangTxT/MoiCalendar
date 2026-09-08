using System.Text.Json;

namespace MoiCalendar.Tests;

public sealed class ProductionDeploymentTests
{
    [Fact]
    public void StaticWebAppConfiguration_PreservesClientRoutesAndExcludesStaticAssets()
    {
        var path = RepositoryPath(
            "src", "MoiCalendar.App", "wwwroot", "staticwebapp.config.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var fallback = document.RootElement.GetProperty("navigationFallback");
        var exclusions = fallback.GetProperty("exclude")
            .EnumerateArray()
            .Select(value => value.GetString())
            .ToArray();

        Assert.Equal("/index.html", fallback.GetProperty("rewrite").GetString());
        Assert.Contains("/_framework/*", exclusions);
        Assert.Contains("/_content/*", exclusions);
        Assert.DoesNotContain(exclusions, value =>
            value is not null && value.Contains("authentication", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(exclusions, value =>
            value is not null && value.Contains("settings", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AzureWorkflow_GeneratesProductionConfigBeforeSingleExplicitPublish()
    {
        var workflow = File.ReadAllText(RepositoryPath(
            ".github", "workflows", "azure-static-web-apps-polite-rock-09eddaf00.yml"));
        var configureIndex = workflow.IndexOf("cloud:configure:production", StringComparison.Ordinal);
        var publishIndex = workflow.IndexOf("dotnet publish", StringComparison.Ordinal);
        var validateIndex = workflow.IndexOf("deploy:validate", StringComparison.Ordinal);
        var deployIndex = workflow.IndexOf("uses: Azure/static-web-apps-deploy@v1", StringComparison.Ordinal);

        Assert.True(configureIndex >= 0 && configureIndex < publishIndex);
        Assert.True(publishIndex < validateIndex && validateIndex < deployIndex);
        Assert.Contains("uses: actions/setup-node@v4", workflow, StringComparison.Ordinal);
        Assert.Contains("skip_app_build: true", workflow, StringComparison.Ordinal);
        Assert.Contains(
            "app_location: \"src/MoiCalendar.App/bin/Release/net10.0/publish/wwwroot\"",
            workflow,
            StringComparison.Ordinal);
        Assert.Contains("MOICALENDAR_PUBLIC_BASE_URL", workflow, StringComparison.Ordinal);
        Assert.Contains("MOICALENDAR_CLOUD_PUBLIC_KEY", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("MOICALENDAR_SERVICE", workflow, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("MOICALENDAR_DATABASE_PASSWORD", workflow, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReleaseBuild_UsesProductionEnvironmentAndKeepsDeveloperOverridesOut()
    {
        var project = File.ReadAllText(RepositoryPath(
            "src", "MoiCalendar.App", "MoiCalendar.App.csproj"));

        Assert.Contains(
            "<WasmApplicationEnvironmentName>Production</WasmApplicationEnvironmentName>",
            project,
            StringComparison.Ordinal);
        Assert.Contains("Content Remove=\"wwwroot\\appsettings.Local.json\"", project, StringComparison.Ordinal);
        Assert.Contains("Content Remove=\"wwwroot\\appsettings.Managed.json\"", project, StringComparison.Ordinal);
        Assert.DoesNotContain("appsettings.Production.json\" />", project, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionGenerator_UsesOnlyBrowserSafeDeploymentInputs()
    {
        var script = File.ReadAllText(RepositoryPath(
            "scripts", "configure-cloud-backend.mjs"));

        foreach (var variable in new[]
        {
            "MOICALENDAR_PUBLIC_BASE_URL",
            "MOICALENDAR_CLOUD_ENABLED",
            "MOICALENDAR_CLOUD_BASE_URL",
            "MOICALENDAR_CLOUD_PUBLIC_KEY"
        })
        {
            Assert.Contains(variable, script, StringComparison.Ordinal);
        }

        Assert.Contains("appsettings.${environmentName}.json", script, StringComparison.Ordinal);
        Assert.Contains("validatePublicKey", script, StringComparison.Ordinal);
        Assert.DoesNotContain("MOICALENDAR_CLIENT_SECRET", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("MOICALENDAR_WEBDAV_PASSWORD", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProductionPublishValidator_RequiresRemoteCloudAndExpectedStaticFiles()
    {
        var validator = File.ReadAllText(RepositoryPath(
            "scripts", "validate-production-publish.mjs"));

        foreach (var file in new[]
        {
            "index.html",
            "appsettings.Production.json",
            "staticwebapp.config.json",
            "service-worker.js",
            "service-worker-assets.js"
        })
        {
            Assert.Contains(file, validator, StringComparison.Ordinal);
        }

        Assert.Contains("cloud.Enabled !== true", validator, StringComparison.Ordinal);
        Assert.Contains("url.hostname === \"localhost\"", validator, StringComparison.Ordinal);
        Assert.Contains("[\"Local\", \"Managed\"]", validator, StringComparison.Ordinal);
        Assert.Contains("appsettings.${forbiddenEnvironment}.json", validator, StringComparison.Ordinal);
    }

    [Fact]
    public void AuthenticationRedirects_AreWiredAndDocumentedForTheConfiguredHost()
    {
        var program = File.ReadAllText(RepositoryPath(
            "src", "MoiCalendar.App", "Program.cs"));
        var settings = File.ReadAllText(RepositoryPath(
            "src", "MoiCalendar.App", "Pages", "Settings.razor"));
        var documentation = File.ReadAllText(RepositoryPath(
            "docs", "authentication-redirects.md"));

        Assert.Contains(
            "appConfiguration.MicrosoftLoginCallbackUrl.AbsoluteUri",
            program,
            StringComparison.Ordinal);
        Assert.Contains(
            "AppConfiguration.CloudAccountRedirectUrl.AbsoluteUri",
            settings,
            StringComparison.Ordinal);
        foreach (var url in new[]
        {
            "http://localhost:5262/settings",
            "https://localhost:7104/settings",
            "http://localhost:5262/authentication/login-callback",
            "https://localhost:7104/authentication/login-callback"
        })
        {
            Assert.Contains(url, documentation, StringComparison.Ordinal);
        }
    }

    private static string RepositoryPath(params string[] parts) =>
        Path.Combine(new[] { FindRepositoryRoot() }.Concat(parts).ToArray());

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AGENTS.md")) &&
                Directory.Exists(Path.Combine(directory.FullName, "src")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        throw new InvalidOperationException("无法定位 MoiCalendar 仓库根目录。");
    }
}
