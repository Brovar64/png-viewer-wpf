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
using System.Windows.Forms;
using System.Collections.Generic;
using System.Linq;

namespace PngViewer
{
    public partial class TransparentImageWindow : Window, IDisposable
    {
        private string _imagePath;
        private BitmapSource _originalImage;
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
        
        // Fullscreen capture window
        private Window _captureWindow = null;
        
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
        private const int HWND_BOTTOM = 1;
        
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
            
            // Create the fullscreen capture window
            CreateCaptureWindow();
            
            // Start visibility timer
            _visibilityTimer.Start();
        }
        
        private void CreateCaptureWindow()
        {
            // Find the screen our window is on
            var currentScreen = Screen.FromHandle(_windowHandle);
            
            // Create an invisible window that covers the entire screen's working area
            _captureWindow = new Window
            {
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)), // Nearly transparent
                Topmost = true,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                Title = "PNG Capture Area",
                Width = currentScreen.WorkingArea.Width,
                Height = currentScreen.WorkingArea.Height,
                Left = currentScreen.WorkingArea.Left,
                Top = currentScreen.WorkingArea.Top
            };
            
            // Make it not focusable so it doesn't steal focus
            _captureWindow.Focusable = false;
            
            // Wire up mouse wheel event to be forwarded to our window
            _captureWindow.MouseWheel += (s, e) =>
            {
                // Forward the mouse wheel event to our window
                Window_MouseWheel(s, e);
                e.Handled = true;
            };
            
            // Add an event handler to close the capture window when the main window closes
            _captureWindow.Closed += (s, e) => 
            {
                _captureWindow = null;
            };
            
            // Show the window
            _captureWindow.Show();
            
            // Further customize it with Win32 API calls
            var hwnd = new WindowInteropHelper(_captureWindow).Handle;
            
            // Make it a tool window that doesn't appear in Alt+Tab
            var exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT | WS_EX_LAYERED);
            
            // Set it to be slightly more transparent
            SetLayeredWindowAttributes(hwnd, 0, 1, LWA_ALPHA);
            
            // Position it behind our PNG window but still above most other windows
            SetWindowPos(hwnd, (IntPtr)HWND_BOTTOM, 0, 0, 0, 0, 
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
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
                
                // Make sure it's visible and on top after resizing
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
                
                // When we drag to a new position, we need to update the capture window
                UpdateCaptureWindowPosition();
            }
        }
        
        private void UpdateCaptureWindowPosition()
        {
            if (_captureWindow != null)
            {
                // Find the screen our window is now on
                var currentScreen = Screen.FromHandle(_windowHandle);
                
                // Update the capture window to match the current screen
                _captureWindow.Width = currentScreen.WorkingArea.Width;
                _captureWindow.Height = currentScreen.WorkingArea.Height;
                _captureWindow.Left = currentScreen.WorkingArea.Left;
                _captureWindow.Top = currentScreen.WorkingArea.Top;
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
        
        private void Window_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            // Calculate new zoom factor based on wheel direction
            double zoomChange = e.Delta > 0 ? ZOOM_FACTOR_STEP : -ZOOM_FACTOR_STEP;
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
                
                e.Handled = true;
            }
        }
        
        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            // Close window on Escape or Space
            if (e.Key == Key.Escape || e.Key == Key.Space)
            {
                Close();
            }
            // Reset zoom on 'R' key
            else if (e.Key == Key.R)
            {
                ResetZoom();
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
            
            // Close the capture window
            CloseCaptureWindow();
            
            base.OnClosing(e);
            Dispose();
        }
        
        private void CloseCaptureWindow()
        {
            if (_captureWindow != null)
            {
                _captureWindow.Close();
                _captureWindow = null;
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
                
                // Close the capture window
                CloseCaptureWindow();
                
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