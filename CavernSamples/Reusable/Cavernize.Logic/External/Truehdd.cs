using System.IO.Compression;

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
            using (FileStream output = File.Create(input))
            using (ExtractTrackFromContainer extractor = new(source.Track, output)) {
                while (extractor.Process()) {
                }
            }
            if (new FileInfo(input).Length == 0) {
                throw new IOException("The selected TrueHD track contained no audio packets.");
            }
        }

        return Decode(executable, workspace.DirectoryPath, input, workspace.FilePath("decoded"),
            workspace.FilePath("truehdd.json"), workspace.FilePath("truehdd.log"), status);
    }

    static (CavernizeTrack Track, string Version) Decode(string executable, string workingDirectory, string input,
        string stem, string summary, string log, Action<string> status) {
        status($"Decoding with TrueHDD. Decoder log: {log}");
        TruehddDecode.Result result = TruehddDecode.Run(executable, workingDirectory, input, stem, summary, log);
        string atmos = stem + ".atmos";
        bool foundAtmos = File.Exists(atmos);
        AudioReader reader = null;
        CavernizeTrack decoded;
        try {
            reader = foundAtmos ? new DolbyAtmosMasterFormatReader(atmos) : AudioReader.Open(stem + ".caf");
            decoded = new(reader, foundAtmos ? Codec.DAMF : Codec.PCM_LE, 0);
            if (decoded.Length != result.Samples || decoded.SampleRate != result.SampleRate ||
                (decoded.Renderer is CoreAudioFormatRenderer caf && caf.HasCompletePcmPayload == false)) {
                throw new IOException("The decoded payload is incomplete or disagrees with the TrueHDD summary.");
            }
        } catch (Exception error) {
            reader?.Dispose();
            throw new IOException($"{error.Message}{Environment.NewLine}Decoder log: {log}{Environment.NewLine}Decode summary: {summary}", error);
        }
        status($"TrueHDD {result.Version} decoded {decoded.Length:N0} sample frames.");
        return (decoded, result.Version);
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
            using FileStream output = File.Create(tempTrack);
            using ExtractTrackFromContainer extractor = new(source.Track, output);
            while (extractor.Process()) {
                // Extraction in progress
            }
        }

        string toDecode = needTempTrack ? tempTrack : source.Path;
        (track, _) = Decode(executable, Path.GetDirectoryName(executable), toDecode, tempTrack,
            tempTrack + ".truehdd.json", tempTrack + ".truehdd.log", UpdateStatusMessage);
        if (needTempTrack && !RenderingSettings.KeepTempFiles) {
            File.Delete(tempTrack);
        }
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
            QFile.DeleteIfExists(tempTrack + ".truehdd.log");
            QFile.DeleteIfExists(tempTrack + ".truehdd.json");
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
