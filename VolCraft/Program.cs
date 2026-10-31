using System.Runtime.CompilerServices;
using System.Text;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.WebGPU;
using Silk.NET.WebGPU.Extensions.WGPU;
using Silk.NET.Windowing;


public unsafe class VolCraft
{
    private static IWindow _window = null!;
    private static WebGPU _wgpu = null!;
    private static Wgpu _wgpuExt = null!;
    private static Instance* _instance;
    private static Surface* _surface;
    private static Adapter* _adapter;
    private static Device* _device;
    private static Queue* _queue;
    private static TextureView* _depthView;
    private static Texture* _depthTexture;
    private static ShaderModule* _shaderModule;
    private static RenderPipeline* _renderPipeline;
    private static TextureFormat _surfaceFormat;
    private static SurfaceConfiguration _surfaceConfiguration;
    private static string _shaderSource = null!;
    
    public static void Main(String[] args)
    {
        WindowOptions options = WindowOptions.Default with
        {
            Size = new Vector2D<int>(800, 600),
            Title = "Hello!"
        };
        _window = Window.Create(options);
        _window.Load += OnLoad;
        _window.Update += OnUpdate;
        _window.Render += OnRender;
        _window.Resize += OnResize;
        _window.Closing += OnClose;
        _window.Run();
    }

    private static void OnLoad()
    {
        IInputContext input = _window.CreateInput();
        for (int i = 0; i < input.Keyboards.Count; i++)
        {
            input.Keyboards[i].KeyDown += OnKeyDown;
        }

        _wgpu = WebGPU.GetApi();
        _wgpuExt = new Wgpu(_wgpu.Context);
        InstanceDescriptor descriptor = new InstanceDescriptor();
        _instance = _wgpu.CreateInstance(&descriptor);
        _surface = WebGPUSurface.CreateWebGPUSurface(_window, _wgpu, _instance);

        InstanceEnumerateAdapterOptions adapterOptions = new InstanceEnumerateAdapterOptions();
        nuint count = _wgpuExt.InstanceEnumerateAdapters(_instance, &adapterOptions, null);
        if (count == 0)
        {
            throw new InvalidOperationException("No adapters were found");
        }

        Adapter** adapters = stackalloc Adapter*[(int)count];
        _wgpuExt.InstanceEnumerateAdapters(_instance, &adapterOptions, adapters);
        _adapter = adapters[0];
        DeviceDescriptor deviceDescriptor = new DeviceDescriptor();
        Device* device = null;
        _wgpu.AdapterRequestDevice(_adapter, &deviceDescriptor, new PfnRequestDeviceCallback(OnDeviceRequested),
            &device);
        Thread.Sleep(1000); // TODO: do this better
        if (device == null)
        {
            throw new InvalidOperationException("No device");
        }

        _device = device;
        if (_device == null)
        {
            throw new InvalidOperationException("No device");
        }
        _queue = _wgpu.DeviceGetQueue(_device);
        ConfigureSurface();
        ConfigureDepthSurface();
        try
        {
            _shaderSource = File.ReadAllText("shader.wgsl");
        }
        catch (Exception e)
        {
            throw new FileNotFoundException("cant load shader");
        }

        // PipelineLayoutDescriptor pipelineLayoutDescriptor = new PipelineLayoutDescriptor();
        // _pipelineLayout = _wgpu.DeviceCreatePipelineLayout(_device, &pipelineLayoutDescriptor);
        
        
        ChainedStruct chainedStruct = new ChainedStruct
        {
            SType = SType.ShaderModuleWgslDescriptor
        };
        ShaderModuleWGSLDescriptor wgslDescriptor;
        byte[] utf8Shader = Encoding.UTF8.GetBytes(_shaderSource);
        fixed (byte* str = utf8Shader)
        {
            wgslDescriptor = new ShaderModuleWGSLDescriptor
            {
                Chain = chainedStruct,
                Code = str,
            };
        }
        
        ShaderModuleDescriptor shaderModuleDescriptor = new ShaderModuleDescriptor
        {
            NextInChain = (ChainedStruct*)&wgslDescriptor
        };
        _shaderModule = _wgpu.DeviceCreateShaderModule(_device, &shaderModuleDescriptor);

        VertexState vertexState;
        FragmentState fragState;
        
        byte[] verStr = Encoding.UTF8.GetBytes("vs_main");
        fixed (byte* str = verStr)
        {
            vertexState = new VertexState
            {
                Module = _shaderModule,
                EntryPoint = str,
            };
        }

        ColorTargetState colorTargetState = new ColorTargetState
        {
            Format =  _surfaceFormat,
            WriteMask = ColorWriteMask.All
            
        };
        byte[] fragStr = Encoding.UTF8.GetBytes("fs_main");
        fixed (byte* str = fragStr)
        {
            fragState = new FragmentState
            {
                Module = _shaderModule,
                EntryPoint = str,
                TargetCount = 1,
                Targets =  &colorTargetState,
                
            };
        }
        
        
        RenderPipelineDescriptor renderPipelineDescriptor = new RenderPipelineDescriptor
        {
            Vertex = vertexState,
            Fragment = &fragState,
            Primitive = new PrimitiveState
            {
                  Topology =  PrimitiveTopology.TriangleList
            },
            Multisample = new MultisampleState
            {
                Count = 1,
                Mask = 0xFFFFFFFF
            },
           // DepthStencil = _depthTexture
            
        };
        _renderPipeline = _wgpu.DeviceCreateRenderPipeline(_device, &renderPipelineDescriptor);

    }

    private static void OnDeviceRequested(RequestDeviceStatus status, Device* device, byte* message, void* userdata)
    {
        if (status != RequestDeviceStatus.Success)
        {
            throw new InvalidOperationException("Device request failed");
        } 

        *(Device**)userdata = device;
    }

    private static void ConfigureSurface()
    {
        SurfaceCapabilities capabilities = new SurfaceCapabilities();
        _wgpu.SurfaceGetCapabilities(_surface, _adapter, &capabilities);
        if (capabilities.FormatCount > 0)
        {
            _surfaceFormat = capabilities.Formats[0];
        }
        else
        {
            _surfaceFormat = _wgpu.SurfaceGetPreferredFormat(_surface, _adapter);
        }

        Vector2D<int> size = _window.FramebufferSize;
        _surfaceConfiguration = new SurfaceConfiguration
        {
            Usage = TextureUsage.RenderAttachment,
            Device = _device,
            Format = _surfaceFormat,
            PresentMode = PresentMode.Fifo,
            AlphaMode = CompositeAlphaMode.Auto,
            Width = (uint)Math.Max(1, size.X),
            Height = (uint)Math.Max(1, size.Y)
        };
        _wgpu.SurfaceConfigure(_surface, in _surfaceConfiguration);
    }

    public static void ConfigureDepthSurface()
    {
        TextureDescriptor textureDescriptor = new TextureDescriptor
        {
            Dimension = TextureDimension.Dimension2D,
            Size = new Extent3D(_surfaceConfiguration.Width, _surfaceConfiguration.Height, 1),
            Format = TextureFormat.Depth24Plus,
            MipLevelCount = 1,
            SampleCount = 1,
            Usage = TextureUsage.RenderAttachment
        };
        _depthTexture = _wgpu.DeviceCreateTexture(_device, &textureDescriptor);
        TextureViewDescriptor textureViewDescriptor = new TextureViewDescriptor
        {
            Format = TextureFormat.Depth24Plus,
            Dimension = TextureViewDimension.Dimension2D,
            BaseMipLevel = 0,
            MipLevelCount = 1,
            BaseArrayLayer = 0,
            ArrayLayerCount = 1,
            Aspect = TextureAspect.DepthOnly
        };
        _depthView = _wgpu.TextureCreateView(_depthTexture, &textureViewDescriptor);
    }

    private static void OnResize(Vector2D<int> size)
    {
        if (_surface != null && _device == null && size.X > 0 && size.Y > 0)
        {
            ConfigureSurface();
            ConfigureDepthSurface();
        }
    }
    private static void OnUpdate(double deltaTime)
    {
        
    }

    private static void OnRender(double deltaTime)
    {
        
        SurfaceTexture surfaceTexture = new  SurfaceTexture();
        _wgpu.SurfaceGetCurrentTexture(_surface, &surfaceTexture);
        if (surfaceTexture.Status != SurfaceGetCurrentTextureStatus.Success)
        {
            ConfigureSurface();
        }

        TextureView* colorView = _wgpu.TextureCreateView(surfaceTexture.Texture, null);
        CommandEncoderDescriptor encoderDescriptor = new CommandEncoderDescriptor();
        CommandEncoder* encoder = _wgpu.DeviceCreateCommandEncoder(_device, &encoderDescriptor);
        RenderPassColorAttachment colorAttachment = new RenderPassColorAttachment
        {
            View = colorView,
            LoadOp = LoadOp.Clear,
            StoreOp = StoreOp.Store,
            ClearValue = new Color(1, 0.03, 0.04, 1.0),
        };
        // RenderPassDepthStencilAttachment depthAttachment = new RenderPassDepthStencilAttachment
        // {
        //     View = _depthView,
        //     DepthLoadOp = LoadOp.Clear,
        //     DepthStoreOp = StoreOp.Store,
        //     DepthClearValue = 1.0f,
        //     StencilLoadOp = LoadOp.Undefined,
        //     StencilStoreOp = StoreOp.Undefined
        // };
        RenderPassDescriptor passDescriptor = new RenderPassDescriptor
        {
            ColorAttachmentCount = 1,
            ColorAttachments = &colorAttachment,
           // DepthStencilAttachment = &depthAttachment
        };
        
        RenderPassEncoder* pass = _wgpu.CommandEncoderBeginRenderPass(encoder, &passDescriptor);
        _wgpu.RenderPassEncoderSetPipeline(pass, _renderPipeline);
        _wgpu.RenderPassEncoderDraw(pass, 3, 1, 0 ,0);
        
        
        
        _wgpu.RenderPassEncoderEnd(pass);
        CommandBufferDescriptor commandBufferDescripter = new CommandBufferDescriptor();
        CommandBuffer* commandBuffer = _wgpu.CommandEncoderFinish(encoder, &commandBufferDescripter);
        _wgpu.QueueSubmit(_queue, 1, &commandBuffer);
        _wgpu.SurfacePresent(_surface);
        
        _wgpu.CommandBufferRelease(commandBuffer);
        _wgpu.RenderPassEncoderRelease(pass);
        _wgpu.TextureViewRelease(colorView);
        _wgpu.CommandEncoderRelease(encoder);
        _wgpu.TextureRelease(surfaceTexture.Texture);
    }

    private static void OnKeyDown(IKeyboard keyboard, Key key, int keyCode)
    {
        if(key == Key.Escape) _window.Close();
    }

    private static void OnClose()
    {
        _wgpu.RenderPipelineRelease(_renderPipeline);
        _wgpu.ShaderModuleRelease(_shaderModule);
        _wgpu.TextureRelease(_depthTexture);
        _wgpu.TextureViewRelease(_depthView);
        _wgpu.QueueRelease(_queue);
        _wgpu.DeviceRelease(_device);
        _wgpu.AdapterRelease(_adapter);
        _wgpu.SurfaceRelease(_surface);
        _wgpu.InstanceRelease(_instance);
    }
}
