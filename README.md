# PNG Viewer WPF

A high-performance, memory-efficient PNG viewer application built with C# and WPF.

## Features

- Browse and view PNG files in a selected folder
- Efficient memory management and resource utilization
- Fast thumbnail generation with lazy loading
- High-quality image display with pixel-perfect rendering
- Powerful image manipulation capabilities:
  - Smooth zooming with mouse wheel (Ctrl+Wheel) or buttons
  - 90° rotation (left/right)
  - Precise cropping functionality (Shift+Drag to select area)
  - Save modified images
- Floating transparent PNG view with:
  - Direct mouse wheel zoom in/out
  - Fullscreen red bounding box that appears when zoomed
  - Multi-monitor support for fullscreen bounding box
  - Automatic screen detection when moving between monitors
  - Drag to reposition
  - Press 'R' key to reset zoom
  - Press 'B' key to toggle bounding box visibility
- Responsive interface that works well with large image collections
- Live memory usage monitoring
- Proper resource cleanup

## Memory Optimization Features

This application includes several optimizations to minimize memory usage:

1. **Virtualized UI**: Only visible thumbnails consume memory resources
2. **LRU Cache**: Least Recently Used cache system automatically frees memory  
3. **Downscaled Loading**: Large images are decoded at reduced resolution
4. **Lazy Loading**: Thumbnails are generated only when needed
5. **Proper Resource Disposal**: Implements IDisposable pattern 
6. **Explicit GC Control**: Strategic garbage collection to minimize memory pressure
7. **Background Processing**: All heavy operations run in background threads

## Requirements

- Windows operating system
- .NET 6.0 or higher

## Usage

1. Click "Select PNG Folder" to browse your directories
2. Click on any thumbnail to open the image viewer
3. Use the toolbar buttons to manipulate the image:
   - Rotate Left/Right: Rotate the image by 90 degrees
   - Zoom controls: Zoom in, zoom out, or reset zoom
   - Crop: Select an area (Shift+Drag) and click Crop
   - Save As: Save the modified image to a new file

### Keyboard and Mouse Controls

- **Ctrl + Mouse Wheel**: Zoom in/out in the standard viewer
- **Mouse Wheel**: Zoom in/out in the floating transparent viewer
- **Drag**: Pan the image when zoomed in
- **Shift + Drag**: Select area for cropping
- **Escape or Space**: Close the floating transparent viewer
- **R key**: Reset zoom in the floating transparent viewer
- **B key**: Toggle the fullscreen bounding box visibility

### Fullscreen Bounding Box

When using the floating transparent PNG viewer and zooming in/out, a red border automatically appears around the entire screen. This helps maintain context and orientation when examining image details.

The fullscreen border:
- Automatically appears when zooming (not displayed at 1.0x zoom)
- Automatically follows the PNG when moved between monitors
- Can be toggled on/off with the 'B' key
- Works with any monitor setup (single, dual, or multiple)

## License

MIT License