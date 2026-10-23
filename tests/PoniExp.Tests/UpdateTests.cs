using PoniExp.Daemon.Service;

namespace PoniExp.Tests;

/// <summary>
/// <c>poniexp-client update</c>: stop the service, swap the binary, start it, check the daemon answers with the new
/// version, and put the old binary back if it doesn't. Real files in a scratch directory; the service is faked.
/// </summary>
public sealed class UpdateTests : IDisposable
{
    private const string Old = "old binary";
    private const string New = "new binary";

    private readonly string _dir = Directory.CreateTempSubdirectory("poniexp-update-").FullName;

    private string Installed => Path.Combine(_dir, "bin", "poniexp-client");

    private string Source => Path.Combine(_dir, "artifacts", "poniexp-client");

    public UpdateTests()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Installed)!);
        Directory.CreateDirectory(Path.GetDirectoryName(Source)!);
        File.WriteAllText(Installed, Old);
        File.WriteAllText(Source, New);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>
    /// The service, faked: each step records what the installed binary is at that moment. A started daemon answers with
    /// the version of the binary installed when it started, unless <see cref="Crashes"/> says that binary doesn't run.
    /// </summary>
    private sealed class FakeService(string installed) : IServiceControl
    {
        public static readonly Dictionary<string, string> Versions = new() { [Old] = "1.0.0+old", [New] = "2.0.0+new" };

        public List<string> Steps { get; } = [];

        public HashSet<string> FailsToStart { get; } = [];

        public HashSet<string> Crashes { get; } = [];

        public string? AlwaysAnswers { get; init; }

        public bool FailsToStop { get; init; }

        private string? _running;

        private string Current => File.ReadAllText(installed);

        public Task<string> VersionOfAsync(string exe, CancellationToken ct)
        {
            Steps.Add($"version ({File.ReadAllText(exe)})");
            return Task.FromResult(Versions[File.ReadAllText(exe)]);
        }

        public Task StopAsync(CancellationToken ct)
        {
            Steps.Add($"stop ({Current})");
            if (FailsToStop)
            {
                throw new InvalidOperationException("the daemon still answers");
            }

            _running = null;
            return Task.CompletedTask;
        }

        public Task StartAsync(CancellationToken ct)
        {
            Steps.Add($"start ({Current})");
            if (FailsToStart.Contains(Current))
            {
                throw new InvalidOperationException("'launchctl bootstrap' failed (5)");
            }

            _running = Crashes.Contains(Current) ? null : Current;
            return Task.CompletedTask;
        }

        public Task<string?> WaitForDaemonAsync(CancellationToken ct)
        {
            Steps.Add($"health ({Current})");
            return Task.FromResult(AlwaysAnswers ?? (_running is null ? null : Versions[_running]));
        }
    }

    private Task<string> UpdateAsync(FakeService service, string? source = null) =>
        new ClientUpdater(service, TextWriter.Null).UpdateAsync(source ?? Source, Installed, CancellationToken.None);

    // ---- happy path ----

    [Fact]
    public async Task Given_ANewBinary_When_Updating_Then_TheServiceStopsBeforeTheSwapAndStartsAndAnswersAfterIt()
    {
        // Given
        var service = new FakeService(Installed);

        // When
        await UpdateAsync(service);

        // Then
        Assert.Equal([$"version ({New})", $"stop ({Old})", $"start ({New})", $"health ({New})"], service.Steps);
    }

    [Fact]
    public async Task Given_ANewBinary_When_Updating_Then_TheNewBinaryIsInstalled()
    {
        // Given
        var service = new FakeService(Installed);

        // When
        await UpdateAsync(service);

        // Then
        Assert.Equal(New, File.ReadAllText(Installed));
    }

    [Fact]
    public async Task Given_ANewBinary_When_Updating_Then_TheVersionTheDaemonReportsIsReturned()
    {
        // Given
        var service = new FakeService(Installed);

        // When
        var version = await UpdateAsync(service);

        // Then
        Assert.Equal("2.0.0+new", version);
    }

    [Fact]
    public async Task Given_ANewBinary_When_Updating_Then_NothingButTheInstalledBinaryIsLeftInItsFolder()
    {
        // Given
        var service = new FakeService(Installed);

        // When
        await UpdateAsync(service);

        // Then
        Assert.Equal([Installed], Directory.GetFiles(Path.GetDirectoryName(Installed)!));
    }

    [Fact]
    public async Task Given_AnExecutableNewBinary_When_Updating_Then_TheInstalledBinaryIsExecutable()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // no execute bit
        }

        // Given
        var service = new FakeService(Installed);
        File.SetUnixFileMode(Installed, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.SetUnixFileMode(Source, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        // When
        await UpdateAsync(service);

        // Then
        Assert.True(File.GetUnixFileMode(Installed).HasFlag(UnixFileMode.UserExecute));
    }

    // ---- rollback ----

    [Fact]
    public async Task Given_TheNewBinaryFailsToStart_When_Updating_Then_ItFailsSayingItRolledBack()
    {
        // Given
        var service = new FakeService(Installed);
        service.FailsToStart.Add(New);

        // When
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => UpdateAsync(service));

        // Then
        Assert.Contains("rolled back", error.Message);
    }

    [Fact]
    public async Task Given_TheNewBinaryFailsToStart_When_Updating_Then_ThePreviousBinaryIsPutBack()
    {
        // Given
        var service = new FakeService(Installed);
        service.FailsToStart.Add(New);

        // When
        await Assert.ThrowsAsync<InvalidOperationException>(() => UpdateAsync(service));

        // Then
        Assert.Equal(Old, File.ReadAllText(Installed));
    }

    [Fact]
    public async Task Given_TheNewBinaryFailsToStart_When_Updating_Then_TheServiceIsStoppedAndStartedAgainOnThePreviousBinary()
    {
        // Given
        var service = new FakeService(Installed);
        service.FailsToStart.Add(New);

        // When
        await Assert.ThrowsAsync<InvalidOperationException>(() => UpdateAsync(service));

        // Then: stopped first, since a service manager may keep restarting the broken one
        Assert.Equal([$"start ({New})", $"stop ({New})", $"start ({Old})"], service.Steps[^3..]);
    }

    [Fact]
    public async Task Given_TheNewDaemonNeverAnswers_When_Updating_Then_ThePreviousBinaryIsPutBack()
    {
        // Given
        var service = new FakeService(Installed);
        service.Crashes.Add(New);

        // When
        await Assert.ThrowsAsync<InvalidOperationException>(() => UpdateAsync(service));

        // Then
        Assert.Equal(Old, File.ReadAllText(Installed));
    }

    [Fact]
    public async Task Given_TheDaemonAnswersWithAnotherVersion_When_Updating_Then_ThePreviousBinaryIsPutBack()
    {
        // Given: something else answers (e.g. a daemon running outside the service)
        var service = new FakeService(Installed) { AlwaysAnswers = "1.0.0+old" };

        // When
        await Assert.ThrowsAsync<InvalidOperationException>(() => UpdateAsync(service));

        // Then
        Assert.Equal(Old, File.ReadAllText(Installed));
    }

    [Fact]
    public async Task Given_AFailedUpdate_When_ItRollsBack_Then_NothingButTheInstalledBinaryIsLeftInItsFolder()
    {
        // Given
        var service = new FakeService(Installed);
        service.FailsToStart.Add(New);

        // When
        await Assert.ThrowsAsync<InvalidOperationException>(() => UpdateAsync(service));

        // Then
        Assert.Equal([Installed], Directory.GetFiles(Path.GetDirectoryName(Installed)!));
    }

    // ---- refused before anything stops ----

    [Fact]
    public async Task Given_NoFileAtTheSourcePath_When_Updating_Then_ItFailsWithoutStoppingTheService()
    {
        // Given
        var service = new FakeService(Installed);
        var missing = Path.Combine(_dir, "artifacts", "missing");

        // When
        var error = await Record.ExceptionAsync(() => UpdateAsync(service, missing));

        // Then
        Assert.IsType<ArgumentException>(error);
        Assert.Empty(service.Steps);
    }

    [Fact]
    public async Task Given_TheSourceIsTheInstalledBinary_When_Updating_Then_ItFailsWithoutStoppingTheService()
    {
        // Given
        var service = new FakeService(Installed);

        // When
        var error = await Record.ExceptionAsync(() => UpdateAsync(service, Installed));

        // Then
        Assert.IsType<ArgumentException>(error);
        Assert.Empty(service.Steps);
    }

    [Fact]
    public async Task Given_NoInstalledBinary_When_Updating_Then_ItFailsWithoutStoppingTheService()
    {
        // Given
        var service = new FakeService(Installed);
        File.Delete(Installed);

        // When
        var error = await Record.ExceptionAsync(() => UpdateAsync(service));

        // Then
        Assert.IsType<ArgumentException>(error);
        Assert.Empty(service.Steps);
    }

    [Fact]
    public async Task Given_TheServiceFailsToStop_When_Updating_Then_TheInstalledBinaryIsUntouchedAndNothingIsLeftBesideIt()
    {
        // Given
        var service = new FakeService(Installed) { FailsToStop = true };

        // When
        await Assert.ThrowsAsync<InvalidOperationException>(() => UpdateAsync(service));

        // Then
        Assert.Equal(Old, File.ReadAllText(Installed));
        Assert.Equal([Installed], Directory.GetFiles(Path.GetDirectoryName(Installed)!));
    }

    // ---- the version the health check compares ----

    [Fact]
    public async Task Given_ARunningDaemon_When_AskingItsStatus_Then_ItReportsTheClientVersion()
    {
        // Given
        await using var router = await RouterFixture.StartAsync();
        await using var ise = await DaemonFixture.StartAsync(router, "ise");

        // When
        var status = await ise.Api.StatusAsync();

        // Then
        Assert.Equal(PoniExp.Client.ClientVersion.Current, status.Version);
    }

    [Fact]
    public void Given_ThisBuild_When_AskingItsVersion_Then_ItNamesTheCommitItWasBuiltFrom()
    {
        // Given: a build from a git checkout

        // When
        var version = PoniExp.Client.ClientVersion.Current;

        // Then
        Assert.Matches(@"^\d+\.\d+\.\d+\+[0-9a-f]{40}$", version);
    }

    // ---- the platform plans ----

    [Fact]
    public void Given_AMacOSUserService_When_PlanningAnUpdate_Then_LaunchdBootsTheAgentOutAndBackIn()
    {
        // Given
        const string exe = "/Users/m/.local/bin/poniexp-client";

        // When
        var plan = ServiceInstaller.Control(ServiceMode.User, exe, "/Users/m/.poniexp", userHome: "/Users/m", os: OSPlatformKind.MacOS);

        // Then
        Assert.Equal((exe, "bootout", "bootstrap", "/Users/m/Library/LaunchAgents/com.poniexp.daemon.plist"),
            (plan.InstalledExe, plan.Stop.Single()[1], plan.Start.Single()[1], plan.Start.Single()[^1].Replace('\\', '/')));
    }

    [Fact]
    public void Given_AMacOSSystemService_When_PlanningAnUpdate_Then_TheInstalledBinaryIsTheCopyInUsrLocalLib()
    {
        // Given
        const string exe = "/Users/m/.local/bin/poniexp-client";

        // When
        var plan = ServiceInstaller.Control(ServiceMode.System, exe, "/Users/m/.poniexp", userHome: "/Users/m", os: OSPlatformKind.MacOS);

        // Then
        Assert.Equal(("/usr/local/lib/poniexp/poniexp-client", "system/com.poniexp.daemon"),
            (plan.InstalledExe.Replace('\\', '/'), plan.Stop.Single()[^1]));
    }

    [Fact]
    public void Given_AWindowsUserService_When_PlanningAnUpdate_Then_ItStopsThroughTheLocalApiAndStartsWithoutAConsole()
    {
        // Given
        const string exe = @"C:\Users\m\.local\bin\poniexp-client.exe";

        // When
        var plan = ServiceInstaller.Control(ServiceMode.User, exe, @"C:\Users\m\.poniexp", os: OSPlatformKind.Windows);

        // Then
        Assert.Equal((true, 0, 0), (plan.StopThroughApi, plan.Stop.Count, plan.Start.Count));
        Assert.Equal(["--headless", exe, "daemon", "--home", @"C:\Users\m\.poniexp"], plan.StartDetached![1..]);
    }

    [Fact]
    public void Given_AWindowsSystemService_When_PlanningAnUpdate_Then_ScStopsAndStartsTheCopyInProgramFiles()
    {
        // Given
        const string exe = @"C:\Users\m\.local\bin\poniexp-client.exe";

        // When
        var plan = ServiceInstaller.Control(ServiceMode.System, exe, @"C:\Users\m\.poniexp", os: OSPlatformKind.Windows);

        // Then
        Assert.Equal(["sc.exe", "stop", "PoniExp"], plan.Stop.Single());
        Assert.Equal(["sc.exe", "start", "PoniExp"], plan.Start.Single());
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PoniExp", "poniexp-client.exe"), plan.InstalledExe);
    }

    [Theory]
    [InlineData(OSPlatformKind.Windows)]
    [InlineData(OSPlatformKind.MacOS)]
    public void Given_ABootTimeService_When_PlanningAnUpdate_Then_ItNeedsAdmin(OSPlatformKind os)
    {
        // Given
        const string exe = "/home/m/.local/bin/poniexp-client";

        // When
        var plan = ServiceInstaller.Control(ServiceMode.System, exe, "/home/m/.poniexp", userHome: "/home/m", os: os);

        // Then
        Assert.True(plan.NeedsAdmin);
    }

    [Fact]
    public void Given_ALinuxService_When_PlanningAnUpdate_Then_SystemdStopsAndStartsTheUserUnit()
    {
        // Given
        const string exe = "/home/m/.local/bin/poniexp-client";

        // When
        var plan = ServiceInstaller.Control(ServiceMode.User, exe, "/home/m/.poniexp", userHome: "/home/m", os: OSPlatformKind.Linux);

        // Then
        Assert.Equal(exe, plan.InstalledExe);
        Assert.Equal(["systemctl", "--user", "stop", "poniexp.service"], plan.Stop.Single());
        Assert.Equal(["systemctl", "--user", "start", "poniexp.service"], plan.Start.Single());
    }
}
