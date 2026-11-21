using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Data.Sqlite;

namespace KeyFlare.Pages;

public sealed class AdminModel(Store store) : PageModel
{
    public record KeyRow(long Id, string Label, bool Active);
    public record ChannelRow(long Id, string Kind, string Host);
    public record HitRow(Hit Hit, string Alerts);
    public List<KeyRow> Keys { get; } = [];
    public List<ChannelRow> Channels { get; } = [];
    public List<HitRow> Hits { get; } = [];
    public List<IssuedKey> Issued { get; private set; } = [];
    public string? Error { get; private set; }
    public int CurrentPage { get; private set; }
    public bool HasMore { get; private set; }

    public void OnGet(int p = 1)
    {
        CurrentPage = Math.Max(1, p);
        using var db = store.Open();
        using var keys = Store.Command(db, "SELECT id,label,active FROM keys ORDER BY id DESC");
        using (var r = keys.ExecuteReader())
            while (r.Read()) Keys.Add(new(r.GetInt64(0), r.GetString(1), r.GetBoolean(2)));
        using var channels = Store.Command(db, "SELECT id,kind,url FROM channels ORDER BY id");
        using (var r = channels.ExecuteReader())
            while (r.Read()) Channels.Add(new(r.GetInt64(0), r.GetString(1), new Uri(r.GetString(2)).Authority));
        using var hits = Store.Command(db, """
            SELECT h.id,h.key_id,h.time,h.protocol,h.endpoint,
                (SELECT group_concat(c.kind || '#' || c.id || ' ' || CASE d.state WHEN 'sent' THEN '已送达' WHEN 'failed' THEN '失败' ELSE '待发送' END, '，')
                 FROM deliveries d JOIN channels c ON c.id=d.channel_id WHERE d.hit_id=h.id)
            FROM hits h ORDER BY id DESC LIMIT 51 OFFSET $offset
            """, ("$offset", ((long)CurrentPage - 1) * 50));
        using var reader = hits.ExecuteReader();
        while (reader.Read())
        {
            if (Hits.Count == 50) { HasMore = true; break; }
            Hits.Add(new(new(reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3), reader.GetString(4)), reader.IsDBNull(5) ? "—" : reader.GetString(5)));
        }
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

    public IActionResult OnPostRevoke(long id) => Change("UPDATE keys SET active=0 WHERE id=$id", id);
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
