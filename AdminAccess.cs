using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace KeyFlare;

public sealed class AdminAccess(RequestDelegate next, IConfiguration configuration)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (context.GetEndpoint()?.Metadata.GetMetadata<PageActionDescriptor>() is null)
        {
            await next(context);
            return;
        }
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["X-Frame-Options"] = "DENY";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        var password = configuration["KeyFlare:AdminPassword"];
        if (string.IsNullOrWhiteSpace(password))
        {
            context.Response.StatusCode = 503;
            return;
        }
        var header = context.Request.Headers.Authorization.ToString();
        if (header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            byte[] presented = new byte[header.Length];
            if (Convert.TryFromBase64String(header[6..], presented, out int size) &&
                CryptographicOperations.FixedTimeEquals(SHA256.HashData(presented.AsSpan(0, size)), SHA256.HashData(Encoding.UTF8.GetBytes($"admin:{password}"))))
            {
                await next(context);
                return;
            }
        }
        context.Response.StatusCode = 401;
        context.Response.Headers.WWWAuthenticate = "Basic realm=\"KeyFlare\", charset=\"UTF-8\"";
    }
}
