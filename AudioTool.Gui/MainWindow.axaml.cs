using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
    private readonly ObservableCollection<AwbEntryViewModel> _awbEntries = new();
    private readonly string _audioRoot;
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
        _playbackTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _playbackTimer.Tick += OnPlaybackTimerTick;
        _audioRoot = ResolveAudioRoot();
        _preferencesPath = Path.Combine(_audioRoot, "config", "user_preferences.json");
        AwbEntriesGrid.ItemsSource = _awbEntries;
        OutputDirectoryTextBox.Text = Path.Combine(_audioRoot, "work");
        CriEncoderPathTextBox.Text = ResolveDefaultCriEncoderPath();
        KeepHcaCheckBox.IsCheckedChanged += OnPreferenceChanged;
        PatchWaveformCheckBox.IsCheckedChanged += OnPreferenceChanged;
        LoadPreferences();
        SetLoopEnabled(false);
        _uiReady = true;
        UpdateCommandPreview();
        AppendLog($"AUDIO root: {_audioRoot}");
        AppendLog($"Preferencias: {_preferencesPath}");
        _ = RestoreSelectedAudioAsync();
        _ = EnsurePluginsAsync();
    }

    private async void OnBrowseAcbClick(object? sender, RoutedEventArgs e)
    {
        var path = await PickFileAsync("Seleccionar ACB", [new FilePickerFileType("CRI ACB") { Patterns = ["*.acb"] }]);
        if (!string.IsNullOrWhiteSpace(path))
        {
            AcbPathTextBox.Text = path;
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

    private async void OnBrowseCriEncoderClick(object? sender, RoutedEventArgs e)
    {
        var path = await PickFileAsync("Seleccionar encoder CRI HCA", [new FilePickerFileType("Ejecutable") { Patterns = ["*"] }]);
        if (!string.IsNullOrWhiteSpace(path))
        {
            CriEncoderPathTextBox.Text = path;
            SavePreferences();
            UpdateCommandPreview();
        }
    }

    private async void OnInspectAwbClick(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(AwbPathTextBox.Text))
        {
            AppendLog("Selecciona un AWB antes de inspeccionar.");
            return;
        }

        await RunBusyAsync(async () =>
        {
            var result = await RunPythonAsync(["inspect", AwbPathTextBox.Text!]);
            if (result.ExitCode != 0)
            {
                AppendLog(result.CombinedOutput);
                return;
            }

            var metadata = JsonSerializer.Deserialize<AwbMetadata>(result.Stdout, JsonOptions());
            _awbEntries.Clear();
            foreach (var entry in metadata?.Entries ?? [])
            {
                _awbEntries.Add(new AwbEntryViewModel(entry.Index, entry.Id, entry.Extension ?? "", entry.Size, entry.Sha1 ?? ""));
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
        var previewDirectory = Path.Combine(_audioRoot, "work", "preview");
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
            await LoadWavInfoAsync(preview.Wav);
            SetPlayerSource(preview.Wav);
            StartPlayback();
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
            AppendLog("No hay un WAV cargado para reproducir. Usa un WAV de reemplazo o previsualiza una entrada AWB.");
            return;
        }

        StartPlayback();
    }

    private async void OnExecuteClick(object? sender, RoutedEventArgs e)
    {
        if (!ValidateInputs(out var acbPath, out var awbPath, out var wavPath, out var outputDirectory))
        {
            return;
        }

        var stem = Path.GetFileNameWithoutExtension(awbPath);
        var targetAwb = Path.Combine(outputDirectory, $"{stem}.mod.awb");
        var targetAcb = Path.Combine(outputDirectory, $"{Path.GetFileNameWithoutExtension(acbPath)}.mod.acb");
        var selectorMode = SelectorModeComboBox.SelectedIndex == 0 ? "--id" : "--index";
        var selectorValue = ((int)(EntryNumberBox.Value ?? 0)).ToString();
        var patchAwbId = 0;
        if (PatchWaveformCheckBox.IsChecked == true && !TryResolvePatchAwbId(out patchAwbId))
        {
            return;
        }

        await RunBusyAsync(async () =>
        {
            Directory.CreateDirectory(outputDirectory);

            var replaceArgs = new List<string>
            {
                "replace-awb-wav",
                awbPath,
                targetAwb,
                wavPath,
                selectorMode,
                selectorValue
            };

            AddLoopArgs(replaceArgs);
            var criEncoderPath = CriEncoderPathTextBox.Text?.Trim();
            if (!string.IsNullOrWhiteSpace(criEncoderPath))
            {
                replaceArgs.Add("--cri-hca-encoder");
                replaceArgs.Add(criEncoderPath);
            }
            if (KeepHcaCheckBox.IsChecked == true)
            {
                replaceArgs.Add("--keep-hca");
            }

            AppendLog("Generando AWB modificado...");
            var replaceResult = await RunPythonAsync(replaceArgs);
            AppendLog(replaceResult.CombinedOutput);
            if (replaceResult.ExitCode != 0)
            {
                return;
            }

            var replaceReport = LoadReplaceReport(targetAwb);
            LogReplaceWarnings(replaceReport);

            if (PatchWaveformCheckBox.IsChecked == true)
            {
                var patchArgs = new List<string>
                {
                    "patch-acb-waveform",
                    acbPath,
                    targetAcb,
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
                AppendLog("Parcheando WaveformTable del ACB...");
                var patchResult = await RunPythonAsync(patchArgs);
                AppendLog(patchResult.CombinedOutput);
                if (patchResult.ExitCode != 0)
                {
                    return;
                }
            }
            else
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
            AppendLog("El archivo no expone metadata WAV directa. Se normalizará con FFmpeg al generar.");
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
        AppendLog($"WAV: {_wavSamples} samples, {_wavSampleRate} Hz, loop: {DescribeLoop()}.");
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
            if (!string.Equals(check.Status, "warning", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            AppendLog($"Aviso: {check.Name} - {check.Describe()}");
        }
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

    private bool ValidateInputs(out string acbPath, out string awbPath, out string wavPath, out string outputDirectory)
    {
        acbPath = AcbPathTextBox.Text?.Trim() ?? "";
        awbPath = AwbPathTextBox.Text?.Trim() ?? "";
        wavPath = WavPathTextBox.Text?.Trim() ?? "";
        outputDirectory = OutputDirectoryTextBox.Text?.Trim() ?? "";

        foreach (var path in new[] { acbPath, awbPath, wavPath })
        {
            if (!File.Exists(path))
            {
                AppendLog($"No existe el archivo: {path}");
                return false;
            }
        }

        var criEncoderPath = CriEncoderPathTextBox.Text?.Trim();
        if (!string.IsNullOrWhiteSpace(criEncoderPath) && !File.Exists(criEncoderPath))
        {
            AppendLog($"No existe el encoder CRI: {criEncoderPath}");
            return false;
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
        var selectedValue = (int)(EntryNumberBox.Value ?? 0);
        if (SelectorModeComboBox.SelectedIndex == 0)
        {
            awbId = selectedValue;
            return true;
        }

        var entry = _awbEntries.FirstOrDefault(item => item.Index == selectedValue);
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
            ? "Carga un WAV para ver duración y loop."
            : $"WAV: {_wavSamples} samples ({FormatSeconds(_wavSamples)}). Loop: {DescribeLoop()}.";
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
        CommandPreviewTextBlock.Text = $"replace-awb-wav ... {selectorMode} {selectorValue} / patch-acb-waveform ... --id {patchIdText} / patch-acb-stream-awb ...";
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
                ? Path.Combine(_audioRoot, "work")
                : preferences.OutputDirectory;
            CriEncoderPathTextBox.Text = string.IsNullOrWhiteSpace(preferences.CriEncoderPath)
                ? ResolveDefaultCriEncoderPath()
                : preferences.CriEncoderPath;
            SelectorModeComboBox.SelectedIndex = Math.Clamp(preferences.SelectorModeIndex, 0, 1);
            EntryNumberBox.Value = Math.Clamp(preferences.EntryNumber, 0, 999999);
            LoopModeComboBox.SelectedIndex = Math.Clamp(preferences.LoopModeIndex, 0, 2);
            LoopStartBox.Value = Math.Max(0, preferences.LoopStart);
            LoopEndBox.Value = Math.Max(0, preferences.LoopEnd);
            KeepHcaCheckBox.IsChecked = preferences.KeepHca;
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
                CriEncoderPath = CriEncoderPathTextBox.Text?.Trim() ?? "",
                SelectorModeIndex = Math.Clamp(SelectorModeComboBox.SelectedIndex, 0, 1),
                EntryNumber = (int)(EntryNumberBox.Value ?? 0),
                LoopModeIndex = Math.Clamp(LoopModeComboBox.SelectedIndex, 0, 2),
                LoopStart = (int)(LoopStartBox.Value ?? 0),
                LoopEnd = (int)(LoopEndBox.Value ?? 0),
                KeepHca = KeepHcaCheckBox.IsChecked == true,
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
        var startInfo = new ProcessStartInfo
        {
            FileName = ResolvePython(),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = _audioRoot
        };
        startInfo.ArgumentList.Add(scriptPath);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("No se pudo iniciar Python.");
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

    private static string ResolvePython()
    {
        return OperatingSystem.IsWindows() ? "python" : "python3";
    }

    private static string ResolveAudioRoot()
    {
        var directory = AppContext.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(directory))
        {
            if (File.Exists(Path.Combine(directory, "tools", "cri_audio_tool.py")))
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

        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    }

    private string ResolveDefaultCriEncoderPath()
    {
        var candidates = new[]
        {
            Path.Combine(_audioRoot, "vendor", "cri_adxle_tools_3.56.01", "cri", "tools", "ADX2LE", "ver.3", "CriAtomEncoderHcaLite.exe"),
            Path.Combine(_audioRoot, "tools", "CriAtomEncoderHcaLite.exe"),
            Path.Combine(_audioRoot, "PlugIns", "CriAtomEncoderHcaLite.exe")
        };

        return candidates.FirstOrDefault(File.Exists) ?? "";
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

    private sealed record AwbEntryViewModel(int Index, int Id, string Extension, long Size, string Sha1);

    private sealed class UserPreferences
    {
        public string AcbPath { get; set; } = "";
        public string AwbPath { get; set; } = "";
        public string AudioPath { get; set; } = "";
        public string OutputDirectory { get; set; } = "";
        public string CriEncoderPath { get; set; } = "";
        public int SelectorModeIndex { get; set; }
        public int EntryNumber { get; set; }
        public int LoopModeIndex { get; set; }
        public int LoopStart { get; set; }
        public int LoopEnd { get; set; }
        public bool KeepHca { get; set; } = true;
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
        public string? Sha1 { get; set; }
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
