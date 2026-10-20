using System.Globalization;
using System.Text.RegularExpressions;

namespace PoniExp.Core.Protocol;

/// <summary>A graphics card: its name, and its memory in GB when the OS says.</summary>
public sealed record GpuInfo(string Name, double? MemoryGb = null);

/// <summary>
/// What a machine has, detected by its daemon (plus tags from its settings) and reported to the router. Job requirements
/// are matched against it. Memory is whole GB, rounded, as the OS (or a container limit) reports it.
/// </summary>
public sealed record MachineCapabilities(string Os, string OsVersion, string Arch, string? Cpu, int Cores, double MemoryGb,
    IReadOnlyList<GpuInfo> Gpus, IReadOnlyList<string> Tags, DateTimeOffset DetectedOn)
{
    /// <summary>One line: "linux x64, AMD Ryzen 9 (32 cores), 64 GB, RTX 3090 Ti 24 GB [cuda]".</summary>
    public string Summary() =>
        $"{Os} {Arch}, {Cpu ?? "cpu?"} ({Cores} cores), {MemoryGb:0.#} GB" +
        (Gpus.Count > 0 ? ", " + string.Join(", ", Gpus.Select(g => g.MemoryGb is { } m ? $"{g.Name} {m:0.#} GB" : g.Name)) : "") +
        (Tags.Count > 0 ? $" [{string.Join(", ", Tags)}]" : "");
}

/// <summary>
/// One condition a job puts on the machine that claims it: <c>key op value</c>. Ops: <c>=</c>, <c>!=</c>, <c>&gt;=</c>,
/// <c>&lt;=</c> (numbers) and <c>~</c> (contains, ignoring case). Keys: os, os-version, arch, cpu, cores, memory (GB), gpu,
/// gpu-memory (GB), gpus (count), tag. A job's requirements must all hold; gpu and gpu-memory hold if any card meets them.
/// </summary>
public sealed partial record JobRequirement(string Key, string Op, string Value)
{
    public static readonly IReadOnlyList<string> Keys = ["os", "os-version", "arch", "cpu", "cores", "memory", "gpu", "gpu-memory", "gpus", "tag"];
    private static readonly HashSet<string> Numeric = ["cores", "memory", "gpu-memory", "gpus"];
    public const int MaxPerJob = 16;

    public override string ToString() => $"{Key}{Op}{Value}";

    /// <summary>Parses <c>memory&gt;=32</c>, <c>os=linux</c>, <c>gpu~rtx</c>.</summary>
    public static JobRequirement Parse(string text)
    {
        var m = Pattern().Match(text.Trim());
        if (!m.Success)
        {
            throw new FormatException($"'{text}' isn't a requirement: key op value, e.g. memory>=32, os=linux, gpu~rtx");
        }

        var (key, op, value) = (m.Groups[1].Value.ToLowerInvariant(), m.Groups[2].Value, m.Groups[3].Value.Trim());
        if (!Keys.Contains(key))
        {
            throw new FormatException($"'{key}' isn't a requirement key ({string.Join(", ", Keys)})");
        }

        if (Numeric.Contains(key) && !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
        {
            throw new FormatException($"'{key}' is a number");
        }

        if (op is ">=" or "<=" && !Numeric.Contains(key))
        {
            throw new FormatException($"'{key}' isn't a number: use =, != or ~");
        }

        if (value.Length is 0 or > 100)
        {
            throw new FormatException($"'{text}': a value is 1-100 characters");
        }

        return new JobRequirement(key, op, value);
    }

    /// <summary>Whether <paramref name="machine"/> meets this (a machine that reported nothing meets nothing).</summary>
    public bool IsMetBy(MachineCapabilities? machine)
    {
        if (machine is null)
        {
            return false;
        }

        return Key switch
        {
            "os" => Text(machine.Os),
            "os-version" => Text(machine.OsVersion),
            "arch" => Text(machine.Arch),
            "cpu" => Text(machine.Cpu ?? ""),
            "cores" => Number(machine.Cores),
            "memory" => Number(machine.MemoryGb),
            "gpus" => Number(machine.Gpus.Count),
            "gpu" => Op == "!=" ? machine.Gpus.All(g => !Equal(g.Name)) : machine.Gpus.Any(g => Text(g.Name)),
            "gpu-memory" => machine.Gpus.Any(g => g.MemoryGb is { } m && Number(m)),
            "tag" => Op == "!=" ? !machine.Tags.Any(Equal) : machine.Tags.Any(Text),
            _ => false,
        };
    }

    private bool Equal(string actual) => string.Equals(actual, Value, StringComparison.OrdinalIgnoreCase);

    private bool Text(string actual) => Op switch
    {
        "=" => Equal(actual),
        "!=" => !Equal(actual),
        "~" => actual.Contains(Value, StringComparison.OrdinalIgnoreCase),
        _ => false,
    };

    private bool Number(double actual)
    {
        var wanted = double.Parse(Value, CultureInfo.InvariantCulture);
        return Op switch
        {
            "=" => Math.Abs(actual - wanted) < 0.05,
            "!=" => Math.Abs(actual - wanted) >= 0.05,
            ">=" => actual >= wanted - 0.05,
            "<=" => actual <= wanted + 0.05,
            _ => false,
        };
    }

    /// <summary>The requirements <paramref name="machine"/> doesn't meet (empty: it qualifies).</summary>
    public static IReadOnlyList<JobRequirement> Unmet(IReadOnlyList<JobRequirement>? requirements, MachineCapabilities? machine) =>
        [.. (requirements ?? []).Where(r => !r.IsMetBy(machine))];

    [GeneratedRegex(@"^([a-zA-Z-]+)\s*(>=|<=|!=|=|~)\s*(.+)$")]
    private static partial Regex Pattern();
}
