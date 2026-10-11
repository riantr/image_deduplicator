using Avalonia.Controls;
using Avalonia.Interactivity;
using Deduplicator.Models;
using System.IO;

namespace Deduplicator.Views;

public partial class ImagePreviewWindow : Window
{
    /// <summary>
    /// 设计器/资源加载所需的无参构造函数。
    /// Avalonia 的 XAML 运行时加载器要求存在公开的无参构造函数才能实例化该类型。
    /// </summary>
    public ImagePreviewWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// 使用指定图像数据打开预览窗口
    /// </summary>
    public ImagePreviewWindow(ImageData? imageData) : this()
    {
        if (imageData == null) return;

        // 显示文件信息
        FilePathText.Text = $"文件路径：{imageData.FilePath}";

        // 显示文件大小
        try
        {
            var fileInfo = new FileInfo(imageData.FilePath);
            FileSizeText.Text = $"文件大小：{FormatFileSize(fileInfo.Length)}";
        }
        catch
        {
            FileSizeText.Text = "文件大小：未知";
        }

        // 显示图像
        if (imageData.BitmapImage != null)
        {
            PreviewImage.Source = imageData.BitmapImage;
        }
    }

    /// <summary>
    /// 格式化文件大小
    /// </summary>
    private string FormatFileSize(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB" };
        int order = 0;
        double size = bytes;
        
        while (size >= 1024 && order < sizes.Length - 1)
        {
            order++;
            size = size / 1024;
        }
        
        return $"{size:0.##} {sizes[order]}";
    }

    /// <summary>
    /// 关闭按钮点击
    /// </summary>
    private void CloseButton_Click(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
