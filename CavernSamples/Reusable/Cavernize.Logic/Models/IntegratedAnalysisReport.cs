using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.VisualBasic.FileIO;

using Cavern;
using Cavern.Format.Renderers;
using Cavernize.Logic.Rendering;

namespace Cavernize.Logic.Models;

/// <summary>Collects the original quality report and PCM measurements under the selected source identity.</summary>
internal sealed class IntegratedAnalysisReport {
    const string schema = "Cavernize V4";
    static readonly string[] columns = [
        "analysis_schema", "original_source_path", "source_revision", "source_codec", "audio_track_index", "container_track_number",
        "track_language", "track_title", "decoder_version", "settings_id", "render_settings", "quality_metric_id",
        "bed_count", "dynamic_object_count", "unused_target_count", "bed_positions", "quality_note",
        .. PcmPeakExport.Columns, "source_inventory"
    ];
    const int pathColumn = 1, revisionColumn = 2, settingsColumn = 9, metricColumn = 11;
    static readonly JsonSerializerOptions inventoryJson = new() { Converters = { new JsonStringEnumConverter() } };

    readonly string source, revision;
    readonly CavernizeTrack selected;
    readonly List<object[]> newRows = [];

    internal IntegratedAnalysisReport(string source, CavernizeTrack selected) {
        this.source = Path.GetFullPath(source);
        this.selected = selected;
        revision = GetRevision();
    }

    internal void Add(ICavernizeApp app, PcmSamplePeakAnalysis peaks, PostRenderReport report, string decoderVersion, bool grading,
        string exportCodec = null) {
        var settings = app.RenderingSettings;
        var upmix = app.UpmixingSettings;
        string configuration = JsonSerializer.Serialize(new {
            peaks.LayoutName, peaks.LayoutId, grading, Verify = peaks.Diagnostics != null, settings.Gain,
            settings.ListenerGainDbOverride, settings.SurroundSwap, settings.SpeakerVirtualizer, settings.MuteBed, settings.MuteGround,
            RoomCorrection = GetRoomCorrectionId(settings.RoomCorrection), RenderTargetHeightSqueeze = Models.RenderTargets.RenderTarget.HeightSqueeze,
            upmix.MatrixUpmixing, upmix.Cavernize, upmix.Effect, upmix.Smoothness,
            Environment = Listener.EnvironmentType.ToString(), X = Listener.EnvironmentSize.X, Y = Listener.EnvironmentSize.Y,
            Z = Listener.EnvironmentSize.Z, ScreenX = Listener.ScreenSize.X, ScreenY = Listener.ScreenSize.Y,
            MeridianLosslessPackingRenderer.ForcedPresentation, DecoderVersion = decoderVersion, ExportCodec = exportCodec
        });
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{selected.Index}/{selected.Track?.ID}/{configuration}")));
        var sources = report.Sources;
        string positions = sources.Beds == null ? null : JsonSerializer.Serialize(sources.Beds.Select(bed => new {
            Label = bed.Channel.ToString(), bed.Position.X, bed.Position.Y, bed.Position.Z
        }));
        string inventory = JsonSerializer.Serialize(sources, inventoryJson);
        object[] metadata = [schema, source, revision, selected.Codec, selected.Index, selected.Track?.ID, selected.Language,
            selected.Track?.Name, decoderVersion, key, configuration, null, sources.Beds?.Count, sources.DynamicObjects,
            sources.InactiveSources, positions, report.NoLfe ? "NoLFE" : null];
        PcmPeakExport.WriteRows(source, peaks, report.Quality, (row, metricId) => {
            object[] result = [.. metadata, .. row, (string)row[0] == "overall" ? inventory : null];
            result[metricColumn] = metricId;
            newRows.Add(result);
        });
    }

    static string GetRoomCorrectionId(Clip clip) {
        if (clip == null) {
            return null;
        }
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (float[] channel in clip.Data) {
            hash.AppendData(System.Runtime.InteropServices.MemoryMarshal.AsBytes(channel.AsSpan()));
        }
        return $"{clip.SampleRate}/{Convert.ToHexString(hash.GetHashAndReset())}";
    }

    internal string Publish() {
        string path = Path.ChangeExtension(source, ".csv");
        string lockName = "Cavernize-V4-" + PcmPeakExport.ShortHash(OperatingSystem.IsWindows() ? path.ToUpperInvariant() : path);
        using Mutex mutex = new(false, lockName);
        try {
            if (!mutex.WaitOne(TimeSpan.FromSeconds(30))) {
                throw new IOException($"Another Cavernize job is publishing {path}.");
            }
        } catch (AbandonedMutexException) {
            // The previous publisher left no committed partial file.
        }

        try {
            if (revision != GetRevision()) {
                throw new IOException("The source or a DAMF companion changed during analysis.");
            }
            HashSet<string> keys = newRows.Select(x => (string)x[settingsColumn]).ToHashSet();
            PcmPeakExport.WriteAtomic(path, stream => {
                using StreamWriter writer = new(stream, new UTF8Encoding(true), leaveOpen: true);
                PcmPeakExport.WriteRow(writer, columns);
                if (File.Exists(path)) {
                    using TextFieldParser parser = new(path);
                    parser.SetDelimiters(",");
                    string[] header = parser.ReadFields() ?? [];
                    bool legacy = header.SequenceEqual(columns.Take(columns.Length - 1));
                    if (!legacy && !header.SequenceEqual(columns)) {
                        throw new IOException($"An existing CSV at {path} has an unrelated or different schema. Move it aside to publish this report.");
                    }
                    while (!parser.EndOfData) {
                        string[] row = parser.ReadFields();
                        if (row.Length != header.Length || row[0] != schema ||
                            !string.Equals(row[pathColumn], source, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
                            row[revisionColumn] != revision) {
                            throw new IOException($"The existing CSV at {path} belongs to a different source or revision.");
                        }
                        if (!keys.Contains(row[settingsColumn])) {
                            PcmPeakExport.WriteRow(writer, legacy ? row.Append("") : row);
                        }
                    }
                }
                foreach (object[] row in newRows) {
                    PcmPeakExport.WriteRow(writer, row);
                }
            });
            return path;
        } finally {
            mutex.ReleaseMutex();
        }
    }

    string GetRevision() {
        IEnumerable<string> files = [source];
        if (selected.AtmosSourcePath != null) {
            files = files.Concat(new[] { source + ".audio", source + ".metadata", Path.ChangeExtension(source, ".caf") }.Where(File.Exists));
        }
        return string.Join("|", files.Select(file => {
            FileInfo info = new(file);
            return string.Create(CultureInfo.InvariantCulture, $"{info.Length}/{info.LastWriteTimeUtc.Ticks}");
        }));
    }
}
