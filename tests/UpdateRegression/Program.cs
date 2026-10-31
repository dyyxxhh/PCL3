using PCL;
using PCL.Network;
using PCL.Core.Utils;

var failed = 0;
void Test(string name, Action body)
{
    try { body(); Console.WriteLine($"PASS {name}"); }
    catch (Exception ex) { failed++; Console.WriteLine($"FAIL {name}: {ex.Message}"); }
}
void Assert(bool value, string message) { if (!value) throw new Exception(message); }
Requester.LatestHtml = "<html><head><meta property=\"og:url\" content=\"https://github.com/dyyxxhh/PCL3/releases/tag/v0.1.7\"></head></html>";
Test("release page check bypasses REST API 403 and checksum asset", () =>
{
    Requester.FetchJsonRequests = 0;
    Requester.AssetRequests = 0;
    Requester.StringError = null;
    Assert(new UpdatesGithubReleaseModel().IsLatest(UpdateChannel.stable, UpdateArch.x64, SemVer.Parse("0.1.7"), 107), "not latest");
    Assert(Requester.FetchJsonRequests == 0, "REST API was called");
    Assert(Requester.AssetRequests == 0, "checksum asset was requested during version comparison");
});
Test("older local build is detected using public release page", () =>
    Assert(!new UpdatesGithubReleaseModel().IsLatest(UpdateChannel.stable, UpdateArch.x64, SemVer.Parse("0.1.5"), 105), "update missing"));
Test("wrapper preserves the original release page failure", () =>
{
    var original = new IOException("403 Forbidden");
    Requester.StringError = original;
    try { new UpdatesWrapperModel([new UpdatesGithubReleaseModel()]).IsLatest(UpdateChannel.stable, UpdateArch.x64, SemVer.Parse("0.1.7"), 107); throw new Exception("no exception"); }
    catch (Exception ex) { Assert(ReferenceEquals(ex, original) || ReferenceEquals(ex.InnerException, original), "original network error discarded"); }
    finally { Requester.StringError = null; }
});
Test("release asset checksum is validated", () =>
{
    Requester.StringError = null;
    Requester.Sha256 = new string('a', 64);
    Assert(new UpdatesGithubReleaseModel().GetLatestVersion(UpdateChannel.stable, UpdateArch.x64).Sha256 == Requester.Sha256, "valid checksum rejected");
    Requester.Sha256 = "invalid-checksum";
    try { new UpdatesGithubReleaseModel().GetLatestVersion(UpdateChannel.stable, UpdateArch.x64); throw new Exception("accepted invalid hash"); }
    catch (Exception ex) when (ex.Message != "accepted invalid hash") { }
});
Environment.ExitCode = failed == 0 ? 0 : 1;

namespace PCL.Core.App { }
namespace PCL.Core.App.Localization { public static class Lang { public static string Text(string key) => key; } }
namespace PCL
{
    public static class Common
    {
        public static void Log(string text) { }
        public enum LogLevel { Normal }
        public static void Log(Exception ex, string text, LogLevel level = LogLevel.Normal) { }
    }
    public static class Loading { public class LoaderBase { } }
    public class DownloadFile(string[] urls, string output) { }
}
namespace PCL.Network.Loaders
{
    public class LoaderDownload(string name, List<PCL.DownloadFile> files) : PCL.Loading.LoaderBase { }
}
namespace PCL.Network
{
    public struct RequestParam
    {
        public bool UseBrowserUserAgent { get; set; }
        public static RequestParam WithRetry => new();
    }
    public static class Requester
    {
        public static string LatestHtml = "";
        public static Exception? StringError;
        public static string Sha256 = "";
        public static int AssetRequests;
        public static int FetchJsonRequests;
        public static string FetchString(string url, RequestParam param)
        {
            if (url == "https://github.com/dyyxxhh/PCL3/releases/latest")
            {
                if (!param.UseBrowserUserAgent) throw new Exception("release page did not use browser User-Agent");
                if (StringError is not null) throw StringError;
                return LatestHtml;
            }
            AssetRequests++;
            return Sha256;
        }
        public static System.Text.Json.Nodes.JsonNode FetchJson(string url, RequestParam param)
        {
            FetchJsonRequests++;
            throw new System.Net.Http.HttpRequestException("403 Forbidden");
        }
    }
}
