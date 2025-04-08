using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using System.Windows.Forms;
using System.ComponentModel;

namespace PngViewer
{
    public partial class MainWindow : Window
    {
        private List<PngFile> _pngFiles = new List<PngFile>();
        private string _currentDirectory;
        private readonly BackgroundWorker _workerLoadImages = new BackgroundWorker();

        public MainWindow()
        {
            InitializeComponent();
            
            // Configure background worker for loading images
            _workerLoadImages.WorkerReportsProgress = true;
            _workerLoadImages.WorkerSupportsCancellation = true;
            _workerLoadImages.DoWork += LoadImagesWork;
            _workerLoadImages.ProgressChanged += LoadImagesProgressChanged;
            _workerLoadImages.RunWorkerCompleted += LoadImagesCompleted;
        }

        private void btnSelectFolder_Click(object sender, RoutedEventArgs e)
        {
            using (var dialog = new FolderBrowserDialog())
            {
                DialogResult result = dialog.ShowDialog();
                if (result == System.Windows.Forms.DialogResult.OK && !string.IsNullOrWhiteSpace(dialog.SelectedPath))
                {
                    _currentDirectory = dialog.SelectedPath;
                    txtCurrentPath.Text = _currentDirectory;
                    LoadPngFiles(_currentDirectory);
                }
            }
        }

        private void LoadPngFiles(string directoryPath)
        {
            // Clear previous files
            _pngFiles.Clear();
            ImageGrid.ItemsSource = null;
            
            // Cancel any ongoing loading operation
            if (_workerLoadImages.IsBusy)
                _workerLoadImages.CancelAsync();
            
            // Start loading in background
            _workerLoadImages.RunWorkerAsync(directoryPath);
        }

        private void LoadImagesWork(object sender, DoWorkEventArgs e)
        {
            var directoryPath = e.Argument as string;
            var worker = sender as BackgroundWorker;
            
            try
            {
                var files = Directory.GetFiles(directoryPath, "*.png", SearchOption.TopDirectoryOnly);
                int count = 0;
                int total = files.Length;
                
                // Report total count
                worker.ReportProgress(0, total);
                
                var pngFiles = new List<PngFile>();
                
                foreach (var file in files)
                {
                    if (worker.CancellationPending)
                    {
                        e.Cancel = true;
                        return;
                    }
                    
                    var fileInfo = new FileInfo(file);
                    var pngFile = new PngFile
                    {
                        FilePath = file,
                        FileName = fileInfo.Name,
                        FileSize = FormatFileSize(fileInfo.Length)
                    };
                    
                    // Create thumbnail
                    pngFile.ThumbnailPath = CreateThumbnail(file);
                    
                    pngFiles.Add(pngFile);
                    
                    // Report progress
                    count++;
                    worker.ReportProgress((int)((double)count / total * 100), count);
                }
                
                e.Result = pngFiles;
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Error loading PNG files: {ex.Message}", "Error", 
                    MessageBoxButton.OK, MessageBoxImage.Error);
                e.Result = new List<PngFile>();
            }
        }

        private void LoadImagesProgressChanged(object sender, ProgressChangedEventArgs e)
        {
            if (e.UserState is int total && e.ProgressPercentage == 0)
            {
                txtFileCount.Text = $"Loading {total} PNG files...";
            }
            else if (e.UserState is int count)
            {
                txtFileCount.Text = $"Loaded {count} PNG files...";
            }
        }

        private void LoadImagesCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            if (e.Cancelled)
            {
                txtFileCount.Text = "Loading cancelled";
                return;
            }
            
            if (e.Error != null)
            {
                System.Windows.MessageBox.Show($"Error: {e.Error.Message}", "Error", 
                    MessageBoxButton.OK, MessageBoxImage.Error);
                txtFileCount.Text = "Error loading PNG files";
                return;
            }
            
            if (e.Result is List<PngFile> pngFiles)
            {
                _pngFiles = pngFiles;
                ImageGrid.ItemsSource = _pngFiles;
                
                txtFileCount.Text = $"{_pngFiles.Count} PNG files found";
            }
        }

        private string CreateThumbnail(string filePath)
        {
            try
            {
                // For the skeleton implementation, we'll return the file path directly
                // In a full implementation, you might create and cache thumbnails
                return filePath;
            }
            catch
            {
                // Return a placeholder for failed thumbnails
                return null;
            }
        }
        
        private string FormatFileSize(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB", "TB" };
            double len = bytes;
            int order = 0;
            
            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len = len / 1024;
            }
            
            return $"{len:0.##} {sizes[order]}";
        }

        private void Image_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            var image = sender as System.Windows.Controls.Image;
            if (image != null && image.DataContext is PngFile pngFile)
            {
                // Open the image in a new window
                var imageViewer = new ImageViewerWindow(pngFile.FilePath);
                imageViewer.Owner = this;
                imageViewer.Show();
            }
        }
    }

    public class PngFile
    {
        public string FilePath { get; set; }
        public string FileName { get; set; }
        public string FileSize { get; set; }
        public string ThumbnailPath { get; set; }
    }
}