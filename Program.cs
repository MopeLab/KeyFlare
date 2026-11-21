using KeyFlare;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc.RazorPages;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
builder.Services.AddSingleton<Store>();
builder.Services.AddSingleton<AdminPassword>();
builder.Services.AddRazorPages(options => options.Conventions.AuthorizePage("/Admin"));
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options =>
{
    options.LoginPath = "/admin/login";
    options.Cookie.Name = "KeyFlare.Admin";
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.ExpireTimeSpan = TimeSpan.FromHours(8);
    options.Events.OnValidatePrincipal = async context =>
    {
        var credentials = context.HttpContext.RequestServices.GetRequiredService<AdminPassword>();
        if (!credentials.IsCurrent(context.Principal?.FindFirstValue(ClaimTypes.NameIdentifier)))
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync();
        }
    };
});
builder.Services.AddHostedService<AlertWorker>();
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 2 * 1024 * 1024);
var app = builder.Build();
app.Services.GetRequiredService<Store>().Initialize();
app.Services.GetRequiredService<AdminPassword>().Initialize();
app.UseRouting();
app.Use(async (context, next) =>
{
    if (context.GetEndpoint()?.Metadata.GetMetadata<PageActionDescriptor>() is not null)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["X-Frame-Options"] = "DENY";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
    }
    await next(context);
});
app.UseAuthentication();
app.UseAuthorization();
app.MapRazorPages();
app.MapModelApi();
app.Run();
