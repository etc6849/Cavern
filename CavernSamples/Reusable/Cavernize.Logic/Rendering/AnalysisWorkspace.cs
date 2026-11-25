namespace Cavernize.Logic.Rendering;

/// <summary>Owns one isolated analysis directory on the selected working volume.</summary>
internal sealed class AnalysisWorkspace {
    internal static string DefaultRoot => OperatingSystem.IsWindows() ? @"C:\AtmosMetaData\Work" :
        Path.Combine(Path.GetTempPath(), "Cavernize", "Work");

    internal string DirectoryPath { get; }

    internal AnalysisWorkspace(string configuredRoot, long expectedFrames, long sourceLength, bool decode) {
        string root = Path.GetFullPath(configuredRoot ?? DefaultRoot);
        Directory.CreateDirectory(root);
        DriveInfo volume = DriveInfo.GetDrives().Where(x => root.StartsWith(
            Path.TrimEndingDirectorySeparator(x.Name) + Path.DirectorySeparatorChar,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
            string.Equals(Path.TrimEndingDirectorySeparator(root), Path.TrimEndingDirectorySeparator(x.Name),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            .OrderByDescending(x => x.Name.Length).FirstOrDefault() ?? new DriveInfo(Path.GetPathRoot(root));
        if (volume.DriveType == DriveType.Network) {
            throw new IOException("The analysis working directory must be on a local SSD.");
        }

        // Reserve space for the usual 16-channel home Atmos payload and the extracted elementary stream.
        // The decoder also reports write failures if an unusually large payload exceeds this estimate.
        long required = decode ? checked(checked(expectedFrames * 64) + Math.Min(sourceLength, checked(expectedFrames * 16))) : 0;
        required += 128L * 1024 * 1024;
        if (volume.AvailableFreeSpace < required) {
            throw new IOException($"Insufficient space in {root}. This analysis needs an estimated {required:N0} bytes.");
        }

        DirectoryPath = Path.Combine(root, "Cavernize-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(DirectoryPath);
    }

    internal string FilePath(string name) => Path.Combine(DirectoryPath, name);

    internal void Finish(bool keepEvidence) {
        foreach (string file in Directory.EnumerateFiles(DirectoryPath)) {
            if (!keepEvidence || !file.EndsWith("-Verification.json", StringComparison.Ordinal)) {
                File.Delete(file);
            }
        }
        if (!keepEvidence || !Directory.EnumerateFileSystemEntries(DirectoryPath).Any()) {
            Directory.Delete(DirectoryPath, recursive: true);
        }
    }
}
