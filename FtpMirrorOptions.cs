namespace FtpMirrorService;

public sealed class FtpMirrorOptions
{
    public const string SectionName = "FtpMirror";

    public TimeSpan Interval { get; init; } = TimeSpan.FromMinutes(2);
    public TimeSpan RecentFileWindow { get; init; } = TimeSpan.FromMinutes(60);
    public int MaxConcurrentUploads { get; init; } = 5;
    public string SourceDirectory { get; init; } = string.Empty;
    public string UserName { get; init; } = string.Empty;
    public string Password { get; init; } = string.Empty;
    public SftpEndpointOptions Destination { get; init; } = new();
}

public sealed class SftpEndpointOptions
{
    public string Host { get; init; } = string.Empty;
    public int Port { get; init; } = 22;
    public string RootPath { get; init; } = string.Empty;
}

public static class FtpMirrorOptionsValidator
{
    public static bool TryValidate(FtpMirrorOptions options, out string? error)
    {
        if (options.Interval <= TimeSpan.Zero)
        {
            error = "FtpMirror:Interval must be greater than zero.";
            return false;
        }

        if (options.RecentFileWindow <= TimeSpan.Zero)
        {
            error = "FtpMirror:RecentFileWindow must be greater than zero.";
            return false;
        }

        if (options.MaxConcurrentUploads is < 1 or > 20)
        {
            error = "FtpMirror:MaxConcurrentUploads must be between 1 and 20.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(options.UserName) || string.IsNullOrWhiteSpace(options.Password))
        {
            error = "FtpMirror:UserName and FtpMirror:Password are required.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(options.SourceDirectory) || !Path.IsPathRooted(options.SourceDirectory))
        {
            error = "FtpMirror:SourceDirectory must be an absolute local path.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(options.Destination.Host) ||
            options.Destination.Port is < 1 or > 65535 ||
            string.IsNullOrWhiteSpace(options.Destination.RootPath) ||
            !options.Destination.RootPath.StartsWith('/'))
        {
            error = "Destination must specify an SFTP host, port, and absolute root path.";
            return false;
        }

        error = null;
        return true;
    }
}
