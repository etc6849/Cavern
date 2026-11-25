using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;

using Cavern.Format;
using Cavern.Format.Common;
using Cavern.Format.Exceptions;
using Cavern.Format.Operations;
using Cavern.Format.Renderers;
using Cavern.Utilities;

using Cavernize.Logic.CavernSettings;
using Cavernize.Logic.Exceptions;
using Cavernize.Logic.Language;
using Cavernize.Logic.Models;
using Cavernize.Logic.Rendering;
using VoidX.WPF;

namespace Cavernize.Logic.External;

/// <summary>
/// Perform MLP conversions with truehdd by first extracting the MLP track, then using truehdd to create a DAMF, then loading that DAMF to Cavern for render.
/// </summary>
public class Truehdd : ExternalConverter {
    string executable;

    public Truehdd() { }

    internal Truehdd(string executable) => this.executable = executable;
    /// <summary>
    /// The DAMF track after truehdd's run.
    /// </summary>
    CavernizeTrack track;

    /// <summary>
    /// Path to the extracted MLP track.
    /// </summary>
    string tempTrack;

    /// <summary>
    /// Where to save the truehdd zip file.
    /// </summary>
    string cacheZip;

    /// <summary>
    /// If this folder exists, truehdd was properly downloaded and unpacked.
    /// </summary>
    string unpackDir;

    /// <summary>
    /// Save the downloaded truehdd version to this file so it can be checked later.
    /// </summary>
    string versionFile;

    /// <summary>
    /// Translations for user feedback.
    /// </summary>
    readonly ExternalConverterStrings language = new();

    /// <inheritdoc/>
    public override void PrepareOnUI() {
        if (executable != null) {
            return;
        }
        versionFile = Path.Combine(cavernizeData, "truehdd.version");
        unpackDir = Path.Combine(cavernizeData, "truehdd");
        if (File.Exists(versionFile)) {
            return;
        }
        cacheZip = Path.Combine(cavernizeData, "truehdd.zip");

        UpdateStatusMessage(language["LicFe"]);
        string licence = HTTP.GET(licenceUrl) ?? throw new NetworkException(string.Format(language["LicFa"], "truehdd"));
        UpdateStatusMessage(language["LisWa"]);
        LicenceDisplay.SetDescription(string.Format(language["LicNe"], "truehdd", "Meridian Lossless Packing"));
        LicenceDisplay.SetLicenceText(licence);
        if (!LicenceDisplay.Prompt()) {
            throw new OperationCanceledException(language["LicCa"]);
        }
    }

    internal static string Resolve(string configuredPath) {
        string name = OperatingSystem.IsWindows() ? "truehdd.exe" : "truehdd";
        if (!string.IsNullOrWhiteSpace(configuredPath)) {
            string full = Path.GetFullPath(configuredPath);
            return File.Exists(full) ? full : throw new FileNotFoundException("TrueHDD was not found.", full);
        }
        foreach (string root in new[] { AppContext.BaseDirectory, Path.Combine(AppContext.BaseDirectory, "..") }) {
            string bundled = Path.GetFullPath(Path.Combine(root, "Tools", "TrueHDD", name));
            if (File.Exists(bundled)) {
                return bundled;
            }
        }
        string installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Cavernize", "truehdd", name);
        if (File.Exists(installed)) {
            return installed;
        }
        return MediaTool.FindOnPath(name);
    }

    internal string Install() {
        Download();
        return Path.Combine(unpackDir, "truehdd.exe");
    }

    internal static (CavernizeTrack Track, string Version) DecodeAnalysis(CavernizeTrack source, AnalysisWorkspace workspace,
        string executable, Action<string> status) {
        string input = source.Path;
        if (source.Container != Container.NotContainer) {
            status("Extracting the selected TrueHD track...");
            input = workspace.FilePath("source.thd");
            source.Track.Source.Seek(0);
            using (ExtractTrackFromContainer extractor = new(source.Track, File.Create(input))) {
                while (extractor.Process()) {
                }
            }
            if (new FileInfo(input).Length == 0) {
                throw new IOException("The selected TrueHD track contained no audio packets.");
            }
        }

        status("Decoding the selected TrueHD track with TrueHDD...");
        string stem = workspace.FilePath("decoded");
        string summary = workspace.FilePath("truehdd.json");
        string log = workspace.FilePath("truehdd.log");
        List<string> arguments = ["decode", "--json", input, "--output-path", stem];
        if (MeridianLosslessPackingRenderer.ForcedPresentation is int presentation) {
            arguments.AddRange(["--presentation", presentation.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        }
        MediaTool.Run(executable, workspace.DirectoryPath, arguments, summary, log).GetAwaiter().GetResult();
        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(summary));
        JsonElement result = json.RootElement;
        string version = result.GetProperty("version").GetString();
        if (result.GetProperty("skippedFrames").GetInt64() != 0 || result.GetProperty("concealedFrames").GetInt64() != 0 ||
            result.GetProperty("invalidBranches").GetInt64() != 0 || result.GetProperty("samples").GetInt64() <= 0) {
            throw new IOException("TrueHDD reported skipped, concealed, or missing audio.");
        }

        string atmos = stem + ".atmos";
        bool foundAtmos = File.Exists(atmos);
        string decoded = foundAtmos ? atmos : stem + ".caf";
        CavernizeTrack track = new(AudioReader.Open(decoded), foundAtmos ? Codec.DAMF : Codec.PCM_LE, 0);
        if (track.Length != result.GetProperty("samples").GetInt64() ||
            track.SampleRate != result.GetProperty("sampleRate").GetInt32()) {
            track.Dispose();
            throw new IOException("The decoded payload does not match the frame count or sample rate reported by TrueHDD.");
        }
        status($"TrueHDD {version} decoded {track.Length:N0} sample frames.");
        return (track, version);
    }

    /// <summary>
    /// Convert the <paramref name="source"/> track with truehdd to DAMF and load it for rendering.
    /// </summary>
    public override CavernizeTrack Convert(CavernizeTrack source) {
        executable ??= Install(); // Convert can only be called after the licence was accepted or a binary was located.

        if (source.Codec != Codec.TrueHD) {
            throw new CodecNotFoundException(Codec.TrueHD);
        }

        bool needTempTrack = source.Container != Container.NotContainer;
        string folder = Path.GetDirectoryName(source.Path);
        tempTrack = Path.Combine(folder, $"_extracted-{Guid.NewGuid():N}.thd");
        if (needTempTrack) {
            UpdateStatusMessage(language["PrgRB"]);
            source.Track.Source.Seek(0);
            using ExtractTrackFromContainer extractor = new(source.Track, File.Create(tempTrack));
            while (extractor.Process()) {
                // Extraction in progress
            }
        }

        UpdateStatusMessage(string.Format(language["PrgCo"], "truehdd"));
        string toDecode = needTempTrack ? tempTrack : source.Path;
        ProcessStartInfo truehdd = new() {
            FileName = executable,
            WorkingDirectory = Path.GetDirectoryName(executable),
            Arguments = $"decode --progress \"{toDecode}\" --output-path \"{tempTrack}\""
        };
        if (MeridianLosslessPackingRenderer.ForcedPresentation.HasValue) {
            truehdd.Arguments += $" --presentation {MeridianLosslessPackingRenderer.ForcedPresentation.Value}";
        }

        using (Process runner = Process.Start(truehdd)) {
            runner.WaitForExit();
            if (runner.ExitCode != 0) {
                throw new IOException($"TrueHDD exited with code {runner.ExitCode}.");
            }
        }

        if (needTempTrack && !RenderingSettings.KeepTempFiles) {
            File.Delete(tempTrack);
        }

        string atmosPath = tempTrack + ".atmos";
        AudioReader reader;
        Codec codec;
        if (File.Exists(atmosPath)) {
            reader = AudioReader.Open(atmosPath);
            codec = Codec.DAMF;
        } else {
            reader = AudioReader.Open(tempTrack + ".caf");
            codec = Codec.PCM_LE;
        }
        track = new CavernizeTrack(reader, codec, 0);
        return track;
    }

    /// <inheritdoc/>
    public override void Cleanup() {
        track?.Dispose();
        if (track != null && !RenderingSettings.KeepTempFiles) {
            QFile.DeleteIfExists(tempTrack + ".atmos");
            QFile.DeleteIfExists(tempTrack + ".atmos.audio");
            QFile.DeleteIfExists(tempTrack + ".atmos.metadata");
            QFile.DeleteIfExists(tempTrack + ".caf");
        }
    }

    /// <summary>
    /// Download the latest truehdd release from GitHub and unpack it to the <see cref="unpackDir"/>.
    /// </summary>
    void Download() {
        if (File.Exists(versionFile)) {
            return; // Already downloaded
        }

        Directory.CreateDirectory(cavernizeData);
        QFile.DeleteIfExists(cacheZip);
        if (Directory.Exists(unpackDir)) {
            Directory.Delete(unpackDir, true);
        }

        string downloading = string.Format(language["PrgDl"], "truehdd");
        UpdateStatusMessage(downloading);
        string zip;
        try {
            zip = Downloader.GetLatestGitHubVersion("truehdd/truehdd", version);
            Task downloader = Downloader.Download(zip, cacheZip,
                progress => UpdateStatusMessage($"{downloading} {progress:0.00%}"));
            downloader.Wait();
        } catch (Exception e) {
            throw new NetworkException($"{language["ErrNe"]}{Environment.NewLine}{e.Message}");
        }

        UpdateStatusMessage(string.Format(language["PrgEx"], "truehdd"));
        try {
            ZipFile.ExtractToDirectory(cacheZip, unpackDir);
            File.Delete(cacheZip);
        } catch (Exception e) {
            throw new NetworkException($"{language["ErrNe"]}{Environment.NewLine}{e.Message}");
        }

        File.WriteAllText(versionFile, zip);
    }

    /// <summary>
    /// Where to fetch the truehdd licence from.
    /// </summary>
    const string licenceUrl = "https://raw.githubusercontent.com/truehdd/truehdd/refs/heads/main/LICENSE";

    /// <summary>
    /// Which release to download on this platform.
    /// </summary>
    const string version = "x86_64-pc-windows-msvc.zip";
}
