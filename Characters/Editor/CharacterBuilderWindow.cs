using UnityEditor;
using System;
using System.Collections.Generic;
using BonBonStudio.SDK.Characters;
using BonV.Editor;
using UnityEditor.SceneManagement;
using UnityEngine;
using System.IO;

public class CharacterBuilderWindow : EditorWindow
{
    private BuildTarget buildTarget = BuildTarget.StandaloneWindows64;
    private GameObject character;
    
    private Vector2 scrollPosition = Vector2.zero;

    private class ValidationMessage
    {
        public string message;
        public MessageType type;
        public ValidationMessage(string message, MessageType type)
        {
            this.message = message;
            this.type = type;
        }
    }
    private List<ValidationMessage> infoList = new();
    private ThumbnailPanel thumbnails;
    private bool publishAfterExport;
    private bool fileNameExpanded;
    private string lastBundleName;
    private const string PrefsPrefix = "BonBonStudio.CharacterBuilder.";

    private void OnEnable()
    {
        // Also when Unity reopens the window after a recompile or restart, which doesn't go through the menu item
        ExportWindowLayout.FixWidth(this);
        thumbnails = new ThumbnailPanel(this, PrefsPrefix, "character")
        {
            OwnerKey = CharacterKey,
            Unavailable = () => !character ? "Pick a character to render its thumbnail."
                : EditorUtility.IsPersistent(character) ? "The character is a prefab asset. Place it in a scene to render a thumbnail; exports still use the saved one."
                : PrefabStageUtility.GetPrefabStage(character) != null ? "Thumbnails can't render in Prefab Mode. Open a scene with the character instead."
                : null,
            ExpectedScene = () => character && !EditorUtility.IsPersistent(character) ? character.scene : (UnityEngine.SceneManagement.Scene?)null,
            Framing = FrameCharacter,
            FrameLabel = "Frame Character",
            DefaultName = () => character ? character.name : null,
            BundleId = () => character && character.GetComponent<ModelInfo>() is { } info && info ? info.id : null,
            ExportedBundlePath = () => BundleExportPublishing.Models is { } publisher && lastBundleName != null ? publisher.BundlePath(lastBundleName) : null,
        };
        thumbnails.Enable();
    }

    private void OnDisable() => thumbnails.Disable();

    // Kept per character object, so each keeps its own thumbnail and bundle name; null in a scene that was never saved
    string CharacterKey() =>
        character && GlobalObjectId.GetGlobalObjectIdSlow(character) is var id && !id.assetGUID.Empty() ? id.ToString() : null;

    // Keys held down would otherwise keep moving after the window loses focus and stops getting KeyUp
    private void OnLostFocus() => thumbnails.LostFocus();

    private const float FrameFieldOfView = 30f;

    // In front of the character, looking at its head and shoulders, or at all of it when it has no humanoid head
    (Pose pose, float fieldOfView)? FrameCharacter()
    {
        if (!character || EditorUtility.IsPersistent(character)) return null;
        var renderers = character.GetComponentsInChildren<Renderer>();
        if (renderers.Length == 0) return null;
        var bounds = renderers[0].bounds;
        foreach (var renderer in renderers) bounds.Encapsulate(renderer.bounds);

        Vector3 center = bounds.center;
        float radius = bounds.extents.magnitude;
        var animator = character.GetComponent<Animator>();
        var head = animator && animator.isHuman ? animator.GetBoneTransform(HumanBodyBones.Head) : null;
        if (head)
        {
            float height = Mathf.Max(bounds.size.y, 0.1f);
            center = head.position - Vector3.up * (0.06f * height);
            radius = 0.16f * height;
        }
        Vector3 forward = Vector3.ProjectOnPlane(character.transform.forward, Vector3.up).normalized;
        if (forward == Vector3.zero) forward = Vector3.forward;
        float distance = radius / Mathf.Sin(FrameFieldOfView * 0.5f * Mathf.Deg2Rad);
        return (new Pose(center + forward * distance, Quaternion.LookRotation(-forward, Vector3.up)), FrameFieldOfView);
    }

    [MenuItem("BonBonStudio SDK/Export Character Model")]
    public static void OpenCharacterWindow()
    {
        var window = (CharacterBuilderWindow)GetWindow(typeof(CharacterBuilderWindow),true,"Character Model Setup");
        ExportWindowLayout.FixWidth(window);
        window.Show();
        if (Selection.activeGameObject)
            window.character = Selection.activeGameObject;
    }
    
    private void OnGUI()
    {
        // Character GameObject field
        GUILayout.Space(4);
        character = (GameObject)EditorGUILayout.ObjectField(new GUIContent("Character", "The character to export"), character, typeof(GameObject), true);
        buildTarget = EditorUserBuildSettings.activeBuildTarget;

        GUILayout.Space(6);
        bool publishTab = ExportWindowLayout.PublishTab(PrefsPrefix + "Tab", BundleExportPublishing.Models);

        // The panel and the sections under it can be taller than the window, so everything above the build button scrolls
        scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition, GUILayout.ExpandHeight(true));
        string infoProblem = null;
        string bundleName = null;
        string title = null;
        bool namePending = false;
        publishAfterExport = false;
        if (character)
        {
            GUILayout.Space(4);
            string nameProblem;
            float imageSize = ExportWindowLayout.ImageSize(position.width);
            // Sized to its contents: neither column stretches the panel to the window's height
            using (new EditorGUILayout.HorizontalScope(ExportWindowLayout.Panel, GUILayout.ExpandHeight(false)))
            {
                using (new EditorGUILayout.VerticalScope(GUILayout.ExpandHeight(false)))
                {
                    infoProblem = InfoFields(out title);
                    GUILayout.Space(6);
                    IdField();
                    GUILayout.Space(6);
                    var modelInfo = character.GetComponent<ModelInfo>();
                    bundleName = BundleFileName.Field(FileNameSource(title, modelInfo ? modelInfo.id : null), ".vsm", ref fileNameExpanded,
                        out nameProblem, out namePending);
                }
                GUILayout.Space(10);
                using (new EditorGUILayout.VerticalScope(GUILayout.Width(imageSize), GUILayout.ExpandHeight(false)))
                    thumbnails.DrawCompact(imageSize);
            }
            thumbnails.DrawCaptureOptions();
            BonesSection();

            infoProblem ??= nameProblem;
            lastBundleName = nameProblem == null && !namePending ? bundleName : null;
            if (nameProblem == null && publishTab) infoProblem ??= PublishSection(bundleName);
            // Publishing replaces the file at its place in the bucket, so one that's another model's stops it
            if (nameProblem == null && publishAfterExport && !namePending && character.GetComponent<ModelInfo>() is { } published && published
                && BundleFileName.ReplacesOther(BundleExportPublishing.Models.BundlePath(bundleName), published.id, "model") is { } replaces)
            {
                EditorGUILayout.HelpBox(replaces, MessageType.Error);
                infoProblem ??= replaces;
            }
        }

        EditorGUILayout.EndScrollView();
        // Build button at the bottom; with no validation section, a blocked build says why in its tooltip
        GUILayout.FlexibleSpace();
        bool valid = ValidateCharacter();
        string blocked = !character ? "Pick a character to build."
            : !valid ? string.Join("\n", infoList.FindAll(info => info.type == MessageType.Error).ConvertAll(info => info.message))
            : infoProblem;
        GUI.enabled = valid && infoProblem == null;
        if (GUILayout.Button(new GUIContent(publishAfterExport ? "Build and Publish Character" : "Build Character", blocked ?? ""), GUILayout.Height(30)))
        {
            BuildCharacter(title, bundleName, namePending);
        }
        GUI.enabled = true;
        BundleExportPublishing.Models?.DrawStatus();
    }

    // Publishing reports from other threads; this repaints when the log or progress has moved on
    private int publishStatusSeen;
    private void Update()
    {
        var publisher = BundleExportPublishing.Models;
        if (publisher == null || publisher.StatusVersion == publishStatusSeen) return;
        publishStatusSeen = publisher.StatusVersion;
        Repaint();
    }

    // Where the character's file name was kept before it moved into its Model Info; null for a character in a scene that
    // was never saved. Still read, so a model exported then keeps its file name
    string BundleNameKey() => CharacterKey() is { } owner ? PrefsPrefix + "BundleName." + owner : null;

    // The character's file name: kept in its Model Info, or made from the title and the ID
    BundleFileName.Source FileNameSource(string title, string id) => new()
    {
        Stored = character.GetComponent<ModelInfo>() is { } info && info ? info.fileName : null,
        LegacyPrefsKey = BundleNameKey(),
        FindPublished = () => BundleExportPublishing.Models is { } models
            ? ModelInfoUtility.IdTakenBy(models.BundlePath(BundleFileName.Generate(title, character.name, id, "model")), id)
            : null,
        Title = title,
        Fallback = character.name,
        Id = id,
        Kind = "model",
        Store = name => EnsureModelInfo().fileName = name,
    };

    // Only in projects that registered a publisher; returns why publishing can't go ahead, or null
    string PublishSection(string bundleName)
    {
        var publisher = BundleExportPublishing.Models;
        if (publisher == null) return null;

        GUILayout.Space(10);
        GUILayout.Label("Publish", EditorStyles.boldLabel);
        var info = character.GetComponent<ModelInfo>();
        // The Publish tab is the choice to publish
        publishAfterExport = true;
        publisher.DrawOptions(bundleName, info ? info.id ?? "" : "");
        string problem = publishAfterExport ? publisher.Problem(bundleName) : null;
        if (problem != null) EditorGUILayout.HelpBox(problem, MessageType.Error);
        return problem;
    }

    // Whether the humanoid bones are normalized (all sharing the character root's orientation and scale), and the Model
    // Info setting that normalizes them in the exported file. The scene is never normalized: that would move the bones
    // Modular Avatar's setups point at. Nothing for characters without a humanoid Avatar, which can't be checked.
    void BonesSection()
    {
        bool? normalized = BoneNormalizer.HumanoidBonesNormalized(character, out int off, out int total);
        if (normalized == null) return;

        GUILayout.Space(10);
        GUILayout.Label("Bones", EditorStyles.boldLabel);
        if (normalized == true)
        {
            EditorGUILayout.HelpBox($"The bones are normalized: all {total} humanoid bones share the character's orientation.", MessageType.Info);
            return;
        }

        var info = character.GetComponent<ModelInfo>();
        bool stored = info && info.normalizeBonesOnExport;
        bool wanted = EditorGUILayout.ToggleLeft(new GUIContent("Normalize Bones on Export",
            "Normalizes the bones in the exported file, after Modular Avatar has run, so they all share the character's " +
            "orientation. The scene isn't changed. Saved in the character's Model Info."), stored);
        if (wanted != stored) EnsureModelInfo().normalizeBonesOnExport = wanted;

        EditorGUILayout.HelpBox(wanted
                ? $"{off} of {total} humanoid bones are rotated or scaled relative to the character. They'll be normalized in " +
                  "the exported file; the scene stays as it is."
                : $"The bones aren't normalized: {off} of {total} humanoid bones are rotated or scaled relative to the character.",
            wanted ? MessageType.Info : MessageType.Warning);
    }

    // The model's ID from its Model Info, with a warning when the character is a copy sharing another's
    void IdField()
    {
        var info = character.GetComponent<ModelInfo>();
        string id = info ? info.id ?? "" : "";
        if (ExportWindowLayout.IdField(id, "A UUID kept in the character's Model Info. It stays the same through renames, " +
                                           "so the app can tell it's the same model.") && info)
            ModelInfoUtility.ConfirmNewId(info);
        if (info && ModelInfoUtility.IsCopy(info))
        {
            EditorGUILayout.HelpBox("This character looks like a copy of another, so both share this ID.", MessageType.Warning);
            if (GUILayout.Button("Give This Character a New ID")) ModelInfoUtility.ConfirmNewId(info);
        }
    }

    // Name, description and render pipeline, the left side of the panel; the name and description are kept in the character's
    // Model Info so they're in version control and the bundle. Returns why the export can't go ahead, or null. title is what
    // it'll be exported with
    string InfoFields(out string title)
    {
        var info = character.GetComponent<ModelInfo>();
        string storedTitle = info ? info.title ?? "" : "";
        string storedDescription = info ? info.description ?? "" : "";

        GUILayout.Label(new GUIContent("Name*", "What players see in the model list; the file name is made from it. Empty uses the character's name."),
            EditorStyles.boldLabel);
        title = EditorGUILayout.TextField(storedTitle.Length > 0 ? storedTitle : character.name);
        ExportWindowLayout.Placeholder(title, "Enter your character's public name...");

        GUILayout.Space(6);
        GUILayout.Label(new GUIContent("Description", "A few lines shown under the title in the model list."), EditorStyles.boldLabel);
        string description = EditorGUILayout.TextArea(storedDescription, new GUIStyle(EditorStyles.textArea) { wordWrap = true }, GUILayout.MinHeight(64));
        ExportWindowLayout.Placeholder(description, "Enter your character's public description...");

        GUILayout.Space(6);
        BundleRenderPipeline.Field(stacked: true);

        // Leaving the title as the character's name keeps it following the character's name
        string newTitle = title.Trim() == character.name ? "" : title;
        if (newTitle != storedTitle || description != storedDescription)
        {
            info = EnsureModelInfo();
            info.title = newTitle;
            info.description = description;
        }

        title = title.Trim().Length > 0 ? title.Trim() : character.name;
        string problem = title.Length > ModelInfoUtility.MaxTitleLength ? $"The title can be at most {ModelInfoUtility.MaxTitleLength} characters."
            : description.Trim().Length > ModelInfoUtility.MaxDescriptionLength
                ? $"The description can be at most {ModelInfoUtility.MaxDescriptionLength} characters ({description.Trim().Length} now)."
            : null;
        if (problem != null) EditorGUILayout.HelpBox(problem, MessageType.Error);
        return problem;
    }

    // The character's Model Info, added with an ID if it has none (newId, or a new one); recorded for undo and marked to save
    ModelInfo EnsureModelInfo(string newId = null)
    {
        var info = character.GetComponent<ModelInfo>();
        if (!info) info = Undo.AddComponent<ModelInfo>(character);
        Undo.RecordObject(info, "Edit Model Info");
        if (string.IsNullOrEmpty(info.id))
        {
            info.id = newId ?? ModelInfoUtility.NewId();
            info.owner = ModelInfoUtility.OwnerOf(info);
        }
        EditorUtility.SetDirty(info);
        if (PrefabUtility.IsPartOfPrefabInstance(info)) PrefabUtility.RecordPrefabInstancePropertyModifications(info);
        return info;
    }

    // Keeps the Model Info: saves the scene, prefab or prefab asset the character is in. False when that didn't happen.
    bool SaveCharacter()
    {
        if (EditorUtility.IsPersistent(character))
        {
            AssetDatabase.SaveAssetIfDirty(character);
            return true;
        }
        var stage = PrefabStageUtility.GetPrefabStage(character);
        if (stage != null)
        {
            PrefabUtility.SaveAsPrefabAsset(stage.prefabContentsRoot, stage.assetPath, out bool saved);
            if (saved) stage.ClearDirtiness();
            return saved;
        }
        return EditorSceneManager.SaveScene(character.scene);
    }

    bool HasUnsavedChanges()
    {
        if (EditorUtility.IsPersistent(character)) return EditorUtility.IsDirty(character);
        var stage = PrefabStageUtility.GetPrefabStage(character);
        return stage != null ? stage.scene.isDirty : character.scene.isDirty;
    }

    bool ValidateCharacter()
    {
        if (!character) return false;
        infoList.Clear();
        bool allowBuild = true;
        if(!character.GetComponent<Animator>())
        {
            infoList.Add(new ValidationMessage("Character GameObject must have an Animator component.", MessageType.Error));
            allowBuild = false;
        }

        if (character.transform.localPosition != Vector3.zero)
        {
            infoList.Add(new ValidationMessage("Character GameObject will reset to origin.", MessageType.Warning));
        }
        
        return allowBuild;
    }
    void BuildCharacter(string title, string bundleName, bool namePending)
    {
            // The ID is made on the first export; it's picked now, before anything changes, since the file name ends with it
            var existing = character.GetComponent<ModelInfo>();
            string id = existing && !string.IsNullOrEmpty(existing.id) ? existing.id : ModelInfoUtility.NewId();
            if (namePending) bundleName = BundleFileName.Resolve(FileNameSource(title, id), out _);

            // Publishing uploads from a set place, so the export goes there instead of where the dialog would put it
            var publisher = publishAfterExport ? BundleExportPublishing.Models : null;
            string fullpath = publisher != null
                ? publisher.BundlePath(bundleName)
                : EditorUtility.SaveFilePanel("Export Character Bundle", ".", bundleName, "vsm");

            if (string.IsNullOrEmpty(fullpath))
                return;

            // Another model's file there would be replaced: never when publishing, which replaces it for players too
            if (BundleFileName.ReplacesOther(fullpath, id, "model") is { } replaces)
            {
                if (publisher != null)
                {
                    EditorUtility.DisplayDialog("Export", replaces, "OK");
                    return;
                }
                if (!EditorUtility.DisplayDialog("Export", replaces + "\n\nReplace it anyway?", "Replace", "Cancel")) return;
            }

            // The ID and file name are saved with the character, so version control and the bundle both have them
            if (HasUnsavedChanges() && !EditorUtility.DisplayDialog("Export",
                    $"{character.name} is in a scene or prefab with unsaved changes. Save it and export?", "Save and Export", "Cancel"))
                return;
            var modelInfo = EnsureModelInfo(id);
            if (ModelInfoUtility.IdTakenBy(fullpath, modelInfo.id) is { } other)
            {
                EditorUtility.DisplayDialog("Export", $"{other} in that folder already uses this model's ID. If it's another model, " +
                                                      "give this one a new ID; if it's this model under an old name, delete it.", "OK");
                return;
            }
            modelInfo.fileName = Path.GetFileNameWithoutExtension(fullpath);
            if (PrefabUtility.IsPartOfPrefabInstance(modelInfo)) PrefabUtility.RecordPrefabInstancePropertyModifications(modelInfo);
            // The name lives in the Model Info now
            if (BundleNameKey() is { } legacyKey) EditorPrefs.DeleteKey(legacyKey);
            if (!SaveCharacter())
            {
                EditorUtility.DisplayDialog("Export", $"Couldn't save {character.name}'s scene or prefab, so nothing was exported.", "OK");
                return;
            }

            // Capture In Scene's view wins over the saved thumbnail while it's open
            byte[] previewThumbnail = thumbnails.HasSavedThumbnail && !thumbnails.Capturing ? null : thumbnails.RenderPreviewThumbnail();
            string filename = Path.GetFileName(fullpath);
            bool complete = false;
            string failure = null;
            string prefabPath = $"Assets/VSM.prefab";
            try {
                AssetDatabase.DeleteAsset(prefabPath);
                if (File.Exists(prefabPath))
                    File.Delete(prefabPath);

                // Modular Avatar, EditorOnly stripping and other preprocess callbacks change a copy, so the scene stays as it was
                using var prepared = CharacterBuildPipeline.Prepare(character);
                PrefabUtility.SaveAsPrefabAsset(prepared.Character, prefabPath, out var succeededPack);
                if (!succeededPack) {
                    Debug.Log("Prefab creation failed");
                    return;
                }

                AssetBundleBuild bundleBuild = new AssetBundleBuild();
                AssetDatabase.RemoveUnusedAssetBundleNames();
                bundleBuild.assetBundleName = filename;
                bundleBuild.assetNames = new[] { prefabPath };
                bundleBuild.addressableNames = new[] { "VSM" };

                BuildAssetBundleOptions options = BuildAssetBundleOptions.ForceRebuildAssetBundle | BuildAssetBundleOptions.StrictMode;
                // if (character.GetComponentsInChildren<UnityEngine.Video.VideoPlayer>(true).Length > 0) {
                //     Debug.Log("VideoPlayer detected, using uncompressed asset bundle.");
                //     options = options | BuildAssetBundleOptions.UncompressedAssetBundle;
                // }
                BuildPipeline.BuildAssetBundles(Application.temporaryCachePath , new[] { bundleBuild }, options, buildTarget);
                if (File.Exists(fullpath))
                    File.Delete(fullpath);
                File.Move(Application.temporaryCachePath + "/" + filename, fullpath);
                thumbnails.ExportThumbnail(fullpath, previewThumbnail);
                string storedTitle = modelInfo.title?.Trim();
                ModelInfoUtility.WriteSidecar(fullpath, modelInfo.id,
                    string.IsNullOrEmpty(storedTitle) ? character.name : storedTitle, modelInfo.description?.Trim() ?? "", BundleRenderPipeline.Selected);

                complete = true;
                // The publish log under the button reports it from here, through to the upload finishing
                if (publisher != null) publisher.Publish(bundleName, fullpath, modelInfo.id);
                else EditorUtility.DisplayDialog("Export", "Export complete!", "OK");
            }
            catch (CharacterPreprocessException e)
            {
                Debug.LogException(e);
                failure = e.Message;
            }
            finally
            {
                try {
                    AssetDatabase.DeleteAsset(prefabPath);
                    if (File.Exists(prefabPath))
                        File.Delete(prefabPath);
                } 
                catch (Exception ex)
                {
                    Debug.LogError($"Failed to delete prefab at {prefabPath}: {ex.Message}");
                }

                if (!complete)
                    EditorUtility.DisplayDialog("Export", failure ?? "Export failed! See the console for details.", "OK");
            }
    }
}
