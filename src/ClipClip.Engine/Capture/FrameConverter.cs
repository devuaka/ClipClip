using Vortice;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace ClipClip.Engine.Capture;

/// <summary>
/// BGRA to NV12 BT.709 and scaling on the GPU, through the driver's video processor, for every
/// encoder. The output canvas is fixed for an encoder's lifetime: whatever size the game switches
/// to is fitted into it, so a resolution change never restarts the encoder or loses the buffer.
/// </summary>
public sealed class FrameConverter : IDisposable
{
    private readonly GpuDevice _gpu;
    private readonly int _outputWidth;
    private readonly int _outputHeight;
    private readonly int _fps;
    private readonly Dictionary<nint, ID3D11VideoProcessorOutputView> _outputViews = new();

    private ID3D11Texture2D? _input;
    private ID3D11VideoProcessorInputView? _inputView;
    private ID3D11VideoProcessorEnumerator? _enumerator;
    private ID3D11VideoProcessor? _processor;
    private int _inputWidth;
    private int _inputHeight;

    public FrameConverter(GpuDevice gpu, int outputWidth, int outputHeight, int fps)
    {
        _gpu = gpu;
        _outputWidth = outputWidth;
        _outputHeight = outputHeight;
        _fps = fps;
    }

    public void Convert(ID3D11Texture2D source, int contentWidth, int contentHeight, ID3D11Texture2D destination)
    {
        Texture2DDescription sourceDescription = source.Description;
        int width = Math.Min(contentWidth, (int)sourceDescription.Width);
        int height = Math.Min(contentHeight, (int)sourceDescription.Height);
        if (width != _inputWidth || height != _inputHeight) Prepare(width, height);

        _gpu.Context.CopySubresourceRegion(_input!, 0, 0, 0, 0, source, 0,
            new Box(0, 0, 0, width, height, 1));

        ID3D11VideoProcessorOutputView outputView = OutputViewFor(destination);
        var stream = new VideoProcessorStream { Enable = true, InputSurface = _inputView! };
        _gpu.VideoContext.VideoProcessorBlt(_processor!, outputView, 0, 1, [stream]).CheckError();
    }

    private void Prepare(int width, int height)
    {
        ReleaseInput();
        _inputWidth = width;
        _inputHeight = height;

        _input = _gpu.Device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
        });

        var content = new VideoProcessorContentDescription
        {
            InputFrameFormat = VideoFrameFormat.Progressive,
            InputFrameRate = new Rational((uint)_fps, 1),
            InputWidth = (uint)width,
            InputHeight = (uint)height,
            OutputFrameRate = new Rational((uint)_fps, 1),
            OutputWidth = (uint)_outputWidth,
            OutputHeight = (uint)_outputHeight,
            Usage = VideoUsage.OptimalSpeed,
        };
        _gpu.VideoDevice.CreateVideoProcessorEnumerator(content, out _enumerator).CheckError();
        _gpu.VideoDevice.CreateVideoProcessor(_enumerator!, 0, out _processor).CheckError();

        var inputViewDescription = new VideoProcessorInputViewDescription
        {
            ViewDimension = VideoProcessorInputViewDimension.Texture2D,
            Texture2D = new Texture2DVideoProcessorInputView { MipSlice = 0, ArraySlice = 0 },
        };
        _gpu.VideoDevice.CreateVideoProcessorInputView(_input, _enumerator!, inputViewDescription, out _inputView).CheckError();

        ID3D11VideoContext1 context = _gpu.VideoContext;
        context.VideoProcessorSetStreamFrameFormat(_processor!, 0, VideoFrameFormat.Progressive);
        context.VideoProcessorSetStreamAutoProcessingMode(_processor!, 0, false);
        context.VideoProcessorSetStreamColorSpace1(_processor!, 0, ColorSpaceType.RgbFullG22NoneP709);
        context.VideoProcessorSetOutputColorSpace1(_processor!, ColorSpaceType.YcbcrStudioG22LeftP709);

        (RawRect source, RawRect destination) = FitRects(width, height);
        context.VideoProcessorSetStreamSourceRect(_processor!, 0, true, source);
        context.VideoProcessorSetStreamDestRect(_processor!, 0, true, destination);
        context.VideoProcessorSetOutputTargetRect(_processor!, true, new RawRect(0, 0, _outputWidth, _outputHeight));

        // Letterbox bars are black: Y 16, Cb/Cr 128 in studio range.
        var black = new VideoColor { YCbCr = new VideoColorYCbCrA { Y = 16f / 255, Cb = 0.5f, Cr = 0.5f, A = 1 } };
        context.VideoProcessorSetOutputBackgroundColor(_processor!, true, black);
    }

    private (RawRect source, RawRect destination) FitRects(int width, int height)
    {
        double scale = Math.Min((double)_outputWidth / width, (double)_outputHeight / height);
        int fitWidth = Math.Min(_outputWidth, (int)Math.Round(width * scale) & ~1);
        int fitHeight = Math.Min(_outputHeight, (int)Math.Round(height * scale) & ~1);
        int x = ((_outputWidth - fitWidth) / 2) & ~1;
        int y = ((_outputHeight - fitHeight) / 2) & ~1;
        return (new RawRect(0, 0, width, height), new RawRect(x, y, x + fitWidth, y + fitHeight));
    }

    private ID3D11VideoProcessorOutputView OutputViewFor(ID3D11Texture2D texture)
    {
        if (_outputViews.TryGetValue(texture.NativePointer, out ID3D11VideoProcessorOutputView? view))
            return view;

        var description = new VideoProcessorOutputViewDescription
        {
            ViewDimension = VideoProcessorOutputViewDimension.Texture2D,
            Texture2D = new Texture2DVideoProcessorOutputView { MipSlice = 0 },
        };
        _gpu.VideoDevice.CreateVideoProcessorOutputView(texture, _enumerator!, description, out view).CheckError();
        _outputViews[texture.NativePointer] = view!;
        return view!;
    }

    private void ReleaseInput()
    {
        foreach (ID3D11VideoProcessorOutputView view in _outputViews.Values) view.Dispose();
        _outputViews.Clear();
        _inputView?.Dispose();
        _inputView = null;
        _processor?.Dispose();
        _processor = null;
        _enumerator?.Dispose();
        _enumerator = null;
        _input?.Dispose();
        _input = null;
    }

    public void Dispose() => ReleaseInput();
}
