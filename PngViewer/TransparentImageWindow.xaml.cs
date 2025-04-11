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
        private BitmapImage _originalImage;
        private bool _disposed = false;
        private readonly BackgroundWorker _imageLoader = new BackgroundWorker();
        private readonly DispatcherTimer _visibilityTimer;
        private readonly DispatcherTimer _instructionsTimer;
        
        // Dragging related fields
        private bool _isDragging = false;
        private Point _dragStartPoint;
        
        // Zoom related fields
        private double _scale = 1.0;
        private const double SCALE_STEP = 0.2;
        private const double MIN_SCALE = 0.1;
        private const double MAX_SCALE = 10.0;
        
        // Win32 constants for window styles
        private const int GWL_STYLE = -16;
        private const int GWL_EXSTYLE = -20;
        private const int WS_SYSMENU = 0x80000;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_CAPTION = 0x00C00000;
        private const int WS_BORDER = 0x00800000;
        
        // Win32 constants for window positioning
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_SHOWWINDOW = 0x0040;
        private const int HWND_TOPMOST = -1;
        
        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        
        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
        
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
        
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(IntPtr hWnd);
        
        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        
        private const int SW_SHOW = 5;
        
        public bool IsDisposed => _disposed;
        private IntPtr _windowHandle;
        
        public TransparentImageWindow(string imagePath)
        {
            InitializeComponent();
            
            _imagePath = imagePath;
            
            // Configure background loader
            _imageLoader.DoWork += ImageLoader_DoWork;
            _imageLoader.RunWorkerCompleted += ImageLoader_RunWorkerCompleted;
            _imageLoader.WorkerSupportsCancellation = true;
            
            // Set up visibility timer to periodically check and ensure window is visible
            _visibilityTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(500)
            };
            _visibilityTimer.Tick += VisibilityTimer_Tick;
            
            // Setup instructions timer to hide the instructions after a few seconds
            _instructionsTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(5)
            };
            _instructionsTimer.Tick += (s, e) =>
            {
                instructionsText.Visibility = Visibility.Collapsed;
                _instructionsTimer.Stop();
            };
            
            // Apply additional settings after window is loaded
            this.Loaded += TransparentImageWindow_Loaded;
            this.Activated += TransparentImageWindow_Activated;
            this.Deactivated += TransparentImageWindow_Deactivated;
            
            // Register mouse events for custom dragging
            this.MouseMove += Window_MouseMove;
            this.MouseLeftButtonUp += Window_MouseLeftButtonUp;
            
            // Make sure it stays on top
            this.Topmost = true;
            
            // Start loading
            _imageLoader.RunWorkerAsync(imagePath);
        }

        private void TransparentImageWindow_Loaded(object sender, RoutedEventArgs e)
        {
            _windowHandle = new WindowInteropHelper(this).Handle;
            
            // Remove system menu, caption and borders
            var style = GetWindowLong(_windowHandle, GWL_STYLE);
            SetWindowLong(_windowHandle, GWL_STYLE, style & ~(WS_SYSMENU | WS_CAPTION | WS_BORDER));
            
            // Make it a tool window so it doesn't show in taskbar or Alt+Tab
            var exStyle = GetWindowLong(_windowHandle, GWL_EXSTYLE);
            SetWindowLong(_windowHandle, GWL_EXSTYLE, exStyle | WS_EX_TOOLWINDOW);
            
            // Ensure window is visible and topmost
            SetWindowPos(_windowHandle, (IntPtr)HWND_TOPMOST, 0, 0, 0, 0, 
                SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
            
            // Start visibility timer
            _visibilityTimer.Start();
            
            // Start instructions timer
            _instructionsTimer.Start();
        }
        
        private void TransparentImageWindow_Activated(object sender, EventArgs e)
        {
            // When window is activated, ensure it remains topmost
            this.Topmost = true;
        }
        
        private void TransparentImageWindow_Deactivated(object sender, EventArgs e)
        {
            // Prevent window from losing topmost status when deactivated
            this.Topmost = true;
        }
        
        private void VisibilityTimer_Tick(object sender, EventArgs e)
        {
            // Safety mechanism to ensure window stays visible
            if (_windowHandle != IntPtr.Zero && !_disposed)
            {
                if (!IsWindowVisible(_windowHandle))
                {
                    // Window became invisible, make it visible again
                    ShowWindow(_windowHandle, SW_SHOW);
                    
                    // Ensure it's topmost
                    SetWindowPos(_windowHandle, (IntPtr)HWND_TOPMOST, 0, 0, 0, 0, 
                        SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
                    
                    // Update topmost property
                    this.Topmost = true;
                }
            }
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
            
            if (e.Result is BitmapImage bitmap)
            {
                _originalImage = bitmap;
                
                // Set the image source
                mainImage.Source = _originalImage;
                
                // Set initial window size to match original image
                Width = _originalImage.PixelWidth;
                Height = _originalImage.PixelHeight;
                
                // Set canvas size to match
                mainCanvas.Width = Width;
                mainCanvas.Height = Height;
                
                // Position instructions at bottom
                Canvas.SetLeft(instructionsText, (Width - instructionsText.ActualWidth) / 2);
                Canvas.SetBottom(instructionsText, 10);
                
                // Center on screen
                CenterWindowOnScreen();
                
                // Make sure it's visible and on top
                if (_windowHandle != IntPtr.Zero)
                {
                    SetWindowPos(_windowHandle, (IntPtr)HWND_TOPMOST, 0, 0, 0, 0, 
                        SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
                }
            }
        }
        
        private void ZoomIn()
        {
            if (_originalImage == null) return;
            
            // Increase scale
            _scale += SCALE_STEP;
            if (_scale > MAX_SCALE) _scale = MAX_SCALE;
            
            // Apply the new scale
            ApplyScale();
            
            // Show brief feedback
            ShowScaleFeedback();
        }
        
        private void ZoomOut()
        {
            if (_originalImage == null) return;
            
            // Decrease scale
            _scale -= SCALE_STEP;
            if (_scale < MIN_SCALE) _scale = MIN_SCALE;
            
            // Apply the new scale
            ApplyScale();
            
            // Show brief feedback
            ShowScaleFeedback();
        }
        
        private void ShowScaleFeedback()
        {
            // Update the instructions text to show current scale
            instructionsText.Text = $"Scale: {_scale:F1}x";
            instructionsText.Visibility = Visibility.Visible;
            
            // Position it at the bottom center
            Canvas.SetLeft(instructionsText, (Width - instructionsText.ActualWidth) / 2);
            Canvas.SetBottom(instructionsText, 10);
            
            // Restart the timer to hide the feedback after a delay
            _instructionsTimer.Stop();
            _instructionsTimer.Start();
        }
        
        private void ApplyScale()
        {
            try
            {
                // Calculate the new size based on scale
                double newWidth = _originalImage.PixelWidth * _scale;
                double newHeight = _originalImage.PixelHeight * _scale;
                
                // Get the center of the window before scaling
                double centerX = Left + (Width / 2);
                double centerY = Top + (Height / 2);
                
                // Update the window size and image size
                Width = newWidth;
                Height = newHeight;
                
                // Update canvas size
                mainCanvas.Width = newWidth;
                mainCanvas.Height = newHeight;
                
                // Keep the same source image - we'll let WPF do the scaling
                mainImage.Width = newWidth;
                mainImage.Height = newHeight;
                
                // Recenter window
                Left = centerX - (Width / 2);
                Top = centerY - (Height / 2);
                
                // Update the instructions position
                Canvas.SetLeft(instructionsText, (Width - instructionsText.ActualWidth) / 2);
                Canvas.SetBottom(instructionsText, 10);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error applying scale: {ex.Message}", "Error", 
                    MessageBoxButton.OK, MessageBoxImage.Error);
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
            // Start the custom dragging operation
            _isDragging = true;
            _dragStartPoint = e.GetPosition(this);
            this.CaptureMouse();
            
            // Ensure it remains topmost
            this.Topmost = true;
            
            e.Handled = true;
        }
        
        private void Window_MouseMove(object sender, MouseEventArgs e)
        {
            if (_isDragging)
            {
                // Get current mouse position relative to window
                Point currentPosition = e.GetPosition(this);
                
                // Calculate the offset from where drag started
                Vector offset = currentPosition - _dragStartPoint;
                
                // Calculate new window position
                double newLeft = Left + offset.X;
                double newTop = Top + offset.Y;
                
                // Apply the new position
                Left = newLeft;
                Top = newTop;
                
                // Ensure the window stays topmost
                this.Topmost = true;
            }
        }
        
        private void Window_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_isDragging)
            {
                _isDragging = false;
                this.ReleaseMouseCapture();
            }
        }
        
        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            // Handle keyboard shortcuts
            if (e.Key == Key.OemPlus || e.Key == Key.Add)
            {
                // Plus key - zoom in
                ZoomIn();
                e.Handled = true;
            }
            else if (e.Key == Key.OemMinus || e.Key == Key.Subtract)
            {
                // Minus key - zoom out
                ZoomOut();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape || e.Key == Key.Space)
            {
                // Close window on Escape or Space
                Close();
                e.Handled = true;
            }
        }
        
        protected override void OnClosing(CancelEventArgs e)
        {
            // Stop the timers
            _visibilityTimer.Stop();
            _instructionsTimer.Stop();
            
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
                // Stop the timers
                _visibilityTimer.Stop();
                _instructionsTimer.Stop();
                
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
                Activated -= TransparentImageWindow_Activated;
                Deactivated -= TransparentImageWindow_Deactivated;
                MouseMove -= Window_MouseMove;
                MouseLeftButtonUp -= Window_MouseLeftButtonUp;
            }
            
            _disposed = true;
        }
        
        private void ReleaseImage(ref BitmapImage image)
        {
            if (image != null)
            {
                // Clear references to help garbage collection
                image = null;
            }
        }
    }
}