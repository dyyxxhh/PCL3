using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using PCL.Core.Shared;
using PCL.Core.Shared.Minecraft;

namespace PCL.Linux.Cli;

internal sealed class LinuxGameIntro(Process helper, GameIntroLaunch launch) : IDisposable
{
    private const string EmbeddedHost = "PCL3_LinuxGameIntroHost.py";
    // Leave ample room below Linux's per-argument size limit, including on 4 KiB page systems.
    private const int MaxHostBytes = 32 * 1024;

    internal GameIntroLaunch Launch { get; } = launch;

    internal static async Task<LinuxGameIntro?> TryStartAsync(string profile = "vanilla-1.21.1")
    {
        var wayland = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));
        if (!GameIntroLaunch.IsEnabled(Environment.GetEnvironmentVariable("PCL3_GAME_INTRO_PREVIEW")) ||
            (!wayland && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DISPLAY"))) ||
            !File.Exists("/usr/bin/python3")) return null;
        Process? host = null;
        try
        {
            var script = Environment.GetEnvironmentVariable("PCL3_GAME_INTRO_LINUX_HOST");
            string? source = null;
            if (string.IsNullOrEmpty(script))
            {
                // Invoke the release's embedded code directly rather than executing a writable cached script.
                using var resource = typeof(LinuxGameIntro).Assembly.GetManifestResourceStream(EmbeddedHost);
                if (resource is null || resource.Length is <= 0 or > MaxHostBytes) return null;
                using var reader = new StreamReader(resource, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: false);
                source = reader.ReadToEnd();
                if (source.Contains('\0')) return null;
            }
            else
            {
                // Explicit override for developer fixtures; never search the working directory or PATH.
                if (!Path.IsPathFullyQualified(script) || !File.Exists(script)) return null;
                InstanceBackupStore.RequireSafePath(script);
            }
            if (!GameIntroBundle.TryPrepare(out var clip, out var agent) || agent.Contains('=')) return null;
            var nonce = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
            var start = new ProcessStartInfo("/usr/bin/python3")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardInput = true
            };
            start.ArgumentList.Add("-I");
            if (source is not null)
            {
                start.ArgumentList.Add("-c");
                start.ArgumentList.Add(source);
            }
            else
            {
                start.ArgumentList.Add(script!);
            }
            start.ArgumentList.Add(clip);
            start.Environment["GDK_BACKEND"] = wayland ? "wayland" : "x11";
            host = Process.Start(start);
            if (host is null) return null;
            await host.StandardInput.WriteLineAsync(nonce).ConfigureAwait(false);
            host.StandardInput.Close();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var line = await host.StandardOutput.ReadLineAsync(deadline.Token).ConfigureAwait(false);
            if (!ushort.TryParse(line, NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port == 0) return null;
            var result = new LinuxGameIntro(host, new GameIntroLaunch(agent, $"{port.ToString(CultureInfo.InvariantCulture)}:{nonce}", profile));
            host = null;
            return result;
        }
        catch (Exception failure) when (failure is IOException or InvalidOperationException or UnauthorizedAccessException or
            System.ComponentModel.Win32Exception or OperationCanceledException or DecoderFallbackException)
        {
            return null;
        }
        finally
        {
            if (host is not null) Stop(host);
        }
    }

    public void Dispose() => Stop(helper);

    private static void Stop(Process process)
    {
        try { if (!process.HasExited) process.Kill(); }
        catch (Exception failure) when (failure is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        finally { process.Dispose(); }
    }
}
