using System.IO;
using UnityEditor;
using UnityEngine;

namespace BonV.Editor
{
    /// <summary>
    /// Renders square thumbnails from a scene camera. BonBonStudio lists a bundle with the image of the same name next to it,
    /// such as AbstractEcho2.png beside AbstractEcho2.vss.
    /// </summary>
    public static class ThumbnailCapture
    {
        public enum Format { Png, Jpg }

        // The publish script takes the first of these it finds next to a bundle, so saving one removes the others
        public static readonly string[] Extensions = { ".jpg", ".jpeg", ".png" };

        public static string ExtensionOf(Format format) => format == Format.Png ? ".png" : ".jpg";

        /// <summary>
        /// Renders the camera into a size by size texture. The camera keeps its vertical field of view, so the sides are cropped.
        /// The caller owns the texture.
        /// </summary>
        public static Texture2D Render(Camera camera, int size)
        {
            var previousActive = RenderTexture.active;
            var target = new RenderTexture(size, size, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB) { antiAliasing = 8 };
            try
            {
                RenderInto(camera, target);
                RenderTexture.active = target;
                var image = new Texture2D(size, size, TextureFormat.RGB24, false);
                image.ReadPixels(new Rect(0, 0, size, size), 0, 0);
                image.Apply();
                return image;
            }
            finally
            {
                RenderTexture.active = previousActive;
                Object.DestroyImmediate(target);
            }
        }

        /// <summary>
        /// Renders what the camera sees into the target through a hidden copy, so the scene's camera isn't touched:
        /// one that's switched off, like a reference camera, stays off and the scene isn't marked as changed.
        /// </summary>
        public static void RenderInto(Camera source, RenderTexture target)
        {
            // Hidden cameras of our own, like the temporary one, aren't in the scene, so they render directly
            bool ownCamera = (source.gameObject.hideFlags & HideFlags.DontSaveInEditor) != 0;
            var camera = ownCamera ? source : RenderCopyOf(source);
            try
            {
                camera.targetTexture = target;
                camera.aspect = (float)target.width / target.height;
                camera.Render();
            }
            finally
            {
                camera.targetTexture = null;
                camera.ResetAspect();
            }
        }

        // One copy reused for every render: a new camera per frame makes the render pipeline set up and tear down
        // its per-camera buffers each time, which stalls the editor at preview rates
        private static Camera renderCopy;
        private static Camera renderCopySource;

        private static Camera RenderCopyOf(Camera source)
        {
            if (renderCopy && renderCopySource == source)
            {
                // Follows the source's moves and camera settings since the last render
                renderCopy.CopyFrom(source);
                renderCopy.enabled = false;
                return renderCopy;
            }

            ReleaseRenderCopy();
            renderCopy = CreateHiddenCopy(source, "Thumbnail Render Camera");
            renderCopySource = source;
            // Hidden objects outlive a script reload, so the copy goes first instead of being left behind
            AssemblyReloadEvents.beforeAssemblyReload -= ReleaseRenderCopy;
            AssemblyReloadEvents.beforeAssemblyReload += ReleaseRenderCopy;
            return renderCopy;
        }

        /// <summary>
        /// Destroys the reused render camera; the next render makes a new one.
        /// </summary>
        public static void ReleaseRenderCopy()
        {
            if (renderCopy) Object.DestroyImmediate(renderCopy.gameObject);
            renderCopy = null;
            renderCopySource = null;
        }

        /// <summary>
        /// A switched-off camera outside the scene, with the source's settings, position and render pipeline data when given.
        /// It's never saved or exported and is invisible in the Hierarchy; the caller destroys its GameObject.
        /// </summary>
        public static Camera CreateHiddenCopy(Camera source, string name)
        {
            var rig = new GameObject(name) { hideFlags = HideFlags.HideAndDontSave };
            var camera = rig.AddComponent<Camera>();
            if (source)
            {
                // Also matches the copy's position, rotation and layer to the source
                camera.CopyFrom(source);
                // Render pipeline settings such as post-processing live on a separate component, like URP's UniversalAdditionalCameraData
                foreach (var component in source.GetComponents<Component>())
                {
                    // Null for a component whose script is missing
                    if (!component || component is Camera || component is Transform || !component.GetType().Name.EndsWith("AdditionalCameraData")) continue;
                    var copy = rig.GetComponent(component.GetType());
                    if (!copy) copy = rig.AddComponent(component.GetType());
                    EditorUtility.CopySerialized(component, copy);
                }
            }
            camera.enabled = false;
            return camera;
        }

        /// <summary>
        /// Writes the image to the path, as JPG when the path ends in .jpg or .jpeg and PNG otherwise.
        /// </summary>
        public static void Save(Texture2D image, string path)
        {
            string extension = Path.GetExtension(path).ToLowerInvariant();
            bool jpg = extension == ".jpg" || extension == ".jpeg";
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, Encode(image, jpg ? Format.Jpg : Format.Png));
        }

        public static byte[] Encode(Texture2D image, Format format) => format == Format.Jpg ? image.EncodeToJPG(90) : image.EncodeToPNG();

        /// <summary>
        /// A PNG or JPG as a size by size PNG: returned as is when it already is one, scaled otherwise, with a non-square image
        /// cropped to its centre square. Null when it isn't an image.
        /// </summary>
        public static byte[] ToPng(byte[] data, int size)
        {
            var source = new Texture2D(2, 2);
            var previousActive = RenderTexture.active;
            RenderTexture target = null;
            Texture2D result = null;
            try
            {
                if (!source.LoadImage(data)) return null;
                bool png = data.Length > 4 && data[0] == 0x89 && data[1] == 'P' && data[2] == 'N' && data[3] == 'G';
                if (png && source.width == size && source.height == size) return data;

                target = RenderTexture.GetTemporary(size, size, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                // The centre square of the source, so a wide or tall image is cropped rather than squashed
                float aspect = (float)source.width / source.height;
                var scale = aspect >= 1 ? new Vector2(1 / aspect, 1) : new Vector2(1, aspect);
                Graphics.Blit(source, target, scale, (Vector2.one - scale) / 2);
                RenderTexture.active = target;
                result = new Texture2D(size, size, TextureFormat.RGB24, false);
                result.ReadPixels(new Rect(0, 0, size, size), 0, 0);
                result.Apply();
                return result.EncodeToPNG();
            }
            finally
            {
                RenderTexture.active = previousActive;
                if (target) RenderTexture.ReleaseTemporary(target);
                Object.DestroyImmediate(source);
                if (result) Object.DestroyImmediate(result);
            }
        }

        /// <summary>
        /// Writes an encoded image beside the bundle as bundle-name plus the format's extension, replacing any thumbnail already there.
        /// Returns the path written.
        /// </summary>
        public static string WriteNextTo(byte[] image, Format format, string bundlePath)
        {
            string path = NextTo(bundlePath, ExtensionOf(format));
            RemoveNextTo(bundlePath);
            File.WriteAllBytes(path, image);
            return path;
        }

        private static string NextTo(string bundlePath, string extension) =>
            Path.Combine(Path.GetDirectoryName(bundlePath)!, Path.GetFileNameWithoutExtension(bundlePath)) + extension.ToLowerInvariant();

        private static void RemoveNextTo(string bundlePath)
        {
            foreach (string extension in Extensions)
            {
                string path = NextTo(bundlePath, extension);
                if (File.Exists(path)) File.Delete(path);
            }
        }

        /// <summary>
        /// The camera a thumbnail is most likely meant to come from: <paramref name="preferred"/> (such as the Worlds main
        /// camera), then Camera.main, then any camera in the scene.
        /// </summary>
        public static Camera DefaultCamera(Camera preferred = null)
        {
            if (preferred) return preferred;
            if (Camera.main) return Camera.main;
            return Object.FindFirstObjectByType<Camera>(FindObjectsInactive.Include);
        }
    }
}
