using Android.Content;
using Android.Graphics;
using Android.Provider;
using Android.Webkit;
using System.Text.Json;
using Path = System.IO.Path;

namespace AccessProbe.Mobile;

public sealed partial class MainActivity
{
    private Android.Net.Uri? savedLoginQr;
    private string? savedLoginQrPlatform;
    private bool preparingQr;
    private bool awaitingOfficialApp;
    private int qrNavigationRevision;
    private (WebView Web, string Platform, int Navigation, bool OpenApp)? pendingQrSave;
    private const int QrStoragePermission = 302;

    private async Task PrepareOfficialQr(string platform)
    {
        if (web is null || loginPlatform != platform || busy || preparingQr) return;
        var loginWeb = web; var navigation = qrNavigationRevision;
        DeleteSavedLoginQr();
        if (!MobilePasswordlessLogin.IsQrDocument(platform, web.Url))
        {
            loginClient?.ResetNavigation();
            web.LoadUrl(MobilePlatforms.Login(platform).AbsoluteUri);
            status.Text = "正在打开官方登录页，二维码显示后再保存。";
            return;
        }
        try
        {
            await Evaluate(loginWeb, MobilePasswordlessLogin.SelectQrScript(platform));
            if (IsCurrentQrPage(loginWeb, platform, navigation)) status.Text = "请等待官网二维码显示；然后保存并在官方 App 内扫码确认。";
        }
        catch { if (alive) status.Text = "二维码页面尚未准备好，请加载完成后重试。"; }
    }

    private async Task SaveOfficialQr(string platform, bool openApp)
    {
        if (web is null || loginPlatform != platform || preparingQr || busy || pendingQrSave is not null) return;
        if (!MobilePasswordlessLogin.IsQrDocument(platform, web.Url))
        {
            status.Text = "请先打开官网的扫码登录页，再保存二维码。";
            return;
        }
        if (OperatingSystem.IsAndroidVersionAtLeast(29) == false
            && CheckSelfPermission(Android.Manifest.Permission.WriteExternalStorage) != Android.Content.PM.Permission.Granted)
        {
            pendingQrSave = (web, platform, qrNavigationRevision, openApp);
            RequestPermissions([Android.Manifest.Permission.WriteExternalStorage], QrStoragePermission);
            return;
        }
        preparingQr = true;
        var loginWeb = web; var navigation = qrNavigationRevision;
        var captureId = Guid.NewGuid().ToString("N");
        byte[]? bytes = null;
        try
        {
            await Evaluate(loginWeb, MobilePasswordlessLogin.SelectQrScript(platform));
            if (!IsCurrentQrPage(loginWeb, platform, navigation)) return;
            status.Text = "正在取得当前官网二维码…";
            for (var attempt = 0; attempt < 12; attempt++)
            {
                if (!IsCurrentQrPage(loginWeb, platform, navigation)) return;
                var result = await Evaluate(loginWeb, MobilePasswordlessLogin.ImageScript(platform, captureId));
                string? data = null;
                try { data = JsonSerializer.Deserialize<string>(result); } catch (JsonException) { }
                bytes = MobilePasswordlessLogin.DecodeImage(data);
                if (bytes is not null) break;
                await Task.Delay(300);
            }
            if (!IsCurrentQrPage(loginWeb, platform, navigation)) return;
            if (bytes is null) { status.Text = "未取得官网二维码。请在页面选择扫码登录，等待图片显示后重试；也可使用官网短信登录。"; return; }
            using var bounds = new BitmapFactory.Options { InJustDecodeBounds = true };
            BitmapFactory.DecodeByteArray(bytes, 0, bytes.Length, bounds)?.Dispose();
            if (bounds.OutWidth is < 48 or > 2048 || bounds.OutHeight is < 48 or > 2048)
            { status.Text = "官网二维码图片未识别，请刷新后重试。"; return; }
            using var source = BitmapFactory.DecodeByteArray(bytes, 0, bytes.Length);
            if (source is null) { status.Text = "官网二维码图片未识别，请刷新后重试。"; return; }
            var size = Math.Max(640, Math.Max(source.Width, source.Height));
            var edge = Math.Max(source.Width, source.Height); var padding = edge / 8;
            using var picture = Bitmap.CreateBitmap(size, size, Bitmap.Config.Argb8888!);
            using var canvas = new Canvas(picture); canvas.DrawColor(Color.White);
            using var paint = new Paint { FilterBitmap = false };
            using var rect = new Rect(padding * size / (edge + 2 * padding), padding * size / (edge + 2 * padding),
                (padding + source.Width) * size / (edge + 2 * padding), (padding + source.Height) * size / (edge + 2 * padding));
            canvas.DrawBitmap(source, null, rect, paint);
            if (!IsCurrentQrPage(loginWeb, platform, navigation)) return;
            if (await Evaluate(loginWeb, MobilePasswordlessLogin.VerifyImageScript(platform, captureId)) != "true"
                || !IsCurrentQrPage(loginWeb, platform, navigation))
            { if (IsCurrentQrPage(loginWeb, platform, navigation)) status.Text = "官网二维码已更新，请重新保存当前二维码。"; return; }
            DeleteSavedLoginQr();
            var resolver = ContentResolver!;
            using var values = new ContentValues();
            var name = "聚合平台-" + AccessProbe.PlatformCatalog.Name(platform) + "-登录二维码-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".png";
            values.Put(MediaStore.IMediaColumns.DisplayName, name);
            values.Put(MediaStore.IMediaColumns.MimeType, "image/png");
            if (OperatingSystem.IsAndroidVersionAtLeast(29))
            {
                values.Put(MediaStore.IMediaColumns.RelativePath, MobilePasswordlessLogin.PictureFolder);
                values.Put(MediaStore.IMediaColumns.IsPending, 1);
            }
            else
            {
#pragma warning disable CS0618 // Public Pictures directory is required only on Android 9.
                var directory = Path.Combine(Android.OS.Environment.GetExternalStoragePublicDirectory(Android.OS.Environment.DirectoryPictures)!.AbsolutePath, "聚合平台登录");
                Directory.CreateDirectory(directory);
                values.Put(MediaStore.IMediaColumns.Data, Path.Combine(directory, name));
#pragma warning restore CS0618
            }
            var target = resolver.Insert(MediaStore.Images.Media.ExternalContentUri!, values);
            if (target is null) throw new IOException("The media store did not create an image.");
            try
            {
                RememberQrImage(target);
                using (var stream = resolver.OpenOutputStream(target) ?? throw new IOException("The media store did not provide a stream."))
                    if (!picture.Compress(Bitmap.CompressFormat.Png!, 100, stream)) throw new IOException("QR image encoding failed.");
                if (!IsCurrentQrPage(loginWeb, platform, navigation)
                    || await Evaluate(loginWeb, MobilePasswordlessLogin.VerifyImageScript(platform, captureId)) != "true"
                    || !IsCurrentQrPage(loginWeb, platform, navigation))
                {
                    DeleteQrImage(target); target.Dispose();
                    if (IsCurrentQrPage(loginWeb, platform, navigation)) status.Text = "官网二维码已更新，请重新保存当前二维码。";
                    return;
                }
                if (OperatingSystem.IsAndroidVersionAtLeast(29))
                {
                    values.Clear(); values.Put(MediaStore.IMediaColumns.IsPending, 0);
                    resolver.Update(target, values, null, null);
                }
                savedLoginQr = target; savedLoginQrPlatform = platform;
            }
            catch { DeleteQrImage(target); target.Dispose(); throw; }
            status.Text = "二维码已保存到相册。请在" + MobilePasswordlessLogin.AppName(platform) + "的扫一扫中选择相册，确认后回到这里。";
            if (openApp) OpenOfficialLoginApp(platform);
        }
        catch { if (alive && web == loginWeb) status.Text = "二维码保存未完成，请确认图片已显示后重试。"; }
        finally
        {
            preparingQr = false;
            if (bytes is not null) System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes);
            if (alive && web == loginWeb)
                try { await Evaluate(loginWeb, MobilePasswordlessLogin.ClearImageScript(captureId)); } catch { }
        }
    }

    private bool IsCurrentQrPage(WebView loginWeb, string platform, int revision) => alive && web == loginWeb
        && loginPlatform == platform && qrNavigationRevision == revision && MobilePasswordlessLogin.IsQrDocument(platform, web.Url);

    private void OpenOfficialLoginApp(string platform)
    {
        if (savedLoginQr is null || savedLoginQrPlatform != platform) { status.Text = "请先保存当前官网二维码。"; return; }
        var package = MobilePasswordlessLogin.AppPackage(platform);
        using var intent = package is null ? null : PackageManager!.GetLaunchIntentForPackage(package);
        if (intent is null) { status.Text = "未找到" + MobilePasswordlessLogin.AppName(platform) + "。二维码已保存，可自行打开官方 App 或使用官网短信登录。"; return; }
        try { awaitingOfficialApp = true; StartActivity(intent); }
        catch { awaitingOfficialApp = false; status.Text = "未能打开官方 App，请自行打开扫一扫，从相册选择刚保存的二维码。"; }
    }

    private void DeleteSavedLoginQr()
    {
        if (savedLoginQr is not { } uri) return;
        DeleteQrImage(uri);
        uri.Dispose(); savedLoginQr = null; savedLoginQrPlatform = null;
    }

    private string QrPreferencesName => syntheticUi ? "synthetic-official-login-qr" : "official-login-qr";
    private string[] RememberedQrImages()
    {
        try
        {
            using var preferences = GetSharedPreferences(QrPreferencesName, FileCreationMode.Private);
            return JsonSerializer.Deserialize<string[]>(preferences?.GetString("images", "[]") ?? "[]") ?? [];
        }
        catch { return []; }
    }
    private void StoreQrImages(IEnumerable<string> images)
    {
        using var preferences = GetSharedPreferences(QrPreferencesName, FileCreationMode.Private);
        using var editor = preferences?.Edit();
        if (editor?.PutString("images", JsonSerializer.Serialize(images.Distinct().ToArray()))?.Commit() != true)
            throw new IOException("QR cleanup record could not be stored.");
    }
    private static bool IsCreatedQrAddress(string address) => System.Uri.TryCreate(address, UriKind.Absolute, out var uri)
        && uri.Scheme == "content" && uri.Host == "media" && uri.IsDefaultPort && uri.UserInfo.Length == 0
        && uri.Query.Length == 0 && uri.Fragment.Length == 0
        && uri.AbsolutePath.StartsWith("/external/images/media/", StringComparison.Ordinal)
        && long.TryParse(uri.AbsolutePath["/external/images/media/".Length..], out var id) && id > 0;
    private void RememberQrImage(Android.Net.Uri uri)
    {
        var address = uri.ToString();
        if (address is null || !IsCreatedQrAddress(address)) throw new IOException("Unexpected QR media address.");
        var previous = RememberedQrImages();
        if (previous.Length >= 32) throw new IOException("Previous QR images still require cleanup.");
        StoreQrImages(previous.Append(address));
    }
    private void DeleteQrImage(Android.Net.Uri uri)
    {
        var address = uri.ToString();
        if (address is null || !IsCreatedQrAddress(address)) return;
        try
        {
            if (ContentResolver?.Delete(uri, null, null) is >= 0)
                StoreQrImages(RememberedQrImages().Where(value => value != address));
        }
        catch
        {
            // MediaStore can reject deleting an already removed row after a process restart.
            // Forget it only when querying this recorded URI confirms that it no longer exists.
            try
            {
                using var cursor = ContentResolver?.Query(uri, ["_id"], null, null, null);
                if (cursor is not null && !cursor.MoveToFirst())
                    StoreQrImages(RememberedQrImages().Where(value => value != address));
            }
            catch { } // A permission or provider failure retains the record for a later retry.
        }
    }
    private void CleanupOrphanedLoginQr()
    {
        foreach (var address in RememberedQrImages())
        {
            if (!IsCreatedQrAddress(address)) continue;
            using var uri = Android.Net.Uri.Parse(address);
            if (uri is not null) DeleteQrImage(uri);
        }
    }

    private void LoginPageStarted()
    {
        qrNavigationRevision++;
        if (savedLoginQr is not null && resumed && !awaitingOfficialApp) DeleteSavedLoginQr();
    }

    private async Task ResumeOfficialAppLogin(string platform)
    {
        var loginWeb = web;
        try
        {
            await Task.Delay(700, loginLifetime?.Token ?? CancellationToken.None);
            if (!alive || !resumed || loginPlatform != platform || loginWeb is null || web != loginWeb) return;
            loginWeb.OnResume();
            status.Text = "正在确认官网登录结果；未完成时请在官方 App 内确认后返回。";
            await CaptureLogin(platform, false);
            if (alive && resumed && web == loginWeb && loginPlatform == platform && !busy
                && !HasLoginEvidence(platform, MobilePlatforms.ReadBrowserCookies(platform)))
                status.Text = "官网尚未确认登录。请在官方 App 内扫码确认；二维码过期时请刷新并重新保存。";
        }
        catch (OperationCanceledException) { }
        catch { if (alive && web == loginWeb) status.Text = "请等待官网返回课程主页，也可点击“登录完成并读取课程”重试。"; }
    }

    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Android.Content.PM.Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        if (requestCode != QrStoragePermission || pendingQrSave is not { } pending) return;
        pendingQrSave = null;
        if (!IsCurrentQrPage(pending.Web, pending.Platform, pending.Navigation)) return;
        if (grantResults.Length > 0 && grantResults[0] == Android.Content.PM.Permission.Granted)
        {
            CleanupOrphanedLoginQr();
            _ = SaveOfficialQr(pending.Platform, pending.OpenApp);
        }
        else if (alive) status.Text = "未允许保存到相册。可使用官网短信登录，或让另一台设备扫描页面二维码。";
    }
}
