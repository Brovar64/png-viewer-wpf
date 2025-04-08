using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using System.Windows.Forms;
using System.ComponentModel;

namespace PngViewer
{
    public partial class MainWindow : Window, IDisposable
    {
        private ObservableCollection<PngFile> _pngFiles = new ObservableCollection<PngFile>();
        private string _currentDirectory;
        private readonly BackgroundWorker _workerLoadImages = new BackgroundWorker();
        private readonly LRUCache<string, BitmapImage> _thumbnailCache = new LRUCache<string, BitmapImage>(200); // Cache 200 thumbnails max
        private CancellationTokenSource _cts;
        private readonly DispatcherTimer _memoryMonitorTimer;
        private bool _disposed = false;
        private int _lastLoadedIndex = 0;
        private bool _isLoadingThumbnails = false;
        
        public MainWindow()
        {
            InitializeComponent();
            
            // Configure background worker for loading images
            _workerLoadImages.WorkerReportsProgress = true;
            _workerLoadImages.WorkerSupportsCancellation = true;
            _workerLoadImages.DoWork += LoadImagesWork;
            _workerLoadImages.ProgressChanged += LoadImagesProgressChanged;
            _workerLoadImages.RunWorkerCompleted += LoadImagesCompleted;
            
            // Setup memory monitor
            _memoryMonitorTimer = new DispatcherTimer();
            _memoryMonitorTimer.Interval = TimeSpan.FromSeconds(2);
            _memoryMonitorTimer.Tick += MemoryMonitor_Tick;
            _memoryMonitorTimer.Start();
            
            ImageGrid.ItemsSource = _pngFiles;
            
            // Initialize _cts to avoid null reference
            _cts = new CancellationTokenSource();
        }

        private void MemoryMonitor_Tick(object sender, EventArgs e)
        {
            // Update memory usage display
            Process currentProcess = Process.GetCurrentProcess();
            long memoryUsageMB = currentProcess.WorkingSet64 / (1024 * 1024);
            long privateMemoryMB = currentProcess.PrivateMemorySize64 / (1024 * 1024);
            
            txtMemoryUsage.Text = $"Memory: {memoryUsageMB} MB (Private: {privateMemoryMB} MB)";
            
            // If memory gets too high, clear some caches
            if (memoryUsageMB > 500) // 500 MB threshold
            {
                // Reduce cache size
                _thumbnailCache.Trim(100);
                GC.Collect();
            }
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
            _thumbnailCache.Clear();
            
            // Cancel any ongoing operations
            if (_cts != null)
            {
                _cts.Cancel();
                _cts.Dispose();
            }
            _cts = new CancellationTokenSource();
            
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
                if (!Directory.Exists(directoryPath))
                {
                    e.Result = new List<PngFile>();
                    return;
                }
                
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
                    
                    pngFiles.Add(pngFile);
                    
                    // Report progress
                    count++;
                    worker.ReportProgress((int)((double)count / total * 100), count);
                    
                    // Process in batches to prevent UI freezing
                    if (count % 20 == 0)
                    {
                        Thread.Sleep(10); // Brief pause to allow UI to update
                    }
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
                // Add files to the observable collection in batches
                _pngFiles.Clear();
                foreach (var file in pngFiles)
                {
                    _pngFiles.Add(file);
                }
                
                txtFileCount.Text = $"{_pngFiles.Count} PNG files found";
                
                // Load first batch of thumbnails
                LoadVisibleThumbnails();
            }
        }
        
        private void ScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            LoadVisibleThumbnails();
        }
        
        private void LoadVisibleThumbnails()
        {
            // Only load thumbnails for visible items
            if (_pngFiles.Count == 0 || _isLoadingThumbnails)
                return;
                
            // Calculate visible range based on current scroll position
            var scrollViewer = FindVisualChild<ScrollViewer>(ImageGrid);
            if (scrollViewer == null)
                return;
            
            _isLoadingThumbnails = true;
            
            try
            {
                // Get visible items and load thumbnails
                var loadThumbnailsTask = Task.Run(() => 
                {
                    if (_cts == null || _cts.Token.IsCancellationRequested)
                        return;
                        
                    int startIndex = Math.Max(0, _lastLoadedIndex - 10);
                    int endIndex = Math.Min(_pngFiles.Count - 1, _lastLoadedIndex + 30);
                    
                    for (int i = startIndex; i <= endIndex; i++)
                    {
                        if (_cts == null || _cts.Token.IsCancellationRequested)
                            return;
                            
                        if (i < 0 || i >= _pngFiles.Count)
                            continue;
                            
                        var file = _pngFiles[i];
                        
                        Dispatcher.Invoke(() =>
                        {
                            try
                            {
                                if (file.Thumbnail == null)
                                {
                                    file.Thumbnail = CreateThumbnail(file.FilePath);
                                    _lastLoadedIndex = i;
                                }
                            }
                            catch (Exception ex)
                            {
                                Debug.WriteLine($"Error loading thumbnail: {ex.Message}");
                            }
                        });
                        
                        // Brief pause to prevent UI freeze
                        Thread.Sleep(10);
                    }
                }, _cts.Token);
                
                loadThumbnailsTask.ContinueWith(_ => 
                {
                    Dispatcher.Invoke(() => _isLoadingThumbnails = false);
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error in LoadVisibleThumbnails: {ex.Message}");
                _isLoadingThumbnails = false;
            }
        }
        
        private BitmapImage CreateThumbnail(string filePath)
        {
            // Check if thumbnail is already in cache
            if (_thumbnailCache.TryGet(filePath, out BitmapImage cachedThumbnail))
            {
                return cachedThumbnail;
            }
            
            try
            {
                if (!File.Exists(filePath))
                    return null;
                
                // Create a small, memory-efficient thumbnail
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                
                // Use file stream instead of UriSource for more reliable loading
                using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read))
                {
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                    bitmap.DecodePixelWidth = 150; // Reduce size significantly
                    bitmap.StreamSource = stream;
                    bitmap.EndInit();
                    
                    if (bitmap.CanFreeze)
                    {
                        bitmap.Freeze(); // Make immutable for thread safety and better performance
                    }
                }
                
                // Add to cache
                _thumbnailCache.Add(filePath, bitmap);
                
                return bitmap;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error creating thumbnail for {filePath}: {ex.Message}");
                // Return placeholder for failed thumbnails
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
                try 
                {
                    if (File.Exists(pngFile.FilePath))
                    {
                        // Open the image in a new window
                        var imageViewer = new ImageViewerWindow(pngFile.FilePath);
                        imageViewer.Owner = this;
                        imageViewer.Show();
                    }
                    else
                    {
                        System.Windows.MessageBox.Show($"File not found: {pngFile.FilePath}", "Error", 
                            MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
                catch (Exception ex)
                {
                    System.Windows.MessageBox.Show($"Error opening image: {ex.Message}", "Error", 
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }
        
        private void Window_Closing(object sender, CancelEventArgs e)
        {
            // Clean up resources
            Dispose();
        }
        
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
        
        protected virtual void Dispose(bool disposing)
        {
            if (_disposed)
                return;
                
            if (disposing)
            {
                // Dispose managed resources
                _memoryMonitorTimer.Stop();
                
                if (_cts != null)
                {
                    _cts.Cancel();
                    _cts.Dispose();
                    _cts = null;
                }
                
                // Clear caches
                _thumbnailCache.Clear();
                
                // Clear collections
                _pngFiles.Clear();
            }
            
            _disposed = true;
        }
        
        // Helper method to find a visual child of a specific type
        private static T FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            if (parent == null)
                return null;
                
            int childCount = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
                
            for (int i = 0; i < childCount; i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
                
                if (child is T result)
                    return result;
                    
                var descendant = FindVisualChild<T>(child);
                if (descendant != null)
                    return descendant;
            }
            
            return null;
        }
    }

    public class PngFile : INotifyPropertyChanged
    {
        private BitmapImage _thumbnail;
        
        public string FilePath { get; set; }
        public string FileName { get; set; }
        public string FileSize { get; set; }
        
        public BitmapImage Thumbnail
        {
            get => _thumbnail;
            set
            {
                if (_thumbnail != value)
                {
                    _thumbnail = value;
                    OnPropertyChanged(nameof(Thumbnail));
                }
            }
        }
        
        public event PropertyChangedEventHandler PropertyChanged;
        
        protected void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
    
    public class LRUCache<TKey, TValue> where TValue : class
    {
        private readonly Dictionary<TKey, LinkedListNode<CacheItem>> _cacheMap = new Dictionary<TKey, LinkedListNode<CacheItem>>();
        private readonly LinkedList<CacheItem> _lruList = new LinkedList<CacheItem>();
        private int _capacity;
        
        public LRUCache(int capacity)
        {
            _capacity = capacity;
        }
        
        public bool TryGet(TKey key, out TValue value)
        {
            value = default;
            
            if (!_cacheMap.TryGetValue(key, out LinkedListNode<CacheItem> node))
                return false;
                
            // Move to front of LRU list
            _lruList.Remove(node);
            _lruList.AddFirst(node);
            
            value = node.Value.Value;
            return true;
        }
        
        public void Add(TKey key, TValue value)
        {
            if (_cacheMap.TryGetValue(key, out LinkedListNode<CacheItem> existingNode))
            {
                // Update existing item
                _lruList.Remove(existingNode);
                existingNode.Value.Value = value;
                _lruList.AddFirst(existingNode);
                return;
            }
            
            // Check capacity before adding
            if (_cacheMap.Count >= _capacity)
            {
                // Remove least recently used item
                RemoveOldest();
            }
            
            // Add new item
            var newItem = new CacheItem(key, value);
            var newNode = _lruList.AddFirst(newItem);
            _cacheMap[key] = newNode;
        }
        
        public void Clear()
        {
            _cacheMap.Clear();
            _lruList.Clear();
        }
        
        public void Trim(int newSize)
        {
            if (newSize >= _cacheMap.Count)
                return;
                
            int toRemove = _cacheMap.Count - newSize;
            for (int i = 0; i < toRemove; i++)
            {
                RemoveOldest();
            }
        }
        
        private void RemoveOldest()
        {
            if (_lruList.Count == 0)
                return;
                
            var oldest = _lruList.Last;
            _lruList.RemoveLast();
            _cacheMap.Remove(oldest.Value.Key);
            
            // Dispose if disposable
            if (oldest.Value.Value is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
        
        private class CacheItem
        {
            public TKey Key { get; }
            public TValue Value { get; set; }
            
            public CacheItem(TKey key, TValue value)
            {
                Key = key;
                Value = value;
            }
        }
    }
}