using KeyFlare;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
builder.Services.AddSingleton<Store>();
builder.Services.AddRazorPages();
builder.Services.AddHostedService<AlertWorker>();
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 2 * 1024 * 1024);
var app = builder.Build();
app.Services.GetRequiredService<Store>().Initialize();
app.UseRouting();
app.UseMiddleware<AdminAccess>();
app.MapRazorPages();
app.MapModelApi();
app.Run();
