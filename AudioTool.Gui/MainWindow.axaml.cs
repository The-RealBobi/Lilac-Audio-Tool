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

namespace Level5.AudioTool.Gui;

public sealed partial class MainWindow : Window
{
    private readonly ObservableCollection<AwbEntryViewModel> _awbEntries = new();
    private readonly string _audioRoot;
    private bool _uiReady;
    private bool _updatingLoopControls;
    private int _wavSamples;
    private int _wavSampleRate;
    private int? _wavLoopStart;
    private int? _wavLoopEnd;

    public MainWindow()
    {
        InitializeComponent();
        _audioRoot = ResolveAudioRoot();
        AwbEntriesGrid.ItemsSource = _awbEntries;
        OutputDirectoryTextBox.Text = Path.Combine(_audioRoot, "work");
        SetLoopEnabled(false);
        _uiReady = true;
        UpdateCommandPreview();
        AppendLog($"AUDIO root: {_audioRoot}");
    }

    private async void OnBrowseAcbClick(object? sender, RoutedEventArgs e)
    {
        var path = await PickFileAsync("Seleccionar ACB", [new FilePickerFileType("CRI ACB") { Patterns = ["*.acb"] }]);
        if (!string.IsNullOrWhiteSpace(path))
        {
            AcbPathTextBox.Text = path;
            UpdateCommandPreview();
        }
    }

    private async void OnBrowseAwbClick(object? sender, RoutedEventArgs e)
    {
        var path = await PickFileAsync("Seleccionar AWB", [new FilePickerFileType("CRI AWB") { Patterns = ["*.awb"] }]);
        if (!string.IsNullOrWhiteSpace(path))
        {
            AwbPathTextBox.Text = path;
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
            UpdateCommandPreview();
        }
    }

    private async void OnBrowseCriEncoderClick(object? sender, RoutedEventArgs e)
    {
        var path = await PickFileAsync("Seleccionar encoder CRI HCA", [new FilePickerFileType("Ejecutable") { Patterns = ["*"] }]);
        if (!string.IsNullOrWhiteSpace(path))
        {
            CriEncoderPathTextBox.Text = path;
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
        UpdateCommandPreview();
    }

    private void OnSelectorChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady)
        {
            return;
        }

        UpdateCommandPreview();
    }

    private void OnSelectorNumberChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (!_uiReady)
        {
            return;
        }

        UpdateCommandPreview();
    }

    private void OnUseWavLoopClick(object? sender, RoutedEventArgs e)
    {
        ApplyWavLoop();
        LoopModeComboBox.SelectedIndex = _wavLoopStart is null ? 2 : 0;
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
        UpdateCommandPreview();
    }

    private void OnLoopCanvasSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (!_uiReady)
        {
            return;
        }

        UpdateLoopVisuals();
    }

    private void OnLoopCanvasPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_wavSamples <= 0 || LoopModeComboBox.SelectedIndex == 2)
        {
            return;
        }

        LoopModeComboBox.SelectedIndex = 1;
        var x = e.GetPosition(LoopCanvas).X;
        var sample = XToSample(x);
        var start = (int)(LoopStartBox.Value ?? 0);
        var end = (int)(LoopEndBox.Value ?? _wavSamples);
        if (Math.Abs(sample - start) <= Math.Abs(sample - end))
        {
            LoopStartBox.Value = Math.Clamp(sample, 0, Math.Max(0, end - 1));
        }
        else
        {
            LoopEndBox.Value = Math.Clamp(sample, Math.Min(_wavSamples, start + 1), _wavSamples);
        }
    }

    private void OnLoopStartDragDelta(object? sender, VectorEventArgs e)
    {
        if (_wavSamples <= 0)
        {
            return;
        }

        LoopModeComboBox.SelectedIndex = 1;
        var start = (int)(LoopStartBox.Value ?? 0);
        var end = (int)(LoopEndBox.Value ?? _wavSamples);
        LoopStartBox.Value = Math.Clamp(start + DeltaToSamples(e.Vector.X), 0, Math.Max(0, end - 1));
    }

    private void OnLoopEndDragDelta(object? sender, VectorEventArgs e)
    {
        if (_wavSamples <= 0)
        {
            return;
        }

        LoopModeComboBox.SelectedIndex = 1;
        var start = (int)(LoopStartBox.Value ?? 0);
        var end = (int)(LoopEndBox.Value ?? _wavSamples);
        LoopEndBox.Value = Math.Clamp(end + DeltaToSamples(e.Vector.X), Math.Min(_wavSamples, start + 1), _wavSamples);
    }

    private async Task LoadWavInfoAsync(string path)
    {
        var result = await RunPythonAsync(["wav-info", path]);
        if (result.ExitCode != 0)
        {
            _wavSamples = 0;
            _wavSampleRate = 0;
            _wavLoopStart = null;
            _wavLoopEnd = null;
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
        var width = LoopCanvas.Bounds.Width;
        if (width <= 1 || _wavSamples <= 0)
        {
            Canvas.SetLeft(LoopSelectionBorder, 0);
            LoopSelectionBorder.Width = 0;
            return;
        }

        var start = LoopModeComboBox.SelectedIndex == 2 ? 0 : (int)(LoopStartBox.Value ?? 0);
        var end = LoopModeComboBox.SelectedIndex == 2 ? 0 : (int)(LoopEndBox.Value ?? _wavSamples);
        var startX = SampleToX(start);
        var endX = SampleToX(end);

        Canvas.SetLeft(LoopSelectionBorder, startX);
        LoopSelectionBorder.Width = Math.Max(0, endX - startX);
        Canvas.SetLeft(LoopStartThumb, Math.Clamp(startX - LoopStartThumb.Width / 2, 0, Math.Max(0, width - LoopStartThumb.Width)));
        Canvas.SetLeft(LoopEndThumb, Math.Clamp(endX - LoopEndThumb.Width / 2, 0, Math.Max(0, width - LoopEndThumb.Width)));
        LoopSummaryTextBlock.Text = _wavSamples <= 0
            ? "Carga un WAV para ver duración y loop."
            : $"WAV: {_wavSamples} samples ({FormatSeconds(_wavSamples)}). Loop: {DescribeLoop()}.";
    }

    private void SetLoopEnabled(bool enabled)
    {
        LoopStartBox.IsEnabled = enabled;
        LoopEndBox.IsEnabled = enabled;
        LoopStartThumb.IsEnabled = enabled;
        LoopEndThumb.IsEnabled = enabled;
        LoopCanvas.Opacity = enabled ? 1 : 0.45;
    }

    private double SampleToX(int sample)
    {
        return _wavSamples <= 0 ? 0 : LoopCanvas.Bounds.Width * sample / _wavSamples;
    }

    private int XToSample(double x)
    {
        if (_wavSamples <= 0 || LoopCanvas.Bounds.Width <= 0)
        {
            return 0;
        }

        return (int)Math.Round(Math.Clamp(x, 0, LoopCanvas.Bounds.Width) / LoopCanvas.Bounds.Width * _wavSamples);
    }

    private int DeltaToSamples(double deltaX)
    {
        return LoopCanvas.Bounds.Width <= 0 ? 0 : (int)Math.Round(deltaX / LoopCanvas.Bounds.Width * _wavSamples);
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

    private async Task RunBusyAsync(Func<Task> action)
    {
        ExecuteButton.IsEnabled = false;
        InspectAwbButton.IsEnabled = false;
        try
        {
            await action();
        }
        finally
        {
            ExecuteButton.IsEnabled = true;
            InspectAwbButton.IsEnabled = true;
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
        public int SampleCount { get; set; }
        public int SampleRate { get; set; }
        public WavLoop? Loop { get; set; }
    }

    private sealed class WavLoop
    {
        public int Start { get; set; }
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
    }

    private sealed class PreparedWavInfo
    {
        [JsonPropertyName("sample_rate")]
        public int? SampleRate { get; set; }
        [JsonPropertyName("channels")]
        public int? Channels { get; set; }
    }
}
