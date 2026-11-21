namespace PCL;

public class VersionDataModel
{
    public string Changelog { get; set; }
    public string Sha256 { get; set; }
    public required string DownloadUrl { get; init; }
    public string Source { get; set; }
    public int VersionCode { get; set; }
    public string VersionName { get; set; }
}