using System.Diagnostics;
using System.Text;

namespace PvzBackupTool;

public sealed record AdbResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Ok => ExitCode == 0;
    public string All => (StdOut + "\n" + StdErr).Trim();
}

public sealed record DeviceInfo(string Serial, string State, string Model)
{
    public bool IsReady => State == "device";
}

/// <summary>Thin wrapper around the adb executable.</summary>
public sealed class Adb(string exePath)
{
    public string ExePath { get; } = exePath;

    public async Task<AdbResult> RunAsync(string args, CancellationToken ct, int timeoutMs = 120_000)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ExePath,
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        proc.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };

        if (!proc.Start())
            throw new InvalidOperationException("Failed to start adb.");

        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeoutMs);

        try
        {
            await proc.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(proc);
            throw;
        }

        // Let the async readers drain.
        proc.WaitForExit();

        return new AdbResult(proc.ExitCode, stdout.ToString().Trim(), stderr.ToString().Trim());
    }

    static void TryKill(Process p)
    {
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { /* best effort */ }
    }

    /// <summary>Runs a command inside the device shell.</summary>
    public Task<AdbResult> ShellAsync(string cmd, CancellationToken ct, int timeoutMs = 120_000)
        => RunAsync($"shell \"{cmd.Replace("\"", "\\\"")}\"", ct, timeoutMs);

    public async Task<List<DeviceInfo>> ListDevicesAsync(CancellationToken ct)
    {
        var r = await RunAsync("devices -l", ct, 30_000).ConfigureAwait(false);
        var list = new List<DeviceInfo>();

        foreach (var raw in r.StdOut.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("List of devices")) continue;
            if (line.StartsWith("*")) continue; // daemon chatter

            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;

            var model = parts.FirstOrDefault(p => p.StartsWith("model:"))?["model:".Length..] ?? "unknown";
            list.Add(new DeviceInfo(parts[0], parts[1], model));
        }
        return list;
    }

    /// <summary>Lists *.zip names in a device directory. Empty list if the directory is absent.</summary>
    public async Task<HashSet<string>> ListBackupZipsAsync(string deviceDir, CancellationToken ct)
    {
        var r = await ShellAsync($"ls -1 {deviceDir} 2>/dev/null", ct, 30_000).ConfigureAwait(false);
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in r.StdOut.Split('\n'))
        {
            var name = raw.Trim();
            if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                set.Add(name);
        }
        return set;
    }

    public async Task<long> FileSizeAsync(string devicePath, CancellationToken ct)
    {
        var r = await ShellAsync($"stat -c %s {devicePath} 2>/dev/null", ct, 30_000).ConfigureAwait(false);
        return long.TryParse(r.StdOut.Trim(), out var n) ? n : -1;
    }

    /// <summary>
    /// Waits until a file's size stops changing. The MIUI backup zip is written
    /// incrementally, and pulling it mid-write yields a truncated archive with no
    /// central directory, so this gate is required before pulling.
    /// </summary>
    public async Task<long> WaitForStableSizeAsync(
        string devicePath, CancellationToken ct, Action<string>? log = null,
        int stableReads = 4, int pollMs = 3000, int maxWaitMs = 45 * 60 * 1000)
    {
        long last = -1;
        int stable = 0;
        var started = Environment.TickCount64;

        while (Environment.TickCount64 - started < maxWaitMs)
        {
            ct.ThrowIfCancellationRequested();

            var size = await FileSizeAsync(devicePath, ct).ConfigureAwait(false);
            if (size < 0)
            {
                log?.Invoke("  file vanished from device; aborting wait");
                return -1;
            }

            if (size == last && size > 0)
            {
                stable++;
                if (stable >= stableReads) return size;
            }
            else
            {
                if (size != last)
                    log?.Invoke($"  writing... {Fmt.Bytes(size)}");
                stable = 0;
            }

            last = size;
            await Task.Delay(pollMs, ct).ConfigureAwait(false);
        }

        throw new TimeoutException("Timed out waiting for the backup file to finish writing.");
    }
}
