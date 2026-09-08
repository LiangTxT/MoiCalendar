using Microsoft.Extensions.Configuration;
using MoiCalendar.App.Configuration;

namespace MoiCalendar.Tests;

public sealed class ProductionUrlConfigurationTests
{
    [Fact]
    public void PlannedProductionDomain_GeneratesExactAuthenticationRedirects()
    {
        var configuration = new ConfigurationManager
        {
            ["MoiCalendar:PublicBaseUrl"] = "https://app.moicalendar.com",
            ["MoiCalendar:MicrosoftAuthentication:RedirectPath"] =
                "authentication/login-callback"
        };

        var result = MoiCalendarConfiguration.Load(
            configuration,
            new Uri("http://localhost:5262/"));

        Assert.Equal("https://app.moicalendar.com/", result.PublicBaseUrl.AbsoluteUri);
        Assert.Equal(
            "https://app.moicalendar.com/settings",
            result.CloudAccountRedirectUrl.AbsoluteUri);
        Assert.Equal(
            "https://app.moicalendar.com/authentication/login-callback",
            result.MicrosoftLoginCallbackUrl.AbsoluteUri);
    }

    [Theory]
    [InlineData(
        "http://localhost:5262/",
        "http://localhost:5262/settings",
        "http://localhost:5262/authentication/login-callback")]
    [InlineData(
        "https://localhost:7104/",
        "https://localhost:7104/settings",
        "https://localhost:7104/authentication/login-callback")]
    public void MissingPublicBaseUrl_PreservesLocalDevelopmentRedirects(
        string fallbackBaseUrl,
        string expectedCloudRedirect,
        string expectedMicrosoftRedirect)
    {
        var result = MoiCalendarConfiguration.Load(
            new ConfigurationManager(),
            new Uri(fallbackBaseUrl));

        Assert.Equal(expectedCloudRedirect, result.CloudAccountRedirectUrl.AbsoluteUri);
        Assert.Equal(expectedMicrosoftRedirect, result.MicrosoftLoginCallbackUrl.AbsoluteUri);
    }

    [Theory]
    [InlineData("/authentication/login-callback")]
    [InlineData("authentication\\login-callback")]
    [InlineData("authentication/../login-callback")]
    [InlineData("authentication/login-callback?source=test")]
    [InlineData("https://login.example.com/callback")]
    public void MicrosoftRedirectPath_RejectsValuesThatCanEscapeConfiguredOrigin(string path)
    {
        var configuration = new ConfigurationManager
        {
            ["MoiCalendar:MicrosoftAuthentication:RedirectPath"] = path
        };

        Assert.Throws<InvalidOperationException>(() =>
            MoiCalendarConfiguration.Load(
                configuration,
                new Uri("http://localhost:5262/")));
    }

    [Fact]
    public void PublicBaseUrl_RejectsEmbeddedCredentials()
    {
        var configuration = new ConfigurationManager
        {
            ["MoiCalendar:PublicBaseUrl"] = "https://user:password@app.moicalendar.com/"
        };

        Assert.Throws<InvalidOperationException>(() =>
            MoiCalendarConfiguration.Load(
                configuration,
                new Uri("http://localhost:5262/")));
    }
}
