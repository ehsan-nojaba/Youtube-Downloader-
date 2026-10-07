using YtDownloader.Web.Components;
using YtDownloader.Web.Services;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHttpClient("Api", client => client.BaseAddress = new Uri(builder.Configuration["Api:BaseUrl"] ?? "https://localhost:7266/"));
builder.Services.AddScoped<BrowserJobHistory>();
builder.Services.AddTransient(provider => new DownloadSession(provider.GetRequiredService<IHttpClientFactory>().CreateClient("Api"), provider.GetRequiredService<ILogger<DownloadSession>>(), provider.GetRequiredService<BrowserJobHistory>()));

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

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



