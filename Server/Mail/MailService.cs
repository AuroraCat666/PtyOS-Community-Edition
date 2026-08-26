using System.Collections.Concurrent;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace RPGReServer;

public class SmtpSettings
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 465;
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string From { get; set; } = "";
    public bool Enabled => !string.IsNullOrEmpty(Host) && !string.IsNullOrEmpty(Username) && !string.IsNullOrEmpty(Password);
}

public class MailService
{
    private readonly SmtpSettings _settings;
    private readonly ConcurrentDictionary<string, CodeEntry> _codes = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastSent = new();
    private static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(180);
    private const int MaxAttempts = 3;

    private sealed record CodeEntry(string Code, DateTimeOffset ExpiresAt, int Attempts);

    public MailService(SmtpSettings settings) => _settings = settings;

    public bool Enabled => _settings.Enabled;

    /// <summary>返回距上次发送剩余的冷却秒数，0 表示可以发送。</summary>
    public int RemainingCooldown(string key)
    {
        if (!_lastSent.TryGetValue(key, out var last)) return 0;
        var remaining = last + Cooldown - DateTimeOffset.UtcNow;
        return remaining > TimeSpan.Zero ? (int)Math.Ceiling(remaining.TotalSeconds) : 0;
    }

    /// <summary>生成并记录验证码，返回验证码（用于日志或测试）。</summary>
    public string CreateCode(string key, TimeSpan? lifetime = null)
    {
        string code = Random.Shared.Next(100000, 1000000).ToString();
        _codes[key] = new CodeEntry(code, DateTimeOffset.UtcNow + (lifetime ?? TimeSpan.FromMinutes(5)), 0);
        return code;
    }

    public bool VerifyCode(string key, string code)
    {
        if (!_codes.TryGetValue(key, out var entry)) return false;
        if (entry.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            _codes.TryRemove(key, out _);
            return false;
        }
        if (entry.Code.Equals(code, StringComparison.Ordinal))
        {
            _codes.TryRemove(key, out _);
            return true;
        }
        if (entry.Attempts + 1 >= MaxAttempts) _codes.TryRemove(key, out _);
        else _codes[key] = entry with { Attempts = entry.Attempts + 1 };
        return false;
    }

    public async Task SendCodeAsync(string cooldownKey, string kind, string to, string subject, string hint, string code)
    {
        if (!Enabled) throw new InvalidOperationException("SMTP 未配置");
        string html = BuildHtml(kind, to, hint, code);
        string plain = $"{hint}\n\n你的验证码是：{code}\n验证码 5 分钟内有效，请勿泄露给他人。\n\n—— PtyOS 社区";
        var builder = new BodyBuilder { HtmlBody = html, TextBody = plain };
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(_settings.From));
        message.To.Add(MailboxAddress.Parse(to));
        message.Subject = subject;
        message.Body = builder.ToMessageBody();
        await SendMessageAsync(message);
        _lastSent[cooldownKey] = DateTimeOffset.UtcNow;
    }

    private static string BuildHtml(string kind, string to, string hint, string code)
    {
        string title = kind == "findPassword" ? "找回密码" : "注册验证";
        string action = kind == "findPassword"
            ? "你正在使用 PtyOS 社区账号找回密码功能"
            : "你正在注册 PtyOS 社区账号";
        string footer = kind == "findPassword"
            ? "如果不是你本人操作，请忽略本邮件，你的密码不会被修改。"
            : "如果这不是你本人操作，请忽略本邮件。";
        return $@"<!doctype html>
<html lang=""zh-CN"">
<head><meta charset=""utf-8""><meta name=""viewport"" content=""width=device-width,initial-scale=1""></head>
<body style=""margin:0;padding:0;background:#f2f4f6;font-family:'Microsoft YaHei','Segoe UI',sans-serif;color:#17191d;"">
  <table role=""presentation"" width=""100%"" cellpadding=""0"" cellspacing=""0"" style=""background:#f2f4f6;padding:32px 12px;"">
    <tr><td align=""center"">
      <table role=""presentation"" width=""100%"" cellpadding=""0"" cellspacing=""0"" style=""max-width:520px;background:#ffffff;border-radius:10px;overflow:hidden;box-shadow:0 8px 30px rgba(18,24,28,.08);"">
        <tr><td style=""background:#00a6b6;padding:22px 30px;"">
          <span style=""font-size:17px;font-weight:700;color:#ffffff;letter-spacing:1px;"">PtyOS <span style=""opacity:.75;font-weight:400;"">COMMUNITY</span></span>
        </td></tr>
        <tr><td style=""padding:34px 30px 26px;"">
          <h1 style=""margin:0 0 10px;font-size:22px;"">{title}</h1>
          <p style=""margin:0 0 22px;font-size:14px;line-height:1.8;color:#697078;"">{action}（{hint}）。</p>
          <p style=""margin:0 0 8px;font-size:13px;color:#697078;"">你的验证码是</p>
          <div style=""background:#f2f4f6;border:1px solid #dfe3e7;border-radius:8px;padding:18px;text-align:center;"">
            <span style=""font-size:34px;font-weight:800;letter-spacing:10px;color:#00a6b6;font-family:Consolas,monospace;"">{code}</span>
          </div>
          <p style=""margin:18px 0 0;font-size:13px;color:#697078;"">验证码 <strong style=""color:#17191d;"">5 分钟内</strong>有效，请勿泄露给他人。</p>
          <p style=""margin:8px 0 0;font-size:12px;color:#a4aab0;"">{footer}</p>
        </td></tr>
        <tr><td style=""padding:0 30px 26px;font-size:12px;color:#a4aab0;"">此邮件由系统自动发送，请勿回复。</td></tr>
      </table>
    </td></tr>
  </table>
</body>
</html>";
    }

    public async Task SendAsync(string to, string subject, string body)
    {
        if (!Enabled) throw new InvalidOperationException("SMTP 未配置");
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(_settings.From));
        message.To.Add(MailboxAddress.Parse(to));
        message.Subject = subject;
        message.Body = new TextPart("plain") { Text = body };
        await SendMessageAsync(message);
    }

    public async Task SendAnnouncementAsync(string to, string subject, string typeName, string content, string publishedAt)
    {
        if (!Enabled) throw new InvalidOperationException("SMTP 未配置");
        string html = BuildAnnouncementHtml(typeName, content, publishedAt);
        string plain = $"{content}\n\n—— PtyOS 社区 {publishedAt}";
        var builder = new BodyBuilder { HtmlBody = html, TextBody = plain };
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(_settings.From));
        message.To.Add(MailboxAddress.Parse(to));
        message.Subject = subject;
        message.Body = builder.ToMessageBody();
        await SendMessageAsync(message);
    }

    private static string BuildAnnouncementHtml(string typeName, string content, string publishedAt)
    {
        string escapedContent = content.Replace("\n", "<br>");
        return $@"<!doctype html>
<html lang=""zh-CN"">
<head><meta charset=""utf-8""><meta name=""viewport"" content=""width=device-width,initial-scale=1""></head>
<body style=""margin:0;padding:0;background:#f2f4f6;font-family:'Microsoft YaHei','Segoe UI',sans-serif;color:#17191d;"">
  <table role=""presentation"" width=""100%"" cellpadding=""0"" cellspacing=""0"" style=""background:#f2f4f6;padding:32px 12px;"">
    <tr><td align=""center"">
      <table role=""presentation"" width=""100%"" cellpadding=""0"" cellspacing=""0"" style=""max-width:520px;background:#ffffff;border-radius:10px;overflow:hidden;box-shadow:0 8px 30px rgba(18,24,28,.08);"">
        <tr><td style=""background:#00a6b6;padding:22px 30px;"">
          <span style=""font-size:17px;font-weight:700;color:#ffffff;letter-spacing:1px;"">PtyOS <span style=""opacity:.75;font-weight:400;"">COMMUNITY</span></span>
        </td></tr>
        <tr><td style=""padding:34px 30px 26px;"">
          <div style=""display:inline-block;background:#e7f4f5;color:#007b88;padding:4px 10px;border-radius:4px;font-size:12px;font-weight:700;margin-bottom:12px;"">{typeName}</div>
          <h1 style=""margin:0 0 16px;font-size:22px;"">公告</h1>
          <div style=""font-size:14px;line-height:1.8;color:#3d434a;"">{escapedContent}</div>
        </td></tr>
        <tr><td style=""padding:0 30px 26px;font-size:12px;color:#a4aab0;"">—— PtyOS 社区 {publishedAt}</td></tr>
        <tr><td style=""padding:0 30px 26px;font-size:12px;color:#a4aab0;"">此邮件由系统自动发送，请勿回复。</td></tr>
      </table>
    </td></tr>
  </table>
</body>
</html>";
    }

    private async Task SendMessageAsync(MimeMessage message)
    {
        if (!Enabled) throw new InvalidOperationException("SMTP 未配置");
        using var client = new SmtpClient();
        await client.ConnectAsync(_settings.Host, _settings.Port,
            _settings.Port == 465 ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTlsWhenAvailable);
        await client.AuthenticateAsync(_settings.Username, _settings.Password);
        await client.SendAsync(message);
        await client.DisconnectAsync(true);
    }
}
