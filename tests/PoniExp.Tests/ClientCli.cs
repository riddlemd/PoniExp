using System.Diagnostics;
using System.Text;

namespace PoniExp.Tests;

/// <summary>
/// The real <c>poniexp-client</c>, built beside the tests, run as its own process against a <see cref="DaemonFixture"/>'s home
/// (<c>--home</c>), with <c>SSH_AUTH_SOCK</c> set to a <see cref="FakeSshAgent"/> or unset. It never sees the user's own home or
/// SSH agent.
/// </summary>
public sealed class ClientCli : IDisposable
{
    private readonly Process _process;
    private readonly StringBuilder _output = new();
    private readonly Task _reading;

    private ClientCli(Process process)
    {
        _process = process;
        _reading = Task.WhenAll(PumpAsync(process.StandardOutput), PumpAsync(process.StandardError));
    }

    /// <summary>src/PoniExp.Cli's build output, in the configuration these tests were built in.</summary>
    public static string Dll
    {
        get
        {
            var here = new DirectoryInfo(AppContext.BaseDirectory); // tests/PoniExp.Tests/bin/<configuration>/<tfm>/
            var configuration = here.Parent!.Name;
            var root = here;
            while (!File.Exists(Path.Combine(root.FullName, "PoniExp.slnx")))
            {
                root = root.Parent ?? throw new InvalidOperationException("can't find PoniExp.slnx above " + AppContext.BaseDirectory);
            }

            var dll = Path.Combine(root.FullName, "src", "PoniExp.Cli", "bin", configuration, here.Name, "poniexp-client.dll");
            return File.Exists(dll) ? dll : throw new FileNotFoundException("build src/PoniExp.Cli first", dll);
        }
    }

    public string Output
    {
        get
        {
            lock (_output)
            {
                return _output.ToString();
            }
        }
    }

    public bool HasExited => _process.HasExited;

    /// <summary>Starts <c>poniexp-client --home &lt;home&gt; args</c> for <paramref name="machine"/>; its daemon must be running.</summary>
    public static async Task<ClientCli> StartAsync(DaemonFixture machine, FakeSshAgent? agent, params string[] args)
    {
        // A CLI that finds no daemon in its home falls back to the system daemon: never let it reach the real one.
        Assert.True(await machine.Api.IsRunningAsync(), $"{machine.Name}'s daemon isn't running");
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in (string[])[Dll, "--home", machine.Home, .. args])
        {
            start.ArgumentList.Add(arg);
        }

        start.Environment.Remove("PONIEXP_HOME");
        start.Environment.Remove("SSH_AUTH_SOCK");
        if (agent is not null)
        {
            start.Environment["SSH_AUTH_SOCK"] = agent.SocketPath;
        }

        var process = Process.Start(start)!;
        process.StandardInput.Close(); // nobody at the keyboard: a prompt reads end of input
        return new ClientCli(process);
    }

    /// <summary>Runs it to the end: its exit code and everything it printed.</summary>
    public static async Task<(int Exit, string Output)> RunAsync(DaemonFixture machine, FakeSshAgent? agent, params string[] args)
    {
        using var cli = await StartAsync(machine, agent, args);
        return (await cli.WaitAsync(TimeSpan.FromSeconds(60)), cli.Output);
    }

    public async Task<int> WaitAsync(TimeSpan timeout)
    {
        using var cancel = new CancellationTokenSource(timeout);
        try
        {
            await _process.WaitForExitAsync(cancel.Token);
        }
        catch (OperationCanceledException)
        {
            Assert.Fail($"poniexp-client didn't finish within {timeout}; it printed:\n{Output}");
        }

        await _reading;
        return _process.ExitCode;
    }

    /// <summary>What Ctrl+C at a terminal sends.</summary>
    public void Interrupt() => Process.Start("kill", ["-INT", _process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)])!.WaitForExit();

    public async Task WaitForOutputAsync(string text) => await RouterFixture.Eventually(() => Output.Contains(text, StringComparison.Ordinal));

    private async Task PumpAsync(StreamReader reader)
    {
        var buffer = new char[1024];
        int read;
        while ((read = await reader.ReadAsync(buffer)) > 0)
        {
            lock (_output)
            {
                _output.Append(buffer, 0, read);
            }
        }
    }

    public void Dispose()
    {
        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
        }

        _process.Dispose();
    }
}
