using CsiHub.Features.Home.Services;
using CsiHub.Features.Shell;
using CsiHub.Ingestion;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Configuration.AddJsonFile("array_geometry.json", optional: true, reloadOnChange: true);

builder.Services.AddCsiIngestion(builder.Configuration.GetSection("CsiIngestion"));
builder.Services.Configure<CsiAoaOptions>(builder.Configuration.GetSection("CsiAoaOptions"));
builder.Services.Configure<ArrayGeometryOptions>(builder.Configuration.GetSection("ArrayGeometry"));

builder.Services.AddSingleton<CsiNodeConfigurationService>();
builder.Services.AddSingleton<RfChannelEvaluator>();
builder.Services.AddSingleton<CsiNodeStateStore>();
builder.Services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<CsiNodeStateStore>());
builder.Services.AddSingleton<HardwareConfigService>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();