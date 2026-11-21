using System.Text.Json;

namespace KeyFlare;

public static class ModelApi
{
    private const string Reply = "Hello! How can I help you?";
    private const string OpenAI = "OpenAI", Claude = "Anthropic", Gemini = "Gemini";

    public static void MapModelApi(this WebApplication app)
    {
        app.MapGet("/v1/models", async (HttpContext c, Store store) =>
        {
            if (c.Request.Headers.ContainsKey("x-goog-api-key") || c.Request.Query.ContainsKey("key"))
                return await GeminiModels(c, store);
            bool claude = c.Request.Headers.ContainsKey("x-api-key") || c.Request.Headers.ContainsKey("anthropic-version");
            var protocol = claude ? Claude : OpenAI;
            if (await Record(c, store, protocol, false) is null) return Error(protocol, 401);
            return claude
                ? Results.Json(new { data = new[] { new { id = "claude-sonnet-4-5", type = "model", display_name = "Claude Sonnet", created_at = "2025-09-29T00:00:00Z" } }, has_more = false, first_id = "claude-sonnet-4-5", last_id = "claude-sonnet-4-5" })
                : Results.Json(new { @object = "list", data = new[] { new { id = "gpt-4o", @object = "model", created = 0, owned_by = "openai" } } });
        });
        app.MapPost("/v1/chat/completions", (HttpContext c, Store s) => Generate(c, s, OpenAI, "/v1/chat/completions"));
        app.MapPost("/v1/responses", (HttpContext c, Store s) => Generate(c, s, OpenAI, "/v1/responses"));
        app.MapPost("/v1/messages", (HttpContext c, Store s) => Generate(c, s, Claude, "/v1/messages"));
        foreach (var version in new[] { "v1", "v1beta" })
        {
            var models = "/" + version + "/models";
            if (version == "v1beta") app.MapGet(models, (HttpContext c, Store s) => GeminiModels(c, s));
            foreach (var method in new[] { "generateContent", "streamGenerateContent" })
            {
                var route = models + "/{model}:" + method;
                app.MapPost(route, (HttpContext c, Store s) => Generate(c, s, Gemini, route));
            }
        }
    }

    private static async Task<IResult> GeminiModels(HttpContext c, Store s) => await Record(c, s, Gemini, false) is not null
        ? Results.Json(new { models = new[] { new { name = "models/gemini-2.5-flash", displayName = "Gemini Flash", supportedGenerationMethods = new[] { "generateContent", "streamGenerateContent" } } } })
        : Error(Gemini, 403);

    private static async Task<(RequestCapture Capture, JsonElement Json)?> Record(HttpContext c, Store s, string protocol, bool generate)
    {
        var bearer = c.Request.Headers.Authorization.ToString();
        string secret = protocol switch
        {
            Claude => c.Request.Headers["x-api-key"].ToString(),
            Gemini => c.Request.Headers["x-goog-api-key"].FirstOrDefault() ?? c.Request.Query["key"].ToString(),
            _ when bearer.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) => bearer[7..],
            _ => ""
        };
        if (!s.IsActive(secret)) return null;
        var data = await RequestCapture.Read(c);
        return s.Record(secret, protocol, c.Request.Path.Value!, generate, data.Capture) ? data : null;
    }

    private static IResult Error(string protocol, int code)
    {
        var message = code == 400 ? "Invalid generation request." : "Invalid API key.";
        var type = code == 400 ? "invalid_request_error" : "authentication_error";
        object body = protocol switch
        {
            Claude => new { type = "error", error = new { type, message } },
            Gemini => new { error = new { code, message, status = code == 400 ? "INVALID_ARGUMENT" : "PERMISSION_DENIED" } },
            _ => new { error = new { message, type, code = code == 400 ? "invalid_request" : "invalid_api_key" } }
        };
        return Results.Json(body, statusCode: code);
    }

    private static async Task<IResult> Generate(HttpContext c, Store s, string protocol, string route)
    {
        if (await Record(c, s, protocol, true) is not { } data) return Error(protocol, protocol == Gemini ? 403 : 401);
        var body = data.Json;
        if (body.ValueKind != JsonValueKind.Object) return Error(protocol, 400);
        var model = data.Capture.Model;
        if (string.IsNullOrWhiteSpace(model)) return Error(protocol, 400);
        bool stream = body.TryGetProperty("stream", out var flag) && flag.ValueKind == JsonValueKind.True;
        var suffix = Guid.NewGuid().ToString("N");
        var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (protocol == Gemini)
        {
            var result = new { candidates = new[] { new { content = new { role = "model", parts = new[] { new { text = Reply } } }, finishReason = "STOP", index = 0 } }, modelVersion = model };
            if (!route.EndsWith(":streamGenerateContent")) return Results.Json(result);
            return c.Request.Query["alt"] == "sse" ? Events([(null, result)]) : Results.Json(new[] { result });
        }
        if (protocol == Claude)
        {
            var id = "msg_" + suffix;
            var usage = new { input_tokens = 1, output_tokens = 8 };
            var block = new { type = "text", text = Reply };
            if (!stream) return Results.Json(new { id, type = "message", role = "assistant", model, content = new[] { block }, stop_reason = "end_turn", stop_sequence = (string?)null, usage });
            return Events([
                ("message_start", new { type = "message_start", message = new { id, type = "message", role = "assistant", model, content = Array.Empty<object>(), stop_reason = (string?)null, stop_sequence = (string?)null, usage } }),
                ("content_block_start", new { type = "content_block_start", index = 0, content_block = new { type = "text", text = "" } }),
                ("content_block_delta", new { type = "content_block_delta", index = 0, delta = new { type = "text_delta", text = Reply } }),
                ("content_block_stop", new { type = "content_block_stop", index = 0 }),
                ("message_delta", new { type = "message_delta", delta = new { stop_reason = "end_turn", stop_sequence = (string?)null }, usage = new { output_tokens = 8 } }),
                ("message_stop", new { type = "message_stop" })]);
        }
        if (route == "/v1/responses")
        {
            var id = "resp_" + suffix;
            var itemId = "msg_" + suffix;
            var part = new { type = "output_text", text = Reply, annotations = Array.Empty<object>() };
            var item = new { id = itemId, type = "message", status = "completed", role = "assistant", content = new[] { part } };
            object Response(string status, object[] output) => new { id, @object = "response", status, model, created_at = created, output, error = (object?)null, incomplete_details = (object?)null };
            var result = Response("completed", [item]);
            if (!stream) return Results.Json(result);
            return Events([
                ("response.created", new { type = "response.created", sequence_number = 0, response = Response("in_progress", []) }),
                ("response.output_item.added", new { type = "response.output_item.added", sequence_number = 1, output_index = 0, item = new { id = itemId, type = "message", status = "in_progress", role = "assistant", content = Array.Empty<object>() } }),
                ("response.content_part.added", new { type = "response.content_part.added", sequence_number = 2, item_id = itemId, output_index = 0, content_index = 0, part = part with { text = "" } }),
                ("response.output_text.delta", new { type = "response.output_text.delta", sequence_number = 3, item_id = itemId, output_index = 0, content_index = 0, delta = Reply }),
                ("response.output_text.done", new { type = "response.output_text.done", sequence_number = 4, item_id = itemId, output_index = 0, content_index = 0, text = Reply }),
                ("response.content_part.done", new { type = "response.content_part.done", sequence_number = 5, item_id = itemId, output_index = 0, content_index = 0, part }),
                ("response.output_item.done", new { type = "response.output_item.done", sequence_number = 6, output_index = 0, item }),
                ("response.completed", new { type = "response.completed", sequence_number = 7, response = result })]);
        }
        var chatId = "chatcmpl-" + suffix;
        if (!stream) return Results.Json(new { id = chatId, @object = "chat.completion", created, model,
            choices = new[] { new { index = 0, message = new { role = "assistant", content = Reply }, finish_reason = "stop" } },
            usage = new { prompt_tokens = 1, completion_tokens = 8, total_tokens = 9 } });
        object Chunk(object delta, string? finish) => new { id = chatId, @object = "chat.completion.chunk", created, model, choices = new[] { new { index = 0, delta, finish_reason = finish } } };
        return Events([(null, Chunk(new { role = "assistant", content = Reply }, null)), (null, Chunk(new { }, "stop"))], done: true);
    }

    private static IResult Events((string? Name, object Data)[] events, bool done = false)
    {
        var frames = events.Select(e => (e.Name is null ? "" : $"event: {e.Name}\n") + $"data: {JsonSerializer.Serialize(e.Data)}\n\n");
        return Results.Text(string.Concat(frames) + (done ? "data: [DONE]\n\n" : ""), "text/event-stream");
    }
}
