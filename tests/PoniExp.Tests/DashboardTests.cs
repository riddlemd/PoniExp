using System.Net;
using PoniExp.Core.Protocol;
using PoniExp.Server;
using PoniExp.Server.Dashboard;

namespace PoniExp.Tests;

/// <summary>Phase 13: the dashboard, signed in with the owner key, on its own port.</summary>
public class DashboardTests
{
    [Fact]
    public async Task Given_an_owner_signed_sign_in_When_it_is_opened_Then_the_dashboard_shows_the_router()
    {
        // Given
        await using var setup = await Setup.StartAsync();
        await setup.Router.MemberAsync("wojtek");
        var signIn = setup.SignIn();

        // When
        var signedIn = await setup.Dashboard.GetAsync(signIn.Link(setup.DashboardUrl));
        var page = await setup.GetWithCookieAsync("/", CookieOf(signedIn));

        // Then
        Assert.Equal(HttpStatusCode.OK, signedIn.StatusCode);
        var cookie = signedIn.Headers.GetValues("Set-Cookie").Single();
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("PoniExp router", html);
        Assert.Contains("wojtek", html);
    }

    [Fact]
    public async Task Given_no_sign_in_When_the_dashboard_is_opened_Then_it_asks_for_one()
    {
        // Given
        await using var setup = await Setup.StartAsync();

        // When
        var page = await setup.Dashboard.GetAsync("/");

        // Then
        Assert.Equal(HttpStatusCode.Unauthorized, page.StatusCode);
        Assert.Contains("poniexp-client dashboard", await page.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Given_a_used_sign_in_When_it_is_opened_again_Then_it_is_refused()
    {
        // Given
        await using var setup = await Setup.StartAsync();
        var link = setup.SignIn().Link(setup.DashboardUrl);
        Assert.Equal(HttpStatusCode.OK, (await setup.Dashboard.GetAsync(link)).StatusCode);

        // When
        var again = await setup.Dashboard.GetAsync(link);

        // Then
        Assert.Equal(HttpStatusCode.Forbidden, again.StatusCode);
        Assert.Contains("already used", await TextOf(again));
        Assert.False(again.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task Given_a_sign_in_signed_by_another_key_When_it_is_opened_Then_it_is_refused()
    {
        // Given
        await using var setup = await Setup.StartAsync();
        var forged = setup.SignIn(TestOwner.Create());

        // When
        var response = await setup.Dashboard.GetAsync(forged.Link(setup.DashboardUrl));

        // Then
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("isn't signed by the owner", await TextOf(response));
    }

    [Fact]
    public async Task Given_a_sign_in_outside_the_clock_skew_When_it_is_opened_Then_it_is_refused()
    {
        // Given
        await using var setup = await Setup.StartAsync();
        var stale = setup.SignIn(issued: DateTimeOffset.UtcNow + TimeSpan.FromMinutes(6)); // after the start, so only the skew refuses it

        // When
        var response = await setup.Dashboard.GetAsync(stale.Link(setup.DashboardUrl));

        // Then
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("too old", await TextOf(response));
    }

    [Fact]
    public async Task Given_a_sign_in_signed_before_the_router_started_When_it_is_opened_Then_it_is_refused()
    {
        // Given
        await using var setup = await Setup.StartAsync();
        var replayable = setup.SignIn(issued: DateTimeOffset.UtcNow - TimeSpan.FromSeconds(30)); // within the skew

        // When
        var response = await setup.Dashboard.GetAsync(replayable.Link(setup.DashboardUrl));

        // Then
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("too old", await TextOf(response));
    }

    [Fact]
    public async Task Given_a_sign_in_for_another_router_When_it_is_opened_Then_it_is_refused()
    {
        // Given
        await using var setup = await Setup.StartAsync();
        var elsewhere = setup.SignIn(routerId: new string('a', 32));

        // When
        var response = await setup.Dashboard.GetAsync(elsewhere.Link(setup.DashboardUrl));

        // Then
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("another router", await TextOf(response));
    }

    [Fact]
    public async Task Given_a_signed_in_token_When_it_is_tampered_with_Then_it_is_refused()
    {
        // Given
        await using var setup = await Setup.StartAsync();
        var token = CookieOf(await setup.Dashboard.GetAsync(setup.SignIn().Link(setup.DashboardUrl)));
        var parts = token.Split('.');
        var longer = setup.Sessions.Validate(token)!.ExpiresOn.AddDays(30).ToUnixTimeSeconds();
        var body = System.Buffers.Text.Base64Url.EncodeToString(System.Text.Encoding.UTF8.GetBytes(
            System.Text.Encoding.UTF8.GetString(System.Buffers.Text.Base64Url.DecodeFromChars(parts[1])).Replace("\"exp\":", $"\"exp\":{longer},\"x\":")));

        // When
        var page = await setup.GetWithCookieAsync("/", $"{parts[0]}.{body}.{parts[2]}");

        // Then
        Assert.Equal(HttpStatusCode.Unauthorized, page.StatusCode);
    }

    [Fact]
    public async Task Given_a_session_When_the_owner_key_changes_Then_it_ends()
    {
        // Given
        await using var setup = await Setup.StartAsync();
        var token = CookieOf(await setup.Dashboard.GetAsync(setup.SignIn().Link(setup.DashboardUrl)));
        var session = setup.Sessions.Validate(token)!;

        // When
        setup.Router.Admin.SetOwner(TestOwner.Create().Key.ToString());

        // Then
        Assert.Null(setup.Sessions.Validate(token));
        Assert.Equal(HttpStatusCode.Unauthorized, (await setup.GetWithCookieAsync("/", token)).StatusCode);
        Assert.False(setup.Actions.IsLive(session));
    }

    [Fact]
    public async Task Given_a_signed_in_token_When_its_lifetime_passes_Then_it_is_refused()
    {
        // Given
        await using var setup = await Setup.StartAsync(new Dictionary<string, string?> { ["PoniExp:DashboardSessionLifetime"] = "00:00:01" });
        var token = CookieOf(await setup.Dashboard.GetAsync(setup.SignIn().Link(setup.DashboardUrl)));
        Assert.Equal(HttpStatusCode.OK, (await setup.GetWithCookieAsync("/", token)).StatusCode);

        // When
        await Task.Delay(TimeSpan.FromSeconds(2.1));

        // Then
        Assert.Equal(HttpStatusCode.Unauthorized, (await setup.GetWithCookieAsync("/", token)).StatusCode);
    }

    [Fact]
    public async Task Given_an_ended_session_When_it_makes_a_change_Then_it_is_refused()
    {
        // Given
        await using var setup = await Setup.StartAsync();
        var ended = new DashboardSession(setup.Owner.Key.Fingerprint, DateTimeOffset.UtcNow - TimeSpan.FromSeconds(1));

        // When
        var refused = await Assert.ThrowsAsync<RouterException>(() => setup.Actions.SetTrafficLogAsync(ended, true));

        // Then
        Assert.Contains("sign-in has ended", refused.Message);
        Assert.False(setup.Actions.Read(false).TrafficLogOn);
    }

    [Fact]
    public async Task Given_a_live_session_When_it_approves_a_waiting_machine_Then_the_machine_joins()
    {
        // Given
        await using var setup = await Setup.StartAsync();
        var keys = Core.Crypto.MachineKeys.Create();
        var client = setup.Router.Client(keys);
        await client.ConnectAsync();
        var approved = Wait.For<MembershipStatus>(h => client.MembershipChanged += h);
        await client.RequestJoinAsync("ise", new MachineDescription("TestOS 1", "x64", "ise-host"));
        await RouterFixture.Eventually(() => setup.Actions.Read(false).Machines.Any(m => m.Machine is { Name: "ise", Status: MembershipStatus.Pending }));

        // When
        await setup.Actions.ApproveAsync(setup.Live, keys.MachineId);

        // Then
        Assert.Equal(MembershipStatus.Approved, await approved);
        Assert.Contains(setup.Actions.Read(false).Machines, m => m.Machine is { Name: "ise", Status: MembershipStatus.Approved });
    }

    [Fact]
    public async Task Given_a_live_session_When_it_forgets_the_join_requests_Then_none_wait()
    {
        // Given
        await using var setup = await Setup.StartAsync();
        var client = setup.Router.Client(Core.Crypto.MachineKeys.Create());
        await client.ConnectAsync();
        await client.RequestJoinAsync("ise", new MachineDescription("TestOS 1", "x64", "ise-host"));
        await RouterFixture.Eventually(() => setup.Actions.Read(false).Machines.Any(m => m.Machine.Status == MembershipStatus.Pending));

        // When
        var cleared = await setup.Actions.ClearPendingAsync(setup.Live);

        // Then
        Assert.Equal("ise", Assert.Single(cleared).Name);
        Assert.DoesNotContain(setup.Actions.Read(false).Machines, m => m.Machine.Status == MembershipStatus.Pending);
    }

    [Fact]
    public async Task Given_a_live_session_When_it_turns_the_traffic_log_on_Then_traffic_is_recorded()
    {
        // Given
        await using var setup = await Setup.StartAsync();

        // When
        var on = await setup.Actions.SetTrafficLogAsync(setup.Live, true);
        await setup.Router.MemberAsync("wojtek");

        // Then
        Assert.True(on);
        var state = setup.Actions.Read(false);
        Assert.True(state.TrafficLogOn);
        Assert.Contains(state.Traffic, t => t.Outcome == TrafficOutcome.JoinRequest);
    }

    [Fact]
    public async Task Given_the_dashboard_port_When_relay_or_admin_paths_are_asked_Then_they_are_not_there()
    {
        // Given
        await using var setup = await Setup.StartAsync();
        var cookie = CookieOf(await setup.Dashboard.GetAsync(setup.SignIn().Link(setup.DashboardUrl)));

        // When
        var health = await setup.GetWithCookieAsync("/health", cookie);
        var admin = await setup.GetWithCookieAsync("/admin/machines", cookie);
        var hub = await setup.Dashboard.PostAsync(HubMethods.Path + "/negotiate?negotiateVersion=1", null);

        // Then
        Assert.Equal(HttpStatusCode.NotFound, health.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, admin.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, hub.StatusCode);
    }

    [Fact]
    public async Task Given_the_relay_port_When_dashboard_paths_are_asked_Then_only_the_router_greeting_answers()
    {
        // Given
        await using var setup = await Setup.StartAsync();
        using var relay = new HttpClient { BaseAddress = setup.Router.Url };

        // When
        var signIn = await relay.GetAsync(setup.SignIn().Link(setup.Router.Url).PathAndQuery);
        var root = await relay.GetAsync("/");
        var circuit = await relay.PostAsync("/_blazor/negotiate?negotiateVersion=1", null);

        // Then
        Assert.Equal(HttpStatusCode.NotFound, signIn.StatusCode);
        Assert.Equal("PoniExp router\n", await root.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, circuit.StatusCode);
    }

    [Fact]
    public async Task Given_the_dashboard_port_When_a_live_page_connects_Then_it_needs_the_sign_in()
    {
        // Given
        await using var setup = await Setup.StartAsync();
        var cookie = CookieOf(await setup.Dashboard.GetAsync(setup.SignIn().Link(setup.DashboardUrl)));

        // When
        var anonymous = await setup.Dashboard.PostAsync("/_blazor/negotiate?negotiateVersion=1", null);
        using var signed = new HttpRequestMessage(HttpMethod.Post, "/_blazor/negotiate?negotiateVersion=1");
        signed.Headers.Add("Cookie", $"{DashboardSessions.Cookie}={cookie}");
        var allowed = await setup.Dashboard.SendAsync(signed);

        // Then
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
    }

    private static async Task<string> TextOf(HttpResponseMessage response) => WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());

    private static string CookieOf(HttpResponseMessage response) =>
        response.Headers.GetValues("Set-Cookie").Single().Split(';')[0].Split('=', 2)[1];

    private sealed class Setup : IAsyncDisposable
    {
        private Setup(RouterFixture router, int port, TestOwner owner)
        {
            Router = router;
            Owner = owner;
            DashboardUrl = new Uri($"http://127.0.0.1:{port}/");
            Dashboard = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { BaseAddress = DashboardUrl };
        }

        public RouterFixture Router { get; }

        public TestOwner Owner { get; }

        public Uri DashboardUrl { get; }

        public HttpClient Dashboard { get; }

        public DashboardSessions Sessions => Router.App.Services.GetRequiredService<DashboardSessions>();

        public DashboardActions Actions => Router.App.Services.GetRequiredService<DashboardActions>();

        public string RouterId => Router.App.Services.GetRequiredService<PoniExp.Server.Owner.RouterKeys>().Id;

        /// <summary>A session as a fresh sign-in makes it.</summary>
        public DashboardSession Live => new(Owner.Key.Fingerprint, DateTimeOffset.UtcNow + TimeSpan.FromHours(1));

        public static async Task<Setup> StartAsync(IDictionary<string, string?>? settings = null)
        {
            var port = RouterFixture.RestartablePort();
            var router = await RouterFixture.StartAsync(settings: settings, dashboardPort: port);
            var owner = TestOwner.Create();
            router.Admin.SetOwner(owner.Key.ToString());
            return new Setup(router, port, owner);
        }

        public DashboardSignIn SignIn(TestOwner? by = null, DateTimeOffset? issued = null, string? routerId = null)
        {
            var id = Guid.NewGuid();
            var on = Signatures.Truncate(issued ?? DateTimeOffset.UtcNow);
            var router = routerId ?? RouterId;
            var signer = by ?? Owner;
            return new DashboardSignIn(id, router, on, signer.SignAsync(signer.Key, DashboardSignIn.Payload(id, router, on), default).Result);
        }

        public async Task<HttpResponseMessage> GetWithCookieAsync(string path, string token)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Add("Cookie", $"{DashboardSessions.Cookie}={token}");
            return await Dashboard.SendAsync(request);
        }

        public async ValueTask DisposeAsync()
        {
            Dashboard.Dispose();
            await Router.DisposeAsync();
        }
    }
}
