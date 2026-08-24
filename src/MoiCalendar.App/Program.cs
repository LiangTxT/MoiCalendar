using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using MoiCalendar.App;
using MoiCalendar.App.Configuration;
using MoiCalendar.Core;
using MoiCalendar.Storage;
using MoiCalendar.Sync;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

var appConfiguration = MoiCalendarConfiguration.Load(
    builder.Configuration,
    new Uri(builder.HostEnvironment.BaseAddress));

builder.Services.AddSingleton(appConfiguration);
builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });
builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
builder.Services.AddScoped<IEventRepository, IndexedDbEventRepository>();
builder.Services.AddScoped<CalendarEventService>();
builder.Services.AddSingleton<ISyncProviderSelection, InMemorySyncProviderSelection>();

await builder.Build().RunAsync();
