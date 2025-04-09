using System;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Threading.Tasks;
using System.ComponentModel;
using System.Windows.Interop;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Threading;

namespace PngViewer
{
    public partial class TransparentImageWindow : Window, IDisposable
    {
        private string _imagePath;
        private BitmapSource _originalImage;
        private bool _disposed = false;
        private readonly BackgroundWorker _imageLoader = new BackgroundWorker();
        
        // Win32 constants for window styles
        private const int GWL_STYLE = -16;
        private const int GWL_EXSTYLE = -20;
        private const int WS_SYSMENU = 0x80000;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_CAPTION = 0x00C00000;
        private const int WS_BORDER = 0x00800000;
        
        // For hit testing
        private const int WM_NCHITTEST = 0x0084;
        private const int HTCAPTION = 2;
        private const int HTCLIENT = 1;
        
        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        
        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
        
        public TransparentImageWindow(string imagePath)
        {
            InitializeComponent();
            
            _imagePath = imagePath;
            
            // Set window caption to display image name
            Title = $"PNG Viewer - {Path.GetFileName(imagePath)}";
            
            // Configure background loader
            _imageLoader.DoWork += ImageLoader_DoWork;
            _imageLoader.RunWorkerCompleted += ImageLoader_RunWorkerCompleted;
            _imageLoader.WorkerSupportsCancellation = true;
            
            // Apply additional settings after window is loaded
            this.Loaded += TransparentImageWindow_Loaded;
            
            // Start loading
            _imageLoader.RunWorkerAsync(imagePath);
        }

        private void TransparentImageWindow_Loaded(object sender, RoutedEventArgs e)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            
            // Remove system menu, caption and borders
            var style = GetWindowLong(hwnd, GWL_STYLE);
            SetWindowLong(hwnd, GWL_STYLE, style & ~(WS_SYSMENU | WS_CAPTION | WS_BORDER));
            
            // Make it a tool window so it doesn't show in taskbar or Alt+Tab
            var exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_TOOLWINDOW);
            
            // Add message hook for handling hit-testing
            HwndSource source = HwndSource.FromHwnd(hwnd);
            source.AddHook(new HwndSourceHook(WndProc));
            
            // Use a timer to allow the window to fully render before removing borders
            DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
            timer.Tick += (s, args) => 
            {
                timer.Stop();
                
                // Apply window style changes again to ensure they stick
                SetWindowLong(hwnd, GWL_STYLE, style & ~(WS_SYSMENU | WS_CAPTION | WS_BORDER));
            };
            timer.Start();
            
            // Make this window topmost to ensure it stays visible
            this.Topmost = true;
        }
        
        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            // Handle hit testing to make the whole window draggable
            if (msg == WM_NCHITTEST)
            {
                // Get the point coordinates for the hit test
                Point ptScreen = new Point(lParam.ToInt32() & 0xFFFF, lParam.ToInt32() >> 16);
                
                // Convert to client coordinates
                Point ptClient = PointFromScreen(ptScreen);
                
                // Hit test against visible parts of the window
                bool hitVisible = HitTestVisibleArea(ptClient);
                
                // If we hit a visible pixel, return HTCAPTION so the window can be dragged
                if (hitVisible)
                {
                    handled = true;
                    return new IntPtr(HTCAPTION);
                }
                
                // Otherwise, return HTCLIENT to allow it to be clickthrough
                handled = true;
                return new IntPtr(HTCLIENT);
            }
            
            return IntPtr.Zero;
        }
        
        private bool HitTestVisibleArea(Point pt)
        {
            // Check if the point is within the bounds of the main image
            if (pt.X < 0 || pt.Y < 0 || pt.X >= mainImage.ActualWidth || pt.Y >= mainImage.ActualHeight)
                return false;
            
            // For fully transparent pixels, we want to detect hits only on non-transparent pixels
            // This code would need to be adapted based on the specific requirements
            
            // If the image is loaded, check if the point is on a visible (non-transparent) pixel
            if (_originalImage != null)
            {
                // Convert point from window coordinates to image coordinates
                int pixelX = (int)(pt.X / _zoomFactor);
                int pixelY = (int)(pt.Y / _zoomFactor);
                
                // Ensure we're within bounds
                if (pixelX >= 0 && pixelX < _originalImage.PixelWidth &&
                    pixelY >= 0 && pixelY < _originalImage.PixelHeight)
                {
                    // For now, we'll assume any point within the image bounds is visible
                    // A more advanced implementation would check pixel transparency
                    return true;
                }
            }
            
            return false;
        }
        
        // Keep track of zoom factor for coordinate calculations
        private double _zoomFactor = 1.0;
        
        private void ImageLoader_DoWork(object sender, DoWorkEventArgs e)
        {
            string filePath = e.Argument as string;
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(filePath);
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile | BitmapCreateOptions.PreservePixelFormat;
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
                
                // Auto-size the window to fit the image exactly
                mainImage.Width = _originalImage.PixelWidth;
                mainImage.Height = _originalImage.PixelHeight;
                
                // Update window size to exactly match the image
                Width = _originalImage.PixelWidth;
                Height = _originalImage.PixelHeight;
                
                // Ensure window is centered on screen
                CenterWindowOnScreen();
                
                // Add a thin border to make the image more visible
                MainBorder.BorderThickness = new Thickness(1);
            }
        }
        
        private void CenterWindowOnScreen()
        {
            double screenWidth = SystemParameters.PrimaryScreenWidth;
            double screenHeight = SystemParameters.PrimaryScreenHeight;
            
            if (_originalImage != null)
            {
                Left = (screenWidth - _originalImage.PixelWidth) / 2;
                Top = (screenHeight - _originalImage.PixelHeight) / 2;
            }
        }
        
        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Allow dragging the window by clicking anywhere on the image
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
                Loaded -= TransparentImageWindow_Loaded;
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