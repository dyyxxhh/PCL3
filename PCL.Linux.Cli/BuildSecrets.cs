using System.Text;

namespace PCL.Linux.Cli;

internal static partial class LinuxSecrets
{
    // The release key is an application credential, not a user credential.
    // This encoding only avoids incidental plain-text disclosure; a determined
    // user can recover any key distributed inside a client executable.
    private static string? Decode(byte[] bytes)
    {
        if (bytes.Length == 0) return null;
        for (var index = 0; index < bytes.Length; index++) bytes[index] ^= 0xA7;
        return Encoding.UTF8.GetString(bytes);
    }
}
