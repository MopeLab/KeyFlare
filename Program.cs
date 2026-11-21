using KeyFlare;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
builder.Services.AddSingleton<Store>();
var app = builder.Build();
app.Services.GetRequiredService<Store>().Initialize();
app.MapModelApi();
app.Run();
