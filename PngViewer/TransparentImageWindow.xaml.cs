using System;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Threading.Tasks;
using System.ComponentModel;

namespace PngViewer
{
    public partial class TransparentImageWindow : Window, IDisposable
    {
        private string _imagePath;
        private BitmapSource _originalImage;
        private bool _disposed = false;
        private readonly BackgroundWorker _imageLoader = new BackgroundWorker();
        
        public TransparentImageWindow(string imagePath)
        {
            InitializeComponent();
            
            _imagePath = imagePath;
            
            // Configure background loader
            _imageLoader.DoWork += ImageLoader_DoWork;
            _imageLoader.RunWorkerCompleted += ImageLoader_RunWorkerCompleted;
            _imageLoader.WorkerSupportsCancellation = true;
            
            // Start loading
            _imageLoader.RunWorkerAsync(imagePath);
            
            // Set the window size to fit the screen initially
            Width = SystemParameters.PrimaryScreenWidth * 0.8;
            Height = SystemParameters.PrimaryScreenHeight * 0.8;
        }
        
        private void ImageLoader_DoWork(object sender, DoWorkEventArgs e)
        {
            string filePath = e.Argument as string;
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(filePath);
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                bitmap.EndInit();
                bitmap.Freeze();
                
                e.Result = bitmap;
            }
            catch (Exception ex)
            {
                e.Result = ex;
            }
        }
        
        private void ImageLoader_RunWorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            if (e.Result is Exception ex)
            {
                MessageBox.Show($"Error loading image: {ex.Message}", "Error", 
                    MessageBoxButton.OK, MessageBoxImage.Error);
                Close();
                return;
            }
            
            if (e.Result is BitmapSource bitmap)
            {
                _originalImage = bitmap;
                mainImage.Source = _originalImage;
                
                // Resize window to match image dimensions, but keep within screen bounds
                ResizeWindowToImage();
                
                // Center on screen
                CenterWindowOnScreen();
            }
        }
        
        private void ResizeWindowToImage()
        {
            if (_originalImage == null) return;
            
            double screenWidth = SystemParameters.PrimaryScreenWidth;
            double screenHeight = SystemParameters.PrimaryScreenHeight;
            
            // Get image dimensions
            double imageWidth = _originalImage.PixelWidth;
            double imageHeight = _originalImage.PixelHeight;
            
            // Calculate scaling factor to fit within screen (with some margin)
            double maxWidth = screenWidth * 0.9;
            double maxHeight = screenHeight * 0.9;
            
            double widthScale = maxWidth / imageWidth;
            double heightScale = maxHeight / imageHeight;
            double scale = Math.Min(widthScale, heightScale);
            
            // If image is smaller than screen, don't scale up
            if (scale > 1)
            {
                // Use actual image size
                Width = imageWidth;
                Height = imageHeight;
                mainImage.Width = imageWidth;
                mainImage.Height = imageHeight;
            }
            else
            {
                // Scale down to fit screen
                Width = imageWidth * scale;
                Height = imageHeight * scale;
                mainImage.Width = imageWidth * scale;
                mainImage.Height = imageHeight * scale;
            }
        }
        
        private void CenterWindowOnScreen()
        {
            double screenWidth = SystemParameters.PrimaryScreenWidth;
            double screenHeight = SystemParameters.PrimaryScreenHeight;
            
            Left = (screenWidth - Width) / 2;
            Top = (screenHeight - Height) / 2;
        }
        
        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Allow dragging the window by clicking anywhere
            DragMove();
        }
        
        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            // Close window on Escape or Space
            if (e.Key == Key.Escape || e.Key == Key.Space)
            {
                Close();
            }
        }
        
        protected override void OnClosing(CancelEventArgs e)
        {
            base.OnClosing(e);
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
                // Cancel any ongoing operations
                if (_imageLoader.IsBusy)
                {
                    _imageLoader.CancelAsync();
                }
                
                // Dispose image resources
                ReleaseImage(ref _originalImage);
                
                // Clear event handlers
                _imageLoader.DoWork -= ImageLoader_DoWork;
                _imageLoader.RunWorkerCompleted -= ImageLoader_RunWorkerCompleted;
            }
            
            _disposed = true;
        }
        
        private void ReleaseImage(ref BitmapSource image)
        {
            if (image != null)
            {
                // Clear references to help garbage collection
                image = null;
            }
        }
    }
}