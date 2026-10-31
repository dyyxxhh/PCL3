using System;
using PCL.Core.App.IoC;
using PCL.Core.Logging;

namespace PCL.Core.App.Essentials;

/// <summary>
/// PCL₃ does not initialize Sentry or collect device-environment telemetry.
/// Keep the logging hook as a no-op for callers shared with upstream.
/// </summary>
[LifecycleScope("telemetry", "遥测")]
[LifecycleService(LifecycleState.Running)]
public sealed partial class TelemetryService
{
    public static bool IsEnabled => false;

    [LifecycleStart]
    private static void _Start()
    {
        // Explicitly override saved opt-ins from older or upstream installations.
        Config.System.Telemetry = false;
        Context.Info("PCL₃ 已禁用遥测、Sentry 和设备环境调查");
    }

    public static void ReportException(Exception ex, string plain, LogLevel level)
    {
        // Local logs remain available; no data is uploaded.
    }
}
