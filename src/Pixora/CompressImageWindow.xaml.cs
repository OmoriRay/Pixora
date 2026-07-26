using Microsoft.Win32;
using Pixora.Services;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Pixora;

public partial class CompressImageWindow : Window
{
    private readonly string _sourcePath;
    private readonly long _fileSize;
    private readonly BitmapSource _source;
    private readonly DispatcherTimer _estimateTimer;
    private CancellationTokenSource? _estimateCts;
    private int _estimateGeneration;
    private bool _updatingOutputPath;
    private bool _updatingSizeText;

    public CompressImageWindow(string sourcePath, long fileSize, BitmapSource source)
    {
        InitializeComponent();
        ThemeManager.ApplyTo(this);
        _sourcePath = sourcePath;
        _fileSize = fileSize;
        _source = source;
        _estimateTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(320),
        };
        _estimateTimer.Tick += EstimateTimer_Tick;
        Closed += (_, _) =>
        {
            _estimateTimer.Stop();
            CancelEstimate();
        };

        SourceText.Text = Path.GetFileName(sourcePath);
        DetailText.Text = $"{source.PixelWidth} x {source.PixelHeight}  原始大小：{FormatFileSize(fileSize)}";
        WidthText.Text = source.PixelWidth.ToString();
        HeightText.Text = source.PixelHeight.ToString();
        OutputPathText.Text = string.Empty;
        UpdateQualityState();
        RefreshEstimate();
    }

    public ImageCompressionOptions? Options { get; private set; }

    private ImageCompressionFormat SelectedFormat => PngRadio.IsChecked == true
        ? ImageCompressionFormat.Png
        : ImageCompressionFormat.Jpeg;

    private int JpegQuality => Math.Clamp((int)Math.Round(QualitySlider.Value), 1, 100);

    private void FormatRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        UpdateQualityState();
        UpdateOutputPathExtension();
        QueueEstimate();
    }

    private void ResizeCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        QueueEstimate();
    }

    private void SizeTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (!IsLoaded || _updatingSizeText || ResizeCheckBox.IsChecked != true)
        {
            return;
        }

        UpdateLinkedSize(sender);
        QueueEstimate();
    }

    private void QualitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded)
        {
            return;
        }

        UpdateQualityText();
        QueueEstimate();
    }

    private void OutputPathText_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (!IsLoaded || _updatingOutputPath)
        {
            return;
        }

        UpdateEstimateHeader();
    }

    private void EstimateTimer_Tick(object? sender, EventArgs e)
    {
        _estimateTimer.Stop();
        RefreshEstimate();
    }

    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        TrySelectOutputPath(out _);
    }

    private bool TrySelectOutputPath(out string outputPath)
    {
        outputPath = string.Empty;
        var format = SelectedFormat;
        var currentOutputPath = OutputPathText.Text.Trim();
        var suggestedPath = string.IsNullOrWhiteSpace(currentOutputPath)
            ? ImageCompressor.GetDefaultOutputPath(_sourcePath, format)
            : ImageCompressor.EnsureExtension(currentOutputPath, format);

        var dialog = new SaveFileDialog
        {
            Title = "保存压缩图片",
            Filter = format == ImageCompressionFormat.Jpeg
                ? "JPEG 图片|*.jpg;*.jpeg"
                : "PNG 图片|*.png",
            FileName = Path.GetFileName(suggestedPath),
            DefaultExt = ImageCompressor.GetExtension(format),
            AddExtension = true,
            OverwritePrompt = true,
        };

        var folder = Path.GetDirectoryName(suggestedPath);
        if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
        {
            dialog.InitialDirectory = folder;
        }
        else
        {
            var picturesFolder = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            if (!string.IsNullOrWhiteSpace(picturesFolder) && Directory.Exists(picturesFolder))
            {
                dialog.InitialDirectory = picturesFolder;
            }
        }

        if (dialog.ShowDialog(this) == true)
        {
            outputPath = ImageCompressor.EnsureExtension(dialog.FileName, format);
            OutputPathText.Text = outputPath;
            return true;
        }

        return false;
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        var rawOutputPath = OutputPathText.Text.Trim();
        string outputPath;
        if (string.IsNullOrWhiteSpace(rawOutputPath))
        {
            if (!TrySelectOutputPath(out outputPath))
            {
                return;
            }
        }
        else
        {
            outputPath = ImageCompressor.EnsureExtension(rawOutputPath, SelectedFormat);
        }

        if (string.IsNullOrWhiteSpace(outputPath))
        {
            MessageBox.Show(this, "请选择输出文件。", AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var folder = Path.GetDirectoryName(outputPath);
        if (string.IsNullOrWhiteSpace(folder))
        {
            MessageBox.Show(this, "输出路径无效。", AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"无法创建输出文件夹：\n{ex.Message}", AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (File.Exists(outputPath))
        {
            var confirm = MessageBox.Show(
                this,
                $"文件已存在，是否覆盖？\n\n{outputPath}",
                "压缩图片",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);

            if (confirm != MessageBoxResult.Yes)
            {
                return;
            }
        }

        if (!TryCreateOptions(outputPath, out var options, out var error))
        {
            MessageBox.Show(this, error, AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Options = options;
        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void UpdateQualityState()
    {
        var isJpeg = SelectedFormat == ImageCompressionFormat.Jpeg;
        QualitySlider.IsEnabled = isJpeg;
        QualityText.Foreground = isJpeg
            ? Brushes.White
            : new SolidColorBrush(Color.FromRgb(120, 130, 144));
        FormatHelpText.Text = isJpeg
            ? "适合照片和壁纸，质量越低文件越小。透明区域会以白色底合成。"
            : "无损压缩，适合截图、图标和透明图片；体积可能不一定比原图小。";
        FormatBadge.Text = isJpeg ? "JPEG" : "PNG";
        UpdateQualityText();
    }

    private void UpdateQualityText()
    {
        QualityText.Text = SelectedFormat == ImageCompressionFormat.Jpeg
            ? JpegQuality.ToString()
            : "-";
    }

    private void RefreshEstimate()
    {
        UpdateEstimateHeader();
        var generation = ++_estimateGeneration;
        CancelEstimate();

        if (!TryCreateOptions(
            GetEstimateOutputPath(),
            out var options,
            out var error))
        {
            ShowEstimateUnavailable($"无法计算预估：{error}");
            return;
        }

        EstimateStatusText.Text = "正在计算预计大小...";
        var cts = new CancellationTokenSource();
        _estimateCts = cts;
        _ = RefreshEstimateAsync(options, generation, cts);
    }

    private async Task RefreshEstimateAsync(
        ImageCompressionOptions options,
        int generation,
        CancellationTokenSource cts)
    {
        try
        {
            var token = cts.Token;
            var estimate = await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                var result = ImageCompressor.Estimate(_source, options);
                token.ThrowIfCancellationRequested();
                return result;
            }, token);

            if (token.IsCancellationRequested || generation != _estimateGeneration)
            {
                return;
            }

            await Dispatcher.InvokeAsync(() =>
            {
                if (token.IsCancellationRequested || generation != _estimateGeneration)
                {
                    return;
                }

                EstimatedSizeText.Text = FormatFileSize(estimate.EstimatedBytes);
                CompressionRatioText.Text = FormatCompressionRatio(_fileSize, estimate.EstimatedBytes);
                EstimateStatusText.Text = "预计值使用当前编码参数计算，实际保存大小通常只会有很小差异。";
            }, DispatcherPriority.Background);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (generation != _estimateGeneration || cts.IsCancellationRequested)
            {
                return;
            }

            await Dispatcher.InvokeAsync(
                () => ShowEstimateUnavailable($"无法计算预估：{FriendlyException(ex)}"),
                DispatcherPriority.Background);
        }
        finally
        {
            if (ReferenceEquals(_estimateCts, cts))
            {
                _estimateCts = null;
            }

            cts.Dispose();
        }
    }

    private void QueueEstimate()
    {
        UpdateEstimateHeader();
        EstimateStatusText.Text = "停止调整后计算预计大小...";
        _estimateTimer.Stop();
        _estimateTimer.Start();
    }

    private void UpdateEstimateHeader()
    {
        OutputResolutionText.Text = TryGetOutputSize(out var width, out var height, out _)
            ? $"{width} x {height}"
            : "-";
        OutputFormatText.Text = SelectedFormat == ImageCompressionFormat.Jpeg ? "JPEG" : "PNG";
    }

    private void ShowEstimateUnavailable(string message)
    {
        EstimatedSizeText.Text = "-";
        CompressionRatioText.Text = string.Empty;
        EstimateStatusText.Text = message;
    }

    private void CancelEstimate()
    {
        _estimateCts?.Cancel();
    }

    private void UpdateLinkedSize(object sender)
    {
        if (KeepAspectCheckBox.IsChecked != true)
        {
            return;
        }

        try
        {
            _updatingSizeText = true;
            if (ReferenceEquals(sender, WidthText)
                && int.TryParse(WidthText.Text, out var width)
                && width > 0)
            {
                HeightText.Text = Math.Max(1, (int)Math.Round(width * _source.PixelHeight / (double)_source.PixelWidth)).ToString();
            }
            else if (ReferenceEquals(sender, HeightText)
                && int.TryParse(HeightText.Text, out var height)
                && height > 0)
            {
                WidthText.Text = Math.Max(1, (int)Math.Round(height * _source.PixelWidth / (double)_source.PixelHeight)).ToString();
            }
        }
        finally
        {
            _updatingSizeText = false;
        }
    }

    private void ResetSizeButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _updatingSizeText = true;
            WidthText.Text = _source.PixelWidth.ToString();
            HeightText.Text = _source.PixelHeight.ToString();
        }
        finally
        {
            _updatingSizeText = false;
        }

        QueueEstimate();
    }

    private bool TryCreateOptions(string outputPath, out ImageCompressionOptions options, out string error)
    {
        options = new ImageCompressionOptions(SelectedFormat, JpegQuality, outputPath);
        error = string.Empty;

        if (ResizeCheckBox.IsChecked != true)
        {
            return true;
        }

        if (!TryGetOutputSize(out var width, out var height, out error))
        {
            return false;
        }

        options = new ImageCompressionOptions(SelectedFormat, JpegQuality, outputPath, width, height);
        return true;
    }

    private bool TryGetOutputSize(out int width, out int height, out string error)
    {
        width = _source.PixelWidth;
        height = _source.PixelHeight;
        error = string.Empty;

        if (ResizeCheckBox.IsChecked != true)
        {
            return true;
        }

        if (!int.TryParse(WidthText.Text, out width) || !int.TryParse(HeightText.Text, out height))
        {
            error = "请输入有效的输出宽度和高度。";
            return false;
        }

        if (width <= 0 || height <= 0)
        {
            error = "输出宽度和高度必须大于 0。";
            return false;
        }

        return true;
    }

    private void UpdateOutputPathExtension()
    {
        if (_updatingOutputPath)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(OutputPathText.Text))
        {
            return;
        }

        try
        {
            _updatingOutputPath = true;
            OutputPathText.Text = ImageCompressor.EnsureExtension(OutputPathText.Text, SelectedFormat);
        }
        finally
        {
            _updatingOutputPath = false;
        }
    }

    private string GetEstimateOutputPath()
    {
        var outputPath = OutputPathText.Text.Trim();
        return string.IsNullOrWhiteSpace(outputPath)
            ? ImageCompressor.GetDefaultOutputPath(_sourcePath, SelectedFormat)
            : ImageCompressor.EnsureExtension(outputPath, SelectedFormat);
    }

    private static string FormatCompressionRatio(long originalBytes, long outputBytes)
    {
        if (originalBytes <= 0 || outputBytes <= 0)
        {
            return string.Empty;
        }

        var change = 1.0 - outputBytes / (double)originalBytes;
        if (change >= 0)
        {
            return $"预计减少 {change:P0}，原始大小 {FormatFileSize(originalBytes)}";
        }

        return $"预计增大 {Math.Abs(change):P0}，原始大小 {FormatFileSize(originalBytes)}";
    }

    private static string FormatFileSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        var size = (double)bytes;
        var unit = 0;

        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return $"{size:0.#} {units[unit]}";
    }

    private static string FriendlyException(Exception ex)
    {
        return string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message;
    }
}
