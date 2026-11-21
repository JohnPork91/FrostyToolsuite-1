using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using ImGuiNET;
using Veldrid;
using Veldrid.Sdl2;
using Veldrid.StartupUtilities;

namespace Frosty.ImGuiKit
{
    /// <summary>
    /// Owns the native window, the graphics device and the ImGui render loop.
    /// Replaces Frosty.Controls.FrostyWindow / System.Windows.Application.
    /// </summary>
    public sealed class ImGuiHost : IDisposable
    {
        public static ImGuiHost Current { get; private set; }

        private readonly Sdl2Window window;
        private readonly GraphicsDevice graphicsDevice;
        private readonly CommandList commandList;
        private readonly ImGuiController controller;
        private readonly ConcurrentQueue<Action> uiQueue = new ConcurrentQueue<Action>();
        private readonly TextureCache textures;

        private string pendingTitle;

        /// <summary>Raised on the UI thread when files are dropped onto the window.</summary>
        public event Action<string> FileDropped;

        public Vector4 ClearColor { get; set; } = Theme.WindowBackground;

        public int Width => window.Width;
        public int Height => window.Height;

        public TextureCache Textures => textures;

        public ImGuiHost(string title, int width, int height)
        {
            if (Current != null)
                throw new InvalidOperationException("Only one ImGuiHost may exist per process.");

            VeldridStartup.CreateWindowAndGraphicsDevice(
                new WindowCreateInfo(80, 60, width, height, WindowState.Normal, title),
                new GraphicsDeviceOptions(false, null, true, ResourceBindingModel.Improved, true, true),
                GraphicsBackend.Direct3D11,
                out window,
                out graphicsDevice);

            commandList = graphicsDevice.ResourceFactory.CreateCommandList();
            controller = new ImGuiController(graphicsDevice, graphicsDevice.MainSwapchain.Framebuffer.OutputDescription, window.Width, window.Height);
            textures = new TextureCache(graphicsDevice, controller);

            window.Resized += () =>
            {
                graphicsDevice.MainSwapchain.Resize((uint)window.Width, (uint)window.Height);
                controller.WindowResized(window.Width, window.Height);
            };

            // SDL raises one event per dropped file; marshal onto the UI queue so handlers
            // run between frames like everything else.
            window.DragDrop += e => Post(() => FileDropped?.Invoke(e.File));

            Theme.Apply();
            Current = this;
        }

        /// <summary>Queue work to run on the UI thread at the start of the next frame (safe from any thread).</summary>
        public static void Post(Action action) => Current?.uiQueue.Enqueue(action);

        public void SetTitle(string title) => pendingTitle = title;

        public void RequestClose() => window.Close();

        public void Minimize() => window.WindowState = WindowState.Minimized;

        /// <summary>Runs until the window closes. <paramref name="drawFrame"/> is called once per frame.</summary>
        public void Run(Action drawFrame)
        {
            Stopwatch sw = Stopwatch.StartNew();
            long previous = sw.ElapsedTicks;

            while (window.Exists)
            {
                long now = sw.ElapsedTicks;
                float dt = (now - previous) / (float)Stopwatch.Frequency;
                previous = now;

                InputSnapshot snapshot = window.PumpEvents();
                if (!window.Exists)
                    break;

                while (uiQueue.TryDequeue(out Action queued))
                    queued();

                if (pendingTitle != null)
                {
                    window.Title = pendingTitle;
                    pendingTitle = null;
                }

                controller.Update(dt, snapshot);

                drawFrame();
                Dialogs.DrawAll();

                commandList.Begin();
                commandList.SetFramebuffer(graphicsDevice.MainSwapchain.Framebuffer);
                commandList.ClearColorTarget(0, new RgbaFloat(ClearColor.X, ClearColor.Y, ClearColor.Z, 1f));
                controller.Render(graphicsDevice, commandList);
                commandList.End();
                graphicsDevice.SubmitCommands(commandList);
                graphicsDevice.SwapBuffers(graphicsDevice.MainSwapchain);
            }
        }

        public void Dispose()
        {
            graphicsDevice.WaitForIdle();
            textures.Dispose();
            controller.Dispose();
            commandList.Dispose();
            graphicsDevice.Dispose();
            Current = null;
        }
    }
}
