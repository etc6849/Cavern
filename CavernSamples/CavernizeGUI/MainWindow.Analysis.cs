using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

using Cavernize.Logic.External;
using Cavernize.Logic.Models;
using Cavernize.Logic.Models.RenderTargets;
using Cavernize.Logic.Rendering;
using CavernizeGUI.Resources;

namespace CavernizeGUI;

partial class MainWindow {
    RenderTarget[] additionalAnalysisLayouts = [];
    bool explicitAnalysisTrack;

    void ConfirmAnalysisTrack(object sender, EventArgs e) => explicitAnalysisTrack = tracks.SelectedItem != null;

    void ConfigureIntegratedAnalysis(object sender, RoutedEventArgs e) {
        if (Rendering) {
            return;
        }

        Window dialog = new() {
            Owner = this, Title = "Analysis workspace and layouts", Width = 540, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        StackPanel panel = new() { Margin = new Thickness(14) };
        dialog.Content = panel;
        panel.Children.Add(new TextBlock { Text = "Local SSD directory for report-only analysis" });
        TextBox root = new() { Text = string.IsNullOrWhiteSpace(Settings.Default.analysisWorkDirectory) ?
            AnalysisWorkspace.DefaultRoot : Settings.Default.analysisWorkDirectory };
        panel.Children.Add(root);
        panel.Children.Add(new TextBlock { Text = "TrueHDD executable (optional existing installation)", Margin = new Thickness(0, 8, 0, 0) });
        TextBox decoder = new() { Text = Settings.Default.analysisTrueHdd };
        panel.Children.Add(decoder);
        CheckBox evidence = new() { Content = "Keep full peak-verification JSON on the SSD",
            IsChecked = Settings.Default.analysisKeepEvidence, Margin = new Thickness(0, 8, 0, 0) };
        panel.Children.Add(evidence);
        panel.Children.Add(new TextBlock { Text = "Additional analysis layouts (the main render target is always included)" });
        ListBox layouts = new() { ItemsSource = RenderTarget.Targets, SelectionMode = SelectionMode.Multiple, Height = 185 };
        foreach (RenderTarget layout in additionalAnalysisLayouts) {
            layouts.SelectedItems.Add(layout);
        }
        panel.Children.Add(layouts);
        Button save = new() { Content = "Save", IsDefault = true, Margin = new Thickness(0, 8, 0, 0) };
        save.Click += (_, _) => dialog.DialogResult = true;
        panel.Children.Add(save);
        if (dialog.ShowDialog() == true) {
            Settings.Default.analysisWorkDirectory = root.Text.Trim();
            Settings.Default.analysisTrueHdd = decoder.Text.Trim();
            Settings.Default.analysisKeepEvidence = evidence.IsChecked == true;
            Settings.Default.Save();
            additionalAnalysisLayouts = layouts.SelectedItems.Cast<RenderTarget>().ToArray();
        }
    }

    Action GetIntegratedAnalysisTask() {
        CavernizeTrack selected;
        try {
            if (Rendering) {
                throw new InvalidOperationException(language["OpRun"]);
            }
            selected = IntegratedAnalysis.SelectTrack(LoadedFile,
                explicitAnalysisTrack ? tracks.SelectedIndex : null);
        } catch (Exception error) {
            Error(error.Message);
            return null;
        }

        string original = LoadedFile.Path;
        string work = Settings.Default.analysisWorkDirectory;
        var options = new IntegratedAnalysis.Options(new[] { RenderTarget }.Concat(additionalAnalysisLayouts).Distinct().ToArray(),
            grading.IsChecked, verifyPeakEvents.IsChecked, Settings.Default.analysisKeepEvidence,
            string.IsNullOrWhiteSpace(work) ? null : work);
        return () => {
            IntegratedAnalysis.Result result = IntegratedAnalysis.Run(this, original, selected, options,
                ResolveAnalysisDecoder, taskEngine.UpdateStatus, taskEngine.UpdateProgressBar,
                completed => Dispatcher.Invoke(() => report = completed));
            taskEngine.Progress = 1;
            taskEngine.UpdateStatus($"Analysis saved to {result.Path}" +
                (result.EvidenceDirectory == null ? "" : $". Verification evidence: {result.EvidenceDirectory}"));
            if (Program.ConsoleMode) {
                Dispatcher.Invoke(Close);
            }
        };
    }

    string ResolveAnalysisDecoder() {
        string path = Truehdd.Resolve(Settings.Default.analysisTrueHdd);
        if (path != null) {
            return path;
        }
        Truehdd downloader = new();
        downloader.UpdateStatus += taskEngine.UpdateStatus;
        Dispatcher.Invoke(() => {
            Cavern.WPF.LicenceWindow licence = new();
            try {
                downloader.LicenceDisplay = licence;
                downloader.PrepareOnUI();
            } finally {
                licence.Close();
            }
        });
        return downloader.Install();
    }
}
