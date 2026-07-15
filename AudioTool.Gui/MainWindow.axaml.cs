using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace Level5.AudioTool.Gui;

public sealed partial class MainWindow : Window
{
    private static string AppVersion => $"v{typeof(MainWindow).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0"}";
    private readonly ObservableCollection<AwbEntryViewModel> _awbEntries = new();
    private readonly ObservableCollection<ReplacementQueueItem> _replacementQueue = new();
    private readonly string _audioRoot;
    private readonly string _dataRoot;
    private readonly string _preferencesPath;
    private bool _uiReady;
    private bool _loadingPreferences;
    private bool _updatingLoopControls;
    private int _wavSamples;
    private int _wavSampleRate;
    private int? _wavLoopStart;
    private int? _wavLoopEnd;
    private string? _playerSourcePath;
    private Process? _playbackProcess;
    private readonly DispatcherTimer _playbackTimer;
    private TimeSpan _playbackOffset;
    private DateTime _playbackStartedAt;
    private bool _playbackPaused;
    private TimelineDragMode _timelineDragMode = TimelineDragMode.None;

    public MainWindow()
    {
        InitializeComponent();
        ApplyLocalization();
        _playbackTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _playbackTimer.Tick += OnPlaybackTimerTick;
        _audioRoot = ResolveAudioRoot();
        _dataRoot = ResolveDataRoot();
        _preferencesPath = Path.Combine(_dataRoot, "config", "user_preferences.json");
        AwbEntriesGrid.ItemsSource = _awbEntries;
        ReplacementQueueGrid.ItemsSource = _replacementQueue;
        OutputDirectoryTextBox.Text = Path.Combine(_dataRoot, "work");
        KeepHcaCheckBox.IsCheckedChanged += OnPreferenceChanged;
        KeepReportsCheckBox.IsCheckedChanged += OnPreferenceChanged;
        UseModSuffixCheckBox.IsCheckedChanged += OnPreferenceChanged;
        OverwriteOutputCheckBox.IsCheckedChanged += OnPreferenceChanged;
        PatchWaveformCheckBox.IsCheckedChanged += OnPreferenceChanged;
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
        LoadPreferences();
        SetLoopEnabled(false);
        _uiReady = true;
        UpdateCommandPreview();
        AppendLog($"AUDIO root: {_audioRoot}");
        AppendLog($"Datos: {_dataRoot}");
        AppendLog($"Preferencias: {_preferencesPath}");
        _ = RestoreSelectedAudioAsync();
        _ = EnsurePluginsAsync();
    }

    private void ApplyLocalization()
    {
        var strings = UiText.Current;
        Title = $"L5 Audio Tool {AppVersion}";
        VersionTextBlock.Text = AppVersion;
        SubtitleTextBlock.Text = strings.Subtitle;
        FilesHeaderTextBlock.Text = strings.Files;
        BankLabelTextBlock.Text = strings.Bank;
        BrowseBankButton.Content = strings.Browse;
        ChangesHeaderTextBlock.Text = strings.Changes;
        InspectAwbButton.Content = strings.ReadEntries;
        PreviewEntryButton.Content = strings.PlayEntry;
        SubstituteButton.Content = strings.Replace;
        RemoveReplacementButton.Content = strings.Remove;
        ClearReplacementsButton.Content = strings.Clear;
        LoopHeaderTextBlock.Text = strings.Loop;
        UseWavLoopButton.Content = strings.UseSmpl;
        KeepHcaCheckBox.Content = strings.KeepHca;
        KeepReportsCheckBox.Content = strings.KeepReports;
        UseModSuffixCheckBox.Content = strings.UseModSuffix;
        OverwriteOutputCheckBox.Content = strings.OverwriteOutput;
        PatchWaveformCheckBox.Content = strings.PatchWaveform;
        ExecuteButton.Content = strings.Export;
        EntriesHeaderTextBlock.Text = strings.Entries;
        OperationHeaderTextBlock.Text = strings.Operation;
        AcbPathTextBox.Watermark = strings.AcbWatermark;
        AwbPathTextBox.Watermark = strings.AwbWatermark;
        ReplacementQueueGrid.Columns[0].Header = strings.Mode;
        ReplacementQueueGrid.Columns[1].Header = strings.Entry;
        ReplacementQueueGrid.Columns[2].Header = strings.Audio;
        AwbEntriesGrid.Columns[0].Header = strings.Index;
        AwbEntriesGrid.Columns[1].Header = strings.Id;
        AwbEntriesGrid.Columns[2].Header = strings.Type;
        AwbEntriesGrid.Columns[3].Header = strings.Size;
        AwbEntriesGrid.Columns[4].Header = strings.Clip;

        if (SelectorModeComboBox.Items[1] is ComboBoxItem indexItem)
        {
            indexItem.Content = strings.Index;
        }
        if (LoopModeComboBox.Items[0] is ComboBoxItem autoItem)
        {
            autoItem.Content = strings.AutoWavSmpl;
        }
        if (LoopModeComboBox.Items[1] is ComboBoxItem manualItem)
        {
            manualItem.Content = strings.Manual;
        }
        if (LoopModeComboBox.Items[2] is ComboBoxItem noLoopItem)
        {
            noLoopItem.Content = strings.NoLoop;
        }
    }

    private async void OnBrowseBankClick(object? sender, RoutedEventArgs e)
    {
        var path = await PickFileAsync("Seleccionar ACB/AWB", [new FilePickerFileType("CRI ACB/AWB") { Patterns = ["*.acb", "*.awb"] }]);
        if (!string.IsNullOrWhiteSpace(path))
        {
            await LoadBankPairAsync(path);
        }
    }

    private async void OnBrowseAcbClick(object? sender, RoutedEventArgs e)
    {
        var path = await PickFileAsync("Seleccionar ACB", [new FilePickerFileType("CRI ACB") { Patterns = ["*.acb"] }]);
        if (!string.IsNullOrWhiteSpace(path))
        {
            AcbPathTextBox.Text = path;
            await TryLoadSiblingBankAsync(path);
            SavePreferences();
            UpdateCommandPreview();
        }
    }

    private async Task EnsurePluginsAsync()
    {
        var result = await RunPythonAsync(["ensure-plugins"]);
        if (result.ExitCode != 0)
        {
            AppendLog($"Dependencias: no se pudieron verificar los plugins.\n{result.CombinedOutput}");
            return;
        }

        var report = JsonSerializer.Deserialize<PluginReport>(result.Stdout, JsonOptions());
        foreach (var check in report?.Checks ?? [])
        {
            if (check.Available)
            {
                AppendLog($"Plugin OK: {check.Name} ({check.Source}) {check.Path}");
            }
            else
            {
                AppendLog($"Plugin no disponible: {check.Name}. {check.Error}");
            }
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        SavePreferences();
        StopPlayback();
        base.OnClosed(e);
    }

    private async void OnBrowseAwbClick(object? sender, RoutedEventArgs e)
    {
        var path = await PickFileAsync("Seleccionar AWB", [new FilePickerFileType("CRI AWB") { Patterns = ["*.awb"] }]);
        if (!string.IsNullOrWhiteSpace(path))
        {
            AwbPathTextBox.Text = path;
            await TryLoadSiblingBankAsync(path);
            SavePreferences();
            UpdateCommandPreview();
        }
    }

    private async void OnBrowseWavClick(object? sender, RoutedEventArgs e)
    {
        var path = await PickFileAsync("Seleccionar audio", [new FilePickerFileType("Audio") { Patterns = ["*.wav", "*.flac", "*.ogg", "*.mp3", "*.m4a", "*.aac", "*.aiff", "*.aif"] }]);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        WavPathTextBox.Text = path;
        await LoadWavInfoAsync(path);
        if (_wavSamples > 0)
        {
            SetPlayerSource(path);
        }
        SavePreferences();
        UpdateCommandPreview();
    }

    private async void OnBrowseOutputClick(object? sender, RoutedEventArgs e)
    {
        var folder = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Seleccionar carpeta de salida",
            AllowMultiple = false
        });
        var path = folder.Count > 0 ? folder[0].TryGetLocalPath() : null;
        if (!string.IsNullOrWhiteSpace(path))
        {
            OutputDirectoryTextBox.Text = path;
            SavePreferences();
            UpdateCommandPreview();
        }
    }

    private async void OnInspectAwbClick(object? sender, RoutedEventArgs e)
    {
        await InspectAwbAsync();
    }

    private async Task InspectAwbAsync()
    {
        if (string.IsNullOrWhiteSpace(AwbPathTextBox.Text))
        {
            AppendLog("Selecciona un AWB antes de inspeccionar.");
            return;
        }

        await RunBusyAsync(async () =>
        {
            var inspectArgs = new List<string> { "inspect", AwbPathTextBox.Text! };
            if (!string.IsNullOrWhiteSpace(AcbPathTextBox.Text) && File.Exists(AcbPathTextBox.Text))
            {
                inspectArgs.Add("--acb");
                inspectArgs.Add(AcbPathTextBox.Text!);
            }

            var result = await RunPythonAsync(inspectArgs);
            if (result.ExitCode != 0)
            {
                AppendLog(result.CombinedOutput);
                return;
            }

            var metadata = JsonSerializer.Deserialize<AwbMetadata>(result.Stdout, JsonOptions());
            _awbEntries.Clear();
            foreach (var entry in metadata?.Entries ?? [])
            {
                _awbEntries.Add(new AwbEntryViewModel(entry.Index, entry.Id, entry.Extension ?? "", entry.Size, entry.Name ?? ""));
            }

            AppendLog($"AWB inspeccionado: {_awbEntries.Count} entradas.");
        });
    }

    private async void OnPreviewEntryClick(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(AwbPathTextBox.Text) || !File.Exists(AwbPathTextBox.Text))
        {
            AppendLog("Selecciona un AWB antes de reproducir una entrada.");
            return;
        }

        var selectorMode = SelectorModeComboBox.SelectedIndex == 0 ? "--id" : "--index";
        var selectorValue = ((int)(EntryNumberBox.Value ?? 0)).ToString();
        var previewDirectory = Path.Combine(_dataRoot, "work", "preview");
        await RunBusyAsync(async () =>
        {
            Directory.CreateDirectory(previewDirectory);
            var result = await RunPythonAsync([
                "preview-awb-entry",
                AwbPathTextBox.Text!,
                "--output",
                previewDirectory,
                selectorMode,
                selectorValue
            ]);
            if (result.ExitCode != 0)
            {
                AppendLog(result.CombinedOutput);
                return;
            }

            var preview = JsonSerializer.Deserialize<PreviewReport>(result.Stdout, JsonOptions());
            if (string.IsNullOrWhiteSpace(preview?.Wav) || !File.Exists(preview.Wav))
            {
                AppendLog("No se pudo preparar el WAV de previsualización.");
                return;
            }

            var decoder = string.IsNullOrWhiteSpace(preview.Decoder) ? "desconocido" : preview.Decoder;
            AppendLog($"Previsualización ({decoder}): {preview.Wav}");
            OpenWithSystemPlayer(preview.Wav);
        });
    }

    private void OnPlaybackToggleClick(object? sender, RoutedEventArgs e)
    {
        if (_playbackProcess is not null && !_playbackProcess.HasExited)
        {
            TogglePlaybackPause();
            return;
        }

        var source = _playerSourcePath;
        if (string.IsNullOrWhiteSpace(source) || !File.Exists(source))
        {
            var wavPath = WavPathTextBox.Text?.Trim();
            if (!string.IsNullOrWhiteSpace(wavPath) && File.Exists(wavPath) && _wavSamples > 0)
            {
                source = wavPath;
                SetPlayerSource(source);
            }
        }

        if (string.IsNullOrWhiteSpace(source) || !File.Exists(source))
        {
            AppendLog("No hay un audio cargado para reproducir. Usa un audio de reemplazo o previsualiza una entrada AWB.");
            return;
        }

        StartPlayback();
    }

    private async void OnExecuteClick(object? sender, RoutedEventArgs e)
    {
        var outputDirectory = await PickOutputDirectoryAsync();
        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            return;
        }

        OutputDirectoryTextBox.Text = outputDirectory;
        SavePreferences();

        if (!ValidateInputs(out var acbPath, out var awbPath, out var wavPath, out _))
        {
            return;
        }

        var jobs = BuildReplacementJobs(wavPath);
        if (jobs.Count == 0)
        {
            AppendLog("Añade al menos un audio de reemplazo.");
            return;
        }

        var stem = Path.GetFileNameWithoutExtension(awbPath);
        var targetAwb = BuildExportPath(outputDirectory, awbPath, ".awb");
        var targetAcb = BuildExportPath(outputDirectory, acbPath, ".acb");
        if (IsSamePath(targetAcb, acbPath) || IsSamePath(targetAwb, awbPath))
        {
            AppendLog(UiText.Current.OutputMatchesSource);
            return;
        }

        if (!PrepareOverwrite(targetAcb, targetAwb))
        {
            return;
        }

        if (PatchWaveformCheckBox.IsChecked == true && jobs.Any(job => !TryResolvePatchAwbId(job, out _)))
        {
            return;
        }

        await RunBusyAsync(async () =>
        {
            Directory.CreateDirectory(outputDirectory);
            var currentAwb = awbPath;
            var currentAcb = acbPath;
            var intermediateFiles = new List<string>();

            for (var i = 0; i < jobs.Count; i++)
            {
                var job = jobs[i];
                var isLast = i == jobs.Count - 1;
                var nextAwb = isLast ? targetAwb : Path.Combine(outputDirectory, $"{stem}.batch-{i + 1}.awb");
                var nextAcb = isLast ? targetAcb : Path.Combine(outputDirectory, $"{Path.GetFileNameWithoutExtension(acbPath)}.batch-{i + 1}.acb");
                if (!isLast)
                {
                    intermediateFiles.Add(nextAwb);
                    intermediateFiles.Add(nextAcb);
                    intermediateFiles.Add($"{nextAwb}.wav-replace-report.json");
                    intermediateFiles.Add($"{nextAwb}.replace-report.json");
                    intermediateFiles.Add($"{nextAcb}.patch-report.json");
                    intermediateFiles.Add($"{nextAcb}.stream-awb-report.json");
                }

                var replaceArgs = new List<string>
                {
                    "replace-awb-wav",
                    currentAwb,
                    nextAwb,
                    job.AudioPath,
                    job.SelectorMode,
                    job.Entry.ToString()
                };

                AddLoopArgs(replaceArgs);
                if (KeepHcaCheckBox.IsChecked == true)
                {
                    replaceArgs.Add("--keep-hca");
                }

                AppendLog($"Generando AWB modificado ({i + 1}/{jobs.Count}): {job.Label}");
                var replaceResult = await RunPythonAsync(replaceArgs);
                AppendLog(replaceResult.CombinedOutput);
                if (replaceResult.ExitCode != 0)
                {
                    return;
                }

                var replaceReport = LoadReplaceReport(nextAwb);
                LogReplaceWarnings(replaceReport);
                if (HasReplaceErrors(replaceReport))
                {
                    return;
                }

                if (PatchWaveformCheckBox.IsChecked == true)
                {
                    if (!TryResolvePatchAwbId(job, out var patchAwbId))
                    {
                        return;
                    }

                    var patchArgs = new List<string>
                    {
                        "patch-acb-waveform",
                        currentAcb,
                        nextAcb,
                        "--id",
                        patchAwbId.ToString(),
                        "--samples",
                        replaceReport.EffectiveSampleCount.ToString(),
                        "--encode-type",
                        "2"
                    };
                    if (replaceReport.PreparedWavInfo?.Channels is > 0)
                    {
                        patchArgs.Add("--channels");
                        patchArgs.Add(replaceReport.PreparedWavInfo.Channels.Value.ToString());
                    }
                    if (replaceReport.PreparedWavInfo?.SampleRate is > 0)
                    {
                        patchArgs.Add("--sampling-rate");
                        patchArgs.Add(replaceReport.PreparedWavInfo.SampleRate.Value.ToString());
                    }

                    if (replaceReport.LoopStart is not null && replaceReport.LoopEnd is not null)
                    {
                        patchArgs.Add("--loop-start");
                        patchArgs.Add(replaceReport.LoopStart.Value.ToString());
                        patchArgs.Add("--loop-end");
                        patchArgs.Add(replaceReport.LoopEnd.Value.ToString());
                    }
                    else
                    {
                        patchArgs.Add("--no-loop");
                    }
                    AppendLog($"Parcheando WaveformTable del ACB ({i + 1}/{jobs.Count})...");
                    var patchResult = await RunPythonAsync(patchArgs);
                    AppendLog(patchResult.CombinedOutput);
                    if (patchResult.ExitCode != 0)
                    {
                        return;
                    }

                    currentAcb = nextAcb;
                }

                currentAwb = nextAwb;
            }

            if (PatchWaveformCheckBox.IsChecked != true)
            {
                File.Copy(acbPath, targetAcb, overwrite: true);
                AppendLog("ACB conservado; sólo se actualizará su vínculo con el AWB.");
            }

            var streamPatchArgs = new List<string>
            {
                "patch-acb-stream-awb",
                targetAcb,
                targetAcb,
                "--awb",
                targetAwb,
                "--name",
                Path.GetFileNameWithoutExtension(awbPath)
            };

            AppendLog("Actualizando hash/header del AWB en ACB...");
            var streamPatchResult = await RunPythonAsync(streamPatchArgs);
            AppendLog(streamPatchResult.CombinedOutput);
            if (streamPatchResult.ExitCode == 0)
            {
                DeleteIntermediateFiles(intermediateFiles);
                if (KeepReportsCheckBox.IsChecked != true)
                {
                    DeleteExportReports(targetAcb, targetAwb);
                }
                AppendLog($"Listo: {targetAcb}");
                AppendLog($"Listo: {targetAwb}");
            }
        });
    }

    private void OnLoopModeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady)
        {
            return;
        }

        if (LoopModeComboBox.SelectedIndex == 0)
        {
            ApplyWavLoop();
        }

        SetLoopEnabled(LoopModeComboBox.SelectedIndex == 1 || LoopModeComboBox.SelectedIndex == 0 && _wavLoopStart is not null);
        UpdateLoopVisuals();
        SavePreferences();
        UpdateCommandPreview();
    }

    private void OnSelectorChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady)
        {
            return;
        }

        SavePreferences();
        UpdateCommandPreview();
    }

    private void OnSelectorNumberChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (!_uiReady)
        {
            return;
        }

        SavePreferences();
        UpdateCommandPreview();
    }

    private void OnAwbEntrySelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady || AwbEntriesGrid.SelectedItem is not AwbEntryViewModel entry)
        {
            return;
        }

        SelectorModeComboBox.SelectedIndex = 0;
        EntryNumberBox.Value = entry.Id;
        SavePreferences();
        UpdateCommandPreview();
    }

    private async void OnSubstituteClick(object? sender, RoutedEventArgs e)
    {
        var path = await PickFileAsync("Seleccionar audio de reemplazo", [new FilePickerFileType("Audio") { Patterns = ["*.wav", "*.flac", "*.ogg", "*.mp3", "*.m4a", "*.aac", "*.aiff", "*.aif"] }]);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        WavPathTextBox.Text = path;
        await LoadWavInfoAsync(path);
        if (_wavSamples > 0)
        {
            SetPlayerSource(path);
        }

        AddReplacementToQueue(path, (int)(EntryNumberBox.Value ?? 0));
        SavePreferences();
    }

    private void OnAddReplacementClick(object? sender, RoutedEventArgs e)
    {
        var audioPath = WavPathTextBox.Text?.Trim() ?? "";
        if (!File.Exists(audioPath))
        {
            AppendLog("Selecciona un audio de reemplazo antes de añadirlo a la cola.");
            return;
        }

        AddReplacementToQueue(audioPath, (int)(EntryNumberBox.Value ?? 0));
    }

    private void OnRemoveReplacementClick(object? sender, RoutedEventArgs e)
    {
        if (ReplacementQueueGrid.SelectedItem is ReplacementQueueItem item)
        {
            _replacementQueue.Remove(item);
            UpdateCommandPreview();
        }
    }

    private void OnClearReplacementsClick(object? sender, RoutedEventArgs e)
    {
        _replacementQueue.Clear();
        UpdateCommandPreview();
    }

    private async void OnReplacementSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady || ReplacementQueueGrid.SelectedItem is not ReplacementQueueItem item)
        {
            return;
        }

        if (!File.Exists(item.AudioPath))
        {
            AppendLog(UiText.Current.AudioMissing(item.AudioPath));
            return;
        }

        WavPathTextBox.Text = item.AudioPath;
        await LoadWavInfoAsync(item.AudioPath);
        if (_wavSamples > 0)
        {
            SetPlayerSource(item.AudioPath);
        }
        SavePreferences();
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.Data.Contains(DataFormats.Files) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        var files = e.Data.GetFiles()?
            .Select(item => item.TryGetLocalPath())
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Cast<string>()
            .ToList() ?? [];
        if (files.Count == 0)
        {
            return;
        }

        var audioOffset = 0;
        foreach (var path in files)
        {
            var extension = Path.GetExtension(path).ToLowerInvariant();
            if (extension == ".acb")
            {
                await LoadBankPairAsync(path);
                continue;
            }

            if (extension == ".awb")
            {
                await LoadBankPairAsync(path);
                continue;
            }

            if (!IsSupportedAudio(path))
            {
                continue;
            }

            WavPathTextBox.Text = path;
            AddReplacementToQueue(path, GuessEntryFromFileName(path) ?? (int)(EntryNumberBox.Value ?? 0) + audioOffset);
            audioOffset++;
        }

        var selectedAudio = WavPathTextBox.Text?.Trim();
        if (!string.IsNullOrWhiteSpace(selectedAudio) && File.Exists(selectedAudio))
        {
            await LoadWavInfoAsync(selectedAudio);
            if (_wavSamples > 0)
            {
                SetPlayerSource(selectedAudio);
            }
        }

        SavePreferences();
        UpdateCommandPreview();
        e.Handled = true;
    }

    private void OnUseWavLoopClick(object? sender, RoutedEventArgs e)
    {
        ApplyWavLoop();
        LoopModeComboBox.SelectedIndex = _wavLoopStart is null ? 2 : 0;
        SavePreferences();
        UpdateCommandPreview();
    }

    private void OnLoopNumberChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (!_uiReady || _updatingLoopControls)
        {
            return;
        }

        NormalizeLoopNumbers();
        UpdateLoopVisuals();
        SavePreferences();
        UpdateCommandPreview();
    }

    private void OnLoopTimelineSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (!_uiReady)
        {
            return;
        }

        UpdateLoopVisuals();
    }

    private void OnLoopTimelinePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_wavSamples <= 0)
        {
            return;
        }

        var x = e.GetPosition(LoopTimeline).X;
        var sample = LoopTimeline.XToSample(x);
        if (LoopModeComboBox.SelectedIndex == 2)
        {
            LoopModeComboBox.SelectedIndex = 1;
            var length = Math.Max(1, _wavSamples / 10);
            var startSample = Math.Clamp(sample, 0, Math.Max(0, _wavSamples - 1));
            LoopStartBox.Value = startSample;
            LoopEndBox.Value = Math.Clamp(startSample + length, startSample + 1, _wavSamples);
            _timelineDragMode = TimelineDragMode.End;
            e.Pointer.Capture(LoopTimeline);
            SavePreferences();
            return;
        }

        LoopModeComboBox.SelectedIndex = 1;
        var start = (int)(LoopStartBox.Value ?? 0);
        var end = (int)(LoopEndBox.Value ?? _wavSamples);
        if (Math.Abs(sample - start) <= Math.Abs(sample - end))
        {
            LoopStartBox.Value = Math.Clamp(sample, 0, Math.Max(0, end - 1));
            _timelineDragMode = TimelineDragMode.Start;
        }
        else
        {
            LoopEndBox.Value = Math.Clamp(sample, Math.Min(_wavSamples, start + 1), _wavSamples);
            _timelineDragMode = TimelineDragMode.End;
        }
        e.Pointer.Capture(LoopTimeline);
        SavePreferences();
    }

    private void OnLoopTimelinePointerMoved(object? sender, PointerEventArgs e)
    {
        if (_timelineDragMode == TimelineDragMode.None || _wavSamples <= 0)
        {
            return;
        }

        var sample = LoopTimeline.XToSample(e.GetPosition(LoopTimeline).X);
        var start = (int)(LoopStartBox.Value ?? 0);
        var end = (int)(LoopEndBox.Value ?? _wavSamples);
        if (_timelineDragMode == TimelineDragMode.Start)
        {
            LoopStartBox.Value = Math.Clamp(sample, 0, Math.Max(0, end - 1));
        }
        else
        {
            LoopEndBox.Value = Math.Clamp(sample, Math.Min(_wavSamples, start + 1), _wavSamples);
        }
        SavePreferences();
    }

    private void OnLoopTimelinePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _timelineDragMode = TimelineDragMode.None;
        e.Pointer.Capture(null);
    }

    private void OnLoopTimelinePointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        _timelineDragMode = TimelineDragMode.None;
    }

    private async Task LoadWavInfoAsync(string path)
    {
        var result = await RunPythonAsync(["wav-info", path]);
        if (result.ExitCode != 0)
        {
            StopPlayback();
            _playerSourcePath = null;
            _wavSamples = 0;
            _wavSampleRate = 0;
            _wavLoopStart = null;
            _wavLoopEnd = null;
            LoopTimeline.Peaks = [];
            LoopModeComboBox.SelectedIndex = 2;
            SetLoopEnabled(false);
            UpdateLoopVisuals();
            AppendLog("El archivo no expone metadata directa de loop/duración. Se normalizará con FFmpeg al generar.");
            return;
        }

        var info = JsonSerializer.Deserialize<WavInfo>(result.Stdout, JsonOptions());
        _wavSamples = info?.SampleCount ?? 0;
        _wavSampleRate = info?.SampleRate ?? 0;
        _wavLoopStart = info?.Loop?.Start;
        _wavLoopEnd = info?.Loop?.End;
        LoopTimeline.Peaks = info?.Peaks ?? [];

        _updatingLoopControls = true;
        LoopStartBox.Maximum = Math.Max(0, _wavSamples);
        LoopEndBox.Maximum = Math.Max(0, _wavSamples);
        LoopStartBox.Value = _wavLoopStart ?? 0;
        LoopEndBox.Value = _wavLoopEnd ?? _wavSamples;
        _updatingLoopControls = false;

        LoopModeComboBox.SelectedIndex = _wavLoopStart is null ? 2 : 0;
        SetLoopEnabled(_wavLoopStart is not null);
        UpdateLoopVisuals();
        AppendLog($"Audio: {_wavSamples} samples, {_wavSampleRate} Hz, loop: {DescribeLoop()}.");
    }

    private static ReplaceReport LoadReplaceReport(string targetAwb)
    {
        var reportPath = $"{targetAwb}.wav-replace-report.json";
        var json = File.ReadAllText(reportPath);
        var report = JsonSerializer.Deserialize<ReplaceReport>(json, JsonOptions());
        if (report is null || report.EffectiveSampleCount <= 0)
        {
            throw new InvalidDataException($"No se pudo leer el reporte de reemplazo: {reportPath}");
        }

        return report;
    }

    private void LogReplaceWarnings(ReplaceReport report)
    {
        foreach (var check in report.Checks ?? [])
        {
            if (!string.Equals(check.Status, "warning", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(check.Status, "error", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var prefix = string.Equals(check.Status, "error", StringComparison.OrdinalIgnoreCase) ? "Error" : "Aviso";
            AppendLog($"{prefix}: {check.Name} - {check.Describe()}");
        }
    }

    private bool HasReplaceErrors(ReplaceReport report)
    {
        return report.Checks?.Any(check => string.Equals(check.Status, "error", StringComparison.OrdinalIgnoreCase)) == true;
    }

    private void ApplyWavLoop()
    {
        if (_wavSamples <= 0)
        {
            return;
        }

        _updatingLoopControls = true;
        LoopStartBox.Value = _wavLoopStart ?? 0;
        LoopEndBox.Value = _wavLoopEnd ?? _wavSamples;
        _updatingLoopControls = false;
        SetLoopEnabled(_wavLoopStart is not null);
        UpdateLoopVisuals();
    }

    private void AddLoopArgs(List<string> args)
    {
        if (LoopModeComboBox.SelectedIndex == 2)
        {
            args.Add("--no-loop");
            return;
        }

        if (LoopModeComboBox.SelectedIndex == 1)
        {
            args.Add("--loop-start");
            args.Add(((int)(LoopStartBox.Value ?? 0)).ToString());
            args.Add("--loop-end");
            args.Add(((int)(LoopEndBox.Value ?? 0)).ToString());
        }
    }

    private List<ReplacementJob> BuildReplacementJobs(string singleAudioPath)
    {
        if (_replacementQueue.Count == 0)
        {
            return
            [
                new ReplacementJob(
                    SelectorModeComboBox.SelectedIndex == 0 ? "--id" : "--index",
                    (int)(EntryNumberBox.Value ?? 0),
                    singleAudioPath)
            ];
        }

        return _replacementQueue
            .Where(item => File.Exists(item.AudioPath))
            .Select(item => new ReplacementJob(item.SelectorMode, item.Entry, item.AudioPath))
            .ToList();
    }

    private void AddReplacementToQueue(string audioPath, int entry)
    {
        var selectorMode = SelectorModeComboBox.SelectedIndex == 0 ? "--id" : "--index";
        _replacementQueue.Add(new ReplacementQueueItem(selectorMode, Math.Max(0, entry), audioPath));
        AppendLog($"Cola: {Path.GetFileName(audioPath)} -> {(selectorMode == "--id" ? "ID" : "índice")} {Math.Max(0, entry)}");
        UpdateCommandPreview();
    }

    private static int? GuessEntryFromFileName(string path)
    {
        var fileName = Path.GetFileNameWithoutExtension(path);
        var digits = new string(fileName.TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, out var value) ? value : null;
    }

    private static bool IsSupportedAudio(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".wav" or ".flac" or ".ogg" or ".mp3" or ".m4a" or ".aac" or ".aiff" or ".aif" => true,
            _ => false
        };
    }

    private string BuildExportPath(string outputDirectory, string sourcePath, string extension)
    {
        var suffix = UseModSuffixCheckBox.IsChecked == true ? ".mod" : "";
        return Path.Combine(outputDirectory, $"{Path.GetFileNameWithoutExtension(sourcePath)}{suffix}{extension}");
    }

    private bool PrepareOverwrite(params string[] paths)
    {
        var existing = paths.Where(File.Exists).ToList();
        if (existing.Count == 0)
        {
            return true;
        }

        if (OverwriteOutputCheckBox.IsChecked == true)
        {
            foreach (var path in existing)
            {
                File.Delete(path);
                DeleteExportReportsFor(path);
            }
            return true;
        }

        AppendLog(UiText.Current.OutputExists(string.Join(", ", existing.Select(Path.GetFileName))));
        return false;
    }

    private static bool IsSamePath(string first, string second)
    {
        return string.Equals(Path.GetFullPath(first), Path.GetFullPath(second), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private static void DeleteIntermediateFiles(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private static void DeleteExportReports(string targetAcb, string targetAwb)
    {
        DeleteExportReportsFor(targetAcb);
        DeleteExportReportsFor(targetAwb);
    }

    private static void DeleteExportReportsFor(string target)
    {
        foreach (var path in Directory.EnumerateFiles(Path.GetDirectoryName(target) ?? ".", $"{Path.GetFileName(target)}.*report.json"))
        {
            File.Delete(path);
        }
    }

    private bool ValidateInputs(out string acbPath, out string awbPath, out string wavPath, out string outputDirectory)
    {
        acbPath = AcbPathTextBox.Text?.Trim() ?? "";
        awbPath = AwbPathTextBox.Text?.Trim() ?? "";
        wavPath = WavPathTextBox.Text?.Trim() ?? "";
        outputDirectory = OutputDirectoryTextBox.Text?.Trim() ?? "";

        foreach (var path in new[] { acbPath, awbPath })
        {
            if (!File.Exists(path))
            {
                AppendLog($"No existe el archivo: {path}");
                return false;
            }
        }

        if (_replacementQueue.Count == 0 && !File.Exists(wavPath))
        {
            AppendLog($"No existe el archivo: {wavPath}");
            return false;
        }

        foreach (var item in _replacementQueue)
        {
            if (!File.Exists(item.AudioPath))
            {
                AppendLog($"No existe el archivo en cola: {item.AudioPath}");
                return false;
            }
        }

        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            AppendLog("Selecciona una carpeta de salida.");
            return false;
        }

        if (LoopModeComboBox.SelectedIndex == 1)
        {
            var start = (int)(LoopStartBox.Value ?? 0);
            var end = (int)(LoopEndBox.Value ?? 0);
            if (_wavSamples <= 0 || start < 0 || end <= start || end > _wavSamples)
            {
                AppendLog("El rango de loop manual no es válido.");
                return false;
            }
        }

        return true;
    }

    private bool TryResolvePatchAwbId(out int awbId)
    {
        var selectorMode = SelectorModeComboBox.SelectedIndex == 0 ? "--id" : "--index";
        return TryResolvePatchAwbId(new ReplacementJob(selectorMode, (int)(EntryNumberBox.Value ?? 0), ""), out awbId);
    }

    private bool TryResolvePatchAwbId(ReplacementJob job, out int awbId)
    {
        if (job.SelectorMode == "--id")
        {
            awbId = job.Entry;
            return true;
        }

        var entry = _awbEntries.FirstOrDefault(item => item.Index == job.Entry);
        if (entry is null)
        {
            awbId = -1;
            AppendLog("Para parchear el ACB por índice, primero lee las entradas AWB o usa ID AWB directamente.");
            return false;
        }

        awbId = entry.Id;
        return true;
    }

    private void NormalizeLoopNumbers()
    {
        if (_wavSamples <= 0)
        {
            return;
        }

        _updatingLoopControls = true;
        var start = (int)(LoopStartBox.Value ?? 0);
        var end = (int)(LoopEndBox.Value ?? _wavSamples);
        start = Math.Clamp(start, 0, Math.Max(0, _wavSamples - 1));
        end = Math.Clamp(end, start + 1, _wavSamples);
        LoopStartBox.Value = start;
        LoopEndBox.Value = end;
        _updatingLoopControls = false;
    }

    private void UpdateLoopVisuals()
    {
        var start = LoopModeComboBox.SelectedIndex == 2 ? 0 : (int)(LoopStartBox.Value ?? 0);
        var end = LoopModeComboBox.SelectedIndex == 2 ? 0 : (int)(LoopEndBox.Value ?? _wavSamples);

        LoopTimeline.TotalSamples = _wavSamples;
        LoopTimeline.LoopStart = start;
        LoopTimeline.LoopEnd = end;
        LoopTimeline.HasLoop = LoopModeComboBox.SelectedIndex != 2 && _wavSamples > 0 && end > start;
        UpdatePlaybackVisuals();
        LoopSummaryTextBlock.Text = _wavSamples <= 0
            ? "Carga un audio para ver duración y loop."
            : $"Audio: {_wavSamples} samples ({FormatSeconds(_wavSamples)}). Loop: {DescribeLoop()}.";
    }

    private void SetLoopEnabled(bool enabled)
    {
        LoopStartBox.IsEnabled = enabled;
        LoopEndBox.IsEnabled = enabled;
        LoopTimeline.Opacity = _wavSamples > 0 ? 1 : 0.45;
    }

    private void SetPlayerSource(string path)
    {
        StopPlayback();
        _playerSourcePath = path;
        _playbackOffset = TimeSpan.Zero;
        UpdatePlaybackVisuals();
        PlaybackButton.Content = "▶";
    }

    private void StartPlayback()
    {
        if (string.IsNullOrWhiteSpace(_playerSourcePath) || !File.Exists(_playerSourcePath))
        {
            return;
        }

        StopPlayback();
        if (!TryStartControlledPlayback(_playerSourcePath))
        {
            OpenWithSystemPlayer(_playerSourcePath);
            return;
        }

        _playbackOffset = TimeSpan.Zero;
        _playbackStartedAt = DateTime.UtcNow;
        _playbackPaused = false;
        PlaybackButton.Content = "⏸";
        _playbackTimer.Start();
    }

    private bool TryStartControlledPlayback(string path)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return false;
        }

        var startInfo = new ProcessStartInfo("afplay", [path])
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        _playbackProcess = Process.Start(startInfo);
        return _playbackProcess is not null;
    }

    private void TogglePlaybackPause()
    {
        if (_playbackProcess is null || _playbackProcess.HasExited)
        {
            StopPlayback();
            return;
        }

        if (!OperatingSystem.IsMacOS())
        {
            StopPlayback();
            return;
        }

        if (_playbackPaused)
        {
            SendSignal(_playbackProcess.Id, "CONT");
            _playbackStartedAt = DateTime.UtcNow;
            _playbackPaused = false;
            PlaybackButton.Content = "⏸";
            _playbackTimer.Start();
        }
        else
        {
            _playbackOffset = CurrentPlaybackPosition();
            SendSignal(_playbackProcess.Id, "STOP");
            _playbackPaused = true;
            PlaybackButton.Content = "▶";
            _playbackTimer.Stop();
            UpdatePlaybackVisuals();
        }
    }

    private void StopPlayback()
    {
        _playbackTimer.Stop();
        if (_playbackProcess is not null)
        {
            try
            {
                if (!_playbackProcess.HasExited)
                {
                    _playbackProcess.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // Best effort cleanup for platform players.
            }
            finally
            {
                _playbackProcess.Dispose();
                _playbackProcess = null;
            }
        }

        _playbackOffset = TimeSpan.Zero;
        _playbackPaused = false;
        if (PlaybackButton is not null)
        {
            PlaybackButton.Content = "▶";
        }
        UpdatePlaybackVisuals();
    }

    private void OnPlaybackTimerTick(object? sender, EventArgs e)
    {
        if (_playbackProcess is null || _playbackProcess.HasExited)
        {
            StopPlayback();
            return;
        }

        if (CurrentPlaybackPosition().TotalSeconds >= AudioDuration().TotalSeconds)
        {
            StopPlayback();
            return;
        }

        UpdatePlaybackVisuals();
    }

    private TimeSpan CurrentPlaybackPosition()
    {
        return _playbackPaused ? _playbackOffset : _playbackOffset + (DateTime.UtcNow - _playbackStartedAt);
    }

    private TimeSpan AudioDuration()
    {
        return _wavSampleRate <= 0 || _wavSamples <= 0
            ? TimeSpan.Zero
            : TimeSpan.FromSeconds(_wavSamples / (double)_wavSampleRate);
    }

    private void UpdatePlaybackVisuals()
    {
        if (LoopTimeline is null)
        {
            return;
        }

        var duration = AudioDuration();
        var progress = duration <= TimeSpan.Zero ? 0 : CurrentPlaybackPosition().TotalSeconds / duration.TotalSeconds;
        progress = Math.Clamp(progress, 0, 1);
        LoopTimeline.PlayheadSample = _wavSamples <= 0 ? 0 : (int)Math.Round(_wavSamples * progress);
    }

    private static void SendSignal(int pid, string signal)
    {
        using var process = Process.Start(new ProcessStartInfo("kill", [$"-{signal}", pid.ToString()])
        {
            UseShellExecute = false,
            CreateNoWindow = true
        });
        process?.WaitForExit();
    }

    private string DescribeLoop()
    {
        if (LoopModeComboBox.SelectedIndex == 2 || _wavSamples <= 0)
        {
            return "sin loop";
        }

        var start = (int)(LoopStartBox.Value ?? 0);
        var end = (int)(LoopEndBox.Value ?? _wavSamples);
        return $"{start}..{end} ({FormatSeconds(end - start)})";
    }

    private string FormatSeconds(int samples)
    {
        return _wavSampleRate <= 0 ? "0.000 s" : $"{samples / (double)_wavSampleRate:0.000} s";
    }

    private void UpdateCommandPreview()
    {
        if (!_uiReady || SelectorModeComboBox is null || EntryNumberBox is null || CommandPreviewTextBlock is null)
        {
            return;
        }

        var selectorMode = SelectorModeComboBox.SelectedIndex == 0 ? "--id" : "--index";
        var selectorValue = ((int)(EntryNumberBox.Value ?? 0)).ToString();
        var patchIdText = SelectorModeComboBox.SelectedIndex == 0
            ? selectorValue
            : _awbEntries.FirstOrDefault(item => item.Index == (int)(EntryNumberBox.Value ?? 0))?.Id.ToString() ?? "?";
        var count = _replacementQueue.Count == 0 ? 1 : _replacementQueue.Count;
        CommandPreviewTextBlock.Text = count == 1
            ? $"replace-awb-wav ... {selectorMode} {selectorValue} / patch-acb-waveform ... --id {patchIdText} / patch-acb-stream-awb ..."
            : $"{count} reemplazos en el mismo ACB/AWB / patch-acb-stream-awb al terminar ...";
    }

    private async Task<string?> PickFileAsync(string title, IReadOnlyList<FilePickerFileType> filters)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = filters
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    private async Task<string?> PickOutputDirectoryAsync()
    {
        var start = OutputDirectoryTextBox.Text?.Trim();
        var options = new FolderPickerOpenOptions
        {
            Title = "Seleccionar carpeta de exportación",
            AllowMultiple = false
        };
        if (!string.IsNullOrWhiteSpace(start) && Directory.Exists(start))
        {
            var folder = await StorageProvider.TryGetFolderFromPathAsync(start);
            if (folder is not null)
            {
                options.SuggestedStartLocation = folder;
            }
        }

        var folders = await StorageProvider.OpenFolderPickerAsync(options);
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    private async Task LoadBankPairAsync(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension != ".acb" && extension != ".awb")
        {
            return;
        }

        if (extension == ".acb")
        {
            AcbPathTextBox.Text = path;
            AwbPathTextBox.Text = Path.ChangeExtension(path, ".awb");
        }
        else
        {
            AwbPathTextBox.Text = path;
            AcbPathTextBox.Text = Path.ChangeExtension(path, ".acb");
        }

        SavePreferences();
        UpdateCommandPreview();

        if (File.Exists(AcbPathTextBox.Text) && File.Exists(AwbPathTextBox.Text))
        {
            await InspectAwbAsync();
        }
        else
        {
            AppendLog("No se encontró el par ACB/AWB junto al archivo seleccionado.");
        }
    }

    private async Task TryLoadSiblingBankAsync(string path)
    {
        var sibling = Path.ChangeExtension(path, Path.GetExtension(path).Equals(".acb", StringComparison.OrdinalIgnoreCase) ? ".awb" : ".acb");
        if (File.Exists(sibling))
        {
            if (sibling.EndsWith(".acb", StringComparison.OrdinalIgnoreCase))
            {
                AcbPathTextBox.Text = sibling;
            }
            else
            {
                AwbPathTextBox.Text = sibling;
            }

            await InspectAwbAsync();
        }
    }

    private async Task RestoreSelectedAudioAsync()
    {
        var path = WavPathTextBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        var loopModeIndex = Math.Clamp(LoopModeComboBox.SelectedIndex, 0, 2);
        var loopStart = (int)(LoopStartBox.Value ?? 0);
        var loopEnd = (int)(LoopEndBox.Value ?? 0);
        try
        {
            _loadingPreferences = true;
            await LoadWavInfoAsync(path);
            if (_wavSamples > 0)
            {
                LoopModeComboBox.SelectedIndex = loopModeIndex;
                if (loopModeIndex == 1)
                {
                    LoopStartBox.Value = Math.Clamp(loopStart, 0, Math.Max(0, _wavSamples - 1));
                    LoopEndBox.Value = Math.Clamp(loopEnd, Math.Min(_wavSamples, loopStart + 1), _wavSamples);
                }
                SetLoopEnabled(loopModeIndex == 1 || loopModeIndex == 0 && _wavLoopStart is not null);
                UpdateLoopVisuals();
                SetPlayerSource(path);
            }
        }
        finally
        {
            _loadingPreferences = false;
        }
    }

    private void LoadPreferences()
    {
        if (!File.Exists(_preferencesPath))
        {
            return;
        }

        try
        {
            _loadingPreferences = true;
            var json = File.ReadAllText(_preferencesPath);
            var preferences = JsonSerializer.Deserialize<UserPreferences>(json, PreferenceJsonOptions());
            if (preferences is null)
            {
                return;
            }

            AcbPathTextBox.Text = preferences.AcbPath ?? "";
            AwbPathTextBox.Text = preferences.AwbPath ?? "";
            WavPathTextBox.Text = preferences.AudioPath ?? "";
            OutputDirectoryTextBox.Text = string.IsNullOrWhiteSpace(preferences.OutputDirectory)
                ? Path.Combine(_dataRoot, "work")
                : preferences.OutputDirectory;
            SelectorModeComboBox.SelectedIndex = Math.Clamp(preferences.SelectorModeIndex, 0, 1);
            EntryNumberBox.Value = Math.Clamp(preferences.EntryNumber, 0, 999999);
            LoopModeComboBox.SelectedIndex = Math.Clamp(preferences.LoopModeIndex, 0, 2);
            LoopStartBox.Value = Math.Max(0, preferences.LoopStart);
            LoopEndBox.Value = Math.Max(0, preferences.LoopEnd);
            KeepHcaCheckBox.IsChecked = preferences.KeepHca;
            KeepReportsCheckBox.IsChecked = preferences.KeepReports;
            UseModSuffixCheckBox.IsChecked = preferences.UseModSuffix;
            OverwriteOutputCheckBox.IsChecked = preferences.OverwriteOutput;
            PatchWaveformCheckBox.IsChecked = preferences.PatchWaveform;
        }
        catch (Exception ex)
        {
            AppendLog($"No se pudieron cargar preferencias: {ex.Message}");
        }
        finally
        {
            _loadingPreferences = false;
        }
    }

    private void SavePreferences()
    {
        if (!_uiReady || _loadingPreferences)
        {
            return;
        }

        try
        {
            var preferences = new UserPreferences
            {
                AcbPath = AcbPathTextBox.Text?.Trim() ?? "",
                AwbPath = AwbPathTextBox.Text?.Trim() ?? "",
                AudioPath = WavPathTextBox.Text?.Trim() ?? "",
                OutputDirectory = OutputDirectoryTextBox.Text?.Trim() ?? "",
                SelectorModeIndex = Math.Clamp(SelectorModeComboBox.SelectedIndex, 0, 1),
                EntryNumber = (int)(EntryNumberBox.Value ?? 0),
                LoopModeIndex = Math.Clamp(LoopModeComboBox.SelectedIndex, 0, 2),
                LoopStart = (int)(LoopStartBox.Value ?? 0),
                LoopEnd = (int)(LoopEndBox.Value ?? 0),
                KeepHca = KeepHcaCheckBox.IsChecked == true,
                KeepReports = KeepReportsCheckBox.IsChecked == true,
                UseModSuffix = UseModSuffixCheckBox.IsChecked == true,
                OverwriteOutput = OverwriteOutputCheckBox.IsChecked == true,
                PatchWaveform = PatchWaveformCheckBox.IsChecked == true
            };

            Directory.CreateDirectory(Path.GetDirectoryName(_preferencesPath)!);
            File.WriteAllText(_preferencesPath, JsonSerializer.Serialize(preferences, PreferenceJsonOptions()));
        }
        catch (Exception ex)
        {
            AppendLog($"No se pudieron guardar preferencias: {ex.Message}");
        }
    }

    private void OnPreferenceChanged(object? sender, RoutedEventArgs e)
    {
        SavePreferences();
        UpdateCommandPreview();
    }

    private async Task RunBusyAsync(Func<Task> action)
    {
        ExecuteButton.IsEnabled = false;
        InspectAwbButton.IsEnabled = false;
        PreviewEntryButton.IsEnabled = false;
        try
        {
            await action();
        }
        finally
        {
            ExecuteButton.IsEnabled = true;
            InspectAwbButton.IsEnabled = true;
            PreviewEntryButton.IsEnabled = true;
        }
    }

    private async Task<ProcessResult> RunPythonAsync(IReadOnlyList<string> arguments)
    {
        var scriptPath = Path.Combine(_audioRoot, "tools", "cri_audio_tool.py");
        var python = ResolvePython();
        if (python is null)
        {
            return new ProcessResult(127, "", "No se encontró Python 3. Instala Python o define L5_AUDIO_PYTHON con la ruta del ejecutable.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = python,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = _audioRoot
        };
        startInfo.Environment["L5_AUDIO_ROOT"] = _audioRoot;
        startInfo.Environment["L5_AUDIO_DATA_ROOT"] = _dataRoot;
        startInfo.ArgumentList.Add(scriptPath);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return new ProcessResult(127, "", "No se pudo iniciar Python.");
        }
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        return new ProcessResult(process.ExitCode, stdout, stderr);
    }

    private static JsonSerializerOptions JsonOptions()
    {
        return new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
    }

    private static JsonSerializerOptions PreferenceJsonOptions()
    {
        return new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            WriteIndented = true
        };
    }

    private static string? ResolvePython()
    {
        var explicitPython = Environment.GetEnvironmentVariable("L5_AUDIO_PYTHON");
        if (ToolStarts(explicitPython, "--version"))
        {
            return explicitPython;
        }

        var candidates = OperatingSystem.IsWindows()
            ? new[] { "py", "python" }
            : ["python3", "python"];
        return candidates.FirstOrDefault(candidate => ToolStarts(candidate, "--version"));
    }

    private static bool ToolStarts(string? fileName, string argument)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = fileName,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                ArgumentList = { argument }
            });
            process?.WaitForExit(3000);
            return process is not null && process.HasExited && process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static string ResolveAudioRoot()
    {
        var explicitRoot = Environment.GetEnvironmentVariable("L5_AUDIO_ROOT");
        if (IsAudioRoot(explicitRoot))
        {
            return Path.GetFullPath(explicitRoot!);
        }

        var directory = AppContext.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(directory))
        {
            if (IsAudioRoot(directory))
            {
                return directory;
            }

            var parent = Directory.GetParent(directory);
            if (parent is null)
            {
                break;
            }

            directory = parent.FullName;
        }

        return AppContext.BaseDirectory;
    }

    private static bool IsAudioRoot(string? path)
    {
        return !string.IsNullOrWhiteSpace(path) && File.Exists(Path.Combine(path, "tools", "cri_audio_tool.py"));
    }

    private static string ResolveDataRoot()
    {
        var explicitRoot = Environment.GetEnvironmentVariable("L5_AUDIO_DATA_ROOT");
        if (!string.IsNullOrWhiteSpace(explicitRoot))
        {
            return Path.GetFullPath(explicitRoot);
        }

        var basePath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(basePath))
        {
            basePath = AppContext.BaseDirectory;
        }

        return Path.Combine(basePath, "LilacAudioTool");
    }

    private static void OpenWithSystemPlayer(string path)
    {
        ProcessStartInfo startInfo;
        if (OperatingSystem.IsMacOS())
        {
            startInfo = new ProcessStartInfo("open", [path]);
        }
        else if (OperatingSystem.IsWindows())
        {
            startInfo = new ProcessStartInfo(path) { UseShellExecute = true };
        }
        else
        {
            startInfo = new ProcessStartInfo("xdg-open", [path]);
        }

        Process.Start(startInfo);
    }

    private void AppendLog(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        LogTextBox.Text += $"[{DateTime.Now:HH:mm:ss}] {text.Trim()}{Environment.NewLine}";
        LogTextBox.CaretIndex = LogTextBox.Text.Length;
    }

    private sealed record ProcessResult(int ExitCode, string Stdout, string Stderr)
    {
        public string CombinedOutput => string.Join(Environment.NewLine, new[] { Stdout, Stderr }.Where(static value => !string.IsNullOrWhiteSpace(value)));
    }

    private sealed record AwbEntryViewModel(int Index, int Id, string Extension, long Size, string Name);

    private sealed record ReplacementJob(string SelectorMode, int Entry, string AudioPath)
    {
        public string Label => $"{(SelectorMode == "--id" ? "ID" : UiText.Current.Index)} {Entry} <- {Path.GetFileName(AudioPath) ?? AudioPath}";
    }

    private sealed record ReplacementQueueItem(string SelectorMode, int Entry, string AudioPath)
    {
        public string Mode => SelectorMode == "--id" ? "ID" : UiText.Current.Index;
        public string AudioName => Path.GetFileName(AudioPath) ?? AudioPath;
    }

    private sealed class UiText
    {
        public static UiText Current => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("es", StringComparison.OrdinalIgnoreCase)
            ? Spanish
            : English;

        private static UiText Spanish { get; } = new()
        {
            Subtitle = "Reemplazo de audio ACB/AWB mediante backend Python",
            Files = "Archivos",
            Bank = "ACB/AWB",
            Browse = "Examinar",
            Changes = "Cambios",
            ReadEntries = "Leer entradas AWB",
            PlayEntry = "Reproducir entrada",
            Replace = "Sustituir",
            Remove = "Quitar",
            Clear = "Limpiar",
            Loop = "Loop",
            UseSmpl = "Usar smpl",
            KeepHca = "Conservar HCA generado junto al AWB",
            KeepReports = "Conservar reports/logs de exportación",
            UseModSuffix = "Añadir sufijo .mod",
            OverwriteOutput = "Sobreescribir salida existente",
            PatchWaveform = "Actualizar duración y formato en WaveformTable",
            Export = "Export",
            Entries = "Entradas AWB",
            Operation = "Operación",
            AcbWatermark = "Banco .acb",
            AwbWatermark = "Banco .awb",
            Mode = "Modo",
            Entry = "Entrada",
            Audio = "Audio",
            Index = "Índice",
            Id = "ID",
            Type = "Tipo",
            Size = "Tamaño",
            Clip = "Clip",
            AutoWavSmpl = "Auto WAV smpl",
            Manual = "Manual",
            NoLoop = "Sin loop",
            AudioMissingPrefix = "No existe el archivo en cola",
            OutputExistsPrefix = "La salida ya existe. Activa sobreescritura para reemplazar",
            OutputMatchesSource = "La salida coincide con el banco original. Elige otra carpeta o activa el sufijo .mod para no sobrescribir la fuente."
        };

        private static UiText English { get; } = new()
        {
            Subtitle = "ACB/AWB audio replacement through the Python backend",
            Files = "Files",
            Bank = "ACB/AWB",
            Browse = "Browse",
            Changes = "Changes",
            ReadEntries = "Read AWB entries",
            PlayEntry = "Play entry",
            Replace = "Replace",
            Remove = "Remove",
            Clear = "Clear",
            Loop = "Loop",
            UseSmpl = "Use smpl",
            KeepHca = "Keep generated HCA next to the AWB",
            KeepReports = "Keep export reports/logs",
            UseModSuffix = "Append .mod suffix",
            OverwriteOutput = "Overwrite existing output",
            PatchWaveform = "Update duration and format in WaveformTable",
            Export = "Export",
            Entries = "AWB Entries",
            Operation = "Operation",
            AcbWatermark = ".acb bank",
            AwbWatermark = ".awb bank",
            Mode = "Mode",
            Entry = "Entry",
            Audio = "Audio",
            Index = "Index",
            Id = "ID",
            Type = "Type",
            Size = "Size",
            Clip = "Clip",
            AutoWavSmpl = "Auto WAV smpl",
            Manual = "Manual",
            NoLoop = "No loop",
            AudioMissingPrefix = "Queued file does not exist",
            OutputExistsPrefix = "Output already exists. Enable overwrite to replace",
            OutputMatchesSource = "The output path matches the original bank. Choose another folder or keep the .mod suffix to avoid overwriting the source."
        };

        public string Subtitle { get; init; } = "";
        public string Files { get; init; } = "";
        public string Bank { get; init; } = "";
        public string Browse { get; init; } = "";
        public string Changes { get; init; } = "";
        public string ReadEntries { get; init; } = "";
        public string PlayEntry { get; init; } = "";
        public string Replace { get; init; } = "";
        public string Remove { get; init; } = "";
        public string Clear { get; init; } = "";
        public string Loop { get; init; } = "";
        public string UseSmpl { get; init; } = "";
        public string KeepHca { get; init; } = "";
        public string KeepReports { get; init; } = "";
        public string UseModSuffix { get; init; } = "";
        public string OverwriteOutput { get; init; } = "";
        public string PatchWaveform { get; init; } = "";
        public string Export { get; init; } = "";
        public string Entries { get; init; } = "";
        public string Operation { get; init; } = "";
        public string AcbWatermark { get; init; } = "";
        public string AwbWatermark { get; init; } = "";
        public string Mode { get; init; } = "";
        public string Entry { get; init; } = "";
        public string Audio { get; init; } = "";
        public string Index { get; init; } = "";
        public string Id { get; init; } = "";
        public string Type { get; init; } = "";
        public string Size { get; init; } = "";
        public string Clip { get; init; } = "";
        public string AutoWavSmpl { get; init; } = "";
        public string Manual { get; init; } = "";
        public string NoLoop { get; init; } = "";
        public string AudioMissingPrefix { get; init; } = "";
        public string OutputExistsPrefix { get; init; } = "";
        public string OutputMatchesSource { get; init; } = "";

        public string AudioMissing(string path) => $"{AudioMissingPrefix}: {path}";
        public string OutputExists(string names) => $"{OutputExistsPrefix}: {names}";
    }

    private sealed class UserPreferences
    {
        public string AcbPath { get; set; } = "";
        public string AwbPath { get; set; } = "";
        public string AudioPath { get; set; } = "";
        public string OutputDirectory { get; set; } = "";
        public int SelectorModeIndex { get; set; }
        public int EntryNumber { get; set; }
        public int LoopModeIndex { get; set; }
        public int LoopStart { get; set; }
        public int LoopEnd { get; set; }
        public bool KeepHca { get; set; } = true;
        public bool KeepReports { get; set; }
        public bool UseModSuffix { get; set; } = true;
        public bool OverwriteOutput { get; set; }
        public bool PatchWaveform { get; set; }
    }

    private sealed class AwbMetadata
    {
        public List<AwbEntryMetadata>? Entries { get; set; }
    }

    private sealed class AwbEntryMetadata
    {
        public int Index { get; set; }
        public int Id { get; set; }
        public long Size { get; set; }
        public string? Extension { get; set; }
        public string? Name { get; set; }
    }

    private sealed class WavInfo
    {
        [JsonPropertyName("sample_count")]
        public int SampleCount { get; set; }
        [JsonPropertyName("sample_rate")]
        public int SampleRate { get; set; }
        [JsonPropertyName("loop")]
        public WavLoop? Loop { get; set; }
        [JsonPropertyName("peaks")]
        public double[] Peaks { get; set; } = [];
    }

    private sealed class WavLoop
    {
        [JsonPropertyName("start")]
        public int Start { get; set; }
        [JsonPropertyName("end")]
        public int End { get; set; }
    }

    private sealed class ReplaceReport
    {
        [JsonPropertyName("effective_sample_count")]
        public int EffectiveSampleCount { get; set; }
        [JsonPropertyName("loop_start")]
        public int? LoopStart { get; set; }
        [JsonPropertyName("loop_end")]
        public int? LoopEnd { get; set; }
        [JsonPropertyName("prepared_wav_info")]
        public PreparedWavInfo? PreparedWavInfo { get; set; }
        [JsonPropertyName("checks")]
        public List<ReplaceCheck>? Checks { get; set; }
    }

    private sealed class PreviewReport
    {
        [JsonPropertyName("wav")]
        public string? Wav { get; set; }
        [JsonPropertyName("decoder")]
        public string? Decoder { get; set; }
    }

    private sealed class PreparedWavInfo
    {
        [JsonPropertyName("sample_rate")]
        public int? SampleRate { get; set; }
        [JsonPropertyName("channels")]
        public int? Channels { get; set; }
    }

    private sealed class ReplaceCheck
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }
        [JsonPropertyName("status")]
        public string? Status { get; set; }
        [JsonExtensionData]
        public Dictionary<string, JsonElement>? Extra { get; set; }

        public string Describe()
        {
            if (Extra is null || Extra.Count == 0)
            {
                return "revisa el reporte generado.";
            }

            return string.Join(", ", Extra.Select(item => $"{item.Key}={FormatJsonValue(item.Value)}"));
        }

        private static string FormatJsonValue(JsonElement value)
        {
            return value.ValueKind switch
            {
                JsonValueKind.String => value.GetString() ?? "",
                JsonValueKind.Number => value.ToString(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                JsonValueKind.Null => "null",
                _ => value.GetRawText()
            };
        }
    }

    private sealed class PluginReport
    {
        [JsonPropertyName("checks")]
        public List<PluginCheck>? Checks { get; set; }
    }

    private sealed class PluginCheck
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }
        [JsonPropertyName("available")]
        public bool Available { get; set; }
        [JsonPropertyName("path")]
        public string? Path { get; set; }
        [JsonPropertyName("source")]
        public string? Source { get; set; }
        [JsonPropertyName("error")]
        public string? Error { get; set; }
    }

    private enum TimelineDragMode
    {
        None,
        Start,
        End
    }
}
