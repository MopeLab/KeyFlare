using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Authentication;
using System.Security.Claims;
using System.Text;
using Microsoft.Data.Sqlite;

namespace KeyFlare.Pages;

public sealed class AdminModel(Store store, AdminPassword credentials) : PageModel
{
    public record KeyRow(long Id, string Label, bool Active, string Secret);
    public record ChannelRow(long Id, string Kind, string Host);
    public record HitRow(Hit Hit, string Alerts);
    public record HitList(List<HitRow> Rows, int Page, bool HasMore, long? KeyId, long Count);
    public record KeyDetails(KeyRow Key, string Created, string? LastHit, HitList History);
    public record HitDetails(Hit Hit, string Method, string Target, string UserAgent, string Model, string Input,
        string Headers, string Body, string BodyFormat, string Peer, string Client);
    public List<KeyRow> Keys { get; } = [];
    public List<ChannelRow> Channels { get; } = [];
    public HitList History { get; private set; } = new([], 1, false, null, 0);
    public List<IssuedKey> Issued { get; private set; } = [];
    public string? Error { get; private set; }

    public void OnGet(int p = 1)
    {
        using var db = store.Open();
        History = ReadHistory(db, p);
        using var keys = Store.Command(db, "SELECT id,label,active,secret FROM keys ORDER BY id DESC");
        using (var r = keys.ExecuteReader())
            while (r.Read()) Keys.Add(new(r.GetInt64(0), r.GetString(1), r.GetBoolean(2), r.GetString(3)));
        using var channels = Store.Command(db, "SELECT id,kind,url FROM channels ORDER BY id");
        using (var r = channels.ExecuteReader())
            while (r.Read()) Channels.Add(new(r.GetInt64(0), r.GetString(1), new Uri(r.GetString(2)).Authority));
    }

    private static HitList ReadHistory(SqliteConnection db, int page, long? keyId = null)
    {
        var filter = keyId.HasValue ? "WHERE h.key_id=$key" : "";
        using var count = Store.Command(db, $"SELECT COUNT(*) FROM hits h {filter}");
        if (keyId.HasValue) count.Parameters.AddWithValue("$key", keyId.Value);
        var total = (long)count.ExecuteScalar()!;
        page = (int)Math.Clamp(page, 1L, Math.Max(1L, (total + 49) / 50));
        using var hits = Store.Command(db, $"""
            SELECT h.id,h.key_id,h.time,h.protocol,h.endpoint,
                (SELECT group_concat(c.kind || '#' || c.id || ' ' || CASE d.state WHEN 'sent' THEN '已送达' WHEN 'failed' THEN '失败' ELSE '待发送' END, '，')
                 FROM deliveries d JOIN channels c ON c.id=d.channel_id WHERE d.hit_id=h.id)
            FROM hits h {filter} ORDER BY h.id DESC LIMIT 50 OFFSET $offset
            """, ("$offset", ((long)page - 1) * 50));
        if (keyId.HasValue) hits.Parameters.AddWithValue("$key", keyId.Value);
        using var reader = hits.ExecuteReader();
        List<HitRow> rows = [];
        while (reader.Read())
            rows.Add(new(new(reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3), reader.GetString(4)), reader.IsDBNull(5) ? "—" : reader.GetString(5)));
        return new(rows, page, total > (long)page * 50, keyId, total);
    }

    public IActionResult OnGetHistory(int p = 1)
    {
        using var db = store.Open();
        return Partial("_HitList", ReadHistory(db, p));
    }

    public IActionResult OnGetKey(long id, int p = 1)
    {
        using var db = store.Open();
        using var command = Store.Command(db, """
            SELECT id,label,active,secret,created,(SELECT time FROM hits WHERE key_id=$id ORDER BY id DESC LIMIT 1)
            FROM keys WHERE id=$id
            """, ("$id", id));
        using var r = command.ExecuteReader();
        if (!r.Read()) return NotFound();
        var key = new KeyRow(id, r.GetString(1), r.GetBoolean(2), r.GetString(3));
        return Partial("_KeyDetails", new KeyDetails(key, r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5), ReadHistory(db, p, id)));
    }

    public IActionResult OnGetHit(long id)
    {
        using var db = store.Open();
        using var command = Store.Command(db, """
            SELECT id,key_id,time,protocol,endpoint,method,target,ua,coalesce(model,''),input,headers,body,
                coalesce(peer_ip,''),coalesce(client_ip,'') FROM hits WHERE id=$id
            """, ("$id", id));
        using var r = command.ExecuteReader();
        if (!r.Read()) return NotFound();
        var bytes = (byte[])r[11];
        string body, format = "UTF-8";
        try { body = new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException) { body = Convert.ToBase64String(bytes); format = "Base64"; }
        var hit = new Hit(id, r.GetInt64(1), r.GetString(2), r.GetString(3), r.GetString(4));
        return Partial("_HitDetails", new HitDetails(hit, r.GetString(5), r.GetString(6), r.GetString(7), r.GetString(8),
            r.GetString(9), r.GetString(10), body, format, r.GetString(12), r.GetString(13)));
    }

    public IActionResult OnPostIssue(string? label, int count, string? kind)
    {
        var prefix = kind switch { "OpenAI" => "sk-", "Anthropic" => "sk-ant-", "Gemini" => "AIza", _ => null };
        if (prefix is null || count is < 1 or > 100 || string.IsNullOrWhiteSpace(label) || label.Length > 100)
            return Invalid("请填写 1–100 字的标签，并选择 1–100 个密钥。");
        Issued = store.Issue(label.Trim(), count, prefix);
        OnGet();
        return Page();
    }

    public IActionResult OnPostRevoke(long id, bool confirmed) => confirmed
        ? Change("UPDATE keys SET active=0 WHERE id=$id", id) : Invalid("请确认吊销密钥。");
    public IActionResult OnPostRestore(long id) => Change("UPDATE keys SET active=1 WHERE id=$id", id);
    public async Task<IActionResult> OnPostLogoutAsync()
    {
        await HttpContext.SignOutAsync();
        return RedirectToPage("/Login");
    }
    public async Task<IActionResult> OnPostPasswordAsync(string? current, string? replacement, string? confirmation)
    {
        if (string.IsNullOrWhiteSpace(replacement) || replacement.Length > 1024 || replacement != confirmation)
            return Invalid("请填写新密码，并确保两次输入一致。");
        if (!credentials.Change(current, replacement, User.FindFirstValue(ClaimTypes.NameIdentifier)))
            return Invalid("当前管理密码有误。");
        await HttpContext.SignOutAsync();
        return RedirectToPage("/Login", new { changed = true });
    }
    public IActionResult OnPostRemove(long id) => Change("DELETE FROM channels WHERE id=$id", id);

    public IActionResult OnPostChannel(string? kind, string? url)
    {
        if (kind is not ("Bark" or "Webhook") || url?.Length > 2048 || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback)) || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0)
            return Invalid("请输入 HTTPS 接收地址。本机测试也可使用回环 HTTP 地址。");
        using var db = store.Open();
        using var insert = Store.Command(db, "INSERT INTO channels(kind,url) VALUES($kind,$url)", ("$kind", kind), ("$url", uri.AbsoluteUri));
        insert.ExecuteNonQuery();
        return RedirectToPage();
    }

    private IActionResult Change(string sql, long id)
    {
        using var db = store.Open();
        using var command = Store.Command(db, sql, ("$id", id));
        command.ExecuteNonQuery();
        return RedirectToPage();
    }

    private IActionResult Invalid(string message)
    {
        Error = message;
        Response.StatusCode = 400;
        OnGet();
        return Page();
    }
}
