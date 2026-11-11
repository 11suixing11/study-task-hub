using System.Text.Json;

namespace AccessProbe.Mobile;

public static class MobilePasswordlessLogin
{
    public const string PictureFolder = "Pictures/聚合平台登录";
    private const int MaximumImageBytes = 1024 * 1024;

    public static bool SupportsQr(string platform) => platform is "chaoxing" or "ucampus" or "zhihuishu" or "yuketang" or "changjiang" or "xuetang";
    public static string? AppPackage(string platform) => platform switch
    {
        "chaoxing" => "com.chaoxing.mobile",
        "ucampus" => "cn.unipus.ucampus.student",
        "zhihuishu" => "com.able.wisdomtree",
        "yuketang" or "changjiang" or "xuetang" => "com.tencent.mm",
        _ => null
    };
    public static string AppName(string platform) => platform switch
    {
        "chaoxing" => "学习通", "ucampus" => "U 校园学生版", "zhihuishu" => "知到",
        "yuketang" or "changjiang" or "xuetang" => "微信", _ => "官方 App"
    };
    public static string Guidance(string platform) => platform switch
    {
        "chaoxing" or "ucampus" or "zhihuishu" => "可用官方 App 扫码确认。二维码保存后，在 App 的扫一扫中选择相册识别；不支持相册时可用另一台设备扫码。",
        "yuketang" or "changjiang" or "xuetang" => "可用官网二维码或短信验证码登录。保存二维码后，可在微信扫一扫中选择相册识别。",
        "mooc" => "可在官网选择手机号验证码或已绑定的微信登录。",
        _ => "请使用平台官方提供的登录方式。"
    };

    // Only the official login document is allowed to provide a QR image.
    public static bool IsQrDocument(string platform, string? address)
    {
        if (address is null || !Uri.TryCreate(address, UriKind.Absolute, out var uri)
            || uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0) return false;
        return platform switch
        {
            "chaoxing" => uri.IdnHost == "passport2.chaoxing.com" && uri.AbsolutePath is "/login" or "/mlogin",
            "ucampus" => uri.IdnHost == "sso.unipus.cn" && uri.AbsolutePath is "/sso/login" or "/sso/login.html",
            "zhihuishu" => uri.IdnHost == "login.zhihuishu.com" && uri.AbsolutePath == "/",
            "yuketang" => uri.IdnHost is "www.yuketang.cn" or "pro.yuketang.cn" && uri.AbsolutePath is "/web" or "/web/",
            "changjiang" => uri.IdnHost == "changjiang.yuketang.cn" && uri.AbsolutePath is "/web" or "/web/",
            "xuetang" => uri.IdnHost == "www.xuetangx.com" && uri.AbsolutePath is "/" or "/login" or "/loginw",
            _ => false
        };
    }

    public static string SelectQrScript(string platform) => platform switch
    {
        "chaoxing" => "(() => { const e=document.querySelector('.QR-list'); if(e){e.style.display='block'; const t=document.querySelector('.tab-list'); if(t)t.style.display='none'; return true;} return !!document.getElementById('quickCode'); })()",
        "ucampus" => "(() => { const e=document.querySelector('#login-mode-web .login-icon'); const p=document.getElementById('login-mode-web'); if(e&&p&&getComputedStyle(p).display!=='none'){e.click();return true;} return !!document.getElementById('qrcode'); })()",
        "zhihuishu" => "(() => { const e=document.querySelector('.login-tabs #tab-3'); if(e){e.click();return true;} return !!document.querySelector('.app-qrcode-wrapper'); })()",
        _ => "(() => true)()"
    };

    // Copy the pixels already displayed by the official page. A second image request could
    // regenerate the QR, so neither a fetch nor a cache of a previous image is used here.
    private static string ReadImageScript(string platform)
    {
        var selector = platform switch
        {
            "chaoxing" => "#quickCode",
            "ucampus" => "#qrcode img, #qrcode canvas",
            "zhihuishu" => ".app-qrcode-wrapper .qrcode-box img",
            "yuketang" or "changjiang" => "#qrcode-box img.logma, #qrcode-box canvas",
            "xuetang" => ".scaveng-img img",
            _ => ""
        };
        return $$"""
            const read = () => {
                const e = document.querySelector({{JsonSerializer.Serialize(selector)}});
                if (!e || !e.isConnected || !e.getClientRects().length
                    || getComputedStyle(e).visibility === 'hidden') return null;
                const isCanvas = e.tagName === 'CANVAS';
                const width = isCanvas ? e.width : e.naturalWidth;
                const height = isCanvas ? e.height : e.naturalHeight;
                if (width < 48 || height < 48 || width > 2048 || height > 2048) return null;
                const source = isCanvas ? '' : (e.currentSrc || e.src || '');
                if (!isCanvas) {
                    if (!e.complete || !source) return null;
                    if (!source.startsWith('data:image/png;base64,') && !source.startsWith('data:image/jpeg;base64,')) {
                        let u; try { u = new URL(source, location.href); } catch { return null; }
                        if (u.protocol !== 'https:' || u.origin !== location.origin || u.username || u.password) return null;
                    }
                }
                try {
                    const canvas = isCanvas ? e : document.createElement('canvas');
                    if (!isCanvas) {
                        canvas.width = width; canvas.height = height;
                        canvas.getContext('2d').drawImage(e, 0, 0);
                    }
                    const data = canvas.toDataURL('image/png');
                    if (!data.startsWith('data:image/png;base64,') || data.length > 1398229) return null;
                    return { element: e, source, data, address: location.href };
                } catch { return null; }
            };
            """;
    }

    public static string ImageScript(string platform, string captureId) => "(() => { " + ReadImageScript(platform)
        + "const current = read(); if (!current) return null; current.id = " + JsonSerializer.Serialize(captureId)
        + "; window.__aggregatorLoginQr = current; return current.data; })()";

    // Verify the element and its actual pixels again, including same-URL image/canvas refreshes.
    public static string VerifyImageScript(string platform, string captureId) => "(() => { " + ReadImageScript(platform)
        + "const previous = window.__aggregatorLoginQr; if (!previous || previous.id !== " + JsonSerializer.Serialize(captureId)
        + ") return false; const current = read(); return !!current && current.element === previous.element"
        + " && current.source === previous.source && current.data === previous.data && current.address === previous.address; })()";

    public static string ClearImageScript(string captureId) => "(() => { if (window.__aggregatorLoginQr?.id === "
        + JsonSerializer.Serialize(captureId) + ") delete window.__aggregatorLoginQr; })()";

    public static byte[]? DecodeImage(string? value)
    {
        if (value is null || value.Length > MaximumImageBytes * 4 / 3 + 128) return null;
        var prefix = value.StartsWith("data:image/png;base64,", StringComparison.Ordinal) ? "data:image/png;base64,"
            : value.StartsWith("data:image/jpeg;base64,", StringComparison.Ordinal) ? "data:image/jpeg;base64," : null;
        if (prefix is null) return null;
        try
        {
            var bytes = Convert.FromBase64String(value[prefix.Length..]);
            if (bytes.Length is < 16 or > MaximumImageBytes) return null;
            var png = bytes.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
            var jpeg = bytes[0] == 255 && bytes[1] == 216 && bytes[2] == 255;
            return prefix == "data:image/png;base64," ? (png ? bytes : null) : (jpeg ? bytes : null);
        }
        catch (FormatException) { return null; }
    }
}
