using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace D3DImageCube;

/// <summary>
/// Drives the <see cref="CubeRenderer"/> and bridges its Direct3D surface into WPF through a
/// <see cref="D3DImage"/>. Each WPF composition tick we lock the image, render a fresh frame on the
/// Direct3D device, point the image at the surface and mark it dirty.
/// </summary>
public partial class MainWindow : Window
{
    private readonly CubeRenderer _renderer = new();
    private readonly D3DImage _d3dImage = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private bool _initialized;

    private string? _capturePath;
    private int _frameCount;

    public MainWindow()
    {
        InitializeComponent();

        _capturePath = ScreenCapture.PathFrom(Environment.GetCommandLineArgs());
        Surface.Source = _d3dImage;

        Loaded += OnLoaded;
        SizeChanged += (_, _) => _renderer.Resize(PixelWidth, PixelHeight);
        Closed += (_, _) => { CompositionTarget.Rendering -= OnRendering; _renderer.Dispose(); };
    }

    // Render at the Image's size in physical device pixels, so the cube stays crisp under DPI scaling.
    // Before the first arrange the Image has no size yet, so fall back to the window's own dimensions.
    private int PixelWidth => Scale(Surface.ActualWidth > 0 ? Surface.ActualWidth : ActualWidth, VisualTreeHelper.GetDpi(this).DpiScaleX, 640);
    private int PixelHeight => Scale(Surface.ActualHeight > 0 ? Surface.ActualHeight : ActualHeight, VisualTreeHelper.GetDpi(this).DpiScaleY, 480);

    private static int Scale(double dips, double dpiScale, int fallback) =>
        dips > 0 ? Math.Max(1, (int)Math.Ceiling(dips * dpiScale)) : fallback;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_initialized)
            return;

        UpdateLayout(); // ensure the Image has been arranged so we size the surface correctly
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        _renderer.Initialize(hwnd, PixelWidth, PixelHeight);
        _initialized = true;
        CompositionTarget.Rendering += OnRendering;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        // Always draw the Direct3D frame; the surface is what D3DImage shares.
        _renderer.Render((float)_clock.Elapsed.TotalSeconds);

        // Bridge into WPF only when the front buffer exists (i.e. the window is really composited).
        if (_d3dImage.IsFrontBufferAvailable)
        {
            _d3dImage.Lock();
            _d3dImage.SetBackBuffer(D3DResourceType.IDirect3DSurface9, _renderer.SurfacePointer, enableSoftwareFallback: true);
            _d3dImage.AddDirtyRect(new Int32Rect(0, 0, _d3dImage.PixelWidth, _d3dImage.PixelHeight));
            _d3dImage.Unlock();
        }

        if (_capturePath is null)
            return;

        if (++_frameCount < 2)
            return;

        _renderer.Render(0.8f); // a pleasant three-quarter angle for the screenshot
        _renderer.CaptureToPng(_capturePath);
        _capturePath = null;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Close));
    }
}
