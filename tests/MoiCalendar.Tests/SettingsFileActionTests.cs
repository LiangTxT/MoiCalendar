using System.Net;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MoiCalendar.App.Components;

namespace MoiCalendar.Tests;

public sealed class SettingsFileActionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RendersOneNativeFileControl_WithAccessibleLabelAndDisabledState(bool disabled)
    {
        await using var services = new ServiceCollection().AddLogging()
            .AddSingleton<IJSRuntime, UnusedRuntime>().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<SettingsFileAction>(ParameterView.FromDictionary(
                new Dictionary<string, object?>
                {
                    [nameof(SettingsFileAction.Id)] = "restore-file",
                    [nameof(SettingsFileAction.Label)] = "选择备份恢复…",
                    [nameof(SettingsFileAction.Accept)] = ".json,application/json",
                    [nameof(SettingsFileAction.Disabled)] = disabled
                }));
            return WebUtility.HtmlDecode(component.ToHtmlString());
        });
        Assert.Contains("for=\"restore-file\"", html);
        Assert.Contains("选择备份恢复…", html);
        Assert.Contains("id=\"restore-file\"", html);
        Assert.Contains("accept=\".json,application/json\"", html);
        Assert.Contains("type=\"file\"", html);
        Assert.Contains("settings-data-button", html);
        Assert.Contains("aria-describedby=\"restore-file-status\"", html);
        Assert.Contains("未选择文件", html);
        Assert.Contains("id=\"restore-file-status\"", html);
        Assert.Equal(1, html.Split("<input").Length - 1);
        Assert.Equal(disabled, html.Contains(" disabled"));
    }

    private sealed class UnusedRuntime : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            throw new InvalidOperationException("静态渲染不应打开文件选择器");
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            throw new InvalidOperationException("静态渲染不应打开文件选择器");
    }
}
