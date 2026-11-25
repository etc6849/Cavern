using System.Diagnostics;

namespace Cavernize.Logic.Rendering;

internal static class MediaTool {
    internal static string FindOnPath(string name) {
        foreach (string folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)) {
            string candidate = Path.Combine(folder.Trim('"'), name);
            if (File.Exists(candidate)) {
                return Path.GetFullPath(candidate);
            }
        }
        return null;
    }

    internal static async Task Run(string executable, string workingDirectory, IEnumerable<string> arguments,
        string stdout, string stderr) {
        ProcessStartInfo start = new(executable) {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (string argument in arguments) {
            start.ArgumentList.Add(argument);
        }
        using FileStream output = File.Create(stdout);
        using FileStream errors = File.Create(stderr);
        using Process process = Process.Start(start);
        process.StandardInput.Close();
        Task copyOutput = process.StandardOutput.BaseStream.CopyToAsync(output);
        Task copyErrors = process.StandardError.BaseStream.CopyToAsync(errors);
        await Task.WhenAll(process.WaitForExitAsync(), copyOutput, copyErrors).ConfigureAwait(false);
        if (process.ExitCode != 0) {
            throw new IOException($"{Path.GetFileName(executable)} exited with code {process.ExitCode}. See {stderr}.");
        }
    }
}
