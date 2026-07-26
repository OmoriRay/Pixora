using System.Windows;
using Pixora.Services;

namespace Pixora;

public partial class BatchDeletePreviewWindow : Window
{
    public BatchDeletePreviewWindow(string folder, string summary)
    {
        InitializeComponent();
        ThemeManager.ApplyTo(this);
        SummaryText.Text = summary;
        FolderText.Text = $"目录：{folder}";
    }

    public bool IncludeSubfolders => IncludeSubfoldersCheckBox.IsChecked == true;

    private void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
