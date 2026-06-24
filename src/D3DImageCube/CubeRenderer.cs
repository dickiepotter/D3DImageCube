using System;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using Vortice.Direct3D9;
using Vortice.Mathematics;
using static Vortice.Direct3D9.D3D9;
using PixelFormats = System.Windows.Media.PixelFormats;

namespace D3DImageCube;

/// <summary>
/// A Direct3D 9Ex renderer that draws a spinning, lit cube into an off-screen render-target surface.
/// That surface is handed to WPF's <c>D3DImage</c>, so Direct3D output composites straight into the
/// WPF visual tree. Rendering uses the classic fixed-function pipeline — transforms, a directional
/// light and a per-face material — which is exactly what the original Managed DirectX demo reached for.
/// </summary>
public sealed class CubeRenderer : IDisposable
{
    // D3DTS_WORLD = 256. Vortice's TransformState enum only names View/Projection/Texture stages, and
    // its integer SetTransform overload treats the argument as a world-matrix *index* (it adds the 256
    // base itself), so the world matrix is set by casting 256 to TransformState explicitly.
    private const TransformState WorldTransform = (TransformState)256;

    // Position + outward normal. Colour is supplied per face through the material, the fixed-function way.
    [StructLayout(LayoutKind.Sequential)]
    private struct Vertex(Vector3 position, Vector3 normal)
    {
        public Vector3 Position = position;
        public Vector3 Normal = normal;
    }

    private const int VertexStride = 24; // 6 floats

    private IDirect3D9Ex _d3d = null!;
    private IDirect3DDevice9Ex _device = null!;
    private IDirect3DSurface9 _renderTarget = null!;
    private IDirect3DSurface9 _depthStencil = null!;

    private Vertex[] _vertices = [];
    private GCHandle _pinnedVertices;
    private IntPtr _verticesPtr;
    private Colorvalue[] _faceColors = [];

    private int _width;
    private int _height;

    /// <summary>Native pointer to the render-target surface, for <c>D3DImage.SetBackBuffer</c>.</summary>
    public IntPtr SurfacePointer => _renderTarget.NativePointer;

    /// <summary>Creates the 9Ex device and the off-screen surfaces sized to the target.</summary>
    public void Initialize(IntPtr focusWindow, int width, int height)
    {
        _width = Math.Max(1, width);
        _height = Math.Max(1, height);

        _d3d = Direct3DCreate9Ex();

        var presentParameters = new PresentParameters
        {
            Windowed = true,
            SwapEffect = SwapEffect.Discard,
            BackBufferWidth = (uint)_width,
            BackBufferHeight = (uint)_height,
            BackBufferFormat = Format.X8R8G8B8,
            DeviceWindowHandle = focusWindow,
            EnableAutoDepthStencil = false,
        };

        _device = _d3d.CreateDeviceEx(
            0,
            DeviceType.Hardware,
            focusWindow,
            CreateFlags.HardwareVertexProcessing | CreateFlags.Multithreaded | CreateFlags.FpuPreserve,
            presentParameters);

        BuildGeometry();
        CreateSurfaces();
        ConfigurePipeline();
    }

    private void BuildGeometry()
    {
        // Six faces: an outward normal, a colour, and four corners wound CCW seen from outside.
        (Vector3 normal, Colorvalue color, Vector3 a, Vector3 b, Vector3 c, Vector3 d)[] faces =
        [
            (new(0, 0, 1), Rgb(0.90f, 0.30f, 0.30f), new(-1, -1, 1), new(1, -1, 1), new(1, 1, 1), new(-1, 1, 1)),
            (new(0, 0, -1), Rgb(0.30f, 0.55f, 0.90f), new(1, -1, -1), new(-1, -1, -1), new(-1, 1, -1), new(1, 1, -1)),
            (new(-1, 0, 0), Rgb(0.95f, 0.75f, 0.25f), new(-1, -1, -1), new(-1, -1, 1), new(-1, 1, 1), new(-1, 1, -1)),
            (new(1, 0, 0), Rgb(0.40f, 0.80f, 0.45f), new(1, -1, 1), new(1, -1, -1), new(1, 1, -1), new(1, 1, 1)),
            (new(0, 1, 0), Rgb(0.75f, 0.45f, 0.85f), new(-1, 1, 1), new(1, 1, 1), new(1, 1, -1), new(-1, 1, -1)),
            (new(0, -1, 0), Rgb(0.85f, 0.55f, 0.35f), new(-1, -1, -1), new(1, -1, -1), new(1, -1, 1), new(-1, -1, 1)),
        ];

        _vertices = new Vertex[faces.Length * 6];
        _faceColors = new Colorvalue[faces.Length];
        var v = 0;
        for (var f = 0; f < faces.Length; f++)
        {
            var (normal, color, a, b, c, d) = faces[f];
            _faceColors[f] = color;
            _vertices[v++] = new Vertex(a, normal);
            _vertices[v++] = new Vertex(b, normal);
            _vertices[v++] = new Vertex(c, normal);
            _vertices[v++] = new Vertex(a, normal);
            _vertices[v++] = new Vertex(c, normal);
            _vertices[v++] = new Vertex(d, normal);
        }

        _pinnedVertices = GCHandle.Alloc(_vertices, GCHandleType.Pinned);
        _verticesPtr = _pinnedVertices.AddrOfPinnedObject();
    }

    private void CreateSurfaces()
    {
        // Lockable = false so the surface lives in video memory and can be shared with WPF's
        // compositor. A8R8G8B8 is one of the formats D3DImage accepts.
        _renderTarget = _device.CreateRenderTarget(
            (uint)_width, (uint)_height, Format.A8R8G8B8, MultisampleType.None, 0, lockable: false);
        _depthStencil = _device.CreateDepthStencilSurface(
            (uint)_width, (uint)_height, Format.D24S8, MultisampleType.None, 0, discard: true);

        _device.SetRenderTarget(0, _renderTarget);
        _device.DepthStencilSurface = _depthStencil;
        _device.Viewport = new Vortice.Direct3D9.Viewport { X = 0, Y = 0, Width = _width, Height = _height, MinZ = 0, MaxZ = 1 };
    }

    private void ConfigurePipeline()
    {
        _device.VertexFormat = VertexFormat.Position | VertexFormat.Normal;
        _device.SetRenderState(RenderState.Lighting, true);
        _device.SetRenderState(RenderState.ZEnable, true);
        _device.SetRenderState(RenderState.NormalizeNormals, true);
        _device.SetRenderState(RenderState.SpecularEnable, false);
        _device.SetRenderState(RenderState.CullMode, (int)Cull.None); // depth handles visibility
        _device.SetRenderState(RenderState.Ambient, unchecked((int)0xFF606060));

        var light = new Light9
        {
            Type = LightType.Directional,
            Diffuse = Rgb(1f, 1f, 1f),
            Ambient = Rgb(0f, 0f, 0f),
            Direction = Vector3.Normalize(new Vector3(-0.4f, -0.6f, -0.7f)),
        };
        _device.SetLight(0, ref light);
        _device.LightEnable(0, true);
    }

    /// <summary>Recreates the surfaces at a new size (call before the next <see cref="Render"/>).</summary>
    public void Resize(int width, int height)
    {
        if (_device is null || (width == _width && height == _height))
            return;

        _width = Math.Max(1, width);
        _height = Math.Max(1, height);

        _renderTarget.Dispose();
        _depthStencil.Dispose();
        CreateSurfaces();
    }

    /// <summary>Draws one frame for the given elapsed time (seconds) into the render-target surface.</summary>
    public void Render(float totalSeconds)
    {
        var world = Matrix4x4.CreateRotationY(totalSeconds * 0.9f)
                    * Matrix4x4.CreateRotationX(totalSeconds * 0.6f);
        var view = Matrix4x4.CreateLookAt(new Vector3(0, 0, 6), Vector3.Zero, Vector3.UnitY);
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(
            MathF.PI / 4f, _width / (float)_height, 0.1f, 100f);

        _device.SetTransform(WorldTransform, world);
        _device.SetTransform(TransformState.View, view);
        _device.SetTransform(TransformState.Projection, projection);

        _device.Clear(ClearFlags.Target | ClearFlags.ZBuffer, new Color(0.125f, 0.157f, 0.188f, 1f), 1f, 0);
        _device.BeginScene();

        for (var f = 0; f < _faceColors.Length; f++)
        {
            var material = new Material9 { Diffuse = _faceColors[f], Ambient = _faceColors[f] };
            _device.SetMaterial(ref material);
            _device.DrawPrimitiveUP(PrimitiveType.TriangleList, 2, _verticesPtr + f * 6 * VertexStride, VertexStride);
        }

        _device.EndScene();
    }

    /// <summary>Reads the render-target surface back from the GPU and saves it as a PNG.</summary>
    public void CaptureToPng(string path)
    {
        // The render target lives in video memory and isn't lockable, so copy it into a
        // system-memory surface we can read on the CPU.
        using var staging = _device.CreateOffscreenPlainSurface(
            (uint)_width, (uint)_height, Format.A8R8G8B8, Pool.SystemMemory);
        _device.GetRenderTargetData(_renderTarget, staging);

        var locked = staging.LockRect(LockFlags.ReadOnly);
        try
        {
            var stride = _width * 4;
            var pixels = new byte[stride * _height];
            for (var y = 0; y < _height; y++)
                Marshal.Copy(locked.DataPointer + y * locked.Pitch, pixels, y * stride, stride);

            // D3DFMT_A8R8G8B8 is 0xAARRGGBB, i.e. B,G,R,A in memory — WPF's Bgra32.
            var bitmap = BitmapSource.Create(_width, _height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));

            var directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (directory is not null)
                Directory.CreateDirectory(directory);
            using var stream = File.Create(path);
            encoder.Save(stream);
        }
        finally
        {
            staging.UnlockRect();
        }
    }

    private static Colorvalue Rgb(float r, float g, float b) => new() { R = r, G = g, B = b, A = 1f };

    public void Dispose()
    {
        if (_pinnedVertices.IsAllocated)
            _pinnedVertices.Free();
        _depthStencil?.Dispose();
        _renderTarget?.Dispose();
        _device?.Dispose();
        _d3d?.Dispose();
    }
}
