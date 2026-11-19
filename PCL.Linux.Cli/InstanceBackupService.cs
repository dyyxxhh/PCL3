using System.Security.Cryptography;
using System.Text;
using PCL.Core.Shared.Minecraft;

namespace PCL.Linux.Cli;

internal static class InstanceBackupService
{
    private static InstanceBackupStore Store(ManagedMinecraftInstance instance)
    {
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(instance.DirectoryPath))));
        // Keep backups on the same filesystem as the instance when possible.
        return new InstanceBackupStore(Path.Combine(Path.GetDirectoryName(instance.DirectoryPath)!, ".mcm-backups", id));
    }

    internal static async Task<string> CreateAsync(ManagedMinecraftInstance instance, bool automatic, string? scope = null)
    {
        scope ??= McmConfiguration.Get("backup.scope", instance.Name) ?? "saves";
        var retention = int.TryParse(McmConfiguration.Get("backup.retention", instance.Name), out var value) ? value : 5;
        var result = await Store(instance).CreateAsync(scope == "saves" ? Path.Combine(instance.DirectoryPath, "saves") : instance.DirectoryPath,
            instance.Name, instance.LaunchVersion, scope, automatic, retention).ConfigureAwait(false);
        if (result.UsedOrdinaryCopy) Console.Error.WriteLine("Some files used ordinary copies and may require more disk space.");
        return result.Path;
    }

    internal static IReadOnlyList<string> List(ManagedMinecraftInstance instance) => Store(instance).List();

    internal static async Task<string> RestoreAsync(ManagedMinecraftInstance instance, string backup, bool clone)
    {
        backup = Path.GetFullPath(backup);
        var manifest = await Store(instance).InspectAsync(backup, instance.Name, instance.LaunchVersion).ConfigureAwait(false);
        var cloneName = instance.Name + "-restored-" + Guid.NewGuid().ToString("N")[..8];
        var parent = Path.GetDirectoryName(instance.DirectoryPath)!;
        var destination = clone ? Path.Combine(parent, cloneName) : instance.DirectoryPath;
        var staging = Path.Combine(parent, ".migration-restore-" + Guid.NewGuid().ToString("N"));
        try
        {
            if (clone && manifest.Scope == "saves")
            {
                await InstanceBackupStore.CopyTreeAsync(instance.DirectoryPath, staging).ConfigureAwait(false);
                var currentSaves = Path.Combine(staging, "saves");
                if (Directory.Exists(currentSaves)) Directory.Delete(currentSaves, true);
            }
            var payloadStage = clone && manifest.Scope == "saves" ? Path.Combine(staging, "saves") : staging;
            await InstanceBackupStore.RestorePayloadAsync(backup, manifest, payloadStage).ConfigureAwait(false);
            if (clone)
            {
                await ManagedInstanceService.PublishStagedAsync(instance with { Name = cloneName, DirectoryPath = destination }, staging).ConfigureAwait(false);
            }
            else
            {
                await CreateAsync(instance, false, "instance").ConfigureAwait(false);
                var target = manifest.Scope == "saves" ? Path.Combine(destination, "saves") : destination;
                InstanceBackupStore.ReplaceDirectory(staging, target);
            }
            return clone ? cloneName : instance.Name;
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
        }
    }
}
