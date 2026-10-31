using System.Net;
using System.Text;
using System.Text.Json;
using ContactManage.Adapters.Google;

namespace ContactManage.Tests;

public class GoogleTests
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpMethod Method, Uri Uri, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            lock (Requests) Requests.Add((request.Method, request.RequestUri!, body));
            return respond(request);
        }
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static string IdToken(string email)
    {
        string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"{B64("{\"alg\":\"none\"}")}.{B64($"{{\"email\":\"{email}\"}}")}.sig";
    }

    private static GoogleClientConfig Config() => new() { ClientId = "id.apps.googleusercontent.com", ClientSecret = "shh-secret" };

    // ---- client file

    [Fact]
    public void The_client_file_is_read_and_its_secret_never_printed()
    {
        var file = Path.Combine(Path.GetTempPath(), $"gc-{Guid.NewGuid():N}.json");
        File.WriteAllText(file, """{"installed":{"client_id":"abc.apps.googleusercontent.com","client_secret":"s3cret","auth_uri":"https://accounts.google.com/o/oauth2/auth","token_uri":"https://oauth2.googleapis.com/token","redirect_uris":["http://localhost"]}}""");
        try
        {
            var c = GoogleClientConfig.Load(file);
            Assert.Equal("abc.apps.googleusercontent.com", c.ClientId);
            Assert.Equal("s3cret", c.ClientSecret);
            Assert.DoesNotContain("s3cret", c.ToString());
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void A_non_https_endpoint_in_the_client_file_is_ignored()
    {
        var file = Path.Combine(Path.GetTempPath(), $"gc-{Guid.NewGuid():N}.json");
        File.WriteAllText(file, """{"installed":{"client_id":"a","client_secret":"b","token_uri":"http://evil.example/token"}}""");
        try { Assert.Equal("https", GoogleClientConfig.Load(file).TokenUri.Scheme); }
        finally { File.Delete(file); }
    }

    [Fact]
    public void A_missing_file_a_folder_and_a_web_client_each_get_a_plain_explanation()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"gc-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var web = Path.Combine(Path.GetTempPath(), $"gc-{Guid.NewGuid():N}.json");
        File.WriteAllText(web, """{"web":{"client_id":"a","client_secret":"b"}}""");
        try
        {
            Assert.Contains("not found", Assert.Throws<GoogleAuthException>(() => GoogleClientConfig.Load(Path.Combine(dir, "nope.json"))).Message);
            Assert.Contains("is a folder", Assert.Throws<GoogleAuthException>(() => GoogleClientConfig.Load(dir)).Message);
            Assert.Contains("Desktop app", Assert.Throws<GoogleAuthException>(() => GoogleClientConfig.Load(web)).Message);
        }
        finally { Directory.Delete(dir); File.Delete(web); }
    }

    // ---- sign-in

    [Fact]
    public void The_pkce_challenge_matches_the_rfc_7636_example()
    {
        Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", GoogleAuth.Challenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"));
    }

    [Fact]
    public void The_email_is_read_from_the_id_token_and_garbage_gives_null()
    {
        Assert.Equal("jane@gmail.com", GoogleAuth.EmailFromIdToken(IdToken("jane@gmail.com")));
        Assert.Null(GoogleAuth.EmailFromIdToken("not-a-token"));
        Assert.Null(GoogleAuth.EmailFromIdToken(null));
    }

    [Fact]
    public async Task Sign_in_opens_the_browser_receives_the_code_and_exchanges_it_with_pkce()
    {
        var handler = new Handler(_ => Json($$"""{"access_token":"AT","refresh_token":"RT","expires_in":3600,"id_token":"{{IdToken("jane@gmail.com")}}"}"""));
        using var auth = new GoogleAuth(Config(), handler);
        Uri? opened = null;

        var tokens = await auth.SignInAsync(async url =>
        {
            opened = url;
            // Play the browser: Google would send the user back to redirect_uri with a code and the same state.
            var q = System.Web.HttpUtility.ParseQueryString(url.Query);
            using var browser = new HttpClient();
            var page = await browser.GetStringAsync($"{q["redirect_uri"]}?code=CODE123&state={Uri.EscapeDataString(q["state"]!)}");
            Assert.Contains("signed in", page);
        }, loginHint: "jane@gmail.com");

        Assert.Equal("AT", tokens.AccessToken);
        Assert.Equal("RT", tokens.RefreshToken);
        Assert.Equal("jane@gmail.com", tokens.Email);

        var q2 = System.Web.HttpUtility.ParseQueryString(opened!.Query);
        Assert.Equal("S256", q2["code_challenge_method"]);
        Assert.Equal("offline", q2["access_type"]);
        Assert.Equal("jane@gmail.com", q2["login_hint"]);
        Assert.Contains("https://www.googleapis.com/auth/contacts", q2["scope"]);
        Assert.DoesNotContain("mail.google.com", q2["scope"]);   // contacts only, no Gmail mail access
        Assert.StartsWith("http://127.0.0.1:", q2["redirect_uri"]);

        var post = Assert.Single(handler.Requests);
        var form = System.Web.HttpUtility.ParseQueryString(post.Body);
        Assert.Equal("CODE123", form["code"]);
        Assert.Equal("authorization_code", form["grant_type"]);
        Assert.Equal(q2["redirect_uri"], form["redirect_uri"]);
        Assert.Equal(q2["code_challenge"], GoogleAuth.Challenge(form["code_verifier"]!)); // the verifier proves we started this sign-in
        Assert.DoesNotContain("RT", tokens.ToString()!);
    }

    [Fact]
    public async Task An_answer_with_the_wrong_state_is_ignored_and_the_real_one_still_works()
    {
        var handler = new Handler(_ => Json("""{"access_token":"AT","refresh_token":"RT","expires_in":3600}"""));
        using var auth = new GoogleAuth(Config(), handler);

        var tokens = await auth.SignInAsync(async url =>
        {
            var q = System.Web.HttpUtility.ParseQueryString(url.Query);
            using var browser = new HttpClient();
            var forged = await browser.GetAsync($"{q["redirect_uri"]}?code=EVIL&state=wrong");
            Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);
            await browser.GetStringAsync($"{q["redirect_uri"]}?code=GOOD&state={Uri.EscapeDataString(q["state"]!)}");
        });

        Assert.Equal("AT", tokens.AccessToken);
        Assert.Contains("code=GOOD", Assert.Single(handler.Requests).Body);
    }

    [Fact]
    public async Task Cancelling_on_Googles_page_is_reported_clearly()
    {
        using var auth = new GoogleAuth(Config(), new Handler(_ => Json("{}")));

        var ex = await Assert.ThrowsAsync<GoogleAuthException>(() => auth.SignInAsync(async url =>
        {
            var q = System.Web.HttpUtility.ParseQueryString(url.Query);
            using var browser = new HttpClient();
            await browser.GetAsync($"{q["redirect_uri"]}?error=access_denied&state={Uri.EscapeDataString(q["state"]!)}");
        }));

        Assert.Contains("access_denied", ex.Message);
    }

    [Fact]
    public async Task A_browser_that_cannot_be_opened_fails_at_once_not_after_the_timeout()
    {
        using var auth = new GoogleAuth(Config(), new Handler(_ => Json("{}")));

        var started = DateTime.UtcNow;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            auth.SignInAsync(_ => Task.FromException(new InvalidOperationException("no browser")), timeout: TimeSpan.FromMinutes(5)));

        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task A_sign_in_that_is_never_finished_times_out()
    {
        using var auth = new GoogleAuth(Config(), new Handler(_ => Json("{}")));

        var ex = await Assert.ThrowsAsync<GoogleAuthException>(() =>
            auth.SignInAsync(_ => Task.CompletedTask, timeout: TimeSpan.FromMilliseconds(300)));

        Assert.Contains("not finished in time", ex.Message);
    }

    [Fact]
    public async Task Refreshing_keeps_the_refresh_token_and_a_revoked_one_asks_for_a_new_sign_in()
    {
        var calls = 0;
        var handler = new Handler(_ => ++calls == 1
            ? Json("""{"access_token":"AT2","expires_in":3600}""")
            : Json("""{"error":"invalid_grant","error_description":"Token has been expired or revoked."}""", HttpStatusCode.BadRequest));
        using var auth = new GoogleAuth(Config(), handler);

        var fresh = await auth.RefreshAsync("RT");
        Assert.Equal("AT2", fresh.AccessToken);
        Assert.Equal("RT", fresh.RefreshToken);

        var ex = await Assert.ThrowsAsync<GoogleAuthException>(() => auth.RefreshAsync("RT"));
        Assert.True(ex.SignInAgain);
    }

    // ---- People API

    [Fact]
    public async Task Contacts_and_labels_are_read_page_by_page_with_the_token()
    {
        var handler = new Handler(r =>
        {
            Assert.Equal("Bearer TOKEN", r.Headers.Authorization!.ToString());
            var url = r.RequestUri!.ToString();
            if (url.Contains("contactGroups"))
                return Json("""{"contactGroups":[{"resourceName":"contactGroups/myContacts","groupType":"SYSTEM_CONTACT_GROUP","formattedName":"My Contacts","memberCount":3},{"resourceName":"contactGroups/abc","groupType":"USER_CONTACT_GROUP","name":"Clients","memberCount":2}]}""");
            return url.Contains("pageToken=P2")
                ? Json("""{"connections":[{"resourceName":"people/c3"}]}""")
                : Json("""{"connections":[{"resourceName":"people/c1"},{"resourceName":"people/c2"}],"nextPageToken":"P2"}""");
        });
        using var people = new PeopleApiClient(_ => Task.FromResult("TOKEN"), handler);

        var all = await people.ListConnectionsAsync("names,memberships");
        var groups = await people.ListContactGroupsAsync();

        Assert.Equal(["people/c1", "people/c2", "people/c3"], all.Select(p => p.GetProperty("resourceName").GetString()));
        Assert.Equal(new ContactGroupInfo("contactGroups/myContacts", "My Contacts", true, 3), groups[0]);
        Assert.Equal(new ContactGroupInfo("contactGroups/abc", "Clients", false, 2), groups[1]);
        Assert.All(handler.Requests, r => Assert.Equal(HttpMethod.Get, r.Method));
    }

    [Fact]
    public async Task A_rejected_token_means_sign_in_again_and_rate_limits_are_retried()
    {
        var calls = 0;
        var limited = new Handler(_ => ++calls < 3 ? new HttpResponseMessage(HttpStatusCode.TooManyRequests) : Json("""{"connections":[{"resourceName":"people/c1"}]}"""));
        using (var people = new PeopleApiClient(_ => Task.FromResult("T"), limited, retryUnit: TimeSpan.Zero))
            Assert.Single(await people.ListConnectionsAsync("names"));
        Assert.Equal(3, calls);

        using var rejected = new PeopleApiClient(_ => Task.FromResult("T"), new Handler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)));
        Assert.True((await Assert.ThrowsAsync<GoogleAuthException>(() => rejected.ListConnectionsAsync("names"))).SignInAgain);
    }

    [Fact]
    public async Task Another_error_shows_googles_reason_but_nothing_from_the_request()
    {
        using var people = new PeopleApiClient(_ => Task.FromResult("T"), new Handler(_ =>
            Json("""{"error":{"code":403,"status":"PERMISSION_DENIED","message":"People API has not been used in project 1234 before or it is disabled."}}""", HttpStatusCode.Forbidden)));

        var ex = await Assert.ThrowsAsync<GoogleAuthException>(() => people.ListContactGroupsAsync());

        Assert.Contains("HTTP 403", ex.Message);
        Assert.Contains("PERMISSION_DENIED", ex.Message);
        Assert.False(ex.SignInAgain);
    }
}
