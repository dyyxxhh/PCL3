using System.Text.Json.Nodes;
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
var release = """
[{"draft":false,"prerelease":false,"tag_name":"v0.1.4","published_at":"2026-10-31T00:00:00Z","assets":[
{"name":"PCL._Release_x64.exe","browser_download_url":"https://github.com/dyyxxhh/PCL3/releases/download/v0.1.4/PCL._Release_x64.exe"},
{"name":"PCL._Release_x64.sha256","browser_download_url":"https://github.com/dyyxxhh/PCL3/releases/download/v0.1.4/PCL._Release_x64.sha256"}]}]
""";
Requester.Json = release;
Test("current version check must not fetch a checksum asset", () =>
{
    Requester.StringRequests = 0;
    Requester.StringError = new IOException("proxy TLS EOF on release-assets.githubusercontent.com");
    Assert(new UpdatesGithubReleaseModel().IsLatest(UpdateChannel.stable, UpdateArch.x64, SemVer.Parse("0.1.4"), 104), "not latest");
    Assert(Requester.StringRequests == 0, "unnecessary checksum request");
});
Test("older version comparison must not require asset network access", () =>
    Assert(!new UpdatesGithubReleaseModel().IsLatest(UpdateChannel.stable, UpdateArch.x64, SemVer.Parse("0.1.3"), 103), "update missing"));
Test("wrapper must preserve the original TLS failure", () =>
{
    var original = new IOException("TLS unexpected EOF");
    Requester.JsonError = original;
    try { new UpdatesWrapperModel([new UpdatesGithubReleaseModel()]).IsLatest(UpdateChannel.stable, UpdateArch.x64, SemVer.Parse("0.1.4"), 104); throw new Exception("no exception"); }
    catch (Exception ex) { Assert(ReferenceEquals(ex, original) || ReferenceEquals(ex.InnerException, original), "original network error discarded"); }
    finally { Requester.JsonError = null; }
});
Test("download metadata still requires valid SHA-256", () =>
{
    Requester.StringError = null;
    Requester.StringResult = "invalid-checksum";
    try { new UpdatesGithubReleaseModel().GetLatestVersion(UpdateChannel.stable, UpdateArch.x64); }
    catch (Exception ex) when (ex.Message != "accepted invalid hash") { return; }
    throw new Exception("accepted invalid hash");
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
    public struct RequestParam { public static RequestParam WithRetry => new(); }
    public static class Requester
    {
        public static string Json = "[]";
        public static Exception? JsonError;
        public static Exception? StringError;
        public static string StringResult = "";
        public static int StringRequests;
        public static JsonNode FetchJson(string url, RequestParam param)
        {
            if (!url.Equals("https://api.github.com/repos/dyyxxhh/pcl3/releases", StringComparison.OrdinalIgnoreCase)) throw new Exception("foreign update source");
            if (JsonError is not null) throw JsonError;
            return JsonNode.Parse(Json)!;
        }
        public static string FetchString(string url, RequestParam param)
        {
            StringRequests++;
            if (StringError is not null) throw StringError;
            return StringResult;
        }
    }
}
