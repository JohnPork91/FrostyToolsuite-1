using System;
using System.Collections.Generic;
using System.IO;
using ImGuiNET;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Veldrid;

namespace Frosty.ImGuiKit
{
    public sealed class ImageHandle
    {
        public IntPtr Id { get; internal set; }
        public int Width { get; internal set; }
        public int Height { get; internal set; }

        public bool IsValid => Id != IntPtr.Zero;
    }

    /// <summary>
    /// Decodes PNG/JPG/etc. bytes into GPU textures usable with ImGui.Image.
    /// Replaces the WPF ImageSource / BitmapImage values used by the old UI.
    /// Textures are cached by key for the lifetime of the host.
    /// </summary>
    public sealed class TextureCache : IDisposable
    {
        private sealed class Entry
        {
            public Texture Texture;
            public TextureView View;
            public ImageHandle Handle;
        }

        private static readonly ImageHandle Empty = new ImageHandle();

        private readonly GraphicsDevice gd;
        private readonly ImGuiController controller;
        private readonly Dictionary<object, Entry> entries = new Dictionary<object, Entry>();

        internal TextureCache(GraphicsDevice graphicsDevice, ImGuiController imGuiController)
        {
            gd = graphicsDevice;
            controller = imGuiController;
        }

        /// <summary>Loads an image file from disk (relative paths resolve against the exe directory).</summary>
        public ImageHandle FromFile(string path)
        {
            if (entries.TryGetValue(path, out Entry cached))
                return cached.Handle;

            string full = Path.IsPathRooted(path) ? path : Path.Combine(AppContext.BaseDirectory, path);
            if (!File.Exists(full))
                return Empty;

            return Create(path, File.ReadAllBytes(full));
        }

        /// <summary>Decodes an encoded image. <paramref name="key"/> must be stable (e.g. the mod object).</summary>
        public ImageHandle FromBytes(object key, byte[] encoded)
        {
            if (key == null || encoded == null || encoded.Length == 0)
                return Empty;

            if (entries.TryGetValue(key, out Entry cached))
                return cached.Handle;

            return Create(key, encoded);
        }

        private ImageHandle Create(object key, byte[] encoded)
        {
            try
            {
                using (Image<Rgba32> image = Image.Load<Rgba32>(encoded))
                {
                    int w = image.Width;
                    int h = image.Height;

                    byte[] pixels = new byte[w * h * 4];
                    image.CopyPixelDataTo(pixels);

                    Texture texture = gd.ResourceFactory.CreateTexture(TextureDescription.Texture2D(
                        (uint)w, (uint)h, 1, 1, PixelFormat.R8_G8_B8_A8_UNorm, TextureUsage.Sampled));
                    gd.UpdateTexture(texture, pixels, 0, 0, 0, (uint)w, (uint)h, 1, 0, 0);

                    TextureView view = gd.ResourceFactory.CreateTextureView(texture);
                    IntPtr id = controller.GetOrCreateImGuiBinding(gd.ResourceFactory, view);

                    Entry entry = new Entry
                    {
                        Texture = texture,
                        View = view,
                        Handle = new ImageHandle { Id = id, Width = w, Height = h }
                    };
                    entries[key] = entry;
                    return entry.Handle;
                }
            }
            catch (Exception)
            {
                // Corrupt or unsupported image: cache the failure so we don't retry every frame.
                entries[key] = new Entry { Handle = Empty };
                return Empty;
            }
        }

        /// <summary>Draws the image scaled to fit <paramref name="maxSize"/> while keeping its aspect ratio.</summary>
        public static void DrawFitted(ImageHandle image, System.Numerics.Vector2 maxSize)
        {
            if (image == null || !image.IsValid)
                return;

            float scale = Math.Min(maxSize.X / image.Width, maxSize.Y / image.Height);
            if (scale > 1f)
                scale = 1f;

            ImGui.Image(image.Id, new System.Numerics.Vector2(image.Width * scale, image.Height * scale));
        }

        public void Dispose()
        {
            foreach (Entry entry in entries.Values)
            {
                if (entry.View != null)
                {
                    controller.RemoveImGuiBinding(entry.View);
                    entry.View.Dispose();
                }

                entry.Texture?.Dispose();
            }

            entries.Clear();
        }
    }
}
