using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using System.ComponentModel;
using System.Threading.Tasks;

namespace PngViewer
{
    public partial class ImageViewerWindow : Window, IDisposable
    {
        private string _imagePath;
        private BitmapSource _originalImage;
        private BitmapSource _transformedImage;
        private double _zoomFactor = 1.0;
        private bool _isDragging = false;
        private Point _lastMousePosition;
        private double _rotation = 0;
        private bool _isCropping = false;
        private Point _cropStartPoint;
        private bool _disposed = false;
        private readonly BackgroundWorker _imageLoader = new BackgroundWorker();

        // Constants for zoom sensitivity
        private const double ZOOM_FACTOR_STEP = 0.1;
        private const double MIN_ZOOM = 0.1;
        private const double MAX_ZOOM = 10.0;

        public ImageViewerWindow(string imagePath)
        {
            InitializeComponent();
            
            _imagePath = imagePath;
            Title = $"PNG Viewer - {Path.GetFileName(imagePath)}";
            
            // Configure background loader
            _imageLoader.DoWork += ImageLoader_DoWork;
            _imageLoader.RunWorkerCompleted += ImageLoader_RunWorkerCompleted;
            _imageLoader.WorkerSupportsCancellation = true;
            
            // Start loading
            _imageLoader.RunWorkerAsync(imagePath);
        }
        
        private void ImageLoader_DoWork(object sender, DoWorkEventArgs e)
        {
            string filePath = e.Argument as string;
            try
            {
                // Load the image at a reduced size first for better performance
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(filePath);
                // For large images, decode to a lower resolution first
                if (new FileInfo(filePath).Length > 5 * 1024 * 1024) // 5MB
                {
                    bitmap.DecodePixelWidth = 2000; // Balance between quality and memory
                }
                bitmap.CacheOption = BitmapCacheOption.OnLoad; // Release file lock after loading
                bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                bitmap.EndInit();
                bitmap.Freeze(); // Make it immutable for better performance
                
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
                _transformedImage = _originalImage;
                
                // Set the image
                ApplyTransformations();
                UpdateImageInfo();
                
                // Center content in scroll viewer
                scrollViewer.ScrollToHorizontalOffset((scrollViewer.ExtentWidth - scrollViewer.ViewportWidth) / 2);
                scrollViewer.ScrollToVerticalOffset((scrollViewer.ExtentHeight - scrollViewer.ViewportHeight) / 2);
            }
        }

        private void ApplyTransformations()
        {
            // We'll use a TransformedBitmap here if we need to rotate
            if (_rotation != 0)
            {
                var transform = new RotateTransform(_rotation);
                var rotatedBitmap = new TransformedBitmap(_originalImage, transform);
                if (_transformedImage != _originalImage)
                {
                    // Dispose previous transformed image if it's not the original
                    ReleaseImage(ref _transformedImage);
                }
                _transformedImage = rotatedBitmap;
            }
            else
            {
                if (_transformedImage != _originalImage)
                {
                    // Dispose previous transformed image if it's not the original
                    ReleaseImage(ref _transformedImage);
                }
                _transformedImage = _originalImage;
            }
            
            // Apply the image to the control
            mainImage.Source = _transformedImage;
            
            // Set appropriate width and height based on zoom
            mainImage.Width = _transformedImage.PixelWidth * _zoomFactor;
            mainImage.Height = _transformedImage.PixelHeight * _zoomFactor;
            
            // Update image canvas size
            imageCanvas.Width = mainImage.Width;
            imageCanvas.Height = mainImage.Height;
            
            // Update zoom level display
            txtZoomLevel.Text = $"Zoom: {_zoomFactor * 100:0}%";
            
            // Force garbage collection after major transformations
            Task.Run(() => 
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            });
        }
        
        private void UpdateImageInfo()
        {
            txtImageInfo.Text = $"PNG Image - {_originalImage.PixelWidth} x {_originalImage.PixelHeight} pixels";
        }

        private void btnRotateLeft_Click(object sender, RoutedEventArgs e)
        {
            _rotation = (_rotation - 90) % 360;
            ApplyTransformations();
        }

        private void btnRotateRight_Click(object sender, RoutedEventArgs e)
        {
            _rotation = (_rotation + 90) % 360;
            ApplyTransformations();
        }

        private void btnZoomIn_Click(object sender, RoutedEventArgs e)
        {
            ZoomImage(_zoomFactor + ZOOM_FACTOR_STEP);
        }

        private void btnZoomOut_Click(object sender, RoutedEventArgs e)
        {
            ZoomImage(_zoomFactor - ZOOM_FACTOR_STEP);
        }

        private void btnResetZoom_Click(object sender, RoutedEventArgs e)
        {
            ZoomImage(1.0);
        }

        private void ZoomImage(double newZoomFactor, Point? pivotPoint = null)
        {
            // Enforce min/max zoom limits
            newZoomFactor = Math.Max(MIN_ZOOM, Math.Min(MAX_ZOOM, newZoomFactor));
            
            if (Math.Abs(_zoomFactor - newZoomFactor) < 0.001)
                return;
            
            double relativeX, relativeY;
            
            if (pivotPoint.HasValue)
            {
                // Calculate relative position based on cursor position
                relativeX = (scrollViewer.HorizontalOffset + pivotPoint.Value.X) / (_transformedImage.PixelWidth * _zoomFactor);
                relativeY = (scrollViewer.VerticalOffset + pivotPoint.Value.Y) / (_transformedImage.PixelHeight * _zoomFactor);
            }
            else
            {
                // Fall back to center-based zooming
                var centerX = scrollViewer.HorizontalOffset + (scrollViewer.ViewportWidth / 2);
                var centerY = scrollViewer.VerticalOffset + (scrollViewer.ViewportHeight / 2);
                
                relativeX = centerX / (_transformedImage.PixelWidth * _zoomFactor);
                relativeY = centerY / (_transformedImage.PixelHeight * _zoomFactor);
            }
            
            // Update zoom factor
            _zoomFactor = newZoomFactor;
            
            // Apply new transformations
            ApplyTransformations();
            
            // Adjust scroll position to keep the pivot point
            if (pivotPoint.HasValue)
            {
                scrollViewer.ScrollToHorizontalOffset((relativeX * _transformedImage.PixelWidth * _zoomFactor) - pivotPoint.Value.X);
                scrollViewer.ScrollToVerticalOffset((relativeY * _transformedImage.PixelHeight * _zoomFactor) - pivotPoint.Value.Y);
            }
            else
            {
                scrollViewer.ScrollToHorizontalOffset((relativeX * _transformedImage.PixelWidth * _zoomFactor) - (scrollViewer.ViewportWidth / 2));
                scrollViewer.ScrollToVerticalOffset((relativeY * _transformedImage.PixelHeight * _zoomFactor) - (scrollViewer.ViewportHeight / 2));
            }
        }

        private void Window_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (_transformedImage == null)
                return;
                
            // Get the cursor position relative to the ScrollViewer
            Point cursorPosition = e.GetPosition(scrollViewer);
            
            // Determine zoom direction based on wheel delta
            double zoomChange = e.Delta > 0 ? ZOOM_FACTOR_STEP : -ZOOM_FACTOR_STEP;
            
            // Zoom with cursor as pivot point
            ZoomImage(_zoomFactor + zoomChange, cursorPosition);
            
            // Mark the event as handled to prevent the ScrollViewer from scrolling
            e.Handled = true;
        }

        // Old event handler - kept for reference but not used anymore
        private void ScrollViewer_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            // We're now using Window_PreviewMouseWheel instead
            e.Handled = true;
        }

        private void ScrollViewer_MouseMove(object sender, MouseEventArgs e)
        {
            Point currentPosition = e.GetPosition(scrollViewer);
            
            // Update coordinates display
            Point imagePos = e.GetPosition(mainImage);
            if (imagePos.X >= 0 && imagePos.X < mainImage.Width && 
                imagePos.Y >= 0 && imagePos.Y < mainImage.Height)
            {
                int pixelX = (int)(imagePos.X / _zoomFactor);
                int pixelY = (int)(imagePos.Y / _zoomFactor);
                txtCoordinates.Text = $"X: {pixelX}, Y: {pixelY}";
            }
            else
            {
                txtCoordinates.Text = "";
            }
            
            if (_isDragging)
            {
                // Handle panning
                double deltaX = currentPosition.X - _lastMousePosition.X;
                double deltaY = currentPosition.Y - _lastMousePosition.Y;
                
                scrollViewer.ScrollToHorizontalOffset(scrollViewer.HorizontalOffset - deltaX);
                scrollViewer.ScrollToVerticalOffset(scrollViewer.VerticalOffset - deltaY);
                
                _lastMousePosition = currentPosition;
            }
            else if (_isCropping)
            {
                // Update crop selection rectangle
                Point currentPoint = e.GetPosition(imageCanvas);
                
                double left = Math.Min(_cropStartPoint.X, currentPoint.X);
                double top = Math.Min(_cropStartPoint.Y, currentPoint.Y);
                double width = Math.Abs(currentPoint.X - _cropStartPoint.X);
                double height = Math.Abs(currentPoint.Y - _cropStartPoint.Y);
                
                Canvas.SetLeft(cropBorder, left);
                Canvas.SetTop(cropBorder, top);
                cropBorder.Width = width;
                cropBorder.Height = height;
            }
        }

        private void ScrollViewer_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (Keyboard.Modifiers == ModifierKeys.Shift && cropBorder.Visibility != Visibility.Visible)
            {
                // Start cropping
                _isCropping = true;
                _cropStartPoint = e.GetPosition(imageCanvas);
                
                // Show and initialize crop border
                cropBorder.Visibility = Visibility.Visible;
                Canvas.SetLeft(cropBorder, _cropStartPoint.X);
                Canvas.SetTop(cropBorder, _cropStartPoint.Y);
                cropBorder.Width = 0;
                cropBorder.Height = 0;
                
                e.Handled = true;
            }
            else if (!_isCropping)
            {
                // Start dragging for panning
                _isDragging = true;
                _lastMousePosition = e.GetPosition(scrollViewer);
                scrollViewer.Cursor = Cursors.Hand;
                
                e.Handled = true;
            }
        }

        private void ScrollViewer_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_isDragging)
            {
                _isDragging = false;
                scrollViewer.Cursor = Cursors.Arrow;
            }
            
            if (_isCropping)
            {
                _isCropping = false;
                // Keep the crop border visible for the potential crop operation
            }
        }

        private void btnCrop_Click(object sender, RoutedEventArgs e)
        {
            if (cropBorder.Visibility != Visibility.Visible || 
                cropBorder.Width < 10 || cropBorder.Height < 10)
            {
                MessageBox.Show("Please select an area to crop first\n\nHint: Hold Shift and drag to select an area", 
                                "Crop", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            
            try
            {
                // Get the crop rectangle in the original image coordinates
                double left = Canvas.GetLeft(cropBorder) / _zoomFactor;
                double top = Canvas.GetTop(cropBorder) / _zoomFactor;
                double width = cropBorder.Width / _zoomFactor;
                double height = cropBorder.Height / _zoomFactor;
                
                // Ensure coordinates are within image bounds
                left = Math.Max(0, Math.Min(left, _transformedImage.PixelWidth - 1));
                top = Math.Max(0, Math.Min(top, _transformedImage.PixelHeight - 1));
                width = Math.Min(width, _transformedImage.PixelWidth - left);
                height = Math.Min(height, _transformedImage.PixelHeight - top);
                
                // Create a cropped bitmap
                var cropRect = new Int32Rect(
                    (int)left, 
                    (int)top, 
                    (int)width, 
                    (int)height);
                    
                var croppedBitmap = new CroppedBitmap(_transformedImage, cropRect);
                
                // Update the image
                ReleaseImage(ref _originalImage);
                _originalImage = croppedBitmap;
                ReleaseImage(ref _transformedImage);
                _transformedImage = _originalImage;
                _rotation = 0; // Reset rotation after crop
                
                // Apply the new image
                ApplyTransformations();
                UpdateImageInfo();
                
                // Hide crop border
                cropBorder.Visibility = Visibility.Collapsed;
                
                // Force garbage collection
                GC.Collect();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error cropping image: {ex.Message}", "Error", 
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void btnSave_Click(object sender, RoutedEventArgs e)
        {
            var saveDialog = new SaveFileDialog
            {
                Filter = "PNG Files (*.png)|*.png",
                FileName = Path.GetFileName(_imagePath),
                InitialDirectory = Path.GetDirectoryName(_imagePath)
            };
            
            if (saveDialog.ShowDialog() == true)
            {
                try
                {
                    // Create a PNG encoder
                    var encoder = new PngBitmapEncoder();
                    
                    // Add the current image frame
                    encoder.Frames.Add(BitmapFrame.Create(_transformedImage));
                    
                    // Save to file
                    using (var fileStream = new FileStream(saveDialog.FileName, FileMode.Create))
                    {
                        encoder.Save(fileStream);
                    }
                    
                    MessageBox.Show($"Image saved successfully to:\n{saveDialog.FileName}", 
                        "Save Complete", MessageBoxButton.OK, MessageBoxImage.Information);
                    
                    // Update path if we're saving a new file
                    if (saveDialog.FileName != _imagePath)
                    {
                        _imagePath = saveDialog.FileName;
                        Title = $"PNG Viewer - {Path.GetFileName(_imagePath)}";
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error saving image: {ex.Message}", "Error", 
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            // Escape key to cancel cropping
            if (e.Key == Key.Escape && cropBorder.Visibility == Visibility.Visible)
            {
                cropBorder.Visibility = Visibility.Collapsed;
                _isCropping = false;
                e.Handled = true;
            }
        }
        
        private void ImageViewerWindow_Closing(object sender, CancelEventArgs e)
        {
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
                
                if (_transformedImage != _originalImage)
                {
                    ReleaseImage(ref _transformedImage);
                }
                
                // Clear event handlers
                Closing -= ImageViewerWindow_Closing;
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