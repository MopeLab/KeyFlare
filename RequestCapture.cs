using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;

namespace KeyFlare;

public sealed record RequestCapture(string Method, string Target, string UserAgent, string? Model, string Input,
    string Headers, byte[] Body, string? PeerIp, string? ClientIp)
{
    // Cloudflare's published origin-facing networks: https://www.cloudflare.com/ips/
    private static readonly IPNetwork[] Cloudflare = """
        173.245.48.0/20 103.21.244.0/22 103.22.200.0/22 103.31.4.0/22 141.101.64.0/18
        108.162.192.0/18 190.93.240.0/20 188.114.96.0/20 197.234.240.0/22 198.41.128.0/17
        162.158.0.0/15 104.16.0.0/13 104.24.0.0/14 172.64.0.0/13 131.0.72.0/22
        2400:cb00::/32 2606:4700::/32 2803:f800::/32 2405:b500::/32 2405:8100::/32
        2a06:98c0::/29 2c0f:f248::/32
        """.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(IPNetwork.Parse).ToArray();

    internal static IPAddress? ClientAddress(IPAddress? peer, IHeaderDictionary headers)
    {
        if (peer?.IsIPv4MappedToIPv6 == true) peer = peer.MapToIPv4();
        if (peer is null || !Cloudflare.Any(network => network.Contains(peer))) return peer;
        if (!IPAddress.TryParse(headers["CF-Connecting-IP"].ToString(), out var client)) return null;
        if (client.AddressFamily == AddressFamily.InterNetwork && client.GetAddressBytes()[0] >= 240 &&
            IPAddress.TryParse(headers["CF-Connecting-IPv6"].ToString(), out var v6) && v6.AddressFamily == AddressFamily.InterNetworkV6)
            return v6;
        return client;
    }

    public static async Task<(RequestCapture Capture, JsonElement Json)> Read(HttpContext context)
    {
        var request = context.Request;
        using var stream = new MemoryStream();
        await request.Body.CopyToAsync(stream, context.RequestAborted);
        var bytes = stream.ToArray();
        JsonElement json = default;
        if (bytes.Length > 0)
        {
            try { json = JsonSerializer.Deserialize<JsonElement>(bytes); }
            catch (JsonException) { /* Keep malformed bodies available for inspection. */ }
        }
        var model = request.RouteValues["model"] as string;
        var text = new List<string>();
        if (json.ValueKind == JsonValueKind.Object)
        {
            if (json.TryGetProperty("model", out var field) && field.ValueKind == JsonValueKind.String)
                model ??= field.GetString();
            foreach (var name in new[] { "instructions", "system", "systemInstruction", "messages", "input", "contents" })
                if (json.TryGetProperty(name, out var content)) CollectText(content, text);
        }
        var headers = new StringBuilder();
        foreach (var (name, values) in request.Headers)
            foreach (var value in values) headers.Append(name).Append(": ").AppendLine(value);
        var peer = context.Connection.RemoteIpAddress;
        var target = context.Features.Get<IHttpRequestFeature>()?.RawTarget ?? request.Path + request.QueryString;
        return (new(request.Method, target, request.Headers.UserAgent.ToString(), model, string.Join("\n", text),
            headers.ToString(), bytes, peer?.ToString(), ClientAddress(peer, request.Headers)?.ToString()), json);
    }

    private static void CollectText(JsonElement element, List<string> result)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                result.Add(element.GetString()!);
                break;
            case JsonValueKind.Array:
                foreach (var child in element.EnumerateArray()) CollectText(child, result);
                break;
            case JsonValueKind.Object:
                foreach (var field in element.EnumerateObject())
                    if (field.Name is "text" or "content" or "parts") CollectText(field.Value, result);
                break;
        }
    }
}
