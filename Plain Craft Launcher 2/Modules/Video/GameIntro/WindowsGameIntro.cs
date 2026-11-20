using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using PCL.Core.App;
using PCL.Core.Shared;
using PCL.Core.Shared.Minecraft;

namespace PCL;

internal static class WindowsGameIntro
{
    internal const string HostArgument = "--game-intro-host";

    // Preview remains opt-in until the platform and loader compatibility checks are complete.
    internal static void TryAttach(ProcessStartInfo game, McInstance instance)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("PCL3_GAME_INTRO_PREVIEW"), "1", StringComparison.Ordinal) ||
            GameStartupOptionsFile.IsFullscreen(instance.PathIndie) ||
            game.Arguments.Contains("--quickPlay", StringComparison.OrdinalIgnoreCase) ||
            game.Arguments.Contains("--fullscreen", StringComparison.OrdinalIgnoreCase)) return;

        var info = instance.Info;
        if (!info.Reliable || info.HasQuilt || info.HasLegacyFabric || info.HasOptiFine || info.HasCleanroom || info.HasLiteLoader || info.HasLabyMod) return;
        var (loader, version) = (info.HasFabric, info.HasForge, info.HasNeoForge) switch
        {
            (true, false, false) => ("fabric", info.Fabric),
            (false, true, false) => ("forge", info.Forge),
            (false, false, true) => ("neoforge", info.NeoForge),
            (false, false, false) => ("vanilla", (string?)null),
            _ => ("unsupported", null)
        };
        var profile = GameIntroLaunch.SelectProfile(info.VanillaName, loader, version);
        if (profile is null || !GameIntroLaunch.MatchesEntryPoint(profile, instance.JsonObject["mainClass"]?.ToString())) return;

        Process? host = null;
        var handedOff = false;
        try
        {
            if (!GameIntroBundle.TryPrepare(out var video, out var agent) || agent.Contains('=') || Environment.ProcessPath is not { } executable) return;
            var nonce = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
            var startInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardInput = true
            };
            if (string.Equals(Path.GetFileName(executable), "dotnet.exe", StringComparison.OrdinalIgnoreCase))
                startInfo.ArgumentList.Add(typeof(WindowsGameIntro).Assembly.Location);
            startInfo.ArgumentList.Add(HostArgument);
            startInfo.ArgumentList.Add(video);
            host = Process.Start(startInfo);
            if (host is null) return;
            host.StandardInput.WriteLine(nonce);
            host.StandardInput.Close();
            using var startupLimit = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var announcement = host.StandardOutput.ReadLineAsync(startupLimit.Token).AsTask().GetAwaiter().GetResult();
            if (!ushort.TryParse(announcement, NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port == 0) return;
            game.Environment["PCL_GAME_INTRO_ENDPOINT"] = FormattableString.Invariant($"{port}:{nonce}");
            game.Arguments = $"\"-javaagent:{agent}={profile}\" {game.Arguments}";
            handedOff = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception or OperationCanceledException)
        {
            Common.Log(ex, "[Launch] 游戏片头预览无法启动");
        }
        finally
        {
            if (host is not null)
            {
                if (!handedOff)
                {
                    try { if (!host.HasExited) host.Kill(); }
                    catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                }
                host.Dispose();
            }
        }
    }

    internal static int RunHost(string[] args)
    {
        if (args.Length != 2 || args[0] != HostArgument || !Path.IsPathFullyQualified(args[1]) ||
            !GameIntroBundle.TryPrepare(out var verifiedClip, out _) ||
            !Path.GetFullPath(args[1]).Equals(Path.GetFullPath(verifiedClip), StringComparison.OrdinalIgnoreCase)) return 2;
        if (Console.IsInputRedirected == false) return 2;
        using var inputDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        string? nonce;
        try { nonce = ReadBoundedLineAsync(Console.In, 65, inputDeadline.Token).GetAwaiter().GetResult(); }
        catch (Exception ex) when (ex is IOException or OperationCanceledException) { return 2; }
        if (nonce is null || nonce.Length != 64 || nonce.Any(ch => ch is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))) return 2;
        var clip = verifiedClip;
        var launcher = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var surface = new WindowsIntroSurface(new Uri(clip));
        var session = new GameIntroSession();
        var clock = Stopwatch.StartNew();
        var timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        using var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start(1);
        Console.WriteLine(((IPEndPoint)server.LocalEndpoint).Port.ToString(CultureInfo.InvariantCulture));
        Console.Out.Flush();
        using var cancel = new CancellationTokenSource();
        StreamWriter? output = null;
        var stopped = false;

        void Apply(GameIntroEvent input)
        {
            if (stopped) return;
            var frame = session.Apply(input, clock.Elapsed);
            surface.Present(frame);
            if (frame.PlayIntroAudio) return;
            stopped = true;
            timer.Stop();
            try
            {
                surface.Stop();
                _ = session.Apply(GameIntroEvent.AudioStopped, clock.Elapsed);
                output?.WriteLine("RELEASE");
            }
            catch (IOException) { }
            finally
            {
                cancel.Cancel();
                launcher.Shutdown();
            }
        }

        async Task ReceiveAsync()
        {
            try
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                while (true)
                {
                    using var client = await server.AcceptTcpClientAsync(deadline.Token);
                    client.NoDelay = true;
                    using var stream = client.GetStream();
                    using var input = new StreamReader(stream, Encoding.ASCII);
                    using var writer = new StreamWriter(stream, Encoding.ASCII) { AutoFlush = true };
                    string? helloLine;
                    using (var handshakeLimit = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token))
                    {
                        handshakeLimit.CancelAfter(TimeSpan.FromSeconds(2));
                        try { helloLine = await ReadBoundedLineAsync(input, 128, handshakeLimit.Token); }
                        catch (OperationCanceledException) when (!deadline.IsCancellationRequested) { continue; }
                    }
                    var hello = helloLine?.Split(' ');
                    if (hello is not ["HELLO", var actual, var pidText] || actual.Length != nonce.Length ||
                        !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(actual), Encoding.ASCII.GetBytes(nonce)) ||
                        !uint.TryParse(pidText, NumberStyles.None, CultureInfo.InvariantCulture, out var gamePid) || gamePid == 0)
                        continue;
                    server.Stop();
                    output = writer;
                    var windowBound = false;
                    var held = false;
                    try
                    {
                        while (await ReadBoundedLineAsync(input, 64, cancel.Token) is { } message)
                        {
                            switch (message)
                            {
                                case "HELD" when windowBound && !held:
                                    held = true;
                                    surface.Arm();
                                    break;
                                case "READY" when held: Apply(GameIntroEvent.GameReady); break;
                                case "SPACE_DOWN" when held: Apply(GameIntroEvent.SpaceDown); break;
                                case "SPACE_UP" when held: Apply(GameIntroEvent.SpaceUp); break;
                                case "BYPASS": Apply(GameIntroEvent.Failure); break;
                                default:
                                    if (windowBound || !message.StartsWith("WINDOW Win32 ", StringComparison.Ordinal) ||
                                        !nuint.TryParse(message.AsSpan("WINDOW Win32 ".Length), NumberStyles.None, CultureInfo.InvariantCulture, out var handle) ||
                                        !surface.Bind((nint)handle, gamePid))
                                    {
                                        Apply(GameIntroEvent.Failure);
                                        return;
                                    }
                                    windowBound = true;
                                    await writer.WriteLineAsync("HOLD");
                                    break;
                            }
                        }
                    }
                    finally
                    {
                        Apply(GameIntroEvent.Failure);
                        output = null;
                    }
                    return;
                }
            }
            catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException or ObjectDisposedException) { }
            finally
            {
                Apply(GameIntroEvent.Failure);
            }
        }

        launcher.Startup += (_, _) =>
        {
            surface.Failed += () => Apply(GameIntroEvent.Failure);
            surface.PrepareMedia();
            if (stopped) return;
            timer.Tick += (_, _) => Apply(surface.Refresh() ? GameIntroEvent.Tick : GameIntroEvent.Failure);
            timer.Start();
            _ = ReceiveAsync();
        };
        return launcher.Run();
    }

    private static async Task<string?> ReadBoundedLineAsync(TextReader reader, int limit, CancellationToken token)
    {
        var buffer = new char[limit];
        var character = new char[1];
        for (var length = 0; length <= buffer.Length; length++)
        {
            if (await reader.ReadAsync(character.AsMemory(), token) == 0) return null;
            if (character[0] == '\n') return new string(buffer, 0, length).TrimEnd('\r');
            if (length == buffer.Length) return null;
            buffer[length] = character[0];
        }
        return null;
    }
}
