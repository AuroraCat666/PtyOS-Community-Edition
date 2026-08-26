using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RPGReServer.Auth;
using RPGReServer.Tcp;
using AuthStatusCode = RPGReServer.Auth.StatusCode;

namespace RPGReServer.Http;

public static class HttpService
{
    private const int MaxAvatarBytes = 2 * 1024 * 1024;
    private static readonly ConcurrentDictionary<string, WebSession> Sessions = new();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private sealed record WebSession(string Username, DateTimeOffset ExpiresAt);
    private sealed record Credentials(string Username, string Password);
    private sealed record UserAction(string Username, bool Banned);
    private sealed record RegisterRequest(string Username, string Password, string Email, string Code);
    private sealed record EmailVerifyRequest(string Username, string Code);
    private sealed record ForgotPasswordRequest(string Email);
    private sealed record ResetPasswordRequest(string Email, string Code, string NewPassword);
    private sealed record CreateUserRequest(string Username, string Password, string Email);
    private sealed record AnnouncementRequest(string Type, string Title, string Content);

    public static async Task Run(AccountStore accounts, RoomManager rooms, AnnouncementStore announcements, MailService mail, ServerConfig config, CancellationToken ct)
    {
        var listener = new HttpListener();
        string prefix = $"http://{config.Host}:{config.HttpPort}/";
        listener.Prefixes.Add(prefix);
        listener.Start();
        Log.Info($"[HTTP] 社区服务已启动: {config.PublicBaseUrl}");
        Log.Info($"[Mail] SMTP {(mail.Enabled ? "已配置" : "未配置，邮件功能不可用")}");

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var context = await listener.GetContextAsync().WaitAsync(ct);
                _ = Task.Run(() => Handle(context, accounts, rooms, announcements, mail, config));
            }
        }
        catch (OperationCanceledException) { }
        finally { listener.Stop(); }
    }

    private static async Task Handle(HttpListenerContext ctx, AccountStore accounts, RoomManager rooms, AnnouncementStore announcements, MailService mail, ServerConfig config)
    {
        try
        {
            string path = ctx.Request.Url?.AbsolutePath.TrimEnd('/') ?? "";
            if (path == "") path = "/";
            Log.Info($"[HTTP] {ctx.Request.HttpMethod} {path}");

            if (path == "/")
            {
                WriteStatic(ctx, "index.html", "text/html; charset=utf-8");
                return;
            }
            if (path is "/index" or "/community" or "/admin")
            {
                Redirect(ctx, "/");
                return;
            }
            if (path == "/assets/app.css") { WriteStatic(ctx, "app.css", "text/css; charset=utf-8"); return; }
            if (path == "/assets/app.js") { WriteStatic(ctx, "app.js", "text/javascript; charset=utf-8"); return; }
            if (path.StartsWith("/avatars/", StringComparison.Ordinal))
            {
                WriteAvatar(ctx, Path.GetFileName(path), config);
                return;
            }

            if (path == "/api")
            {
                WriteJson(ctx, new Dictionary<string, object>
                {
                    ["status"] = "OK", ["AuthMode"] = "verify", ["TestMode"] = config.TestMode,
                    ["AuthVersionMode"] = "public", ["AuthVersion"] = config.ApiVersion
                });
                return;
            }
            if (path == "/api/manifest.json") { WriteManifest(ctx, config); return; }
            if (path == "/api/userlogin")
            {
                await HandleGameLogin(ctx, accounts, ctx.Request.QueryString["username"], ctx.Request.QueryString["password"]);
                return;
            }
            if (path == "/api/userverify")
            {
                await HandleGameVerify(ctx, accounts, ctx.Request.QueryString["username"], ctx.Request.QueryString["verifytoken"]);
                return;
            }

            if (path == "/api/community/register" && ctx.Request.HttpMethod == "POST")
            {
                var body = await ReadJson<RegisterRequest>(ctx);
                if (body == null || !AccountStore.IsValidUsername(body.Username) || body.Password.Length is < 8 or > 128)
                {
                    WriteJson(ctx, new { ok = false, message = "用户名需为 3-24 位字母、数字、下划线或短横线，密码至少 8 位" }, 400);
                    return;
                }
                if (!IsValidQqEmail(body.Email))
                {
                    WriteJson(ctx, new { ok = false, message = "仅支持 QQ 邮箱（@qq.com）注册" }, 400);
                    return;
                }
                if (accounts.FindByEmail(body.Email) != null)
                {
                    WriteJson(ctx, new { ok = false, message = "该邮箱已被注册" }, 409);
                    return;
                }
                string codeKey = $"reg:{body.Email.ToLowerInvariant()}";
                if (!mail.VerifyCode(codeKey, body.Code))
                {
                    WriteJson(ctx, new { ok = false, message = "邮箱验证码错误或已过期" }, 400);
                    return;
                }
                if (!accounts.Create(body.Username, body.Password))
                {
                    WriteJson(ctx, new { ok = false, message = "用户名已存在" }, 409);
                    return;
                }
                accounts.SetEmail(body.Username, body.Email);
                accounts.SetEmailVerified(body.Username);
                CreateSession(ctx, body.Username);
                WriteJson(ctx, new { ok = true, message = "注册成功", user = PublicUser(accounts.Get(body.Username)!) });
                return;
            }

            if (path == "/api/community/send-code" && ctx.Request.HttpMethod == "POST")
            {
                var body = await ReadJson<EmailVerifyRequest>(ctx);
                string email = body?.Username ?? "";
                if (!IsValidQqEmail(email))
                {
                    WriteJson(ctx, new { ok = false, message = "仅支持 QQ 邮箱（@qq.com）" }, 400);
                    return;
                }
                int wait = mail.RemainingCooldown(email.ToLowerInvariant());
                if (wait > 0)
                {
                    WriteJson(ctx, new { ok = false, message = $"发送过于频繁，请 {wait} 秒后再试" }, 429);
                    return;
                }
                if (!mail.Enabled)
                {
                    WriteJson(ctx, new { ok = false, message = "服务器未配置邮件服务" }, 500);
                    return;
                }
                string code = mail.CreateCode($"reg:{email.ToLowerInvariant()}");
                try
                {
                    await mail.SendCodeAsync(email.ToLowerInvariant(), "verify", email, "PtyOS 社区注册验证码", "用于注册 PtyOS 社区账号", code);
                }
                catch (Exception e)
                {
                    Log.Error($"[Mail] 发送注册验证码失败: {e.Message}");
                    WriteJson(ctx, new { ok = false, message = "邮件发送失败，请稍后再试" }, 500);
                    return;
                }
                WriteJson(ctx, new { ok = true, message = "验证码已发送到邮箱" });
                return;
            }

            if (path == "/api/community/forgot-password" && ctx.Request.HttpMethod == "POST")
            {
                var body = await ReadJson<ForgotPasswordRequest>(ctx);
                if (body == null || !IsValidQqEmail(body.Email) || accounts.FindByEmail(body.Email) == null)
                {
                    WriteJson(ctx, new { ok = false, message = "该邮箱未绑定任何账号" }, 404);
                    return;
                }
                int wait = mail.RemainingCooldown(body.Email.ToLowerInvariant());
                if (wait > 0)
                {
                    WriteJson(ctx, new { ok = false, message = $"发送过于频繁，请 {wait} 秒后再试" }, 429);
                    return;
                }
                if (!mail.Enabled)
                {
                    WriteJson(ctx, new { ok = false, message = "服务器未配置邮件服务" }, 500);
                    return;
                }
                string code = mail.CreateCode($"reset:{body.Email.ToLowerInvariant()}");
                try
                {
                    await mail.SendCodeAsync(body.Email.ToLowerInvariant(), "findPassword", body.Email, "PtyOS 社区找回密码", "用于重置 PtyOS 社区账号密码", code);
                }
                catch (Exception e)
                {
                    Log.Error($"[Mail] 发送找回密码验证码失败: {e.Message}");
                    WriteJson(ctx, new { ok = false, message = "邮件发送失败，请稍后再试" }, 500);
                    return;
                }
                WriteJson(ctx, new { ok = true, message = "验证码已发送到邮箱" });
                return;
            }

            if (path == "/api/community/reset-password" && ctx.Request.HttpMethod == "POST")
            {
                var body = await ReadJson<ResetPasswordRequest>(ctx);
                if (body == null || body.NewPassword.Length is < 8 or > 128)
                {
                    WriteJson(ctx, new { ok = false, message = "新密码至少 8 位" }, 400);
                    return;
                }
                var account = string.IsNullOrEmpty(body.Email) ? null : accounts.FindByEmail(body.Email);
                if (account == null)
                {
                    WriteJson(ctx, new { ok = false, message = "该邮箱未绑定任何账号" }, 404);
                    return;
                }
                if (!mail.VerifyCode($"reset:{account.Email.ToLowerInvariant()}", body.Code))
                {
                    WriteJson(ctx, new { ok = false, message = "验证码错误或已过期" }, 400);
                    return;
                }
                accounts.SetPassword(account.Username, body.NewPassword);
                WriteJson(ctx, new { ok = true, message = "密码已重置，请重新登录" });
                return;
            }

            if (path == "/api/community/login" && ctx.Request.HttpMethod == "POST")
            {
                var body = await ReadJson<Credentials>(ctx);
                if (body == null || !accounts.VerifyPassword(body.Username, body.Password, out var account))
                {
                    WriteJson(ctx, new { ok = false, message = "用户名或密码错误" }, 401);
                    return;
                }
                if (account!.Banned)
                {
                    WriteJson(ctx, new { ok = false, message = "此账号已被封禁" }, 403);
                    return;
                }
                CreateSession(ctx, account.Username);
                WriteJson(ctx, new { ok = true, user = PublicUser(account) });
                return;
            }

            if (path == "/api/community/logout" && ctx.Request.HttpMethod == "POST")
            {
                if (ctx.Request.Cookies["rpgr_session"] is { } cookie) Sessions.TryRemove(cookie.Value, out _);
                ctx.Response.Cookies.Add(new Cookie("rpgr_session", "") { Path = "/", Expires = DateTime.UtcNow.AddDays(-1), HttpOnly = true });
                WriteJson(ctx, new { ok = true });
                return;
            }

            if (path == "/api/community/me")
            {
                var account = GetSessionAccount(ctx, accounts);
                WriteJson(ctx, new { authenticated = account != null, user = account == null ? null : PublicUser(account) });
                return;
            }

            if (path == "/api/community/avatar" && ctx.Request.HttpMethod == "POST")
            {
                var account = RequireAccount(ctx, accounts);
                if (account == null) return;
                await HandleAvatarUpload(ctx, accounts, account, config);
                return;
            }

            if (path == "/api/community/status")
            {
                WriteJson(ctx, new { serverName = config.ServerName, motd = config.ServerMotd, roomState = rooms.GetCommunitySnapshot(), memberCount = accounts.GetAll().Count });
                return;
            }

            if (path == "/api/community/admin/users")
            {
                if (RequireAdmin(ctx, accounts) == null) return;
                WriteJson(ctx, new { users = accounts.GetAll().Select(AdminUser) });
                return;
            }

            if (path == "/api/community/admin/rooms")
            {
                if (RequireAdmin(ctx, accounts) == null) return;
                WriteJson(ctx, rooms.GetCommunitySnapshot());
                return;
            }

            if (path == "/api/community/admin/ban" && ctx.Request.HttpMethod == "POST")
            {
                if (RequireAdmin(ctx, accounts) == null) return;
                var body = await ReadJson<UserAction>(ctx);
                bool ok = body != null && accounts.SetBanned(body.Username, body.Banned);
                WriteJson(ctx, new { ok, message = ok ? (body!.Banned ? "用户已封禁" : "用户已解封") : "用户不存在或不能操作管理员" }, ok ? 200 : 400);
                return;
            }

            if (path == "/api/community/admin/delete" && ctx.Request.HttpMethod == "POST")
            {
                if (RequireAdmin(ctx, accounts) == null) return;
                var body = await ReadJson<UserAction>(ctx);
                bool ok = body != null && accounts.Delete(body.Username);
                WriteJson(ctx, new { ok, message = ok ? "用户已删除" : "用户不存在或不能删除管理员" }, ok ? 200 : 400);
                return;
            }

            if (path == "/api/community/admin/create-user" && ctx.Request.HttpMethod == "POST")
            {
                if (RequireAdmin(ctx, accounts) == null) return;
                var body = await ReadJson<CreateUserRequest>(ctx);
                if (body == null || !AccountStore.IsValidUsername(body.Username) || body.Password.Length is < 8 or > 128)
                {
                    WriteJson(ctx, new { ok = false, message = "用户名需为 3-24 位字母、数字、下划线或短横线，密码至少 8 位" }, 400);
                    return;
                }
                if (!string.IsNullOrEmpty(body.Email) && !IsValidQqEmail(body.Email))
                {
                    WriteJson(ctx, new { ok = false, message = "仅支持 QQ 邮箱（@qq.com）" }, 400);
                    return;
                }
                if (!accounts.Create(body.Username, body.Password))
                {
                    WriteJson(ctx, new { ok = false, message = "用户名已存在" }, 409);
                    return;
                }
                if (!string.IsNullOrEmpty(body.Email))
                {
                    accounts.SetEmail(body.Username, body.Email);
                    accounts.SetEmailVerified(body.Username);
                }
                WriteJson(ctx, new { ok = true, message = $"用户 {body.Username} 已创建" });
                return;
            }

            if (path == "/api/community/announcements")
            {
                WriteJson(ctx, new { announcements = announcements.GetAll() });
                return;
            }

            if (path == "/api/community/admin/announcements" && ctx.Request.HttpMethod == "POST")
            {
                if (RequireAdmin(ctx, accounts) == null) return;
                var body = await ReadJson<AnnouncementRequest>(ctx);
                if (body == null || string.IsNullOrWhiteSpace(body.Title) || string.IsNullOrWhiteSpace(body.Content) ||
                    body.Type is not ("maintenance" or "update" or "other"))
                {
                    WriteJson(ctx, new { ok = false, message = "请填写标题与内容，公告类型仅支持维护/更新/其他" }, 400);
                    return;
                }
                var record = announcements.Publish(body.Type, body.Title.Trim(), body.Content.Trim());
                _ = Task.Run(async () => await SendAnnouncementMail(mail, accounts, record));
                WriteJson(ctx, new { ok = true, message = "公告已发布并开始群发邮件" });
                return;
            }

            if (path == "/api/upload" && ctx.Request.HttpMethod == "POST") { await HandleChartUpload(ctx, config); return; }
            if (path == "/api/download" && ctx.Request.HttpMethod == "GET") { HandleChartDownload(ctx, config); return; }

            WriteJson(ctx, new { status = "NOT_FOUND" }, 404);
        }
        catch (Exception e)
        {
            Log.Error($"[HTTP] 处理请求异常: {e}");
            try { WriteJson(ctx, new { status = "ERROR", message = "服务器内部错误" }, 500); } catch { }
        }
    }

    private static object PublicUser(AccountRecord account) => new
    {
        username = account.Username,
        isAdmin = account.IsAdmin,
        avatarUrl = string.IsNullOrEmpty(account.AvatarFile) ? "" : "/avatars/" + account.AvatarFile,
        createdAt = account.CreatedAt,
        lastLoginAt = account.LastLoginAt,
        email = account.Email,
        emailVerified = account.EmailVerified
    };

    private static object AdminUser(AccountRecord account) => new
    {
        username = account.Username,
        isAdmin = account.IsAdmin,
        banned = account.Banned,
        avatarUrl = string.IsNullOrEmpty(account.AvatarFile) ? "" : "/avatars/" + account.AvatarFile,
        createdAt = account.CreatedAt,
        lastLoginAt = account.LastLoginAt,
        email = account.Email,
        emailVerified = account.EmailVerified
    };

    private static bool IsValidQqEmail(string email)
        => !string.IsNullOrEmpty(email) && email.Length <= 320 &&
           email.EndsWith("@qq.com", StringComparison.OrdinalIgnoreCase) &&
           email.Split('@')[0].Length >= 1;

    private static string AnnouncementTypeName(string type) => type switch
    {
        "maintenance" => "维护公告",
        "update" => "更新公告",
        _ => "其他公告"
    };

    private static async Task SendAnnouncementMail(MailService mail, AccountStore accounts, AnnouncementRecord record)
    {
        if (!mail.Enabled) return;
        var recipients = accounts.GetAll().Where(a => !string.IsNullOrEmpty(a.Email)).ToList();
        string subject = $"[{AnnouncementTypeName(record.Type)}] {record.Title}";
        string beijingTime = record.PublishedAt.ToOffset(TimeSpan.FromHours(8)).ToString("yyyy-MM-dd HH:mm");
        string typeName = AnnouncementTypeName(record.Type);
        int failed = 0;
        foreach (var account in recipients)
        {
            try { await mail.SendAnnouncementAsync(account.Email, subject, typeName, record.Content, beijingTime); }
            catch (Exception e) { failed++; Log.Error($"[Mail] 公告邮件发送失败 ({account.Email}): {e.Message}"); }
        }
        Log.Info($"[Mail] 公告已群发 {recipients.Count - failed}/{recipients.Count} 封");
    }

    private static void CreateSession(HttpListenerContext ctx, string username)
    {
        string id = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        Sessions[id] = new WebSession(username, DateTimeOffset.UtcNow.AddDays(7));
        ctx.Response.Cookies.Add(new Cookie("rpgr_session", id) { Path = "/", HttpOnly = true });
    }

    private static AccountRecord? GetSessionAccount(HttpListenerContext ctx, AccountStore accounts)
    {
        string? id = ctx.Request.Cookies["rpgr_session"]?.Value;
        if (string.IsNullOrEmpty(id) || !Sessions.TryGetValue(id, out var session)) return null;
        if (session.ExpiresAt <= DateTimeOffset.UtcNow) { Sessions.TryRemove(id, out _); return null; }
        var account = accounts.Get(session.Username);
        return account is { Banned: false } ? account : null;
    }

    private static AccountRecord? RequireAccount(HttpListenerContext ctx, AccountStore accounts)
    {
        var account = GetSessionAccount(ctx, accounts);
        if (account == null) WriteJson(ctx, new { ok = false, message = "请先登录" }, 401);
        return account;
    }

    private static AccountRecord? RequireAdmin(HttpListenerContext ctx, AccountStore accounts)
    {
        var account = GetSessionAccount(ctx, accounts);
        if (account is not { IsAdmin: true }) { WriteJson(ctx, new { ok = false, message = "需要管理员权限" }, 403); return null; }
        return account;
    }

    private static async Task HandleAvatarUpload(HttpListenerContext ctx, AccountStore accounts, AccountRecord account, ServerConfig config)
    {
        string contentType = ctx.Request.ContentType?.Split(';')[0] ?? "";
        string extension = contentType switch { "image/png" => ".png", "image/jpeg" => ".jpg", "image/webp" => ".webp", _ => "" };
        if (extension == "") { WriteJson(ctx, new { ok = false, message = "仅支持 PNG、JPEG 或 WebP" }, 415); return; }
        if (ctx.Request.ContentLength64 <= 0 || ctx.Request.ContentLength64 > MaxAvatarBytes)
        { WriteJson(ctx, new { ok = false, message = "头像大小需在 2 MB 以内" }, 400); return; }

        string avatarDir = Path.Combine(config.DataDirectory, "avatars");
        Directory.CreateDirectory(avatarDir);
        string fileName = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(account.Username)))[..20].ToLowerInvariant() + extension;
        string filePath = Path.Combine(avatarDir, fileName);
        await using (var output = File.Create(filePath)) await ctx.Request.InputStream.CopyToAsync(output);
        if (!string.IsNullOrEmpty(account.AvatarFile) && account.AvatarFile != fileName)
        {
            string oldPath = Path.Combine(avatarDir, Path.GetFileName(account.AvatarFile));
            if (File.Exists(oldPath)) File.Delete(oldPath);
        }
        accounts.SetAvatar(account.Username, fileName);
        WriteJson(ctx, new { ok = true, avatarUrl = "/avatars/" + fileName });
    }

    private static async Task<T?> ReadJson<T>(HttpListenerContext ctx)
    {
        if (!ctx.Request.HasEntityBody) return default;
        return await JsonSerializer.DeserializeAsync<T>(ctx.Request.InputStream, JsonOptions);
    }

    private static void WriteManifest(HttpListenerContext ctx, ServerConfig config) => WriteJson(ctx, new Dictionary<string, object>
    {
        ["name"] = config.ServerName, ["english_name"] = config.ServerName, ["space_english_name"] = config.ServerName,
        ["domain"] = config.PublicTcpHost, ["protocol"] = new Uri(config.PublicBaseUrl).Scheme, ["iconurl"] = "",
        ["apiOnline"] = new Dictionary<string, object> { ["chart"] = config.EnableChartUpload },
        ["apiURL"] = new Dictionary<string, object>
        {
            ["user-login"] = "/userlogin", ["user-verify"] = "/userverify",
            ["chart-domain"] = config.PublicTcpHost, ["chart-port"] = config.TcpPort
        }
    });

    private static async Task HandleGameLogin(HttpListenerContext ctx, AccountStore accounts, string? username, string? password)
    {
        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password)) { WriteVerify(ctx, false, AuthStatusCode.InvalidParam, null); return; }
        var account = accounts.Get(username);
        if (account == null) { WriteVerify(ctx, false, AuthStatusCode.InvalidUsername, null); return; }
        if (!accounts.VerifyPassword(username, password, out var verified)) { WriteVerify(ctx, false, AuthStatusCode.InvalidPassword, null); return; }
        if (verified!.Banned) { WriteVerify(ctx, false, AuthStatusCode.UserBanned, null); return; }
        WriteVerify(ctx, true, AuthStatusCode.OK, accounts.RotateToken(username));
        await Task.CompletedTask;
    }

    private static async Task HandleGameVerify(HttpListenerContext ctx, AccountStore accounts, string? username, string? token)
    {
        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(token)) { WriteVerify(ctx, false, AuthStatusCode.InvalidParam, null); return; }
        if (accounts.VerifyToken(username, token, out _)) WriteVerify(ctx, true, AuthStatusCode.OK, accounts.RotateToken(username));
        else WriteVerify(ctx, false, AuthStatusCode.InvalidToken, null);
        await Task.CompletedTask;
    }

    private static void WriteVerify(HttpListenerContext ctx, bool status, AuthStatusCode code, string? token)
        => WriteJson(ctx, new { status, verifyToken = token, Code = (int)code });

    private static async Task HandleChartUpload(HttpListenerContext ctx, ServerConfig config)
    {
        string chartDir = Path.Combine(config.DataDirectory, "charts");
        Directory.CreateDirectory(chartDir);
        if (!ctx.Request.HasEntityBody) { WriteJson(ctx, new { status = false, msg = "无效的请求体" }, 400); return; }
        using var memory = new MemoryStream();
        await ctx.Request.InputStream.CopyToAsync(memory);
        byte[] bytes = memory.ToArray();

        // 字节级 multipart 解析：zip 是二进制数据，不能经 UTF-8 字符串转换（会错位/损坏）
        string boundary = ParseBoundary(ctx.Request.ContentType ?? "");
        if (string.IsNullOrEmpty(boundary)) { WriteJson(ctx, new { status = false, msg = "无法解析 multipart 边界" }, 400); return; }

        byte[] headerEnd = { 0x0D, 0x0A, 0x0D, 0x0A };
        int start = IndexOfSequence(bytes, headerEnd, 0);
        if (start < 0) { WriteJson(ctx, new { status = false, msg = "无法解析文件" }, 400); return; }
        start += 4;

        // 文件尾标记：完整 boundary（zip 数据内几乎不可能出现完整随机 boundary）
        byte[] endMarker = Encoding.ASCII.GetBytes("\r\n--" + boundary);
        int end = IndexOfSequence(bytes, endMarker, start);
        if (end < 0 || end < start) { WriteJson(ctx, new { status = false, msg = "无法解析文件" }, 400); return; }

        byte[] fileBytes = new byte[end - start];
        Array.Copy(bytes, start, fileBytes, 0, fileBytes.Length);

        // 校验 zip 完整性：PK 头 + 文件尾附近存在 EOCD 记录
        if (fileBytes.Length < 22 || fileBytes[0] != 0x50 || fileBytes[1] != 0x4B)
        { WriteJson(ctx, new { status = false, msg = "文件不是有效的 zip" }, 400); return; }
        byte[] eocd = { 0x50, 0x4B, 0x05, 0x06 };
        int eocdPos = IndexOfSequence(fileBytes, eocd, Math.Max(0, fileBytes.Length - 65535 - 22));
        if (eocdPos < 0) { WriteJson(ctx, new { status = false, msg = "文件不是有效的 zip" }, 400); return; }

        string chartId = Guid.NewGuid().ToString("N")[..12];
        await File.WriteAllBytesAsync(Path.Combine(chartDir, chartId + ".zip"), fileBytes);
        WriteJson(ctx, new { status = true, scoreid = chartId });
    }

    private static string? ParseBoundary(string contentType)
    {
        foreach (string part in contentType.Split(';'))
        {
            string trimmed = part.Trim();
            if (!trimmed.StartsWith("boundary=", StringComparison.OrdinalIgnoreCase)) continue;
            string value = trimmed["boundary=".Length..].Trim();
            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"') value = value[1..^1];
            return value;
        }
        return null;
    }

    private static int IndexOfSequence(byte[] haystack, byte[] needle, int startIndex)
    {
        for (int i = Math.Max(0, startIndex); i <= haystack.Length - needle.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j]) { match = false; break; }
            }
            if (match) return i;
        }
        return -1;
    }

    private static void HandleChartDownload(HttpListenerContext ctx, ServerConfig config)
    {
        string? chartId = ctx.Request.QueryString["chartid"];
        if (string.IsNullOrEmpty(chartId) || chartId.Any(c => !char.IsLetterOrDigit(c)))
        { WriteJson(ctx, new { status = false, msg = "无效的 chartid" }, 400); return; }
        string filePath = Path.Combine(config.DataDirectory, "charts", chartId + ".zip");
        if (!File.Exists(filePath)) { WriteJson(ctx, new { status = false, msg = "谱面不存在" }, 404); return; }
        byte[] bytes = File.ReadAllBytes(filePath);
        WriteBytes(ctx, bytes, "application/zip");
    }

    private static void WriteStatic(HttpListenerContext ctx, string fileName, string contentType)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Web", fileName);
        if (!File.Exists(path)) { WriteJson(ctx, new { status = "WEB_NOT_BUILT" }, 404); return; }
        ctx.Response.Headers["Cache-Control"] = "no-store";
        WriteBytes(ctx, File.ReadAllBytes(path), contentType);
    }

    private static void WriteAvatar(HttpListenerContext ctx, string fileName, ServerConfig config)
    {
        string path = Path.Combine(config.DataDirectory, "avatars", Path.GetFileName(fileName));
        if (!File.Exists(path)) { ctx.Response.StatusCode = 404; ctx.Response.Close(); return; }
        string type = Path.GetExtension(path).ToLowerInvariant() switch { ".png" => "image/png", ".webp" => "image/webp", _ => "image/jpeg" };
        WriteBytes(ctx, File.ReadAllBytes(path), type);
    }

    private static void WriteBytes(HttpListenerContext ctx, byte[] bytes, string contentType)
    {
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = contentType;
        ctx.Response.ContentLength64 = bytes.Length;
        ctx.Response.OutputStream.Write(bytes);
        ctx.Response.Close();
    }

    private static void Redirect(HttpListenerContext ctx, string location)
    {
        ctx.Response.StatusCode = 308;
        ctx.Response.RedirectLocation = location;
        ctx.Response.Close();
    }

    private static void WriteJson(HttpListenerContext ctx, object value, int statusCode = 200)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        ctx.Response.StatusCode = statusCode;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        ctx.Response.ContentLength64 = bytes.Length;
        ctx.Response.OutputStream.Write(bytes);
        ctx.Response.Close();
    }
}
