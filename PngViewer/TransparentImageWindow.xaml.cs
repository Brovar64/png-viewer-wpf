using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using System.ComponentModel;
using System.Runtime.InteropServices;

// Use aliases to avoid ambiguity
using WinForms = System.Windows.Forms;
using WinInput = System.Windows.Input;
using WinInterop = System.Windows.Interop;
using WinMedia = System.Windows.Media;
using WinImaging = System.Windows.Media.Imaging;

namespace PngViewer
{
    public partial class TransparentImageWindow : Window, IDisposable
    {
        private string _imagePath;
        private WinImaging.BitmapSource _originalImage;
        private bool _disposed = false;
        private readonly BackgroundWorker _imageLoader = new BackgroundWorker();
        private readonly DispatcherTimer _visibilityTimer;
        
        // Dragging related fields
        private bool _isDragging = false;
        private Point _dragStartPoint;
        
        // Zoom related constants and fields
        private const double ZOOM_FACTOR_STEP = 0.1;
        private const double MIN_ZOOM = 0.1;
        private const double MAX_ZOOM = 10.0;
        private double _currentZoom = 1.0;
        
        // Fixed fullscreen window
        private Window _fullscreenWindow = null;
        
        // Win32 constants for window styles
        private const int GWL_STYLE = -16;
        private const int GWL_EXSTYLE = -20;
        private const int WS_SYSMENU = 0x80000;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_CAPTION = 0x00C00000;
        private const int WS_BORDER = 0x00800000;
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const int WS_EX_LAYERED = 0x00080000;
        
        // Win32 constants for window positioning
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_SHOWWINDOW = 0x0040;
        private const int HWND_TOPMOST = -1;
        private const int HWND_NOTOPMOST = -2;
        
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
        
        // SetWindowLong index for opacity
        [DllImport("user32.dll")]
        private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);
        
        private const int SW_SHOW = 5;
        private const uint LWA_ALPHA = 0x00000002;
        
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
            
            // Apply additional settings after window is loaded
            this.Loaded += TransparentImageWindow_Loaded;
            this.Activated += TransparentImageWindow_Activated;
            this.Deactivated += TransparentImageWindow_Deactivated;
            
            // Register mouse events for custom dragging
            this.MouseMove += Window_MouseMove;
            this.MouseLeftButtonUp += Window_MouseLeftButtonUp;
            
            // Make sure it stays on top
            this.Topmost = true;
            
            // Start loading the image first
            _imageLoader.RunWorkerAsync(imagePath);
        }

        private void TransparentImageWindow_Loaded(object sender, RoutedEventArgs e)
        {
            _windowHandle = new WinInterop.WindowInteropHelper(this).Handle;
            
            // Remove system menu, caption and borders
            var style = GetWindowLong(_windowHandle, GWL_STYLE);
            SetWindowLong(_windowHandle, GWL_STYLE, style & ~(WS_SYSMENU | WS_CAPTION | WS_BORDER));
            
            // Make it a tool window so it doesn't show in taskbar or Alt+Tab
            var exStyle = GetWindowLong(_windowHandle, GWL_EXSTYLE);
            SetWindowLong(_windowHandle, GWL_EXSTYLE, exStyle | WS_EX_TOOLWINDOW);
            
            // Ensure window is visible and topmost
            SetWindowPos(_windowHandle, (IntPtr)HWND_TOPMOST, 0, 0, 0, 0, 
                SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
            
            // Create the fullscreen window after the main window is loaded
            CreateFullscreenWindow();
            
            // Start visibility timer
            _visibilityTimer.Start();
        }
        
        private void CreateFullscreenWindow()
        {
            // Create a truly fullscreen window that covers ALL screens
            double maxRight = 0;
            double maxBottom = 0;
            double minLeft = 0;
            double minTop = 0;
            
            // First pass: find the bounds of all screens
            foreach (WinForms.Screen screen in WinForms.Screen.AllScreens)
            {
                if (screen.Bounds.Left < minLeft)
                    minLeft = screen.Bounds.Left;
                    
                if (screen.Bounds.Top < minTop)
                    minTop = screen.Bounds.Top;
                    
                if (screen.Bounds.Right > maxRight)
                    maxRight = screen.Bounds.Right;
                    
                if (screen.Bounds.Bottom > maxBottom)
                    maxBottom = screen.Bounds.Bottom;
            }
            
            // Calculate total dimensions
            double totalWidth = maxRight - minLeft;
            double totalHeight = maxBottom - minTop;
            
            // Create the window that covers everything
            _fullscreenWindow = new Window
            {
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = new WinMedia.SolidColorBrush(WinMedia.Color.FromArgb(1, 255, 0, 0)), // Very slightly red for debug
                Topmost = true,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                Title = "Fullscreen Capture",
                Width = totalWidth,
                Height = totalHeight,
                Left = minLeft,
                Top = minTop
            };
            
            // Add a simple canvas as content
            var canvas = new System.Windows.Controls.Canvas();
            _fullscreenWindow.Content = canvas;
            
            // Disable focusing but enable hit testing
            _fullscreenWindow.Focusable = false;
            canvas.Focusable = false;
            
            // Wire up mouse wheel event directly to the canvas
            canvas.PreviewMouseWheel += (s, e) =>
            {
                ApplyZoom(e.Delta);
                e.Handled = true;
            };
            
            // Close the fullscreen window when this window closes
            this.Closed += (s, e) => CloseFullscreenWindow();
            
            // Show the window
            _fullscreenWindow.Show();
            
            // Apply window style changes to make it transparent to input except for wheel
            var hwnd = new WinInterop.WindowInteropHelper(_fullscreenWindow).Handle;
            var exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_TOOLWINDOW | WS_EX_LAYERED);
            
            // Set high transparency
            SetLayeredWindowAttributes(hwnd, 0, 10, LWA_ALPHA); // Slightly visible for debug
            
            // Make sure it's on top of everything
            SetWindowPos(hwnd, (IntPtr)HWND_TOPMOST, 0, 0, 0, 0, 
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                
            // Ensure our PNG window is below it
            SetWindowPos(_windowHandle, hwnd, 0, 0, 0, 0, 
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }
        
        private void ApplyZoom(int delta)
        {
            // Ensure we're on the UI thread
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => ApplyZoom(delta));
                return;
            }
            
            // Calculate new zoom factor based on wheel direction
            double zoomChange = delta > 0 ? ZOOM_FACTOR_STEP : -ZOOM_FACTOR_STEP;
            double newZoom = _currentZoom + zoomChange;
            
            // Enforce min/max zoom limits
            newZoom = Math.Max(MIN_ZOOM, Math.Min(MAX_ZOOM, newZoom));
            
            // Only update if the zoom actually changed
            if (Math.Abs(_currentZoom - newZoom) > 0.001)
            {
                _currentZoom = newZoom;
                
                // Apply the new zoom factor to the ScaleTransform
                imageScale.ScaleX = _currentZoom;
                imageScale.ScaleY = _currentZoom;
                
                System.Diagnostics.Debug.WriteLine($"Zoom changed to: {_currentZoom}");
            }
        }
        
        private void TransparentImageWindow_Activated(object sender, EventArgs e)
        {
            // Make sure our PNG window is above normal windows
            this.Topmost = true;
            
            // But ensure our fullscreen window is above the PNG
            if (_fullscreenWindow != null)
            {
                _fullscreenWindow.Topmost = true;
                var hwnd = new WinInterop.WindowInteropHelper(_fullscreenWindow).Handle;
                SetWindowPos(hwnd, (IntPtr)HWND_TOPMOST, 0, 0, 0, 0, 
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            }
        }
        
        private void TransparentImageWindow_Deactivated(object sender, EventArgs e)
        {
            // Same as in Activated - ensure proper Z-order
            this.Topmost = true;
            
            if (_fullscreenWindow != null)
            {
                _fullscreenWindow.Topmost = true;
                var hwnd = new WinInterop.WindowInteropHelper(_fullscreenWindow).Handle;
                SetWindowPos(hwnd, (IntPtr)HWND_TOPMOST, 0, 0, 0, 0, 
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            }
        }
        
        private void VisibilityTimer_Tick(object sender, EventArgs e)
        {
            // Safety mechanism to ensure windows stay visible in proper Z-order
            if (_windowHandle != IntPtr.Zero && !_disposed)
            {
                if (!IsWindowVisible(_windowHandle))
                {
                    // Window became invisible, make it visible again
                    ShowWindow(_windowHandle, SW_SHOW);
                    
                    // Update topmost property
                    this.Topmost = true;
                }
            }
            
            // Also check the fullscreen window
            if (_fullscreenWindow != null)
            {
                var hwnd = new WinInterop.WindowInteropHelper(_fullscreenWindow).Handle;
                if (!IsWindowVisible(hwnd))
                {
                    ShowWindow(hwnd, SW_SHOW);
                    _fullscreenWindow.Topmost = true;
                    SetWindowPos(hwnd, (IntPtr)HWND_TOPMOST, 0, 0, 0, 0, 
                        SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                }
            }
        }
        
        private void ImageLoader_DoWork(object sender, DoWorkEventArgs e)
        {
            string filePath = e.Argument as string;
            try
            {
                var bitmap = new WinImaging.BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(filePath);
                bitmap.CacheOption = WinImaging.BitmapCacheOption.OnLoad;
                bitmap.CreateOptions = WinImaging.BitmapCreateOptions.IgnoreColorProfile | WinImaging.BitmapCreateOptions.PreservePixelFormat;
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
            
            if (e.Result is WinImaging.BitmapSource bitmap)
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
                
                // Make sure it's visible
                if (_windowHandle != IntPtr.Zero)
                {
                    SetWindowPos(_windowHandle, (IntPtr)HWND_TOPMOST, 0, 0, 0, 0, 
                        SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
                }
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
        
        private void Window_MouseLeftButtonDown(object sender, WinInput.MouseButtonEventArgs e)
        {
            // Start the custom dragging operation
            _isDragging = true;
            _dragStartPoint = e.GetPosition(this);
            this.CaptureMouse();
            
            // Ensure it remains topmost among normal windows
            this.Topmost = true;
            
            e.Handled = true;
        }
        
        private void Window_MouseMove(object sender, WinInput.MouseEventArgs e)
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
                
                // Ensure the window stays topmost among regular windows
                this.Topmost = true;
            }
        }
        
        private void Window_MouseLeftButtonUp(object sender, WinInput.MouseButtonEventArgs e)
        {
            if (_isDragging)
            {
                _isDragging = false;
                this.ReleaseMouseCapture();
            }
        }
        
        private void Window_KeyDown(object sender, WinInput.KeyEventArgs e)
        {
            // Close window on Escape or Space
            if (e.Key == WinInput.Key.Escape || e.Key == WinInput.Key.Space)
            {
                Close();
            }
            // Reset zoom on 'R' key
            else if (e.Key == WinInput.Key.R)
            {
                ResetZoom();
            }
            // Debug key - add/remove 10% zoom
            else if (e.Key == WinInput.Key.Add || e.Key == WinInput.Key.OemPlus)
            {
                ApplyZoom(120); // Simulate scroll up
            }
            else if (e.Key == WinInput.Key.Subtract || e.Key == WinInput.Key.OemMinus)
            {
                ApplyZoom(-120); // Simulate scroll down
            }
        }
        
        private void ResetZoom()
        {
            _currentZoom = 1.0;
            imageScale.ScaleX = 1.0;
            imageScale.ScaleY = 1.0;
        }
        
        protected override void OnClosing(CancelEventArgs e)
        {
            // Stop the visibility timer
            _visibilityTimer.Stop();
            
            // Close the fullscreen window
            CloseFullscreenWindow();
            
            base.OnClosing(e);
            Dispose();
        }
        
        private void CloseFullscreenWindow()
        {
            if (_fullscreenWindow != null)
            {
                _fullscreenWindow.Close();
                _fullscreenWindow = null;
            }
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
                // Stop the visibility timer
                _visibilityTimer.Stop();
                
                // Close the fullscreen window
                CloseFullscreenWindow();
                
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
        
        private void ReleaseImage(ref WinImaging.BitmapSource image)
        {
            if (image != null)
            {
                // Clear references to help garbage collection
                image = null;
            }
        }
    }
}