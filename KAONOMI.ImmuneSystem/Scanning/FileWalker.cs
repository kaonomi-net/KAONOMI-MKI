namespace KAONOMI.ImmuneSystem.Scanning;

using System.Runtime.InteropServices;

/// <summary>
/// Lists the files under the roots, folder by folder, without following links. Unless <see cref="Options.All"/>, it
/// leaves out the hidden folders and the folders full of files that are not the user's (caches, packages, the system).
/// </summary>
public sealed class FileWalker(Options options, ScanStats stats)
{
    private static readonly HashSet<string> SkippedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", "bower_components", "__pycache__", "venv", "obj", "packages", "Caches", "Cache",
        "AppData", "$Recycle.Bin", "System Volume Information", "Library",
    };

    // Whole-machine scans: the system, the other volumes (already counted as their own drive) and virtual files.
    private static readonly string[] SkippedPaths = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
        ? [@"C:\Windows", @"C:\Program Files", @"C:\Program Files (x86)", @"C:\ProgramData"]
        : ["/System", "/Volumes", "/dev", "/proc", "/sys", "/run", "/snap", "/private/var", "/cores", "/usr", "/bin", "/sbin", "/opt/homebrew", "/Applications"];

    private static readonly EnumerationOptions Enumeration = new()
    {
        IgnoreInaccessible = true,
        RecurseSubdirectories = false,
        AttributesToSkip = 0,
        ReturnSpecialDirectories = false,
    };

    /// <summary>The folder being read, for the progress line.</summary>
    public string Current { get; set; } = "";

    public static IReadOnlyList<string> DefaultRoots(bool machine)
    {
        if (!machine)
            return [Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)];
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return ["/"];
        return DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady).Select(d => d.RootDirectory.FullName).ToList();
    }

    public IEnumerable<FileInfo> Files(IEnumerable<string> roots, CancellationToken token)
    {
        var stack = new Stack<DirectoryInfo>(roots.Select(r => new DirectoryInfo(Path.GetFullPath(r))).Reverse());
        while (stack.Count > 0 && !token.IsCancellationRequested)
        {
            var dir = stack.Pop();
            Current = dir.FullName;
            stats.Directories++;
            List<FileSystemInfo> entries;
            try
            {
                entries = dir.EnumerateFileSystemInfos("*", Enumeration).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                continue;
            }

            var subdirs = new List<DirectoryInfo>();
            foreach (var entry in entries)
            {
                if (entry is DirectoryInfo sub)
                {
                    if (Keep(sub))
                        subdirs.Add(sub);
                }
                else if (entry is FileInfo file && file.LinkTarget is null)
                {
                    yield return file;
                }
            }
            for (var i = subdirs.Count - 1; i >= 0; i--)
                stack.Push(subdirs[i]);
        }
    }

    private bool Keep(DirectoryInfo dir)
    {
        try
        {
            if (dir.LinkTarget is not null || dir.Attributes.HasFlag(FileAttributes.ReparsePoint))
                return false;
        }
        catch (IOException)
        {
            return false;
        }

        // Bundles of macOS: the images of an application are its icons, not the user's.
        if (dir.Extension is ".app" or ".framework" or ".bundle" or ".xcassets" or ".photoslibrary" && !options.All)
            return false;
        if (options.All)
            return true;
        if (dir.Name.StartsWith('.') || dir.Attributes.HasFlag(FileAttributes.Hidden) && dir.Attributes.HasFlag(FileAttributes.System))
            return false;
        if (SkippedNames.Contains(dir.Name))
            return false;
        var full = dir.FullName.TrimEnd(Path.DirectorySeparatorChar);
        return !SkippedPaths.Any(p => full.Equals(p, StringComparison.OrdinalIgnoreCase));
    }
}
