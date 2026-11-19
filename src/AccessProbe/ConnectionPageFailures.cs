using Microsoft.Web.WebView2.Core;

namespace AccessProbe;

public static class ConnectionPageFailures
{
    public static string Outcome(CoreWebView2WebErrorStatus error, int httpStatus = 0)
    {
        if (error is CoreWebView2WebErrorStatus.CertificateCommonNameIsIncorrect
            or CoreWebView2WebErrorStatus.CertificateExpired or CoreWebView2WebErrorStatus.ClientCertificateContainsErrors
            or CoreWebView2WebErrorStatus.CertificateRevoked or CoreWebView2WebErrorStatus.CertificateIsInvalid)
            return "certificate_failed";
        if (httpStatus == 429) return "rate_limited";
        if (httpStatus == 403) return "forbidden";
        if (httpStatus == 401) return "verification_required";
        if (httpStatus >= 400) return "http_error";
        return error switch
        {
            CoreWebView2WebErrorStatus.Timeout => "timeout",
            CoreWebView2WebErrorStatus.RedirectFailed => "unsupported_redirect",
            CoreWebView2WebErrorStatus.ErrorHttpInvalidServerResponse => "http_error",
            CoreWebView2WebErrorStatus.ValidAuthenticationCredentialsRequired => "verification_required",
            _ => "network_error"
        };
    }
}
