# KeyFlare

**极致轻量的通用 LLM API 蜜罐。** 在授权测试中，把客户端的 API 地址指向 KeyFlare，给不同投放位置分配独立的诱饵 Key。客户端用这些 Key 查询模型或发起生成请求时，你可以在管理面板里看到哪把 Key 被使用、调用了什么接口、请求来自哪里。

KeyFlare 使用 ASP.NET Core 11 和 SQLite，一个应用进程同时提供模拟 API、管理面板和告警投递。三种协议都在本地返回固定响应，无需配置上游模型或供应商账号。诱饵 Key 的观测范围是发往此实例的请求。

## 部署

1. 在目标机器上安装对应平台的 **ASP.NET Core Runtime 11.0（x64）**。运行 `dotnet --list-runtimes`，确认列表中有 `Microsoft.AspNetCore.App 11.0.x`。[.NET 11 下载页](https://dotnet.microsoft.com/download/dotnet/11.0)
2. 打开 [Publish 工作流](https://github.com/MopeLab/KeyFlare/actions/workflows/publish.yml)，选择一次成功运行，从 **Artifacts** 下载 `KeyFlare-linux-x64` 或 `KeyFlare-win-x64`。解压一次，保留整个目录，包括 `wwwroot` 和 SQLite 本机库。
3. 在含有 `KeyFlare.dll` 的目录打开终端。Linux 和 Windows 均可用以下命令启动：

   ```shell
   dotnet KeyFlare.dll --urls http://127.0.0.1:5000
   ```

4. 首次启动时，从控制台的 `KeyFlare AdminPasswd:` 一行取得初始密码。打开 [本机管理面板](http://127.0.0.1:5000/admin)，输入密码即可登录。以后启动会沿用数据库中的密码，面板底部支持改密。

该示例监听本机回环地址。向其他机器提供 API 时，可通过 HTTPS 反向代理转发到这个地址。客户端应使用代理的公开域名，管理员从同一域名的 `/admin` 登录。

### Cloudflare CDN → Nginx → KeyFlare：透传客户端 IP

以下示例假设 Cloudflare 直接回源到 Nginx，Nginx 与 KeyFlare 位于同一台机器，KeyFlare 监听 `127.0.0.1:5000`。配置完成后，可在调用详情的 **原始请求头 → X-Real-IP** 查看访客公网 IP。当前版本的「连接来源 IP」和「客户端 IP」在此拓扑下均显示 Nginx 的回环地址。

1. 为公开域名启用 Cloudflare 代理（橙云），保留 `CF-Connecting-IP` 请求头。要保留原始 IPv6 地址，请将 **Pseudo IPv4** 设为 **Off** 或 **Add Header**；**Overwrite Headers** 会将该头替换为伪 IPv4。[Cloudflare 请求头说明](https://developers.cloudflare.com/fundamentals/reference/http-headers/#cf-connecting-ip)
2. 确认 Nginx 启用了 `http_realip_module`（可用 `nginx -V 2>&1` 查看编译参数）。创建 `/etc/nginx/cloudflare-realip.conf`，将 Cloudflare 官方 IPv4、IPv6 列表中的**每一个网段**各写成一行 `set_real_ip_from 网段;`。下面的 Bash 命令会生成并安装该文件；官方列表变更时重新生成并重载 Nginx。[IPv4 网段](https://www.cloudflare.com/ips-v4/) · [IPv6 网段](https://www.cloudflare.com/ips-v6/) · [Nginx Real IP 模块](https://nginx.org/en/docs/http/ngx_http_realip_module.html)

   ```bash
   (
     set -e
     tmp=$(mktemp -d)
     trap 'rm -rf "$tmp"' EXIT
     for family in v4 v6; do
       curl -fsSL "https://www.cloudflare.com/ips-$family/" -o "$tmp/$family"
       test -s "$tmp/$family"
     done
     awk 'NF {printf "set_real_ip_from %s;\n", $1}' "$tmp/v4" "$tmp/v6" > "$tmp/realip.conf"
     sudo install -m 0644 "$tmp/realip.conf" /etc/nginx/cloudflare-realip.conf
   )
   ```

3. 在该域名已有的 HTTPS `server` 块中加入以下指令。若已有 `location /`，将代理指令合并进去。只有来自上述可信网段的连接才能通过 `CF-Connecting-IP` 改写 `$remote_addr`。转发时覆盖 `X-Real-IP` 和 `X-Forwarded-For`，避免把访客自行填写的地址当成观测结果。

   ```nginx
   include /etc/nginx/cloudflare-realip.conf;
   real_ip_header CF-Connecting-IP;

   location / {
       proxy_pass http://127.0.0.1:5000;
       proxy_set_header Host $host;
       proxy_set_header X-Real-IP $remote_addr;
       proxy_set_header X-Forwarded-For $remote_addr;
   }
   ```

4. 运行 `sudo nginx -t && sudo nginx -s reload`。从已知公网出口的客户端，通过**启用了 Cloudflare 代理的域名**发起一次模型列表请求：

   ```bash
   curl 'https://你的域名/v1/models' -H 'Authorization: Bearer <诱饵Key>'
   ```

   打开管理面板中的对应调用，核对「原始请求头」里的 `X-Real-IP` 是否与客户端公网出口一致。如果这里显示 Cloudflare 边缘 IP，检查回源节点是否在已加载的可信网段内，以及 Cloudflare 是否传入了 `CF-Connecting-IP`。

### 从源码运行

在安装了 .NET 11 SDK 的环境中，进入仓库根目录后执行：

```shell
dotnet run --project KeyFlare.csproj --urls http://127.0.0.1:5000
```

## 配置

| 环境变量 | 用途 | 示例 |
| --- | --- | --- |
| `ASPNETCORE_URLS` | Kestrel 监听地址，也可用启动参数 `--urls` 指定 | `http://127.0.0.1:5000` |
| `KeyFlare__Database` | SQLite 文件路径，默认是应用内容根目录下的 `App_Data/keyflare.db` | Linux：`/var/lib/keyflare/keyflare.db`；Windows：`D:\KeyFlareData\keyflare.db` |

Linux 可用 `export KeyFlare__Database=/var/lib/keyflare/keyflare.db`，PowerShell 可用 `$env:KeyFlare__Database = 'D:\KeyFlareData\keyflare.db'`，再启动应用。运行账户需要数据库目录的读写权限。更新发布文件时沿用同一数据库路径，即可继续使用已有密钥、管理密码、告警渠道和调用记录。数据库包含完整诱饵 Key、原始请求头及正文，应限制文件访问权限。

### 诱饵密钥与告警

在管理面板的 **诱饵密钥** 中填写投放标签（例如 `staging-agent`），选择密钥外观和数量，再点击 **生成密钥**。点击列表行可查看完整 Key、创建时间、调用次数和逐条请求详情。吊销后该 Key 的请求会被拒绝，恢复后可继续使用原值。

在 **告警渠道** 中选择接收方式并填写地址。远端渠道使用 HTTPS，本机联调也支持回环 HTTP 地址。

| 渠道 | 接收地址与行为 |
| --- | --- |
| Bark | 填写包含设备 Key 的推送地址，如 `https://api.day.app/<设备Key>`。消息包含密钥编号、协议、接口和命中时间。 |
| Webhook | 填写接收 JSON POST 的地址。请求包含 `id`、`keyId`、`time`、`protocol` 和 `endpoint`，接收端返回 HTTP 2xx 即表示送达。 |

模型列表查询只记录命中。生成请求命中启用的诱饵 Key 时，会为当时配置的每个渠道排队告警。投递失败会自动重试，最多尝试四次，可在调用记录中查看结果。

## 客户端接入

以下路径相对于 KeyFlare 的服务地址。支持的生成接口均提供固定文本和流式模拟响应。

| 协议 | 模型列表 | 生成接口 | 诱饵 Key 的位置 |
| --- | --- | --- | --- |
| OpenAI | `GET /v1/models` | `POST /v1/chat/completions`、`POST /v1/responses` | `Authorization: Bearer <Key>` |
| Anthropic | `GET /v1/models` | `POST /v1/messages` | `x-api-key: <Key>` |
| Google Gemini | `GET /v1beta/models` | `POST /v1beta/models/{model}:generateContent`、`:streamGenerateContent` | `x-goog-api-key: <Key>` 或查询参数 `key` |

Gemini 同时支持 `/v1/models` 前缀。OpenAI 兼容客户端通常将 Base URL 设为 `https://你的域名/v1`。可先用下面的 Bash 示例验证一次生成调用，将占位符替换为面板生成的 Key：

```bash
curl http://127.0.0.1:5000/v1/chat/completions \
  -H 'Authorization: Bearer <诱饵Key>' \
  -H 'Content-Type: application/json' \
  -d '{"model":"gpt-4o","messages":[{"role":"user","content":"hello"}]}'
```

收到模拟回复后，在面板 **调用记录** 中点击刷新，再打开对应记录，可查看请求模型、输入文本、User-Agent、请求头、原始请求体及来源 IP。Cloudflare 边缘节点直连应用时，会分别记录连接地址和解析出的客户端地址。如果前面还有自建反向代理，应用看到的直连来源是该代理。
