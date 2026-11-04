using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace BonV.Editor
{
    /// <summary>
    /// The thumbnail part of an export window: picks a scene camera or a temporary one, previews it live, saves thumbnails
    /// and puts one beside each exported bundle. The scene and character export windows each host one.
    /// </summary>
    public class ThumbnailPanel
    {
        // Every thumbnail is this size and PNG, saved or exported
        public const int ThumbnailSize = 1024;
        private const ThumbnailCapture.Format ThumbnailFormat = ThumbnailCapture.Format.Png;
        private const double SlowRenderSeconds = 0.25;
        private const int PreviewResolution = 384;
        private const double PreviewInterval = 0.15;

        private readonly EditorWindow window;
        private readonly string prefsPrefix;
        // What's being exported, as in "No thumbnail saved for this scene yet"
        private readonly string noun;

        /// <summary>
        /// Identifies what's being exported, such as the scene asset's GUID, so each keeps its own thumbnail; null when it
        /// can't be kept yet, such as an unsaved scene.
        /// </summary>
        public Func<string> OwnerKey;

        /// <summary>Why there's nothing to render, or null; shown in place of the camera warnings.</summary>
        public Func<string> Unavailable;

        /// <summary>The scene a picked camera should be in, or null for any.</summary>
        public Func<Scene?> ExpectedScene;

        /// <summary>The camera to pick before Camera.main when none has been chosen, such as the Worlds main camera; null for none.</summary>
        public Func<Camera> PreferredCamera;

        /// <summary>
        /// Where a new temporary camera goes, such as in front of the character, with its field of view; null for none,
        /// which leaves it at the Scene view. Also adds a Frame button that moves the temporary camera there.
        /// </summary>
        public Func<(Pose pose, float fieldOfView)?> Framing;

        /// <summary>The label of the button that moves the temporary camera to <see cref="Framing"/>.</summary>
        public string FrameLabel = "Frame";

        /// <summary>The file name Save Thumbnail suggests before anything has been saved.</summary>
        public Func<string> DefaultName;

        /// <summary>
        /// The scene's or model's ID once it has one. Its thumbnail is then kept under the ID, so it stays with it wherever
        /// the scene or character is opened; before that, under <see cref="OwnerKey"/>.
        /// </summary>
        public Func<string> BundleId;

        /// <summary>
        /// Where the bundle will be exported, when that's known ahead, such as the publish folder; null otherwise. A thumbnail
        /// beside a bundle there with the same ID is picked up when none is kept, as for one exported before thumbnails were kept.
        /// </summary>
        public Func<string> ExportedBundlePath;

        private Camera thumbnailCamera;
        // Made by Create Temporary Camera; outside the scene, so it's never saved or exported
        private Camera temporaryCamera;
        private bool livePreview;
        // Rendered into continuously while live; the saved thumbnail is rendered separately at full size
        private RenderTexture thumbnailPreview;
        private double nextPreviewTime;
        // How long the last preview render took; heavy scenes render slower than the preview rate, so the rate backs off to match
        private double lastRenderSeconds;
        private bool warnedSlowRender;

        public ThumbnailPanel(EditorWindow window, string prefsPrefix, string noun)
        {
            this.window = window;
            this.prefsPrefix = prefsPrefix;
            this.noun = noun;
        }

        /// <summary>Call from the window's OnEnable.</summary>
        public void Enable()
        {
            livePreview = EditorPrefs.GetBool(prefsPrefix + "LivePreview", true);
            EditorApplication.update += PreviewTick;
            EditorApplication.update += FlyTick;
        }

        /// <summary>Call from the window's OnDisable.</summary>
        public void Disable()
        {
            EditorApplication.update -= PreviewTick;
            EditorApplication.update -= FlyTick;
            StopFlying();
            Capturing = false;
            RemoveTemporaryCamera();
            ThumbnailCapture.ReleaseRenderCopy();
            ClearPreview();
            ClearSavedThumbnail();
        }

        /// <summary>Call from the window's OnLostFocus: keys held down would otherwise keep moving without their KeyUp.</summary>
        public void LostFocus() => StopFlying();

        /// <summary>
        /// Whether the image is showing the camera to capture from, in place of the saved thumbnail. The camera settings
        /// for it are drawn by <see cref="DrawCaptureOptions"/>.
        /// </summary>
        public bool Capturing { get; private set; }

        /// <summary>
        /// The thumbnail as an image of the given width with Select Image and Capture In Scene below it, for a window that lays out
        /// its own fields beside it. Capturing swaps the image for the camera's live view until it's saved or cancelled.
        /// </summary>
        public void DrawCompact(float width)
        {
            string key = ThumbnailPrefsKey;
            string unavailable = Unavailable?.Invoke();
            if (Capturing && unavailable != null) StopCapturing();
            PickUpExportedThumbnail();
            string thumbnail = SavedThumbnail;

            var rect = GUILayoutUtility.GetRect(width, width, GUILayout.ExpandWidth(false));
            if (Capturing)
            {
                var camera = ResolveCamera();
                if (camera && !EditorUtility.IsPersistent(camera))
                {
                    if (!thumbnailPreview) RequestPreview();
                    if (thumbnailPreview) EditorGUI.DrawPreviewTexture(rect, thumbnailPreview);
                    HandleFlyControls(rect);
                }
                else
                {
                    EditorGUI.DrawRect(rect, new Color(0.12f, 0.12f, 0.12f));
                    GUI.Label(rect, "No Camera", NoImageStyle);
                }
            }
            else
            {
                DrawSavedThumbnail(rect, thumbnail);
            }

            var buttonHeight = GUILayout.Height(36);
            var buttonWidth = GUILayout.Width((width - 4) / 2);
            using (new EditorGUILayout.HorizontalScope(GUILayout.Width(width)))
            {
                if (Capturing)
                {
                    using (new EditorGUI.DisabledScope(!CanRender))
                    {
                        if (GUILayout.Button(new GUIContent("Save\nCapture", "Save what the camera sees as the thumbnail"), CompactButtonStyle, buttonWidth, buttonHeight))
                        {
                            string saved = SavedThumbnail;
                            string folder = !string.IsNullOrEmpty(saved) ? Path.GetDirectoryName(saved) : Application.dataPath;
                            string name = !string.IsNullOrEmpty(saved) ? Path.GetFileNameWithoutExtension(saved) : DefaultName?.Invoke() ?? "Thumbnail";
                            string path = EditorUtility.SaveFilePanel("Save Thumbnail", folder, name, "png");
                            // Rendered after OnGUI, like the preview; the camera is removed only once it's rendered
                            if (!string.IsNullOrEmpty(path))
                                EditorApplication.delayCall += () =>
                                {
                                    SaveThumbnail(Path.ChangeExtension(path, ".png"), key);
                                    StopCapturing();
                                };
                            GUIUtility.ExitGUI();
                        }
                    }
                    if (GUILayout.Button("Cancel", CompactButtonStyle, buttonWidth, buttonHeight)) StopCapturing();
                }
                else
                {
                    using (new EditorGUI.DisabledScope(key == null))
                    {
                        if (GUILayout.Button(new GUIContent("Select\nImage", key == null ? $"Save the {noun} first so its thumbnail can be kept with it."
                                    : $"Use a PNG or JPG from disk as the {noun}'s thumbnail. Exports crop a non-square image to its centre."),
                                CompactButtonStyle, buttonWidth, buttonHeight))
                        {
                            UseImageFile(key);
                            GUIUtility.ExitGUI();
                        }
                    }
                    using (new EditorGUI.DisabledScope(unavailable != null))
                    {
                        if (GUILayout.Button(new GUIContent("Capture In\nScene", unavailable ?? $"Frame the {noun} with a camera and save what it sees"),
                                CompactButtonStyle, buttonWidth, buttonHeight))
                            StartCapturing();
                    }
                }
            }

            if (!Capturing && string.IsNullOrEmpty(thumbnail))
            {
                // The live preview doesn't run here, so say which view an export would take in place of a thumbnail
                var camera = unavailable == null ? ResolveCamera() : null;
                bool usable = camera && !EditorUtility.IsPersistent(camera);
                GUILayout.Label(new GUIContent(usable ? $"Export uses {camera.name}'s view" : "Export has no thumbnail",
                        usable ? "With no image saved, exports render this camera. Capture In Scene to pick the view." : unavailable ?? "No camera to render one"),
                    EditorStyles.miniLabel, GUILayout.MaxWidth(width));
            }
            if (!Capturing && !string.IsNullOrEmpty(thumbnail))
            {
                using (new EditorGUILayout.HorizontalScope(GUILayout.Width(width)))
                {
                    bool exists = File.Exists(thumbnail);
                    GUILayout.Label(new GUIContent(exists ? Path.GetFileName(thumbnail) : $"{Path.GetFileName(thumbnail)} (missing)", thumbnail),
                        EditorStyles.miniLabel, GUILayout.MaxWidth(width - 50));
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button(new GUIContent("Clear", "Forget this thumbnail; the next export uses the camera's view again"),
                            EditorStyles.miniButton, GUILayout.Width(44)))
                        ClearKeptThumbnail();
                }
            }
        }

        /// <summary>The camera settings while <see cref="Capturing"/>; nothing otherwise.</summary>
        public void DrawCaptureOptions()
        {
            if (!Capturing) return;
            GUILayout.Label("Capture", EditorStyles.boldLabel);
            EditorGUI.BeginChangeCheck();
            ResolveCamera();
            thumbnailCamera = (Camera)EditorGUILayout.ObjectField(
                new GUIContent("Camera", "Scene camera to render from"), thumbnailCamera, typeof(Camera), true);
            livePreview = EditorGUILayout.Toggle(
                new GUIContent("Live Preview", "Keeps rendering the camera while capturing; turn off in heavy scenes"), livePreview);
            if (EditorGUI.EndChangeCheck())
            {
                EditorPrefs.SetBool(prefsPrefix + "LivePreview", livePreview);
                RememberCamera();
                RequestPreview();
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                if (!livePreview && GUILayout.Button("Refresh Preview")) RequestPreview();
                if (GUILayout.Button(new GUIContent(temporaryCamera ? "New Temporary Camera" : "Create Temporary Camera",
                        $"A camera only this window uses, placed {(Framing != null ? $"in front of the {noun}" : "at the Scene view")}. " +
                        "It isn't saved with the scene or exported.")))
                    CreateTemporaryCamera();
                if (temporaryCamera)
                {
                    if (Framing != null && GUILayout.Button(FrameLabel)) ApplyFraming(temporaryCamera);
                    if (GUILayout.Button("Align to Scene View")) AlignToSceneView(temporaryCamera);
                }
            }
            var camera = ResolveCamera();
            if (!camera)
                EditorGUILayout.HelpBox("Pick a camera or create a temporary one.", MessageType.Info);
            else if (EditorUtility.IsPersistent(camera))
                EditorGUILayout.HelpBox("Pick a camera from the scene, not a prefab.", MessageType.Error);
            else if (camera != temporaryCamera && ExpectedScene?.Invoke() is { } expected && camera.gameObject.scene != expected)
                EditorGUILayout.HelpBox($"This camera isn't in the scene the {noun} is in.", MessageType.Warning);
            if (camera && camera == temporaryCamera)
                EditorGUILayout.HelpBox("Hold right-click on the image and drag to look. WASD moves, Q/E go down and up, " +
                                        "Shift goes faster and the scroll wheel changes the field of view.", MessageType.None);
        }

        private void StartCapturing()
        {
            Capturing = true;
            // Always a camera that can be flown: framed on the subject when there's a framing, else at the Scene view.
            // It copies the current camera's settings, such as post-processing
            CreateTemporaryCamera();
        }

        /// <summary>Ends Capture In Scene, removing its temporary camera; for when what's being exported changes, such as another scene.</summary>
        public void StopCapturing()
        {
            Capturing = false;
            RemoveTemporaryCamera();
            ClearPreview();
            if (window) window.Repaint();
        }

        private static GUIStyle _noImageStyle;
        private static GUIStyle NoImageStyle => _noImageStyle ??= new GUIStyle(EditorStyles.boldLabel)
        {
            alignment = TextAnchor.MiddleCenter,
            fontSize = 18,
            normal = { textColor = new Color(0.6f, 0.6f, 0.6f) },
        };

        private static GUIStyle _compactButtonStyle;
        private static GUIStyle CompactButtonStyle => _compactButtonStyle ??= new GUIStyle(GUI.skin.button) { wordWrap = true };

        // The saved image, read from disk again only when it changes
        private Texture2D savedThumbnail;
        private string savedThumbnailPath;
        private DateTime savedThumbnailTime;

        private void DrawSavedThumbnail(Rect rect, string path)
        {
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                var modified = File.GetLastWriteTimeUtc(path);
                if (!savedThumbnail || savedThumbnailPath != path || savedThumbnailTime != modified)
                {
                    ClearSavedThumbnail();
                    savedThumbnail = new Texture2D(2, 2) { hideFlags = HideFlags.HideAndDontSave };
                    if (!savedThumbnail.LoadImage(File.ReadAllBytes(path))) ClearSavedThumbnail();
                    savedThumbnailPath = path;
                    savedThumbnailTime = modified;
                }
            }
            else
            {
                ClearSavedThumbnail();
            }

            if (savedThumbnail)
            {
                // Cropped the way exports crop it
                EditorGUI.DrawPreviewTexture(rect, savedThumbnail, null, ScaleMode.ScaleAndCrop);
                return;
            }
            EditorGUI.DrawRect(rect, new Color(0.12f, 0.12f, 0.12f));
            GUI.Label(rect, "No Image", NoImageStyle);
        }

        private void ClearSavedThumbnail()
        {
            if (savedThumbnail) Object.DestroyImmediate(savedThumbnail);
            savedThumbnail = null;
            savedThumbnailPath = null;
        }

        private void CreateTemporaryCamera()
        {
            // Starts from the current camera's settings, such as field of view and post-processing, so the look carries over
            var source = ResolveCamera();
            RemoveTemporaryCamera();
            temporaryCamera = ThumbnailCapture.CreateHiddenCopy(source, "Thumbnail Camera (Temporary)");
            if (Framing?.Invoke() == null) AlignToSceneView(temporaryCamera);
            else ApplyFraming(temporaryCamera);
            thumbnailCamera = temporaryCamera;
            RequestPreview();
        }

        private void ApplyFraming(Camera camera)
        {
            var framing = Framing?.Invoke();
            if (framing == null) return;
            var (pose, fieldOfView) = framing.Value;
            camera.transform.SetPositionAndRotation(pose.position, pose.rotation);
            camera.fieldOfView = fieldOfView;
            // Close framing needs a near plane closer than a scene camera's
            camera.nearClipPlane = Mathf.Min(camera.nearClipPlane, 0.01f);
            RequestPreview();
        }

        private void AlignToSceneView(Camera camera)
        {
            var sceneView = SceneView.lastActiveSceneView;
            if (!sceneView || !sceneView.camera) return;
            camera.transform.SetPositionAndRotation(sceneView.camera.transform.position, sceneView.camera.transform.rotation);
            RequestPreview();
        }

        private void RemoveTemporaryCamera()
        {
            if (temporaryCamera) Object.DestroyImmediate(temporaryCamera.gameObject);
            temporaryCamera = null;
            StopFlying();
        }

        // Right-drag fly controls on the preview, like the Scene view's, moving the temporary camera
        private bool flying;
        private bool flyShift;
        private const float FlySpeed = 5f;
        private float flyPitch, flyYaw;
        private double lastFlyTime, nextFlyRender;
        private readonly HashSet<KeyCode> flyKeys = new();
        private const double FlyRenderInterval = 1.0 / 30;

        private void HandleFlyControls(Rect rect)
        {
            if (!temporaryCamera || thumbnailCamera != temporaryCamera) return;
            var e = Event.current;
            int id = GUIUtility.GetControlID(FocusType.Passive);
            if (flying || rect.Contains(e.mousePosition)) EditorGUIUtility.AddCursorRect(rect, flying ? MouseCursor.FPS : MouseCursor.Pan);

            switch (e.GetTypeForControl(id))
            {
                case EventType.MouseDown when e.button == 1 && rect.Contains(e.mousePosition):
                    flying = true;
                    GUIUtility.hotControl = id;
                    flyKeys.Clear();
                    lastFlyTime = EditorApplication.timeSinceStartup;
                    Vector3 euler = temporaryCamera.transform.eulerAngles;
                    flyPitch = euler.x > 180 ? euler.x - 360 : euler.x;
                    flyYaw = euler.y;
                    // Lets the drag keep going past the window's edge
                    EditorGUIUtility.SetWantsMouseJumping(1);
                    e.Use();
                    break;
                case EventType.MouseDrag when flying && GUIUtility.hotControl == id:
                    flyYaw += e.delta.x * 0.2f;
                    flyPitch = Mathf.Clamp(flyPitch + e.delta.y * 0.2f, -89f, 89f);
                    temporaryCamera.transform.rotation = Quaternion.Euler(flyPitch, flyYaw, 0);
                    flyShift = e.shift;
                    nextFlyRender = 0;
                    e.Use();
                    break;
                case EventType.MouseUp when flying && e.button == 1:
                    StopFlying();
                    GUIUtility.hotControl = 0;
                    e.Use();
                    break;
                case EventType.KeyDown when flying:
                    if (e.keyCode != KeyCode.None) flyKeys.Add(e.keyCode);
                    flyShift = e.shift;
                    e.Use();
                    break;
                case EventType.KeyUp when flying:
                    flyKeys.Remove(e.keyCode);
                    flyShift = e.shift;
                    e.Use();
                    break;
                case EventType.ScrollWheel when flying || rect.Contains(e.mousePosition):
                    // Scrolling up zooms in, like the Scene view
                    temporaryCamera.fieldOfView = Mathf.Clamp(temporaryCamera.fieldOfView + e.delta.y, 5f, 120f);
                    window.ShowNotification(new GUIContent($"Field of view {temporaryCamera.fieldOfView:0}°"), 0.6f);
                    nextFlyRender = 0;
                    RequestPreview();
                    e.Use();
                    break;
            }
        }

        private void StopFlying()
        {
            flying = false;
            flyKeys.Clear();
            EditorGUIUtility.SetWantsMouseJumping(0);
        }

        private void FlyTick()
        {
            if (!flying || !temporaryCamera) return;
            double now = EditorApplication.timeSinceStartup;
            float deltaTime = Mathf.Min((float)(now - lastFlyTime), 0.1f);
            lastFlyTime = now;

            var local = Vector3.zero;
            if (flyKeys.Contains(KeyCode.W)) local += Vector3.forward;
            if (flyKeys.Contains(KeyCode.S)) local += Vector3.back;
            if (flyKeys.Contains(KeyCode.D)) local += Vector3.right;
            if (flyKeys.Contains(KeyCode.A)) local += Vector3.left;
            var transform = temporaryCamera.transform;
            Vector3 move = transform.TransformDirection(local);
            if (flyKeys.Contains(KeyCode.E)) move += Vector3.up;
            if (flyKeys.Contains(KeyCode.Q)) move += Vector3.down;
            if (move != Vector3.zero)
            {
                transform.position += move.normalized * (FlySpeed * (flyShift ? 3f : 1f) * deltaTime);
                nextFlyRender = 0;
            }

            // Rendered here rather than in OnGUI, and capped so flying stays smooth in heavy scenes
            if (now < nextFlyRender) return;
            // At least twice the render time between renders, so the editor keeps time for input and its other windows
            nextFlyRender = now + Math.Max(FlyRenderInterval, lastRenderSeconds * 2);
            RenderPreview();
            window.Repaint();
        }

        // Rendering a camera in the middle of OnGUI disturbs the window's own drawing, so it waits until after
        private bool previewRequested;
        public void RequestPreview()
        {
            if (previewRequested) return;
            previewRequested = true;
            EditorApplication.delayCall += () =>
            {
                previewRequested = false;
                if (!window) return;
                RenderPreview();
                window.Repaint();
            };
        }

        private bool CanRender => ResolveCamera() is { } camera && !EditorUtility.IsPersistent(camera) && Unavailable?.Invoke() == null;

        private void PreviewTick()
        {
            // FlyTick renders while flying, so this would only double the work; outside Capture In Scene the image shows the saved thumbnail
            if (!livePreview || flying || !Capturing || EditorApplication.timeSinceStartup < nextPreviewTime) return;
            if (!CanRender) return;
            nextPreviewTime = EditorApplication.timeSinceStartup + Math.Max(PreviewInterval, lastRenderSeconds * 4);
            try
            {
                RenderPreview();
            }
            catch (Exception e)
            {
                // Stopped so one broken camera doesn't log an error every tick; turning Live Preview back on retries
                livePreview = false;
                Debug.LogException(e);
                Debug.LogWarning("Live thumbnail preview stopped after an error.");
            }
            window.Repaint();
        }

        private void RenderPreview()
        {
            if (!CanRender) return;
            if (!thumbnailPreview)
            {
                thumbnailPreview = new RenderTexture(PreviewResolution, PreviewResolution, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB)
                {
                    antiAliasing = 4,
                    hideFlags = HideFlags.HideAndDontSave,
                };
            }
            var timer = System.Diagnostics.Stopwatch.StartNew();
            ThumbnailCapture.RenderInto(thumbnailCamera, thumbnailPreview);
            lastRenderSeconds = timer.Elapsed.TotalSeconds;
            if (lastRenderSeconds > SlowRenderSeconds && !warnedSlowRender)
            {
                warnedSlowRender = true;
                Debug.LogWarning($"Thumbnail preview took {lastRenderSeconds * 1000:0} ms to render, so it updates less often in this scene. " +
                                 "Turning off Live Preview leaves the editor at full speed.");
            }
        }

        private void ClearPreview()
        {
            if (thumbnailPreview) Object.DestroyImmediate(thumbnailPreview);
            thumbnailPreview = null;
        }

        // The thumbnail kept for what's being exported: under its ID once it has one, else under its owner
        private string OwnerPrefsKey => OwnerKey?.Invoke() is { Length: > 0 } owner ? prefsPrefix + "ThumbnailFile." + owner : null;
        private string IdPrefsKey => BundleId?.Invoke() is { Length: > 0 } id ? prefsPrefix + "ThumbnailFile.id." + id : null;
        private string ThumbnailPrefsKey => IdPrefsKey ?? OwnerPrefsKey;

        // One saved under the owner before the ID existed still counts until one is kept under the ID
        private string SavedThumbnail
        {
            get
            {
                if (IdPrefsKey is { } idKey && EditorPrefs.GetString(idKey, "") is { Length: > 0 } byId) return byId;
                return OwnerPrefsKey is { } ownerKey ? EditorPrefs.GetString(ownerKey, "") : "";
            }
        }

        private void ClearKeptThumbnail()
        {
            if (IdPrefsKey is { } idKey) EditorPrefs.DeleteKey(idKey);
            if (OwnerPrefsKey is { } ownerKey) EditorPrefs.DeleteKey(ownerKey);
        }

        // Keeps an image an export used, under the ID the export has made by now, and shows it
        private void KeepThumbnail(string path)
        {
            if (ThumbnailPrefsKey is not { } key) return;
            EditorPrefs.SetString(key, path);
            if (window) window.Repaint();
        }

        private readonly HashSet<string> lookedForExported = new();

        private void PickUpExportedThumbnail()
        {
            string idKey = IdPrefsKey;
            if (idKey == null || !string.IsNullOrEmpty(SavedThumbnail) || !lookedForExported.Add(idKey)) return;
            string bundle = ExportedBundlePath?.Invoke();
            if (string.IsNullOrEmpty(bundle) || !File.Exists(bundle) || SceneBundleInfo.Read(bundle)?.id != BundleId()) return;
            foreach (string extension in ThumbnailCapture.Extensions)
            {
                string image = Path.ChangeExtension(bundle, extension);
                if (!File.Exists(image)) continue;
                EditorPrefs.SetString(idKey, image);
                return;
            }
        }

        private void SaveThumbnail(string path, string key)
        {
            if (!window || !ResolveCamera())
            {
                Debug.LogWarning("Thumbnail not saved: the camera or the export window is gone.");
                return;
            }
            var image = ThumbnailCapture.Render(thumbnailCamera, ThumbnailSize);
            try
            {
                ThumbnailCapture.Save(image, path);
                if (key != null) EditorPrefs.SetString(key, path);
                Debug.Log($"Saved thumbnail {path}");
            }
            finally
            {
                Object.DestroyImmediate(image);
            }
            window.Repaint();
        }

        private void UseImageFile(string key)
        {
            string saved = SavedThumbnail;
            string folder = !string.IsNullOrEmpty(saved) && Directory.Exists(Path.GetDirectoryName(saved)) ? Path.GetDirectoryName(saved) : "";
            string path = EditorUtility.OpenFilePanelWithFilters("Use Image as Thumbnail", folder, new[] { "Images", "png,jpg,jpeg" });
            if (string.IsNullOrEmpty(path)) return;

            var image = new Texture2D(2, 2);
            try
            {
                if (!image.LoadImage(File.ReadAllBytes(path)))
                {
                    EditorUtility.DisplayDialog("Thumbnail", $"{Path.GetFileName(path)} couldn't be read as a PNG or JPG.", "OK");
                    return;
                }
                if (image.width != image.height)
                    Debug.Log($"{Path.GetFileName(path)} is {image.width}x{image.height}; exports crop it to its centre square.");
            }
            finally
            {
                Object.DestroyImmediate(image);
            }
            // Used where it is, like a saved thumbnail; moving or deleting the file shows it as missing
            EditorPrefs.SetString(key, path);
            window.Repaint();
        }

        public bool HasSavedThumbnail => SavedThumbnail is { Length: > 0 } thumbnail && File.Exists(thumbnail);

        /// <summary>What the preview camera sees, at the thumbnail size and format; null when there's nothing to render.</summary>
        public byte[] RenderPreviewThumbnail()
        {
            if (!CanRender) return null;
            var image = ThumbnailCapture.Render(thumbnailCamera, ThumbnailSize);
            try
            {
                return ThumbnailCapture.Encode(image, ThumbnailFormat);
            }
            finally
            {
                Object.DestroyImmediate(image);
            }
        }

        /// <summary>
        /// BonStudio lists a bundle with the image of the same name beside it, so each export gets the saved thumbnail, or
        /// else the preview rendered before the build.
        /// </summary>
        public void ExportThumbnail(string bundlePath, byte[] preview)
        {
            string thumbnail = SavedThumbnail;
            // While capturing, what the camera sees is what's wanted, over the saved thumbnail
            bool useCapture = Capturing && preview != null;
            if (HasSavedThumbnail && !useCapture)
            {
                // One saved before thumbnails were fixed at this size and format is converted on the way
                byte[] png = ThumbnailCapture.ToPng(File.ReadAllBytes(thumbnail), ThumbnailSize);
                if (png != null)
                {
                    Debug.Log($"Exported thumbnail {thumbnail} to {ThumbnailCapture.WriteNextTo(png, ThumbnailFormat, bundlePath)}");
                    // Moves one saved under the owner over to the ID
                    KeepThumbnail(thumbnail);
                    return;
                }
                Debug.LogWarning($"The {noun}'s thumbnail {thumbnail} couldn't be read as an image, so none was put next to {Path.GetFileName(bundlePath)}.");
                return;
            }
            if (!string.IsNullOrEmpty(thumbnail) && !useCapture)
                Debug.LogWarning($"The {noun}'s thumbnail {thumbnail} is missing, so the current preview is used instead.");
            if (preview == null)
            {
                Debug.LogWarning($"No thumbnail saved and no camera to render one, so none was put next to {Path.GetFileName(bundlePath)}.");
                return;
            }
            string written = ThumbnailCapture.WriteNextTo(preview, ThumbnailFormat, bundlePath);
            Debug.Log($"Saved the current preview as thumbnail {written}");
            // Kept, so the window shows what was exported and later exports use it until another is saved
            KeepThumbnail(written);
        }

        // Building a scene bundle reloads the scene, replacing every object in it, so the camera is remembered by
        // GlobalObjectId, which survives that; kept per scene, so each scene keeps its own thumbnail camera
        private const string NoCamera = "none";
        private string CameraPrefsKey => prefsPrefix + "ThumbnailCamera." + SceneManager.GetActiveScene().path;

        private void RememberCamera()
        {
            // The temporary camera has no lasting ID, so the scene's own camera stays remembered
            if (thumbnailCamera && thumbnailCamera == temporaryCamera) return;
            if (!thumbnailCamera)
                EditorPrefs.SetString(CameraPrefsKey, NoCamera);
            else if (!EditorUtility.IsPersistent(thumbnailCamera))
                EditorPrefs.SetString(CameraPrefsKey, GlobalObjectId.GetGlobalObjectIdSlow(thumbnailCamera).ToString());
            else
                EditorPrefs.DeleteKey(CameraPrefsKey);
        }

        private Camera ResolveCamera()
        {
            if (thumbnailCamera) return thumbnailCamera;
            string saved = EditorPrefs.GetString(CameraPrefsKey, "");
            // Cleared on purpose, so no default camera is picked in its place
            if (saved == NoCamera) return null;
            if (GlobalObjectId.TryParse(saved, out var id))
                thumbnailCamera = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(id) as Camera;
            if (!thumbnailCamera)
            {
                thumbnailCamera = ThumbnailCapture.DefaultCamera(PreferredCamera?.Invoke());
                // Kept too, so the same camera comes back after the build reloads the scene
                if (thumbnailCamera) RememberCamera();
            }
            return thumbnailCamera;
        }
    }
}
