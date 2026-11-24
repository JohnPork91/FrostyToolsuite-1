using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;
using ImGuiNET;
using Veldrid;
using Veldrid.SPIRV;

namespace Frosty.ImGuiKit
{
    /// <summary>
    /// Veldrid renderer + input bridge for ImGui.NET 1.91.x.
    /// Adapted from the public ImGui.NET / Veldrid sample controller, using the modern
    /// io.AddKeyEvent / AddMouseButtonEvent input API instead of the removed io.KeyMap.
    /// </summary>
    internal sealed class ImGuiController : IDisposable
    {
        private readonly GraphicsDevice gd;
        private bool frameBegun;

        private DeviceBuffer vertexBuffer;
        private DeviceBuffer indexBuffer;
        private DeviceBuffer projMatrixBuffer;
        private Texture fontTexture;
        private TextureView fontTextureView;
        private Shader vertexShader;
        private Shader fragmentShader;
        private ResourceLayout layout;
        private ResourceLayout textureLayout;
        private Pipeline pipeline;
        private ResourceSet mainResourceSet;
        private ResourceSet fontTextureResourceSet;

        private readonly IntPtr fontAtlasId = (IntPtr)1;
        private int windowWidth;
        private int windowHeight;

        private readonly Dictionary<TextureView, ResourceSetInfo> setsByView = new Dictionary<TextureView, ResourceSetInfo>();
        private readonly Dictionary<IntPtr, ResourceSetInfo> viewsById = new Dictionary<IntPtr, ResourceSetInfo>();
        private readonly List<IDisposable> ownedResources = new List<IDisposable>();
        private int lastAssignedId = 100;

        private readonly struct ResourceSetInfo
        {
            public readonly IntPtr ImGuiBinding;
            public readonly ResourceSet ResourceSet;

            public ResourceSetInfo(IntPtr imGuiBinding, ResourceSet resourceSet)
            {
                ImGuiBinding = imGuiBinding;
                ResourceSet = resourceSet;
            }
        }

        private const string VertexShader = @"
#version 450
layout (location = 0) in vec2 in_position;
layout (location = 1) in vec2 in_texCoord;
layout (location = 2) in vec4 in_color;
layout (binding = 0) uniform ProjectionMatrixBuffer { mat4 projection_matrix; };
layout (location = 0) out vec4 color;
layout (location = 1) out vec2 texCoord;
out gl_PerVertex { vec4 gl_Position; };
void main()
{
    gl_Position = projection_matrix * vec4(in_position, 0, 1);
    color = in_color;
    texCoord = in_texCoord;
}";

        private const string FragmentShader = @"
#version 450
layout (location = 0) in vec4 color;
layout (location = 1) in vec2 texCoord;
layout (location = 0) out vec4 outputColor;
layout (binding = 1) uniform sampler MainSampler;
layout (set = 1, binding = 0) uniform texture2D MainTexture;
void main()
{
    outputColor = color * texture(sampler2D(MainTexture, MainSampler), texCoord);
}";

        public ImGuiController(GraphicsDevice graphicsDevice, OutputDescription outputDescription, int width, int height)
        {
            gd = graphicsDevice;
            windowWidth = width;
            windowHeight = height;

            ImGui.CreateContext();
            ImGuiIOPtr io = ImGui.GetIO();
            io.BackendFlags |= ImGuiBackendFlags.RendererHasVtxOffset;
            io.ConfigFlags |= ImGuiConfigFlags.NavEnableKeyboard;
            io.IniFilename = null; // no imgui.ini next to the exe; layout is code-driven

            // the built-in bitmap font is tiny and ASCII-only; use the system UI font when there is one
            string fontPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts", "segoeui.ttf");
            if (File.Exists(fontPath))
                io.Fonts.AddFontFromFileTTF(fontPath, 16f);

            CreateDeviceResources(outputDescription);
            ImGui.NewFrame();
            frameBegun = true;
        }

        public void WindowResized(int width, int height)
        {
            windowWidth = width;
            windowHeight = height;
        }

        private void CreateDeviceResources(OutputDescription outputDescription)
        {
            ResourceFactory factory = gd.ResourceFactory;
            vertexBuffer = factory.CreateBuffer(new BufferDescription(10000, BufferUsage.VertexBuffer | BufferUsage.Dynamic));
            vertexBuffer.Name = "ImGui.NET Vertex Buffer";
            indexBuffer = factory.CreateBuffer(new BufferDescription(2000, BufferUsage.IndexBuffer | BufferUsage.Dynamic));
            indexBuffer.Name = "ImGui.NET Index Buffer";

            projMatrixBuffer = factory.CreateBuffer(new BufferDescription(64, BufferUsage.UniformBuffer | BufferUsage.Dynamic));
            projMatrixBuffer.Name = "ImGui.NET Projection Buffer";

            RecreateFontDeviceTexture();

            Shader[] shaders = factory.CreateFromSpirv(
                new ShaderDescription(ShaderStages.Vertex, Encoding.UTF8.GetBytes(VertexShader), "main"),
                new ShaderDescription(ShaderStages.Fragment, Encoding.UTF8.GetBytes(FragmentShader), "main"));
            vertexShader = shaders[0];
            fragmentShader = shaders[1];

            VertexLayoutDescription[] vertexLayouts =
            {
                new VertexLayoutDescription(
                    new VertexElementDescription("in_position", VertexElementSemantic.Position, VertexElementFormat.Float2),
                    new VertexElementDescription("in_texCoord", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float2),
                    new VertexElementDescription("in_color", VertexElementSemantic.Color, VertexElementFormat.Byte4_Norm))
            };

            layout = factory.CreateResourceLayout(new ResourceLayoutDescription(
                new ResourceLayoutElementDescription("ProjectionMatrixBuffer", ResourceKind.UniformBuffer, ShaderStages.Vertex),
                new ResourceLayoutElementDescription("MainSampler", ResourceKind.Sampler, ShaderStages.Fragment)));
            textureLayout = factory.CreateResourceLayout(new ResourceLayoutDescription(
                new ResourceLayoutElementDescription("MainTexture", ResourceKind.TextureReadOnly, ShaderStages.Fragment)));

            GraphicsPipelineDescription pd = new GraphicsPipelineDescription(
                BlendStateDescription.SingleAlphaBlend,
                new DepthStencilStateDescription(false, false, ComparisonKind.Always),
                new RasterizerStateDescription(FaceCullMode.None, PolygonFillMode.Solid, FrontFace.Clockwise, false, true),
                PrimitiveTopology.TriangleList,
                new ShaderSetDescription(vertexLayouts, new[] { vertexShader, fragmentShader }),
                new[] { layout, textureLayout },
                outputDescription,
                ResourceBindingModel.Default);
            pipeline = factory.CreateGraphicsPipeline(ref pd);

            mainResourceSet = factory.CreateResourceSet(new ResourceSetDescription(layout, projMatrixBuffer, gd.PointSampler));
            fontTextureResourceSet = factory.CreateResourceSet(new ResourceSetDescription(textureLayout, fontTextureView));
        }

        /// <summary>Returns an ImGui texture id for the given view (use with ImGui.Image).</summary>
        public IntPtr GetOrCreateImGuiBinding(ResourceFactory factory, TextureView textureView)
        {
            if (!setsByView.TryGetValue(textureView, out ResourceSetInfo rsi))
            {
                ResourceSet resourceSet = factory.CreateResourceSet(new ResourceSetDescription(textureLayout, textureView));
                rsi = new ResourceSetInfo(new IntPtr(lastAssignedId++), resourceSet);

                setsByView.Add(textureView, rsi);
                viewsById.Add(rsi.ImGuiBinding, rsi);
                ownedResources.Add(resourceSet);
            }

            return rsi.ImGuiBinding;
        }

        public void RemoveImGuiBinding(TextureView textureView)
        {
            if (setsByView.TryGetValue(textureView, out ResourceSetInfo rsi))
            {
                setsByView.Remove(textureView);
                viewsById.Remove(rsi.ImGuiBinding);
                ownedResources.Remove(rsi.ResourceSet);
                rsi.ResourceSet.Dispose();
            }
        }

        private void RecreateFontDeviceTexture()
        {
            ImGuiIOPtr io = ImGui.GetIO();
            io.Fonts.GetTexDataAsRGBA32(out IntPtr pixels, out int width, out int height, out int bytesPerPixel);
            io.Fonts.SetTexID(fontAtlasId);

            fontTexture = gd.ResourceFactory.CreateTexture(TextureDescription.Texture2D(
                (uint)width, (uint)height, 1, 1, PixelFormat.R8_G8_B8_A8_UNorm, TextureUsage.Sampled));
            fontTexture.Name = "ImGui.NET Font Texture";
            gd.UpdateTexture(fontTexture, pixels, (uint)(bytesPerPixel * width * height), 0, 0, 0, (uint)width, (uint)height, 1, 0, 0);
            fontTextureView = gd.ResourceFactory.CreateTextureView(fontTexture);

            io.Fonts.ClearTexData();
        }

        public void Render(GraphicsDevice device, CommandList cl)
        {
            if (!frameBegun)
                return;

            frameBegun = false;
            ImGui.Render();
            RenderImDrawData(ImGui.GetDrawData(), device, cl);
        }

        public void Update(float deltaSeconds, InputSnapshot snapshot)
        {
            if (frameBegun)
                ImGui.Render(); // finish a frame nobody rendered

            SetPerFrameImGuiData(deltaSeconds);
            UpdateImGuiInput(snapshot);

            frameBegun = true;
            ImGui.NewFrame();
        }

        private void SetPerFrameImGuiData(float deltaSeconds)
        {
            ImGuiIOPtr io = ImGui.GetIO();
            io.DisplaySize = new Vector2(windowWidth, windowHeight);
            io.DisplayFramebufferScale = Vector2.One;
            io.DeltaTime = deltaSeconds > 0 ? deltaSeconds : 1f / 60f;
        }

        private void UpdateImGuiInput(InputSnapshot snapshot)
        {
            ImGuiIOPtr io = ImGui.GetIO();

            io.AddMousePosEvent(snapshot.MousePosition.X, snapshot.MousePosition.Y);
            io.AddMouseButtonEvent(0, snapshot.IsMouseDown(MouseButton.Left));
            io.AddMouseButtonEvent(1, snapshot.IsMouseDown(MouseButton.Right));
            io.AddMouseButtonEvent(2, snapshot.IsMouseDown(MouseButton.Middle));
            io.AddMouseWheelEvent(0f, snapshot.WheelDelta);

            foreach (char c in snapshot.KeyCharPresses)
                io.AddInputCharacter(c);

            foreach (KeyEvent keyEvent in snapshot.KeyEvents)
            {
                if (TryMapKey(keyEvent.Key, out ImGuiKey imguiKey))
                    io.AddKeyEvent(imguiKey, keyEvent.Down);

                io.AddKeyEvent(ImGuiKey.ModCtrl, (keyEvent.Modifiers & ModifierKeys.Control) != 0);
                io.AddKeyEvent(ImGuiKey.ModShift, (keyEvent.Modifiers & ModifierKeys.Shift) != 0);
                io.AddKeyEvent(ImGuiKey.ModAlt, (keyEvent.Modifiers & ModifierKeys.Alt) != 0);
                io.AddKeyEvent(ImGuiKey.ModSuper, (keyEvent.Modifiers & ModifierKeys.Gui) != 0);
            }
        }

        private static bool TryMapKey(Key key, out ImGuiKey result)
        {
            switch (key)
            {
                case Key.Tab: result = ImGuiKey.Tab; return true;
                case Key.Left: result = ImGuiKey.LeftArrow; return true;
                case Key.Right: result = ImGuiKey.RightArrow; return true;
                case Key.Up: result = ImGuiKey.UpArrow; return true;
                case Key.Down: result = ImGuiKey.DownArrow; return true;
                case Key.PageUp: result = ImGuiKey.PageUp; return true;
                case Key.PageDown: result = ImGuiKey.PageDown; return true;
                case Key.Home: result = ImGuiKey.Home; return true;
                case Key.End: result = ImGuiKey.End; return true;
                case Key.Insert: result = ImGuiKey.Insert; return true;
                case Key.Delete: result = ImGuiKey.Delete; return true;
                case Key.BackSpace: result = ImGuiKey.Backspace; return true;
                case Key.Space: result = ImGuiKey.Space; return true;
                case Key.Enter: result = ImGuiKey.Enter; return true;
                case Key.KeypadEnter: result = ImGuiKey.KeypadEnter; return true;
                case Key.Escape: result = ImGuiKey.Escape; return true;
                case Key.ControlLeft: result = ImGuiKey.LeftCtrl; return true;
                case Key.ControlRight: result = ImGuiKey.RightCtrl; return true;
                case Key.ShiftLeft: result = ImGuiKey.LeftShift; return true;
                case Key.ShiftRight: result = ImGuiKey.RightShift; return true;
                case Key.AltLeft: result = ImGuiKey.LeftAlt; return true;
                case Key.AltRight: result = ImGuiKey.RightAlt; return true;
                default:
                    // letters and function keys are contiguous in both enums
                    if (key >= Key.A && key <= Key.Z)
                    {
                        result = ImGuiKey.A + (key - Key.A);
                        return true;
                    }

                    if (key >= Key.F1 && key <= Key.F12)
                    {
                        result = ImGuiKey.F1 + (key - Key.F1);
                        return true;
                    }

                    result = ImGuiKey.None;
                    return false;
            }
        }

        private void RenderImDrawData(ImDrawDataPtr drawData, GraphicsDevice device, CommandList cl)
        {
            uint vertexOffsetInVertices = 0;
            uint indexOffsetInElements = 0;

            if (drawData.CmdListsCount == 0)
                return;

            uint totalVbSize = (uint)(drawData.TotalVtxCount * Unsafe.SizeOf<ImDrawVert>());
            if (totalVbSize > vertexBuffer.SizeInBytes)
            {
                device.DisposeWhenIdle(vertexBuffer);
                vertexBuffer = device.ResourceFactory.CreateBuffer(new BufferDescription((uint)(totalVbSize * 1.5f), BufferUsage.VertexBuffer | BufferUsage.Dynamic));
            }

            uint totalIbSize = (uint)(drawData.TotalIdxCount * sizeof(ushort));
            if (totalIbSize > indexBuffer.SizeInBytes)
            {
                device.DisposeWhenIdle(indexBuffer);
                indexBuffer = device.ResourceFactory.CreateBuffer(new BufferDescription((uint)(totalIbSize * 1.5f), BufferUsage.IndexBuffer | BufferUsage.Dynamic));
            }

            for (int i = 0; i < drawData.CmdListsCount; i++)
            {
                ImDrawListPtr cmdList = drawData.CmdLists[i];

                cl.UpdateBuffer(vertexBuffer, vertexOffsetInVertices * (uint)Unsafe.SizeOf<ImDrawVert>(),
                    cmdList.VtxBuffer.Data, (uint)(cmdList.VtxBuffer.Size * Unsafe.SizeOf<ImDrawVert>()));
                cl.UpdateBuffer(indexBuffer, indexOffsetInElements * sizeof(ushort),
                    cmdList.IdxBuffer.Data, (uint)(cmdList.IdxBuffer.Size * sizeof(ushort)));

                vertexOffsetInVertices += (uint)cmdList.VtxBuffer.Size;
                indexOffsetInElements += (uint)cmdList.IdxBuffer.Size;
            }

            ImGuiIOPtr io = ImGui.GetIO();
            Matrix4x4 mvp = Matrix4x4.CreateOrthographicOffCenter(0f, io.DisplaySize.X, io.DisplaySize.Y, 0.0f, -1.0f, 1.0f);
            device.UpdateBuffer(projMatrixBuffer, 0, ref mvp);

            cl.SetVertexBuffer(0, vertexBuffer);
            cl.SetIndexBuffer(indexBuffer, IndexFormat.UInt16);
            cl.SetPipeline(pipeline);
            cl.SetGraphicsResourceSet(0, mainResourceSet);

            int vtxOffset = 0;
            int idxOffset = 0;
            for (int n = 0; n < drawData.CmdListsCount; n++)
            {
                ImDrawListPtr cmdList = drawData.CmdLists[n];
                for (int cmdI = 0; cmdI < cmdList.CmdBuffer.Size; cmdI++)
                {
                    ImDrawCmdPtr pcmd = cmdList.CmdBuffer[cmdI];
                    if (pcmd.UserCallback != IntPtr.Zero)
                        throw new NotImplementedException("ImGui user draw callbacks are not supported by this renderer.");

                    if (pcmd.TextureId != IntPtr.Zero)
                    {
                        if (pcmd.TextureId == fontAtlasId)
                            cl.SetGraphicsResourceSet(1, fontTextureResourceSet);
                        else
                            cl.SetGraphicsResourceSet(1, viewsById[pcmd.TextureId].ResourceSet);
                    }

                    cl.SetScissorRect(0,
                        (uint)Math.Max(0, pcmd.ClipRect.X),
                        (uint)Math.Max(0, pcmd.ClipRect.Y),
                        (uint)Math.Max(0, pcmd.ClipRect.Z - pcmd.ClipRect.X),
                        (uint)Math.Max(0, pcmd.ClipRect.W - pcmd.ClipRect.Y));

                    cl.DrawIndexed(pcmd.ElemCount, 1, pcmd.IdxOffset + (uint)idxOffset, (int)pcmd.VtxOffset + vtxOffset, 0);
                }

                vtxOffset += cmdList.VtxBuffer.Size;
                idxOffset += cmdList.IdxBuffer.Size;
            }
        }

        public void Dispose()
        {
            vertexBuffer?.Dispose();
            indexBuffer?.Dispose();
            projMatrixBuffer?.Dispose();
            fontTexture?.Dispose();
            fontTextureView?.Dispose();
            vertexShader?.Dispose();
            fragmentShader?.Dispose();
            layout?.Dispose();
            textureLayout?.Dispose();
            pipeline?.Dispose();
            mainResourceSet?.Dispose();
            fontTextureResourceSet?.Dispose();

            foreach (IDisposable resource in ownedResources)
                resource.Dispose();
            ownedResources.Clear();
        }
    }
}
