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
        private readonly object _thumbnailLock = new object();
        
        // Store the current selected thumbnail for context menu
        private PngFile _currentContextPngFile;
        
        // Keep track of open transparent windows
        private List<TransparentImageWindow> _transparentWindows = new List<TransparentImageWindow>();
        
        // Placeholder for failed thumbnails
        private static BitmapImage _placeholderImage;
        
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
            
            // Create placeholder image
            _placeholderImage = CreatePlaceholderImage();
            
            ImageGrid.ItemsSource = _pngFiles;
            
            // Initialize _cts to avoid null reference
            _cts = new CancellationTokenSource();
        }

        private BitmapImage CreatePlaceholderImage()
        {
            // Create a simple gray placeholder
            var placeholder = new BitmapImage();
            int width = 150;
            int height = 150;
            
            var renderTargetBitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            var visual = new DrawingVisual();
            
            using (var context = visual.RenderOpen())
            {
                // Draw gray background
                context.DrawRectangle(Brushes.LightGray, null, new Rect(0, 0, width, height));
                
                // Draw "No Preview" text
                var textFormat = new FormattedText(
                    "No Preview",
                    System.Globalization.CultureInfo.CurrentCulture,
                    System.Windows.FlowDirection.LeftToRight,
                    new Typeface("Segoe UI"),
                    14,
                    Brushes.DarkGray,
                    VisualTreeHelper.GetDpi(this).PixelsPerDip);
                
                // Center the text
                context.DrawText(textFormat, new Point((width - textFormat.Width) / 2, (height - textFormat.Height) / 2));
            }
            
            renderTargetBitmap.Render(visual);
            
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(renderTargetBitmap));
            
            using (var stream = new MemoryStream())
            {
                encoder.Save(stream);
                stream.Position = 0;
                
                placeholder.BeginInit();
                placeholder.CacheOption = BitmapCacheOption.OnLoad;
                placeholder.StreamSource = stream;
                placeholder.EndInit();
                placeholder.Freeze();
            }
            
            return placeholder;
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
            
            // Reset loading state
            _isLoadingThumbnails = false;
            _lastLoadedIndex = 0;
            
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
                        FileSize = FormatFileSize(fileInfo.Length),
                        // Pre-assign placeholder
                        Thumbnail = _placeholderImage
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
                // Add files to the observable collection
                _pngFiles.Clear();
                foreach (var file in pngFiles)
                {
                    _pngFiles.Add(file);
                }
                
                txtFileCount.Text = $"{_pngFiles.Count} PNG files found";
                
                // Start loading real thumbnails immediately after files are displayed
                if (_pngFiles.Count > 0)
                {
                    // Wait a bit to let UI update before starting thumbnail load
                    var delayTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
                    delayTimer.Tick += (s, args) =>
                    {
                        delayTimer.Stop();
                        LoadVisibleThumbnails();
                    };
                    delayTimer.Start();
                }
            }
        }
        
        private void ScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (Math.Abs(e.VerticalChange) > 5 || Math.Abs(e.HorizontalChange) > 5)
            {
                LoadVisibleThumbnails();
            }
        }
        
        private void LoadVisibleThumbnails()
        {
            // Only load thumbnails for visible items
            if (_pngFiles.Count == 0)
                return;
                
            // Use lock to prevent multiple concurrent loading operations
            lock (_thumbnailLock)
            {
                if (_isLoadingThumbnails)
                    return;
                
                _isLoadingThumbnails = true;
            }
            
            Debug.WriteLine($"Loading thumbnails starting from index {_lastLoadedIndex}");
            
            try
            {
                // Get visible items and load thumbnails
                Task.Run(() => 
                {
                    try
                    {
                        if (_cts == null || _cts.Token.IsCancellationRequested)
                        {
                            FinishLoading();
                            return;
                        }
                        
                        int startIndex = Math.Max(0, _lastLoadedIndex - 5);
                        int endIndex = Math.Min(_pngFiles.Count - 1, startIndex + 20);
                        
                        for (int i = startIndex; i <= endIndex; i++)
                        {
                            if (_cts == null || _cts.Token.IsCancellationRequested)
                            {
                                FinishLoading();
                                return;
                            }
                            
                            if (i < 0 || i >= _pngFiles.Count)
                                continue;
                            
                            Dispatcher.Invoke(DispatcherPriority.Background, new Action(() =>
                            {
                                try
                                {
                                    var file = _pngFiles[i];
                                    
                                    // Only load if it's still a placeholder
                                    if (file.Thumbnail == _placeholderImage || file.Thumbnail == null)
                                    {
                                        Debug.WriteLine($"Loading thumbnail for {file.FileName}");
                                        var thumbnail = CreateThumbnail(file.FilePath);
                                        if (thumbnail != null)
                                        {
                                            file.Thumbnail = thumbnail;
                                            _lastLoadedIndex = i;
                                        }
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Debug.WriteLine($"Error loading thumbnail in Dispatcher: {ex.Message}");
                                }
                            }));
                            
                            // Brief pause to prevent UI freeze
                            Thread.Sleep(20);
                        }
                        
                        // If we processed the last batch, reset the index to start over
                        if (endIndex >= _pngFiles.Count - 1)
                        {
                            _lastLoadedIndex = 0;
                        }
                        
                        FinishLoading();
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Error in thumbnail loading task: {ex.Message}");
                        FinishLoading();
                    }
                }, _cts.Token);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error starting LoadVisibleThumbnails: {ex.Message}");
                FinishLoading();
            }
        }
        
        private void FinishLoading()
        {
            Dispatcher.Invoke(DispatcherPriority.Background, new Action(() =>
            {
                lock (_thumbnailLock)
                {
                    _isLoadingThumbnails = false;
                }
            }));
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
                {
                    Debug.WriteLine($"File not found: {filePath}");
                    return _placeholderImage;
                }
                
                // Create a small, memory-efficient thumbnail
                var bitmap = new BitmapImage();
                
                using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read))
                {
                    // Important: Read the image byte data
                    byte[] imageData = new byte[stream.Length];
                    stream.Read(imageData, 0, (int)stream.Length);
                    
                    // Create thumbnail from memory
                    using (var memoryStream = new MemoryStream(imageData))
                    {
                        bitmap.BeginInit();
                        bitmap.CacheOption = BitmapCacheOption.OnLoad;
                        bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                        bitmap.DecodePixelWidth = 150; // Reduce size significantly
                        bitmap.StreamSource = memoryStream;
                        bitmap.EndInit();
                        
                        if (bitmap.CanFreeze)
                        {
                            bitmap.Freeze(); // Make immutable for thread safety and better performance
                        }
                    }
                }
                
                // Add to cache
                _thumbnailCache.Add(filePath, bitmap);
                
                return bitmap;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error creating thumbnail for {filePath}: {ex.Message}");
                return _placeholderImage;
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
                        // Create a transparent image window instead of FloatingImage
                        var transparentWindow = new TransparentImageWindow(pngFile.FilePath);
                        _transparentWindows.Add(transparentWindow);
                        transparentWindow.Show();
                        
                        // Register for disposal when the window closes
                        transparentWindow.Closed += (s, args) =>
                        {
                            _transparentWindows.Remove(transparentWindow);
                            transparentWindow.Dispose();
                        };
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
        
        private void Image_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Store the selected PNG file for context menu
            var image = sender as System.Windows.Controls.Image;
            if (image != null && image.DataContext is PngFile pngFile)
            {
                _currentContextPngFile = pngFile;
            }
        }
        
        private void MenuItemOpenViewer_Click(object sender, RoutedEventArgs e)
        {
            if (_currentContextPngFile != null && File.Exists(_currentContextPngFile.FilePath))
            {
                try
                {
                    // Open in standard viewer
                    var imageViewer = new ImageViewerWindow(_currentContextPngFile.FilePath);
                    imageViewer.Owner = this;
                    imageViewer.Show();
                }
                catch (Exception ex)
                {
                    System.Windows.MessageBox.Show($"Error opening image: {ex.Message}", "Error", 
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }
        
        private void MenuItemOpenTransparent_Click(object sender, RoutedEventArgs e)
        {
            if (_currentContextPngFile != null && File.Exists(_currentContextPngFile.FilePath))
            {
                try
                {
                    // Create a transparent image window instead of FloatingImage
                    var transparentWindow = new TransparentImageWindow(_currentContextPngFile.FilePath);
                    _transparentWindows.Add(transparentWindow);
                    transparentWindow.Show();
                    
                    // Register for disposal when the window closes
                    transparentWindow.Closed += (s, args) =>
                    {
                        _transparentWindows.Remove(transparentWindow);
                        transparentWindow.Dispose();
                    };
                }
                catch (Exception ex)
                {
                    System.Windows.MessageBox.Show($"Error opening transparent image: {ex.Message}", "Error", 
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
                
                // Dispose any open transparent windows
                foreach (var window in _transparentWindows.ToList())
                {
                    window.Close();
                    window.Dispose();
                }
                _transparentWindows.Clear();
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