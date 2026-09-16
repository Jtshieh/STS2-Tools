using System.Text;
using System.Text.Json;

// Read the outer process's original stdout/stderr files through read-only binds. Do not change any log sink.
internal sealed class NormalBootLogs
{
    private readonly Dictionary<string, byte[]> prefixes = new(StringComparer.Ordinal);
    internal sealed record StreamSnapshot(string Path, int CapturedBytes, int CompleteLineBytes, string Sha256,
        int Lines, bool TrailingPartialLine, string[] ErrorLines, string[] WarningLines, string[] EssentialLines, string[] CompleteLines);
    internal sealed record Snapshot(StreamSnapshot Stdout, StreamSnapshot Stderr)
    {
        internal bool EssentialObserved => Stdout.EssentialLines.Length + Stderr.EssentialLines.Length > 0;
        internal bool CompleteObserved => Stdout.CompleteLines.Length + Stderr.CompleteLines.Length > 0;
        internal int ErrorCount => Stdout.ErrorLines.Length + Stderr.ErrorLines.Length;
    }

    internal Snapshot Read()
    {
        var config = NormalBootConfig.Current;
        return new(Read(config.ObservedStdoutPath), Read(config.ObservedStderrPath));
    }

    private StreamSnapshot Read(string path)
    {
        ManagedBootstrap.Require(new FileInfo(path).LinkTarget is null, "Log observation target is a symlink");
        string[] mounts = File.ReadAllLines("/proc/self/mountinfo").Where(line =>
        {
            string[] fields = line.Split(' ');
            return fields.Length > 6 && fields[4] == path && fields[5].Split(',').Contains("ro", StringComparer.Ordinal);
        }).ToArray();
        ManagedBootstrap.Require(mounts.Length == 1, "Each complete process log must have one explicit read-only file bind: " + path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        long length = stream.Length;
        ManagedBootstrap.Require(length is >= 0 and <= 16777216, "Log exceeded the explicit 16 MiB per-stream bound");
        byte[] bytes = new byte[checked((int)length)];
        stream.ReadExactly(bytes);
        ManagedBootstrap.Require(stream.Length >= length, "Outer log was truncated while being observed");
        if (prefixes.TryGetValue(path, out byte[]? previous))
            ManagedBootstrap.Require(bytes.Length >= previous.Length && bytes.AsSpan(0, previous.Length).SequenceEqual(previous),
                "Outer log prefix was replaced or truncated; evidence is no longer append-only");
        prefixes[path] = bytes;
        int complete = Array.LastIndexOf(bytes, (byte)'\n') + 1;
        string text = new UTF8Encoding(false, true).GetString(bytes, 0, complete);
        string[] lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        string[] errors = lines.Where(line => line.Contains("[ERROR]", StringComparison.Ordinal)
            || line.StartsWith("ERROR:", StringComparison.Ordinal)
            || line.StartsWith("SCRIPT ERROR:", StringComparison.Ordinal)
            || line.Contains("Unhandled exception", StringComparison.OrdinalIgnoreCase)).ToArray();
        string[] warnings = lines.Where(line => line.Contains("[WARN]", StringComparison.Ordinal)
            || line.Contains("[WARNING]", StringComparison.Ordinal) || line.StartsWith("WARNING:", StringComparison.Ordinal)).ToArray();
        return new(path, bytes.Length, complete, ManagedBootstrap.Sha256(bytes), lines.Length, complete != bytes.Length,
            errors, warnings,
            lines.Where(line => line.Contains("main menu loaded (essential)", StringComparison.Ordinal)).ToArray(),
            lines.Where(line => line.Contains("main menu loaded (complete)", StringComparison.Ordinal)).ToArray());
    }

    internal static object Evidence(Snapshot snapshot) => new
    {
        stdout = snapshot.Stdout, stderr = snapshot.Stderr, essentialLogObserved = snapshot.EssentialObserved,
        deferredCompleteLogObserved = snapshot.CompleteObserved, detectedErrorLineCount = snapshot.ErrorCount,
        fullOriginalLogReviewRequired = true,
        diagnosticClassification = "All matching lines retained without an allowlist; absence of these patterns is not proof of no startup error",
        warningPolicy = "Original full logs retained for the concrete E003/D026 semantic comparison; warning count alone never accepts or rejects progress"
    };
}
