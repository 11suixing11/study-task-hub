using System.Net;
using System.Text.Json;
using AccessProbe;

public static class NoticeChecks
{
    public static async Task RunAsync()
    {
        var count = 0;
        void Check(bool condition) { count++; if (!condition) throw new Exception($"Notice check {count} failed."); }
        Check(NoticeReader.IsFixedNoticeEndpoint(NoticeReader.Endpoint));
        Check(NoticeReader.IsFixedNoticeRequest("POST", NoticeReader.Endpoint));
        Check(!NoticeReader.IsFixedNoticeRequest("GET", NoticeReader.Endpoint));
        Check(!NoticeReader.IsFixedNoticeEndpoint("https://notice.xuexitong.com/pc/notice/getNoticeList"));
        Check(NoticeReader.CreateFormFields().Count == 15);
        Check(NoticeReader.CreateFormFields()["lastValue"] == string.Empty);
        Check(NoticeReader.CreateFormFields("cursor")["lastValue"] == "cursor");
        Check(!NoticeReader.IsFixedNoticeEndpoint(NoticeReader.Endpoint + "?token=secret"));
        Check(!NoticeReader.IsFixedNoticeEndpoint("https://notice.chaoxing.com.evil.example/pc/notice/getNoticeList"));
        Check(!NoticeReader.IsFixedNoticeEndpoint("http://notice.chaoxing.com/pc/notice/getNoticeList"));
        Check(!NoticeReader.IsFixedNoticeEndpoint("https://notice.chaoxing.com:9443/pc/notice/getNoticeList"));
        var browserRedirect = NoticeReader.ClassifyBrowserProbe(true, true, 302, 0, "opaqueredirect", false);
        Check(browserRedirect.Outcome == "redirect_status_hidden" && browserRedirect.RequestObserved
            && browserRedirect.ResponseObserved && browserRedirect.EndpointStatusObservedDuringWindow == 302);
        Check(NoticeReader.ClassifyBrowserProbe(true, true, 200, null, null, true).Outcome == "fetch_rejected_endpoint_status_observed");
        Check(NoticeReader.ClassifyBrowserProbe(true, false, null, null, null, true).Outcome == "fetch_rejected_after_request_observed");
        Check(NoticeReader.ClassifyBrowserProbe(false, false, null, null, null, true).Outcome == "fetch_rejected_without_request_observed");
        Check(NoticeReader.ClassifyBrowserProbe(false, false, null, 0, "opaqueredirect", false).Outcome == "redirect_status_hidden");
        Check(NoticeReader.ClassifyBrowserProbe(true, true, 302, 0, "opaque", false).Outcome == "opaque_response");
        Check(NoticeReader.ClassifyBrowserProbe(false, false, null, null, null, false).Outcome == "fetch_response_without_http_event");
        Check(NoticeReader.TryParseBrowserFetchMessage("{\"nonce\":\"n1\",\"kind\":\"response\",\"status\":0,\"responseType\":\"opaqueredirect\"}", "n1", out var parsedRedirect)
            && parsedRedirect == new BrowserFetchMessage(0, "opaqueredirect", false));
        Check(NoticeReader.TryParseBrowserFetchMessage("{\"nonce\":\"n1\",\"kind\":\"fetch_rejected\"}", "n1", out var parsedRejection)
            && parsedRejection == new BrowserFetchMessage(null, null, true));
        foreach (var malformed in new[]
        {
            "not json",
            "[]",
            "{\"nonce\":123,\"kind\":\"response\",\"status\":200,\"responseType\":\"cors\"}",
            "{\"nonce\":\"n1\",\"kind\":123,\"status\":200,\"responseType\":\"cors\"}",
            "{\"nonce\":\"n1\",\"kind\":\"response\",\"status\":\"200\",\"responseType\":\"cors\"}",
            "{\"nonce\":\"n1\",\"kind\":\"response\",\"status\":700,\"responseType\":\"cors\"}",
            "{\"nonce\":\"n1\",\"kind\":\"unknown\"}"
        })
            Check(!NoticeReader.TryParseBrowserFetchMessage(malformed, "n1", out _));
        Check(!NoticeReader.TryParseBrowserFetchMessage("{\"nonce\":\"other\",\"kind\":\"fetch_rejected\"}", "n1", out _));
        Check(!JsonSerializer.Serialize(browserRedirect).Contains("secret"));
        string Item(object? endTime, object? tag = null) => JsonSerializer.Serialize(new
        {
            extendParam = new { cparams = JsonSerializer.Serialize(new { funConfig = new { funTag = tag ?? 3, content = new { key = "sample-key", title = "合成测试作业", courseName = "合成课程", endTime } } }) }
        });
        string Wrap(params string[] items) => "{\"notices\":{\"list\":[" + string.Join(',', items) + "]}}";
        Check(NoticeReader.Parse("<html>用户登录</html>").Outcome == "html_not_task_data");
        Check(NoticeReader.Parse("{}").Outcome == "schema_unrecognized");
        Check(NoticeReader.Parse("[]").Outcome == "schema_unrecognized");
        Check(NoticeReader.Parse("not json").Outcome == "schema_unrecognized");
        Check(NoticeReader.Parse("{\"notices\":{\"list\":{}}}").Outcome == "schema_unrecognized");
        Check(NoticeReader.Parse("{\"status\":false,\"notices\":{\"list\":[]}}").Outcome == "api_rejected");
        Check(NoticeReader.Parse("{\"status\":true,\"notices\":{\"list\":[]}}").Outcome == "parsed_partial");
        var cursorPage = NoticeReader.Parse("{\"notices\":{\"list\":[],\"lastGetId\":\"cursor-1\",\"lastPage\":false}}");
        Check(cursorPage.NextCursor == "cursor-1" && !cursorPage.LastPage && cursorPage.NextCursorPresent);
        var lastPage = NoticeReader.Parse("{\"notices\":{\"list\":[],\"lastGetId\":null,\"lastPage\":true}}");
        Check(lastPage.LastPage && !lastPage.NextCursorPresent);
        var empty = NoticeReader.Parse(Wrap());
        Check(empty.Outcome == "parsed_partial" && empty.Tasks.Count == 0 && empty.Coverage != "complete");
        var valid = NoticeReader.Parse(Wrap(Item(1800000000000L)));
        Check(valid.Tasks.Count == 1 && valid.Tasks[0].Deadline == DateTimeOffset.FromUnixTimeMilliseconds(1800000000000L));
        Check(valid.Tasks[0].SubmissionState.StartsWith("未知"));
        Check(NoticeReader.Parse(Wrap(Item("1800000000000"))).Tasks[0].Deadline != null);
        Check(NoticeReader.Parse(Wrap(Item(1800000000))).InvalidTimes == 1);
        Check(NoticeReader.Parse(Wrap(Item("明天"))).Tasks[0].Deadline == null);
        Check(NoticeReader.Parse(Wrap(Item(null))).Tasks[0].Deadline == null);
        Check(NoticeReader.Parse(Wrap(Item(0))).Tasks[0].Deadline == null);
        Check(NoticeReader.Parse(Wrap(Item(long.MaxValue))).InvalidTimes == 1);
        Check(NoticeReader.Parse(Wrap("null", Item(0))).Skipped == 1);
        Check(NoticeReader.Parse(Wrap(Item(0, 2))).Tasks.Count == 0);
        Check(NoticeReader.Parse(Wrap(Item(0, "3"))).Skipped == 1);
        Check(NoticeReader.Parse(Wrap(Item(0), Item(0))).Tasks.Count == 2);
        Check(NoticeReader.Parse(new string('a', NoticeReader.MaximumBytes + 1)).Outcome == "response_too_large");
        foreach (var (status, body, expected) in new[]
        {
            (HttpStatusCode.OK, "<html>登录</html>", "html_not_task_data"),
            (HttpStatusCode.Found, "", "redirect_received"),
            (HttpStatusCode.Unauthorized, "", "unauthorized"),
            (HttpStatusCode.Forbidden, "", "forbidden"),
            (HttpStatusCode.TooManyRequests, "", "rate_limited"),
            (HttpStatusCode.InternalServerError, "", "http_error"),
            (HttpStatusCode.OK, Wrap(Item(0)), "parsed_partial"),
            (HttpStatusCode.OK, new string('a', NoticeReader.MaximumBytes + 1), "response_too_large")
        })
        {
            using var handler = new StubHandler(status, body);
            using var client = new HttpClient(handler);
            var result = await NoticeReader.FetchAsync(client, CancellationToken.None);
            Check(result.Outcome == expected && handler.Requests == 1 && result.HttpDiagnostic?.StatusCode == (int)status);
            Check(handler.IsExpectedNoticePost);
        }
        foreach (var (location, expected) in new (string?, string)[]
        {
            (null, "missing_location"),
            ("", "invalid_location"),
            ("/login?token=secret", "same_host_https"),
            ("https://passport2.chaoxing.com/login?token=secret", "official_auth_host_https"),
            ("https://passport2.xuexitong.com/login?token=secret", "official_auth_host_https"),
            ("https://i.chaoxing.com/private?token=secret", "other_official_host_https"),
            ("//school.example/sso?token=secret", "external_domain"),
            ("http://passport2.chaoxing.com/login?token=secret", "insecure_http"),
            ("https://passport2.chaoxing.com.evil.example/?token=secret", "external_domain"),
            ("https://user:secret@passport2.chaoxing.com/", "embedded_credentials"),
            ("https://passport2.chaoxing.com:9443/", "non_default_port"),
            ("custom-app://secret", "external_protocol"),
            ("https://[", "invalid_location")
        })
        {
            using var handler = new StubHandler(HttpStatusCode.Found, "", location);
            using var client = new HttpClient(handler);
            var result = await NoticeReader.FetchAsync(client, CancellationToken.None);
            Check(result.HttpDiagnostic?.RedirectCategory == expected && handler.Requests == 1);
            Check(handler.IsExpectedNoticePost);
            var serialized = JsonSerializer.Serialize(result.HttpDiagnostic);
            Check(!serialized.Contains("secret") && !serialized.Contains("token") && !serialized.Contains("http:"));
        }

        string Page(string? nextCursor, bool isLastPage, params string[] items) =>
            "{\"notices\":{\"list\":[" + string.Join(',', items) + "],\"lastGetId\":"
            + (nextCursor is null ? "null" : JsonSerializer.Serialize(nextCursor))
            + ",\"lastPage\":" + (isLastPage ? "true" : "false") + "}}";

        var pagedBodies = new Dictionary<string, string>
        {
            [""] = Page("c1", false, Item(1800000000000L)),
            ["c1"] = Page("c2", false, Item(1800000001000L)),
            ["c2"] = Page(null, true, Item(1800000002000L))
        };
        using (var handler = new PagingStubHandler(cursor => pagedBodies[cursor]))
        using (var client = new HttpClient(handler))
        {
            var result = await NoticeReader.FetchRecentAsync(client, CancellationToken.None);
            Check(result.Outcome == "parsed_partial" && result.PageCount == 3 && result.Total == 3
                && result.Tasks.Count == 3 && result.TerminationReason == "last_page"
                && !result.NextCursorPresent && handler.Cursors.SequenceEqual(new[] { "", "c1", "c2" }));
            Check(handler.AllRequestsExpected);
        }

        using (var handler = new PagingStubHandler(_ => Page("next", false)))
        using (var client = new HttpClient(handler))
        {
            var result = await NoticeReader.FetchRecentAsync(client, CancellationToken.None);
            Check(result.TerminationReason == "empty_page" && result.PageCount == 1 && handler.Cursors.Count == 1);
        }

        var repeatedBodies = new Dictionary<string, string>
        {
            [""] = Page("same", false, Item(1800000000000L)),
            ["same"] = Page("same", false, Item(1800000001000L))
        };
        using (var handler = new PagingStubHandler(cursor => repeatedBodies[cursor]))
        using (var client = new HttpClient(handler))
        {
            var result = await NoticeReader.FetchRecentAsync(client, CancellationToken.None);
            Check(result.TerminationReason == "cursor_repeated" && result.PageCount == 2 && result.Total == 2);
        }

        var limitedBodies = new Dictionary<string, string>();
        for (var index = 0; index < NoticeReader.MaximumPages; index++)
        {
            var cursor = index == 0 ? string.Empty : $"p{index}";
            limitedBodies[cursor] = Page($"p{index + 1}", false, Item(1800000000000L + index));
        }
        using (var handler = new PagingStubHandler(cursor => limitedBodies[cursor]))
        using (var client = new HttpClient(handler))
        {
            var result = await NoticeReader.FetchRecentAsync(client, CancellationToken.None);
            Check(result.TerminationReason == "page_limit" && result.PageCount == NoticeReader.MaximumPages
                && handler.Cursors.Count == NoticeReader.MaximumPages);
        }

        using (var handler = new PagingStubHandler(
            cursor => cursor == string.Empty ? Page("auth-fails", false, Item(1800000000000L)) : "",
            cursor => cursor == "auth-fails" ? HttpStatusCode.Forbidden : HttpStatusCode.OK))
        using (var client = new HttpClient(handler))
        {
            var result = await NoticeReader.FetchRecentAsync(client, CancellationToken.None);
            Check(result.Outcome == "forbidden" && result.Tasks.Count == 0 && result.TerminationReason == "request_failure"
                && result.PageCount == 1 && handler.Cursors.Count == 2);
        }
        Console.WriteLine($"PASS: {count} synthetic notice parsing and HTTP checks; no account accessed.");
    }

    private sealed class StubHandler(HttpStatusCode status, string body, string? location = null) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        public bool IsExpectedNoticePost { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (!NoticeReader.IsFixedNoticeRequest(request.Method.Method, request.RequestUri?.AbsoluteUri ?? string.Empty))
                throw new Exception("Unexpected request target or method.");
            Requests++;
            var encodedForm = await request.Content!.ReadAsStringAsync(cancellationToken);
            var fields = encodedForm.Split('&')
                .Select(part => part.Split('=', 2))
                .ToDictionary(pair => Uri.UnescapeDataString(pair[0].Replace('+', ' ')),
                    pair => pair.Length > 1 ? Uri.UnescapeDataString(pair[1].Replace('+', ' ')) : string.Empty);
            var expectedFields = NoticeReader.CreateFormFields();
            IsExpectedNoticePost = request.Content.Headers.ContentType?.MediaType == "application/x-www-form-urlencoded"
                && fields.Count == expectedFields.Count
                && expectedFields.All(field => fields.TryGetValue(field.Key, out var value) && value == field.Value);
            var response = new HttpResponseMessage(status) { Content = new StringContent(body) };
            if (location != null) response.Headers.TryAddWithoutValidation("Location", location);
            return response;
        }
    }

    private sealed class PagingStubHandler(
        Func<string, string> bodyFactory,
        Func<string, HttpStatusCode>? statusFactory = null) : HttpMessageHandler
    {
        public List<string> Cursors { get; } = [];
        public bool AllRequestsExpected { get; private set; } = true;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (!NoticeReader.IsFixedNoticeRequest(request.Method.Method, request.RequestUri?.AbsoluteUri ?? string.Empty))
                throw new Exception("Unexpected paging request target or method.");
            var encodedForm = await request.Content!.ReadAsStringAsync(cancellationToken);
            var fields = encodedForm.Split('&')
                .Select(part => part.Split('=', 2))
                .ToDictionary(pair => Uri.UnescapeDataString(pair[0].Replace('+', ' ')),
                    pair => pair.Length > 1 ? Uri.UnescapeDataString(pair[1].Replace('+', ' ')) : string.Empty);
            var cursor = fields.TryGetValue("lastValue", out var value) ? value : string.Empty;
            Cursors.Add(cursor);
            var expectedFields = NoticeReader.CreateFormFields(cursor);
            AllRequestsExpected &= fields.Count == expectedFields.Count
                && expectedFields.All(field => fields.TryGetValue(field.Key, out var actual) && actual == field.Value)
                && request.Content.Headers.ContentType?.MediaType == "application/x-www-form-urlencoded";
            var status = statusFactory?.Invoke(cursor) ?? HttpStatusCode.OK;
            var response = new HttpResponseMessage(status)
            {
                Content = new StringContent(status == HttpStatusCode.OK ? bodyFactory(cursor) : string.Empty)
            };
            return response;
        }
    }
}
