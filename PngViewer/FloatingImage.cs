using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Runtime.InteropServices;

namespace PngViewer
{
    public class FloatingImage : IDisposable
    {
        private Form _form;
        private System.Windows.Forms.PictureBox _pictureBox;
        private string _imagePath;
        private bool _disposed = false;

        public bool IsDisposed => _disposed;

        public FloatingImage(string imagePath)
        {
            _imagePath = imagePath;
            CreateFloatingImage();
        }

        private void CreateFloatingImage()
        {
            // Create a completely borderless Windows Forms form
            _form = new Form
            {
                FormBorderStyle = FormBorderStyle.None,
                ShowInTaskbar = false,
                TopMost = true,
                StartPosition = FormStartPosition.CenterScreen,
                BackColor = System.Drawing.Color.Fuchsia, // This will be made transparent
                TransparencyKey = System.Drawing.Color.Fuchsia,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink
            };

            // Create picture box that will display the image
            _pictureBox = new System.Windows.Forms.PictureBox
            {
                SizeMode = PictureBoxSizeMode.AutoSize,
                BackColor = System.Drawing.Color.Fuchsia,
                Dock = DockStyle.None,
                Margin = new System.Windows.Forms.Padding(0)
            };

            try
            {
                // Load the image directly as a Windows.Forms.Image
                using (var fileStream = new FileStream(_imagePath, FileMode.Open, FileAccess.Read))
                {
                    var image = System.Drawing.Image.FromStream(fileStream);
                    _pictureBox.Image = image;
                }

                // Set form size to match image
                _form.ClientSize = _pictureBox.Image.Size;
                
                // Add picture box to form
                _form.Controls.Add(_pictureBox);

                // Set up event handlers
                _pictureBox.MouseDown += PictureBox_MouseDown;
                _form.KeyDown += Form_KeyDown;
                _form.FormClosed += Form_FormClosed;

                // Show the form (pure image)
                _form.Show();
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Error creating floating image: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                Dispose();
            }
        }

        private void Form_FormClosed(object sender, FormClosedEventArgs e)
        {
            // Auto-dispose when the form is closed
            Dispose();
        }

        private void Form_KeyDown(object sender, System.Windows.Forms.KeyEventArgs e)
        {
            // Close on Escape or Space
            if (e.KeyCode == Keys.Escape || e.KeyCode == Keys.Space)
            {
                _form.Close();
            }
        }

        private bool _isDragging = false;
        private System.Drawing.Point _lastMousePosition;

        private void PictureBox_MouseDown(object sender, System.Windows.Forms.MouseEventArgs e)
        {
            if (e.Button == System.Windows.Forms.MouseButtons.Left)
            {
                _isDragging = true;
                _lastMousePosition = e.Location;
                
                // Track mouse movement even when mouse is outside the form
                _pictureBox.Capture = true;
                _pictureBox.MouseMove += PictureBox_MouseMove;
                _pictureBox.MouseUp += PictureBox_MouseUp;
            }
        }

        private void PictureBox_MouseMove(object sender, System.Windows.Forms.MouseEventArgs e)
        {
            if (_isDragging)
            {
                // Calculate how far the mouse has moved
                int deltaX = e.X - _lastMousePosition.X;
                int deltaY = e.Y - _lastMousePosition.Y;
                
                // Move the form by that amount
                _form.Location = new System.Drawing.Point(
                    _form.Location.X + deltaX,
                    _form.Location.Y + deltaY);
            }
        }

        private void PictureBox_MouseUp(object sender, System.Windows.Forms.MouseEventArgs e)
        {
            if (e.Button == System.Windows.Forms.MouseButtons.Left)
            {
                _isDragging = false;
                _pictureBox.Capture = false;
                _pictureBox.MouseMove -= PictureBox_MouseMove;
                _pictureBox.MouseUp -= PictureBox_MouseUp;
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
                if (_pictureBox != null)
                {
                    _pictureBox.MouseDown -= PictureBox_MouseDown;
                    _pictureBox.MouseMove -= PictureBox_MouseMove;
                    _pictureBox.MouseUp -= PictureBox_MouseUp;
                    
                    if (_pictureBox.Image != null)
                    {
                        _pictureBox.Image.Dispose();
                        _pictureBox.Image = null;
                    }
                    
                    _pictureBox.Dispose();
                    _pictureBox = null;
                }

                if (_form != null)
                {
                    _form.KeyDown -= Form_KeyDown;
                    _form.FormClosed -= Form_FormClosed;
                    
                    if (!_form.IsDisposed)
                    {
                        _form.Close();
                        _form.Dispose();
                    }
                    
                    _form = null;
                }
            }

            _disposed = true;
        }
    }
}