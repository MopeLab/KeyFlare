using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace KeyFlare;

public sealed class AlertWorker(Store store, ILogger<AlertWorker> logger) : BackgroundService
{
    private sealed record Delivery(Hit Hit, long Channel, string Kind, string Url, long Attempt);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
        { Timeout = TimeSpan.FromSeconds(8), MaxResponseContentBufferSize = 65536 };
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (Claim() is { } job)
                {
                    bool delivered = await Send(client, job, stoppingToken);
                    Finish(job, delivered);
                    continue;
                }
            }
            catch (SqliteException ex) { logger.LogWarning("告警队列暂不可用，SQLite 错误码 {Code}", ex.SqliteErrorCode); }
            await Task.Delay(1000, stoppingToken);
        }
    }

    private Delivery? Claim()
    {
        using var db = store.Open();
        using var tx = db.BeginTransaction();
        using var find = Store.Command(db, """
            SELECT h.id,h.key_id,h.time,h.protocol,h.endpoint,c.id,c.kind,c.url,d.attempts+1
            FROM deliveries d JOIN hits h ON h.id=d.hit_id JOIN channels c ON c.id=d.channel_id
            WHERE d.state='pending' AND d.next_try<=unixepoch() ORDER BY d.hit_id,d.channel_id LIMIT 1
            """);
        find.Transaction = tx;
        Delivery job;
        using (var r = find.ExecuteReader())
        {
            if (!r.Read()) return null;
            job = new(new(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3), r.GetString(4)), r.GetInt64(5), r.GetString(6), r.GetString(7), r.GetInt64(8));
        }
        using var claim = Store.Command(db, "UPDATE deliveries SET attempts=attempts+1,next_try=unixepoch()+60 WHERE hit_id=$h AND channel_id=$c", ("$h", job.Hit.Id), ("$c", job.Channel));
        claim.Transaction = tx;
        claim.ExecuteNonQuery();
        tx.Commit();
        return job;
    }

    private static async Task<bool> Send(HttpClient client, Delivery job, CancellationToken token)
    {
        var hit = job.Hit;
        object payload = job.Kind == "Bark"
            ? new { title = "KeyFlare 诱饵密钥命中", body = $"Key #{hit.KeyId} · {hit.Protocol} · {hit.Endpoint}\n{hit.Time}", group = "KeyFlare" }
            : hit;
        try
        {
            using var response = await client.PostAsJsonAsync(job.Url, payload, token);
            if (!response.IsSuccessStatusCode || job.Kind != "Bark") return response.IsSuccessStatusCode;
            var result = await response.Content.ReadFromJsonAsync<JsonElement>(token);
            return result.ValueKind == JsonValueKind.Object && result.TryGetProperty("code", out var code)
                && code.ValueKind == JsonValueKind.Number && code.TryGetInt32(out var value) && value == 200;
        }
        catch (HttpRequestException) { return false; }
        catch (JsonException) { return false; }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return false; }
    }

    private void Finish(Delivery job, bool success)
    {
        using var db = store.Open();
        using var update = Store.Command(db, """
            UPDATE deliveries SET state=$state,next_try=unixepoch()+$delay
            WHERE hit_id=$h AND channel_id=$c AND attempts=$attempt
            """, ("$state", success ? "sent" : job.Attempt >= 4 ? "failed" : "pending"), ("$delay", 10 * job.Attempt),
            ("$h", job.Hit.Id), ("$c", job.Channel), ("$attempt", job.Attempt));
        update.ExecuteNonQuery();
    }
}
