using Microsoft.Win32;
using Pixora.Services;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;

namespace Pixora;

public partial class BatchCompressWindow : Window
{
    private CancellationTokenSource? _runCts;
    private readonly BatchCompressionSettings _settings = BatchCompressionSettings.Load();
    private readonly List<string> _logLines = [];
    private string? _lastRunLogPath;
    private bool _isRunning;
    private bool _isApplyingSettings;
    private bool _outputFolderEditedByUser;
    private int _logCount;

    public BatchCompressWindow(string? initialInputPath = null)
    {
        InitializeComponent();
        ApplySavedWindowPlacement();
        ApplySavedSettings(initialInputPath);
        UpdateQualityState();
        UpdateOpenButtons();
    }

    private void InputPathText_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isApplyingSettings)
        {
            return;
        }

        if (!_outputFolderEditedByUser)
        {
            SetDefaultOutputFolder(force: false);
        }
    }

    private void OutputFolderText_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (IsLoaded && !_isApplyingSettings)
        {
            _outputFolderEditedByUser = true;
        }

        UpdateOpenButtons();
    }

    private void QualitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (QualityText is not null)
        {
            QualityText.Text = ((int)Math.Round(QualitySlider.Value)).ToString();
        }
    }

    private void FormatComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateQualityState();
    }

    private void BrowseInputFileButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择要压缩的图片",
            Filter = "图片文件|*.jpg;*.jpeg;*.jpe;*.jfif;*.png;*.apng;*.bmp;*.gif;*.webp;*.tif;*.tiff;*.ico;*.cur;*.heic;*.heif;*.avif;*.avifs;*.jxr;*.wdp;*.hdp;*.hdr|所有文件|*.*",
            CheckFileExists = true,
            Multiselect = false,
        };

        var initialFolder = GetExistingFolder(InputPathText.Text);
        if (initialFolder is not null)
        {
            dialog.InitialDirectory = initialFolder;
        }

        if (dialog.ShowDialog(this) == true)
        {
            InputPathText.Text = dialog.FileName;
            _outputFolderEditedByUser = false;
            SetDefaultOutputFolder(force: true);
        }
    }

    private void BrowseInputFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (TrySelectFolder("选择要批量压缩的目录", InputPathText.Text, out var folder))
        {
            InputPathText.Text = folder;
            _outputFolderEditedByUser = false;
            SetDefaultOutputFolder(force: true);
        }
    }

    private void BrowseOutputFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (TrySelectFolder("选择输出目录", OutputFolderText.Text, out var folder))
        {
            OutputFolderText.Text = folder;
            _outputFolderEditedByUser = true;
        }
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isRunning)
        {
            return;
        }

        if (!TryCreateOptions(out var options, out var error))
        {
            MessageBox.Show(this, error, AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (BatchImageCompressor.IsOutputFolderInsideInputFolder(options)
            && !ConfirmOutputFolderInsideInput(options))
        {
            return;
        }

        SetRunning(true);
        LogListBox.Items.Clear();
        _logLines.Clear();
        _lastRunLogPath = null;
        UpdateOpenButtons();
        _logCount = 0;
        StatusText.Text = "正在预扫描...";
        SummaryText.Text = string.Empty;
        ProgressBar.Value = 0;
        SaveCurrentSettings(options);
        AddLog($"输入：{options.InputPath}");
        AddLog($"输出目录：{options.OutputFolder}");
        AddLog($"设置：格式 {FormatOptionName(options.Format)}，质量 {options.JpegQuality}，最大宽高 {options.MaxWidth} x {options.MaxHeight}，包含子目录 {FormatBoolean(options.IncludeSubfolders)}，覆盖已有 {FormatBoolean(options.OverwriteExisting)}");

        _runCts = new CancellationTokenSource();
        var progress = new Progress<BatchCompressionProgress>(UpdateProgress);

        try
        {
            var preflight = await BatchImageCompressor.PreflightAsync(options, progress, _runCts.Token);
            var preflightSummary = FormatPreflightSummary(preflight);
            SummaryText.Text = preflightSummary;
            AddLog(preflightSummary);
            AddPreflightIssueLogs(preflight);

            if (preflight.Compressible == 0)
            {
                StatusText.Text = "预扫描完成（没有可压缩图片）";
                WriteRunLogToOutputFolder(options, StatusText.Text, preflightSummary);
                MessageBox.Show(this, "预扫描完成，但没有可以压缩的图片。动图和无法读取的图片不会压缩，详情请看日志。", AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (!ConfirmPreflight(options, preflight))
            {
                StatusText.Text = "已取消";
                AddLog("用户在预扫描后取消。");
                return;
            }

            StatusText.Text = "正在压缩...";
            ProgressBar.Value = 0;
            var result = await BatchImageCompressor.CompressAsync(options, progress, _runCts.Token);
            ProgressBar.Value = result.Total == 0 ? 0 : result.Total;
            StatusText.Text = HasCompletionIssues(result)
                ? "批量压缩完成（有文件未压缩）"
                : "批量压缩完成";

            var summary = FormatResultSummary(result);
            SummaryText.Text = summary;
            AddLog(summary);
            AddCompletionIssueLogs(result);
            WriteRunLogToOutputFolder(options, StatusText.Text, summary);
            ShowCompletionIssueDialog(result);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "已取消";
            AddLog("任务已取消。");
            WriteRunLogToOutputFolder(options, StatusText.Text, SummaryText.Text);
        }
        catch (Exception ex)
        {
            StatusText.Text = "批量压缩失败";
            AddLog($"失败：{FriendlyException(ex)}");
            WriteRunLogToOutputFolder(options, StatusText.Text, FriendlyException(ex));
            MessageBox.Show(this, $"批量压缩失败：\n{FriendlyException(ex)}", AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _runCts?.Dispose();
            _runCts = null;
            SetRunning(false);
        }
    }

    private void CancelRunButton_Click(object sender, RoutedEventArgs e)
    {
        _runCts?.Cancel();
        CancelRunButton.IsEnabled = false;
        StatusText.Text = "正在取消...";
    }

    private void OpenOutputFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var folder = OutputFolderText.Text.Trim();
        if (string.IsNullOrWhiteSpace(folder))
        {
            MessageBox.Show(this, "请先选择输出目录。", AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            Directory.CreateDirectory(folder);
            OpenShellPath(folder);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"打开输出目录失败：\n{FriendlyException(ex)}", AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OpenLogButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_lastRunLogPath) || !File.Exists(_lastRunLogPath))
        {
            MessageBox.Show(this, "本次任务还没有生成可打开的日志。", AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Information);
            UpdateOpenButtons();
            return;
        }

        try
        {
            OpenShellPath(_lastRunLogPath);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"打开日志失败：\n{FriendlyException(ex)}", AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_isRunning)
        {
            SaveCurrentSettings();
            return;
        }

        var confirm = MessageBox.Show(
            this,
            "批量压缩还在运行，确定要取消并关闭吗？",
            "批量压缩图片",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (confirm != MessageBoxResult.Yes)
        {
            e.Cancel = true;
            return;
        }

        _runCts?.Cancel();
        SaveCurrentSettings();
    }

    private void UpdateProgress(BatchCompressionProgress progress)
    {
        ProgressBar.Maximum = Math.Max(1, progress.Total);
        ProgressBar.Value = Math.Clamp(progress.Completed, 0, Math.Max(1, progress.Total));
        StatusText.Text = progress.Total == 0
            ? progress.Message
            : $"{progress.Completed}/{progress.Total}  {progress.Message}";
        AddLog(progress.Message);
    }

    private void AddLog(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        _logCount++;
        var line = $"{_logCount:0000}  {message}";
        _logLines.Add(line);
        LogListBox.Items.Add(line);
        if (LogListBox.Items.Count > 0)
        {
            LogListBox.ScrollIntoView(LogListBox.Items[^1]);
        }
    }

    private bool TryCreateOptions(out BatchCompressionOptions options, out string error)
    {
        options = new BatchCompressionOptions(string.Empty, string.Empty, ImageCompressionFormat.Jpeg, 82, 0, 0, false, false);
        error = string.Empty;

        var inputPath = InputPathText.Text.Trim();
        if (string.IsNullOrWhiteSpace(inputPath))
        {
            error = "请选择输入文件或目录。";
            return false;
        }

        if (!File.Exists(inputPath) && !Directory.Exists(inputPath))
        {
            error = "输入文件或目录不存在。";
            return false;
        }

        var outputFolder = OutputFolderText.Text.Trim();
        if (string.IsNullOrWhiteSpace(outputFolder))
        {
            error = "请选择输出目录。";
            return false;
        }

        if (!TryParseNonNegative(MaxWidthText.Text, out var maxWidth)
            || !TryParseNonNegative(MaxHeightText.Text, out var maxHeight))
        {
            error = "最大宽高必须是 0 或正整数。";
            return false;
        }

        var format = GetSelectedFormat();
        var quality = Math.Clamp((int)Math.Round(QualitySlider.Value), 1, 100);
        options = new BatchCompressionOptions(
            inputPath,
            outputFolder,
            format,
            quality,
            maxWidth,
            maxHeight,
            IncludeSubfoldersCheckBox.IsChecked == true,
            OverwriteExistingCheckBox.IsChecked == true);
        return true;
    }

    private ImageCompressionFormat GetSelectedFormat()
    {
        if (FormatComboBox.SelectedItem is ComboBoxItem { Tag: string tag }
            && Enum.TryParse<ImageCompressionFormat>(tag, out var format))
        {
            return format;
        }

        return ImageCompressionFormat.Jpeg;
    }

    private void ApplySavedSettings(string? initialInputPath)
    {
        _isApplyingSettings = true;
        try
        {
            SelectFormat(_settings.Format);
            QualitySlider.Value = Math.Clamp(_settings.JpegQuality, 1, 100);
            QualityText.Text = ((int)Math.Round(QualitySlider.Value)).ToString();
            MaxWidthText.Text = Math.Max(0, _settings.MaxWidth).ToString();
            MaxHeightText.Text = Math.Max(0, _settings.MaxHeight).ToString();
            IncludeSubfoldersCheckBox.IsChecked = _settings.IncludeSubfolders;
            OverwriteExistingCheckBox.IsChecked = _settings.OverwriteExisting;

            var inputPath = !string.IsNullOrWhiteSpace(initialInputPath)
                ? initialInputPath
                : _settings.LastInputPath;
            if (!string.IsNullOrWhiteSpace(inputPath) && (File.Exists(inputPath) || Directory.Exists(inputPath)))
            {
                InputPathText.Text = inputPath;
            }

            var canUseSavedOutputFolder = !string.IsNullOrWhiteSpace(_settings.LastOutputFolder)
                && (string.IsNullOrWhiteSpace(initialInputPath) || PathsEqual(inputPath, _settings.LastInputPath));
            if (canUseSavedOutputFolder)
            {
                OutputFolderText.Text = _settings.LastOutputFolder;
                _outputFolderEditedByUser = true;
            }
            else
            {
                _outputFolderEditedByUser = false;
                SetDefaultOutputFolder(force: true);
            }
        }
        finally
        {
            _isApplyingSettings = false;
        }
    }

    private void SelectFormat(ImageCompressionFormat format)
    {
        foreach (var item in FormatComboBox.Items.OfType<ComboBoxItem>())
        {
            if (item.Tag is string tag
                && Enum.TryParse<ImageCompressionFormat>(tag, out var itemFormat)
                && itemFormat == format)
            {
                FormatComboBox.SelectedItem = item;
                return;
            }
        }

        FormatComboBox.SelectedIndex = 0;
    }

    private void UpdateQualityState()
    {
        if (QualitySlider is null || QualityText is null)
        {
            return;
        }

        var isJpeg = GetSelectedFormat() == ImageCompressionFormat.Jpeg;
        QualitySlider.IsEnabled = isJpeg && !_isRunning;
        QualityText.Text = isJpeg ? ((int)Math.Round(QualitySlider.Value)).ToString() : "-";
    }

    private void SetDefaultOutputFolder(bool force)
    {
        if (!force && _outputFolderEditedByUser)
        {
            return;
        }

        var folder = BatchImageCompressor.GetDefaultOutputFolder(InputPathText.Text.Trim());
        if (string.IsNullOrWhiteSpace(folder))
        {
            return;
        }

        OutputFolderText.Text = folder;
        _outputFolderEditedByUser = false;
    }

    private void SetRunning(bool isRunning)
    {
        _isRunning = isRunning;
        StartButton.IsEnabled = !isRunning;
        CancelRunButton.IsEnabled = isRunning;
        InputPathText.IsEnabled = !isRunning;
        OutputFolderText.IsEnabled = !isRunning;
        FormatComboBox.IsEnabled = !isRunning;
        UpdateQualityState();
        MaxWidthText.IsEnabled = !isRunning;
        MaxHeightText.IsEnabled = !isRunning;
        IncludeSubfoldersCheckBox.IsEnabled = !isRunning;
        OverwriteExistingCheckBox.IsEnabled = !isRunning;
        UpdateOpenButtons();
    }

    private void SaveCurrentSettings(BatchCompressionOptions? options = null)
    {
        try
        {
            _settings.LastInputPath = options?.InputPath ?? TrimToNull(InputPathText.Text);
            _settings.LastOutputFolder = options?.OutputFolder ?? TrimToNull(OutputFolderText.Text);
            _settings.Format = options?.Format ?? GetSelectedFormat();
            _settings.JpegQuality = options?.JpegQuality ?? Math.Clamp((int)Math.Round(QualitySlider.Value), 1, 100);
            _settings.MaxWidth = options?.MaxWidth ?? ReadNonNegative(MaxWidthText.Text, _settings.MaxWidth);
            _settings.MaxHeight = options?.MaxHeight ?? ReadNonNegative(MaxHeightText.Text, _settings.MaxHeight);
            _settings.IncludeSubfolders = options?.IncludeSubfolders ?? IncludeSubfoldersCheckBox.IsChecked == true;
            _settings.OverwriteExisting = options?.OverwriteExisting ?? OverwriteExistingCheckBox.IsChecked == true;
            SaveWindowPlacement();
            _settings.Save();
        }
        catch (Exception ex)
        {
            ErrorLog.WriteException("SaveBatchCompressionSettings", "保存批量压缩设置失败。", ex);
        }
    }

    private void ApplySavedWindowPlacement()
    {
        if (_settings.BatchCompressWindowWidth >= MinWidth)
        {
            Width = _settings.BatchCompressWindowWidth;
        }

        if (_settings.BatchCompressWindowHeight >= MinHeight)
        {
            Height = _settings.BatchCompressWindowHeight;
        }

        var savedLeft = _settings.BatchCompressWindowLeft;
        var savedTop = _settings.BatchCompressWindowTop;
        if (savedLeft.HasValue
            && savedTop.HasValue
            && IsRectVisibleOnVirtualScreen(savedLeft.Value, savedTop.Value, Width, Height))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = savedLeft.Value;
            Top = savedTop.Value;
        }

        if (_settings.BatchCompressWindowMaximized)
        {
            WindowState = WindowState.Maximized;
        }
    }

    private void SaveWindowPlacement()
    {
        _settings.BatchCompressWindowMaximized = WindowState == WindowState.Maximized;
        var bounds = RestoreBounds;
        if (bounds.Width >= MinWidth)
        {
            _settings.BatchCompressWindowWidth = bounds.Width;
        }

        if (bounds.Height >= MinHeight)
        {
            _settings.BatchCompressWindowHeight = bounds.Height;
        }

        if (IsRectVisibleOnVirtualScreen(bounds.Left, bounds.Top, bounds.Width, bounds.Height))
        {
            _settings.BatchCompressWindowLeft = bounds.Left;
            _settings.BatchCompressWindowTop = bounds.Top;
        }
    }

    private static bool IsRectVisibleOnVirtualScreen(double left, double top, double width, double height)
    {
        if (double.IsNaN(left)
            || double.IsNaN(top)
            || width <= 0
            || height <= 0)
        {
            return false;
        }

        var right = left + width;
        var bottom = top + height;
        var screenLeft = SystemParameters.VirtualScreenLeft;
        var screenTop = SystemParameters.VirtualScreenTop;
        var screenRight = screenLeft + SystemParameters.VirtualScreenWidth;
        var screenBottom = screenTop + SystemParameters.VirtualScreenHeight;
        return right > screenLeft
            && left < screenRight
            && bottom > screenTop
            && top < screenBottom;
    }

    private void UpdateOpenButtons()
    {
        if (OpenOutputFolderButton is not null)
        {
            OpenOutputFolderButton.IsEnabled = !_isRunning && !string.IsNullOrWhiteSpace(OutputFolderText?.Text);
        }

        if (OpenLogButton is not null)
        {
            OpenLogButton.IsEnabled = !_isRunning
                && !string.IsNullOrWhiteSpace(_lastRunLogPath)
                && File.Exists(_lastRunLogPath);
        }
    }

    private static bool TryParseNonNegative(string value, out int result)
    {
        return int.TryParse(value.Trim(), out result) && result >= 0;
    }

    private static int ReadNonNegative(string value, int fallback)
    {
        return TryParseNonNegative(value, out var result) ? result : fallback;
    }

    private static bool TrySelectFolder(string title, string currentPath, out string folder)
    {
        folder = string.Empty;
        var dialog = new OpenFileDialog
        {
            Title = title,
            FileName = "选择此目录",
            CheckFileExists = false,
            ValidateNames = false,
            Multiselect = false,
        };

        var initialFolder = GetExistingFolder(currentPath);
        if (initialFolder is not null)
        {
            dialog.InitialDirectory = initialFolder;
        }

        if (dialog.ShowDialog() != true)
        {
            return false;
        }

        folder = Directory.Exists(dialog.FileName)
            ? dialog.FileName
            : Path.GetDirectoryName(dialog.FileName) ?? string.Empty;
        return !string.IsNullOrWhiteSpace(folder);
    }

    private static string? GetExistingFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            var fullPath = Path.GetFullPath(path);
            if (Directory.Exists(fullPath))
            {
                return fullPath;
            }

            if (File.Exists(fullPath))
            {
                return Path.GetDirectoryName(fullPath);
            }

            var parent = Path.GetDirectoryName(fullPath);
            return !string.IsNullOrWhiteSpace(parent) && Directory.Exists(parent) ? parent : null;
        }
        catch
        {
            return null;
        }
    }

    private void WriteRunLogToOutputFolder(BatchCompressionOptions options, string status, string summary)
    {
        try
        {
            Directory.CreateDirectory(options.OutputFolder);
            var logPath = GetAvailableLogPath(options.OutputFolder);
            var lines = new List<string>
            {
                $"{AppInfo.Name} 批量压缩日志",
                $"时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}",
                $"状态：{status}",
                $"输入：{options.InputPath}",
                $"输出目录：{options.OutputFolder}",
                $"格式：{FormatOptionName(options.Format)}",
                $"质量：{options.JpegQuality}",
                $"最大宽高：{options.MaxWidth} x {options.MaxHeight}",
                $"包含子目录：{FormatBoolean(options.IncludeSubfolders)}",
                $"覆盖已有压缩文件：{FormatBoolean(options.OverwriteExisting)}",
            };

            if (!string.IsNullOrWhiteSpace(summary))
            {
                lines.Add($"结果：{summary}");
            }

            lines.Add(string.Empty);
            lines.Add("处理记录：");
            lines.AddRange(_logLines);

            File.WriteAllLines(logPath, lines, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            _lastRunLogPath = logPath;
            AddLog($"日志已写入输出目录：{Path.GetFileName(logPath)}");
            UpdateOpenButtons();
        }
        catch (Exception ex)
        {
            AddLog($"写入输出目录日志失败：{FriendlyException(ex)}");
            ErrorLog.WriteException("WriteBatchCompressionLog", "写入批量压缩日志失败。", ex);
        }
    }

    private static string GetAvailableLogPath(string outputFolder)
    {
        var path = Path.Combine(outputFolder, $"{AppInfo.BatchCompressLogPrefix}{DateTime.Now:yyyyMMdd_HHmmss}.log");
        if (!File.Exists(path))
        {
            return path;
        }

        var baseName = Path.GetFileNameWithoutExtension(path);
        for (var index = 1; index < 10_000; index++)
        {
            var candidate = Path.Combine(outputFolder, $"{baseName}_{index}.log");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        return Path.Combine(outputFolder, $"{baseName}_{DateTime.Now:fff}.log");
    }

    private bool ConfirmOutputFolderInsideInput(BatchCompressionOptions options)
    {
        var sameFolder = PathsEqual(options.InputPath, options.OutputFolder);
        var message = sameFolder
            ? "输出目录和输入目录相同。\n\n程序会把压缩后的文件直接写在原目录里，并使用“_压缩”后缀；下次再批量压缩这个目录时，这些压缩文件也可能被扫描到。\n\n是否继续？"
            : "输出目录位于输入目录里面。\n\n程序会跳过输出目录里的文件，避免把已经压缩过的文件再次压缩。\n\n是否继续？";

        return MessageBox.Show(
            this,
            message,
            "输出目录提醒",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No) == MessageBoxResult.Yes;
    }

    private bool ConfirmPreflight(BatchCompressionOptions options, BatchCompressionPreflightResult preflight)
    {
        var message = FormatPreflightConfirmation(options, preflight);
        return MessageBox.Show(
            this,
            message,
            "确认批量压缩",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Information,
            MessageBoxResult.OK) == MessageBoxResult.OK;
    }

    private static string FormatPreflightConfirmation(BatchCompressionOptions options, BatchCompressionPreflightResult preflight)
    {
        var lines = new List<string>
        {
            "预扫描完成，确认开始压缩？",
            string.Empty,
            $"将压缩：{preflight.Compressible:N0} 张",
            $"跳过动图：{preflight.Animated:N0} 个",
            $"无法读取：{preflight.Failed:N0} 个",
            $"输出目录：{options.OutputFolder}",
        };

        if (preflight.AnimatedItems.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add("动图暂不支持批量压缩，会保留原文件不处理。");
        }

        if (preflight.FailedItems.Count > 0)
        {
            lines.Add("无法读取的文件会在日志里列出原因。");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string FormatPreflightSummary(BatchCompressionPreflightResult preflight)
    {
        return $"预扫描完成：共 {preflight.Total:N0} 个，将压缩 {preflight.Compressible:N0}，跳过动图 {preflight.Animated:N0}，无法读取 {preflight.Failed:N0}";
    }

    private void AddPreflightIssueLogs(BatchCompressionPreflightResult preflight)
    {
        if (preflight.AnimatedItems.Count > 0)
        {
            AddLog($"预扫描：将跳过动图 {preflight.AnimatedItems.Count:N0} 个。");
            foreach (var issue in preflight.AnimatedItems.Take(8))
            {
                AddLog($"预扫描跳过：{Path.GetFileName(issue.FilePath)}，{issue.Reason}");
            }
        }

        if (preflight.FailedItems.Count > 0)
        {
            AddLog($"预扫描：无法读取 {preflight.FailedItems.Count:N0} 个。");
            foreach (var issue in preflight.FailedItems.Take(8))
            {
                AddLog($"预扫描失败：{Path.GetFileName(issue.FilePath)}，{issue.Reason}");
            }
        }

        var issueCount = preflight.AnimatedItems.Count + preflight.FailedItems.Count;
        if (issueCount > 8)
        {
            AddLog("预扫描问题文件较多，窗口日志只补充显示前几项。");
        }
    }

    private static void OpenShellPath(string path)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true,
        });
    }

    private static bool PathsEqual(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        try
        {
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string? TrimToNull(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string FormatBoolean(bool value)
    {
        return value ? "是" : "否";
    }

    private static string FormatOptionName(ImageCompressionFormat format)
    {
        return format == ImageCompressionFormat.Png ? "PNG" : "JPEG";
    }

    private static string FormatResultSummary(BatchCompressionResult result)
    {
        var ratio = result.OriginalBytes > 0 && result.OutputBytes > 0
            ? $"，{FormatCompressionRatio(result.OriginalBytes, result.OutputBytes)}"
            : string.Empty;
        var skippedText = result.Skipped > 0
            ? $"，跳过动图 {result.Skipped:N0}"
            : string.Empty;
        var failedText = result.Failed > 0
            ? $"，失败 {result.Failed:N0}"
            : string.Empty;
        return $"共 {result.Total:N0} 个，成功 {result.Saved:N0}{skippedText}{failedText}{ratio}";
    }

    private void AddCompletionIssueLogs(BatchCompressionResult result)
    {
        if (!HasCompletionIssues(result))
        {
            return;
        }

        if (result.SkippedItems.Count > 0)
        {
            AddLog($"未压缩：跳过动图 {result.SkippedItems.Count:N0} 个。");
            foreach (var issue in result.SkippedItems.Take(8))
            {
                AddLog($"跳过：{Path.GetFileName(issue.FilePath)}，{issue.Reason}");
            }
        }

        if (result.FailedItems.Count > 0)
        {
            AddLog($"未压缩：压缩失败 {result.FailedItems.Count:N0} 个。");
            foreach (var issue in result.FailedItems.Take(8))
            {
                AddLog($"失败：{Path.GetFileName(issue.FilePath)}，{issue.Reason}");
            }
        }

        var issueCount = result.SkippedItems.Count + result.FailedItems.Count;
        if (issueCount > 8)
        {
            AddLog("未压缩文件较多，完整过程记录请在上方日志里查看。");
        }
    }

    private void ShowCompletionIssueDialog(BatchCompressionResult result)
    {
        var message = FormatCompletionIssueMessage(result);
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        MessageBox.Show(this, message, "部分文件未压缩", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private static string FormatCompletionIssueMessage(BatchCompressionResult result)
    {
        if (!HasCompletionIssues(result))
        {
            return string.Empty;
        }

        var lines = new List<string>
        {
            "批量压缩完成，但有文件未压缩。",
            string.Empty,
        };

        if (result.SkippedItems.Count > 0)
        {
            lines.Add($"跳过动图：{result.SkippedItems.Count:N0} 个");
            foreach (var issue in result.SkippedItems.Take(5))
            {
                lines.Add($"- {Path.GetFileName(issue.FilePath)}：{issue.Reason}");
            }

            lines.Add(string.Empty);
        }

        if (result.FailedItems.Count > 0)
        {
            lines.Add($"压缩失败：{result.FailedItems.Count:N0} 个");
            foreach (var issue in result.FailedItems.Take(5))
            {
                lines.Add($"- {Path.GetFileName(issue.FilePath)}：{issue.Reason}");
            }

            lines.Add(string.Empty);
        }

        var issueCount = result.SkippedItems.Count + result.FailedItems.Count;
        if (issueCount > 10)
        {
            lines.Add("这里只显示前几项，完整记录在窗口日志里。");
        }
        else
        {
            lines.Add("完整记录也保留在窗口日志里。");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static bool HasCompletionIssues(BatchCompressionResult result)
    {
        return result.SkippedItems.Count > 0 || result.FailedItems.Count > 0;
    }

    private static string FormatCompressionRatio(long originalBytes, long outputBytes)
    {
        var change = 1.0 - outputBytes / (double)originalBytes;
        return change >= 0
            ? $"减少 {change:P0}"
            : $"增大 {Math.Abs(change):P0}";
    }

    private static string FriendlyException(Exception ex)
    {
        return string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message;
    }
}
