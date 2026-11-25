using Cavern.Format.Common;

using Cavern.Format.Operations;
using Cavernize.Logic.Models;

namespace Cavernize.Logic.CommandLine.HiddenCommands;

/// <summary>
/// Extracts the raw bytes of the track with the given index, regardless if it's supported.
/// </summary>
sealed class ExtractCommand : UnsafeCommand {
    /// <inheritdoc/>
    public override string Name => "--extract";

    /// <inheritdoc/>
    public override int Parameters => 2;

    /// <inheritdoc/>
    public override string Help => "Extracts the raw bytes of the track with the given index (starting from 0), regardless if it's supported.";

    /// <inheritdoc/>
    public override void Execute(string[] args, int offset, ICavernizeApp app) {
        CavernizeTrack first = app.LoadedFile.Tracks.FirstOrDefault(x => x.Supported) ??
            throw new CommandException("At least one supported track is required to list tracks.");
        Track[] tracks = first.Track.Source.Tracks;

        if (!int.TryParse(args[offset], out int index) || index < 0 || index >= tracks.Length) {
            throw new CommandException($"The provided index ({args[offset]}) is invalid.");
        }

        Track track = tracks[index];
        string destination = Path.GetFullPath(args[offset + 1]);
        if (string.Equals(destination, Path.GetFullPath(app.LoadedFile.Path),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) {
            throw new CommandException("Extraction cannot overwrite the source container.");
        }
        using FileStream output = File.Create(destination);
        using ExtractTrackFromContainer extractor = new(track, output);
        string fileName = Path.GetFileName(args[offset + 1]);
        Console.WriteLine($"Extracting track {index} to \"{fileName}\"...");
        while (extractor.Process()) {
        }
        throw new CommandProcessingCanceledException();
    }
}
