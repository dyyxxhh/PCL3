using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace PCL.Core.Shared.Minecraft;

public sealed class InstanceBackupStore(string directory)
{
    public sealed record FileEntry(string Path, long Length, string Sha256);
    public sealed record Manifest(string Identity, string Version, string Scope, bool Automatic, FileEntry[] Files);
    public sealed record Result(string Path, bool UsedOrdinaryCopy);

    private string Root { get; } = Path.GetFullPath(directory);
    private const string ManifestName = "backup.json";
    private const string PayloadName = "data";
    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public IReadOnlyList<string> List()
    {
        RequireSafePath(Root);
        if (!Directory.Exists(Root)) return [];
        return Directory.EnumerateDirectories(Root).Where(path => !Path.GetFileName(path).StartsWith('.') &&
            (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0 && File.Exists(Path.Combine(path, ManifestName)))
            .OrderDescending(StringComparer.Ordinal).ToArray();
    }

    public async Task<Result> CreateAsync(string source, string identity, string version, string scope, bool automatic,
        int retention = 5, CancellationToken cancellationToken = default)
    {
        if (scope is not ("saves" or "instance")) throw new ArgumentException("Invalid backup scope.");
        source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source));
        RequireSafePath(source);
        RequireSafePath(Root);
        if (Root.Equals(source, PathComparison) || Root.StartsWith(source + Path.DirectorySeparatorChar, PathComparison))
            throw new IOException("Backup storage must be outside the source directory.");
        if (scope == "instance" && !Directory.Exists(source)) throw new DirectoryNotFoundException(source);
        Directory.CreateDirectory(Root);
        var id = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmssfffffff'Z'", System.Globalization.CultureInfo.InvariantCulture)
            + "-" + Guid.NewGuid().ToString("N");
        var stage = Path.Combine(Root, "." + id);
        var destination = Path.Combine(Root, id);
        try
        {
            var payload = Path.Combine(stage, PayloadName);
            var copied = await CopyTreeAsync(source, payload, cancellationToken).ConfigureAwait(false);
            var files = await InventoryAsync(payload, cancellationToken).ConfigureAwait(false);
            var manifest = new Manifest(identity, version, scope, automatic, files);
            await File.WriteAllTextAsync(Path.Combine(stage, ManifestName), JsonSerializer.Serialize(manifest), cancellationToken).ConfigureAwait(false);
            Directory.Move(stage, destination);
            if (automatic) Prune(Math.Clamp(retention, 1, 100));
            return new Result(destination, copied);
        }
        finally { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
    }

    public async Task<Manifest> InspectAsync(string backup, string identity, string version, CancellationToken cancellationToken = default)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(backup));
        if (!string.Equals(Path.GetDirectoryName(full), Root, PathComparison) || Path.GetFileName(full).StartsWith('.'))
            throw new InvalidDataException("Backup must belong to this instance's backup directory.");
        var metadata = Path.Combine(full, ManifestName);
        RequireSafePath(metadata);
        var text = await File.ReadAllTextAsync(metadata, cancellationToken).ConfigureAwait(false);
        var manifest = JsonSerializer.Deserialize<Manifest>(text);
        if (manifest is null || manifest.Identity != identity || manifest.Version != version ||
            manifest.Scope is not ("saves" or "instance") || manifest.Files is null)
            throw new InvalidDataException("Backup has an invalid manifest or targets a different instance version.");
        return manifest;
    }

    public static async Task RestorePayloadAsync(string backup, Manifest manifest, string staging, CancellationToken cancellationToken = default)
    {
        var payload = Path.Combine(backup, PayloadName);
        RequireSafePath(payload);
        if (!Directory.Exists(payload)) throw new InvalidDataException("The backup has no payload.");
        await CopyTreeAsync(payload, staging, cancellationToken).ConfigureAwait(false);
        var files = await InventoryAsync(staging, cancellationToken).ConfigureAwait(false);
        if (!manifest.Files.SequenceEqual(files)) throw new InvalidDataException("Backup integrity verification failed.");
    }

    public static void ReplaceDirectory(string staging, string destination)
    {
        RequireSafePath(staging);
        RequireSafePath(destination);
        var old = destination + ".previous-" + Guid.NewGuid().ToString("N");
        var existed = Directory.Exists(destination);
        if (existed) Directory.Move(destination, old);
        try { Directory.Move(staging, destination); }
        catch { if (existed) Directory.Move(old, destination); throw; }
        if (existed)
        {
            // The swap committed. A cleanup failure must not roll it back.
            try { Directory.Delete(old, true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    public static void RequireSafePath(string path)
    {
        for (var part = Path.GetFullPath(path); part is not null; part = Path.GetDirectoryName(part))
        {
            try
            {
                if ((File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException($"Symbolic links and reparse points are unsupported: {part}");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    public static async Task<bool> CopyTreeAsync(string source, string destination, CancellationToken cancellationToken = default)
    {
        RequireSafePath(source);
        RequireSafePath(destination);
        Directory.CreateDirectory(destination);
        if (!Directory.Exists(source)) return false;
        return await CopyContentsAsync(source, destination, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> CopyContentsAsync(string source, string destination, CancellationToken cancellationToken)
    {
        var copied = false;
        foreach (var path in Directory.EnumerateFileSystemEntries(source))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var attributes = SafeAttributes(path);
            var target = Path.Combine(destination, Path.GetFileName(path));
            if ((attributes & FileAttributes.Directory) != 0)
            {
                Directory.CreateDirectory(target);
                copied |= await CopyContentsAsync(path, target, cancellationToken).ConfigureAwait(false);
                continue;
            }
            await using var input = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            await using var output = File.Open(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            if (input.Length == 0 || TryClone(input, output)) continue;
            input.Position = 0;
            output.SetLength(0);
            await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            copied = true;
        }
        return copied;
    }

    private static bool TryClone(FileStream input, FileStream output)
    {
        if (OperatingSystem.IsLinux())
            return Reflink(output.SafeFileHandle, 0x40049409, input.SafeFileHandle) == 0;
        if (!OperatingSystem.IsWindows()) return false;
        // Try 64 KiB-aligned extents, which also align on 4 KiB ReFS volumes.
        const long alignment = 65536;
        const long maxExtent = 1024 * 1024 * 1024;
        var aligned = input.Length / alignment * alignment;
        if (aligned == 0) return false;
        output.SetLength(input.Length);
        for (long offset = 0; offset < aligned; offset += maxExtent)
        {
            var extent = new CloneExtent(input.SafeFileHandle.DangerousGetHandle(), offset, offset, Math.Min(maxExtent, aligned - offset));
            if (!CloneExtents(output.SafeFileHandle, 0x00098344, ref extent, Marshal.SizeOf<CloneExtent>(), IntPtr.Zero, 0, out _, IntPtr.Zero))
                return false;
        }
        input.Position = output.Position = aligned;
        input.CopyTo(output);
        GC.KeepAlive(input);
        return true;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct CloneExtent(IntPtr Source, long SourceOffset, long TargetOffset, long Length);

    [DllImport("libc", EntryPoint = "ioctl")]
    private static extern int Reflink(SafeFileHandle destination, nuint request, SafeFileHandle source);

    [DllImport("kernel32.dll", EntryPoint = "DeviceIoControl")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloneExtents(SafeFileHandle target, uint code, ref CloneExtent extent, int size,
        IntPtr output, int outputSize, out int returned, IntPtr overlapped);

    private static async Task<FileEntry[]> InventoryAsync(string root, CancellationToken cancellationToken)
    {
        var entries = new List<FileEntry>();
        await VisitAsync(root).ConfigureAwait(false);
        return entries.OrderBy(entry => entry.Path, StringComparer.Ordinal).ToArray();

        async Task VisitAsync(string folder)
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(folder))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if ((SafeAttributes(path) & FileAttributes.Directory) != 0)
                {
                    entries.Add(new FileEntry(Path.GetRelativePath(root, path) + "/", -1, ""));
                    await VisitAsync(path).ConfigureAwait(false);
                }
                else
                {
                    await using var input = File.OpenRead(path);
                    entries.Add(new FileEntry(Path.GetRelativePath(root, path), input.Length,
                        Convert.ToHexString(await SHA256.HashDataAsync(input, cancellationToken).ConfigureAwait(false))));
                }
            }
        }
    }

    private static FileAttributes SafeAttributes(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
            throw new IOException($"Unsupported file in backup: {path}");
        return attributes;
    }

    private void Prune(int retention)
    {
        foreach (var backup in List())
        {
            var path = Path.Combine(backup, ManifestName);
            try
            {
                RequireSafePath(path);
                if (JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path))?.Automatic != true) continue;
                if (retention-- <= 0) Directory.Delete(backup, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        }
    }
}
