using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Normalizes the bones of a model without touching its meshes.
///
/// The model gets a new armature whose bones all share the model root's orientation (identity local rotation, unit
/// scale). The original bones are reparented under their normalized counterparts, so the SkinnedMeshRenderers keep
/// skinning to the original bones. If the model has a humanoid Avatar, a new one is built that maps onto the normalized
/// bones.
///
/// This window and GameObject > Normalize Bones work on a copy in the scene. The character export does it to the
/// exported copy instead, after Modular Avatar has run, through <see cref="NormalizeInPlace"/>: normalizing in the scene
/// moves the bones that Modular Avatar's Merge Armature and Bone Proxy setups point at.
///
/// Same idea as Haï's Denormalized Avatar Exporter:
/// https://docs.hai-vr.dev/docs/products/denormalized-avatar-exporter
/// </summary>
public class BoneNormalizer : EditorWindow
{
    public enum BoneSelection
    {
        [Tooltip("Only the bones mapped in the humanoid Avatar. Hair, skirts, tails... keep their orientation.")]
        Humanoid,
        [Tooltip("Every bone used by a SkinnedMeshRenderer in the model.")]
        AllSkinnedBones,
    }

    const string NormalizedPrefix = "N_";
    const string ArmatureName = "NormalizedArmature";
    const string DefaultOutputFolder = "Assets/ZZZ_GeneratedAssets/NormalizedAvatars";

    const string ModularAvatarWarning =
        "This model uses Modular Avatar. Normalizing it in the scene moves the bones its Merge Armature and Bone Proxy " +
        "components point at, so they stop attaching. To export it normalized, turn on Normalize Bones on Export in the " +
        "Export Character Model window instead: that normalizes the exported copy after Modular Avatar has run.";

    GameObject target;
    BoneSelection selection = BoneSelection.Humanoid;
    bool resetToRestPose = false;
    bool hideOriginal = true;
    string outputFolder = DefaultOutputFolder;

    [MenuItem("BonBonStudio SDK/Helpers/Bone Normalizer")]
    static void Open()
    {
        var window = GetWindow<BoneNormalizer>("Bone Normalizer");
        if (Selection.activeGameObject != null) window.SetTarget(Selection.activeGameObject);
    }

    [MenuItem("GameObject/Normalize Bones", false, 49)]
    static void NormalizeSelected()
    {
        var go = Selection.activeGameObject;
        if (UsesModularAvatar(go) && !EditorUtility.DisplayDialog("Normalize Bones", ModularAvatarWarning, "Normalize Anyway", "Cancel"))
            return;
        NormalizeCopy(go);
    }

    [MenuItem("GameObject/Normalize Bones", true)]
    static bool NormalizeSelectedValidate()
    {
        var go = Selection.activeGameObject;
        return Validate(go, DefaultSelection(go)) == null;
    }

    void SetTarget(GameObject go)
    {
        target = go;
        selection = DefaultSelection(go);
    }

    void OnGUI()
    {
        EditorGUILayout.HelpBox(
            "Creates a copy of the model whose bones all have a neutral rotation. " +
            "The original bones are parented to the new ones, so meshes are not modified.",
            MessageType.Info);

        var newTarget = (GameObject)EditorGUILayout.ObjectField("Model", target, typeof(GameObject), true);
        if (newTarget != target) SetTarget(newTarget);
        if (UsesModularAvatar(target)) EditorGUILayout.HelpBox(ModularAvatarWarning, MessageType.Warning);
        selection = (BoneSelection)EditorGUILayout.EnumPopup("Bones", selection);
        resetToRestPose = EditorGUILayout.Toggle(
            new GUIContent("Reset To Rest Pose",
                "Restore the Avatar's stored skeleton pose before normalizing. Needs an Avatar. " +
                "Leave off if the scene model's bones were adjusted or the Avatar comes from a different model."),
            resetToRestPose);
        hideOriginal = EditorGUILayout.Toggle("Hide Original", hideOriginal);
        outputFolder = EditorGUILayout.TextField("Avatar Output Folder", outputFolder);

        string error = Validate(target, selection);
        if (error != null) EditorGUILayout.HelpBox(error, MessageType.Warning);

        using (new EditorGUI.DisabledScope(error != null))
        {
            if (GUILayout.Button("Normalize", GUILayout.Height(30)))
                Normalize(target, selection, resetToRestPose, hideOriginal, outputFolder);
        }
    }

    // Bones within this of the root's orientation, and this close to its scale, count as normalized.
    const float MaxAngle = 0.5f;
    const float MaxScaleDifference = 0.001f;

    /// <summary>
    /// Whether every bone mapped in the model's humanoid Avatar shares the model root's orientation and scale, as
    /// normalizing leaves them. Null when the model has no humanoid Avatar to check. <paramref name="off"/> counts the
    /// bones that don't, out of <paramref name="total"/>.
    /// </summary>
    public static bool? HumanoidBonesNormalized(GameObject go, out int off, out int total)
    {
        off = total = 0;
        var animator = go != null ? go.GetComponent<Animator>() : null;
        if (animator == null || !HasHumanoidAvatar(go)) return null;
        Transform root = go.transform;
        for (var bone = HumanBodyBones.Hips; bone < HumanBodyBones.LastBone; bone++)
        {
            var t = animator.GetBoneTransform(bone);
            if (t == null) continue;
            total++;
            Vector3 scale = t.lossyScale, rootScale = root.lossyScale;
            if (Quaternion.Angle(root.rotation, t.rotation) > MaxAngle
                || Mathf.Abs(scale.x - rootScale.x) > MaxScaleDifference
                || Mathf.Abs(scale.y - rootScale.y) > MaxScaleDifference
                || Mathf.Abs(scale.z - rootScale.z) > MaxScaleDifference)
                off++;
        }
        return off == 0;
    }

    /// <summary>Whether the model has Modular Avatar components, which normalizing in the scene breaks.</summary>
    public static bool UsesModularAvatar(GameObject go) =>
        go != null && go.GetComponentsInChildren<Component>(true).Any(c =>
            c != null && c.GetType().Assembly.GetName().Name.StartsWith("nadena.dev.modular-avatar", System.StringComparison.Ordinal));

    /// <summary>
    /// Normalizes a copy of <paramref name="go"/> with the default settings, as GameObject > Normalize Bones does, and
    /// hides the original. Returns the copy, or null on failure (logged).
    /// </summary>
    public static GameObject NormalizeCopy(GameObject go) => Normalize(go, DefaultSelection(go), false, true, DefaultOutputFolder);

    /// <summary>
    /// Normalizes <paramref name="model"/> itself with the default settings, for a copy that's about to be exported.
    /// A humanoid model's new Avatar is saved at <paramref name="avatarPath"/>. <paramref name="oldPaths"/> gives every
    /// transform that was in the model its path from the model's root before normalizing, so animations can be pointed
    /// at where the bones moved. Returns why it couldn't be done, or null.
    /// </summary>
    public static string NormalizeInPlace(GameObject model, string avatarPath, out Dictionary<Transform, string> oldPaths)
    {
        oldPaths = null;
        var selection = DefaultSelection(model);
        string error = model == null ? "Select a model." : ValidateSelection(model, selection);
        if (error != null) return error;
        return NormalizeHierarchy(model, model.name, selection, false, avatarPath, out oldPaths, out _);
    }

    static Avatar GetAvatar(GameObject go)
    {
        var animator = go != null ? go.GetComponent<Animator>() : null;
        return animator != null && animator.avatar != null && animator.avatar.isValid ? animator.avatar : null;
    }

    static bool HasHumanoidAvatar(GameObject go)
    {
        var avatar = GetAvatar(go);
        return avatar != null && avatar.isHuman;
    }

    static BoneSelection DefaultSelection(GameObject go)
    {
        return HasHumanoidAvatar(go) ? BoneSelection.Humanoid : BoneSelection.AllSkinnedBones;
    }

    static string Validate(GameObject go, BoneSelection selection)
    {
        if (go == null) return "Select a model.";
        if (EditorUtility.IsPersistent(go)) return "Use a model instance in the scene, not an asset.";
        return ValidateSelection(go, selection);
    }

    static string ValidateSelection(GameObject go, BoneSelection selection)
    {
        if (selection == BoneSelection.Humanoid && !HasHumanoidAvatar(go))
            return "Humanoid mode needs an Animator with a valid humanoid Avatar on the model root.";
        if (selection == BoneSelection.AllSkinnedBones && go.GetComponentInChildren<SkinnedMeshRenderer>(true) == null)
            return "The model has no SkinnedMeshRenderer.";
        return null;
    }

    static GameObject Normalize(GameObject source, BoneSelection selection, bool resetToRestPose, bool hideOriginal, string outputFolder)
    {
        string error = Validate(source, selection);
        if (error != null)
        {
            Debug.LogError($"[BoneNormalizer] {error}", source);
            return null;
        }

        // Work on a copy so the original stays intact.
        var copy = Instantiate(source, source.transform.parent);
        copy.name = source.name + " (Normalized)";
        copy.transform.SetSiblingIndex(source.transform.GetSiblingIndex() + 1);
        Undo.RegisterCreatedObjectUndo(copy, "Normalize Bones");

        string avatarPath = null;
        if (HasHumanoidAvatar(source))
        {
            EnsureFolder(outputFolder);
            avatarPath = AssetDatabase.GenerateUniqueAssetPath($"{outputFolder}/{source.name} Normalized Avatar.asset");
        }
        error = NormalizeHierarchy(copy, source.name, selection, resetToRestPose, avatarPath, out _, out int count);
        if (error != null)
        {
            Debug.LogError($"[BoneNormalizer] {error}", source);
            DestroyImmediate(copy);
            return null;
        }

        if (hideOriginal)
        {
            Undo.RecordObject(source, "Normalize Bones");
            source.SetActive(false);
        }

        Selection.activeGameObject = copy;
        Debug.Log($"[BoneNormalizer] Normalized {count} bones." + (avatarPath != null ? $" Avatar saved to {avatarPath}" : ""), copy);
        return copy;
    }

    // Normalizes the model's own hierarchy. avatarPath is where a humanoid model's new Avatar is saved. Returns why it
    // failed, or null; on failure the model may be half done, so callers work on a copy.
    static string NormalizeHierarchy(GameObject model, string displayName, BoneSelection selection, bool resetToRestPose,
        string avatarPath, out Dictionary<Transform, string> oldPaths, out int count)
    {
        oldPaths = null;
        count = 0;
        var sourceAvatar = GetAvatar(model);
        bool isHuman = sourceAvatar != null && sourceAvatar.isHuman;
        HumanDescription description = sourceAvatar != null ? sourceAvatar.humanDescription : default;
        Transform root = model.transform;

        // Unbind the Animator before touching the hierarchy. Otherwise rebinding it later restores the
        // local poses it recorded for the old hierarchy, which no longer match once bones are reparented.
        var animator = model.GetComponent<Animator>();
        if (animator != null) animator.avatar = null;

        var transformsByName = new Dictionary<string, Transform>();
        foreach (var t in root.GetComponentsInChildren<Transform>(true))
        {
            if (t != root && !transformsByName.ContainsKey(t.name)) transformsByName.Add(t.name, t);
        }

        if (resetToRestPose && description.skeleton != null)
        {
            foreach (var bone in description.skeleton)
            {
                if (!transformsByName.TryGetValue(bone.name, out var t)) continue;
                t.localPosition = bone.position;
                t.localRotation = bone.rotation;
                t.localScale = bone.scale;
            }
        }

        // Pick the original bones to normalize and the name of their normalized counterpart.
        var normalizedNames = new Dictionary<Transform, string>();
        if (selection == BoneSelection.Humanoid)
        {
            foreach (var human in description.human)
            {
                if (!transformsByName.TryGetValue(human.boneName, out var t))
                    return $"Bone '{human.boneName}' ({human.humanName}) not found in the model.";
                normalizedNames[t] = NormalizedPrefix + human.humanName.Replace(" ", "");
            }
        }
        else
        {
            foreach (var renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                foreach (var t in renderer.bones.Append(renderer.rootBone))
                {
                    if (t != null && t != root && t.IsChildOf(root)) normalizedNames[t] = NormalizedPrefix + t.name;
                }
            }
        }
        if (normalizedNames.Count == 0) return "No bones to normalize.";

        oldPaths = root.GetComponentsInChildren<Transform>(true).ToDictionary(t => t, t => AnimationUtility.CalculateTransformPath(t, root));

        var armature = new GameObject(UniqueName(ArmatureName, transformsByName)).transform;
        armature.SetParent(root, false);

        // Create normalized bones parent-first so each one can be parented as it is created.
        var normalized = new Dictionary<Transform, Transform>();
        foreach (var original in normalizedNames.Keys.OrderBy(Depth))
        {
            var bone = new GameObject(UniqueName(normalizedNames[original], transformsByName)).transform;
            bone.SetParent(FindNormalizedParent(original, root, normalized) ?? armature, false);
            bone.position = original.position;
            bone.localRotation = Quaternion.identity;
            bone.localScale = Vector3.one;
            normalized[original] = bone;
        }

        // Reparent the original bones under their normalized counterparts, keeping their world pose,
        // so skinning (which references the original bones) is unchanged.
        foreach (var original in normalized.Keys)
            original.SetParent(normalized[original], true);
        count = normalized.Count;

        Avatar avatar = sourceAvatar;
        if (isHuman)
        {
            avatar = BuildHumanoidAvatar(model, displayName, sourceAvatar, description, normalized, avatarPath);
            if (avatar == null) return "Failed to build a valid humanoid avatar.";
        }

        if (animator != null)
        {
            // Rebinding can still write the Animator's recorded pose, so put the normalized pose back afterwards.
            var transforms = root.GetComponentsInChildren<Transform>(true);
            var poses = transforms.Select(t => (t.localPosition, t.localRotation, t.localScale)).ToArray();
            animator.avatar = avatar;
            animator.Rebind();
            for (int i = 0; i < transforms.Length; i++)
            {
                transforms[i].localPosition = poses[i].localPosition;
                transforms[i].localRotation = poses[i].localRotation;
                transforms[i].localScale = poses[i].localScale;
            }
        }
        return null;
    }

    // Builds a humanoid avatar that targets the normalized bones and saves it as an asset at path.
    static Avatar BuildHumanoidAvatar(GameObject model, string displayName, Avatar sourceAvatar,
        HumanDescription description, Dictionary<Transform, Transform> normalized, string path)
    {
        var originalToNormalizedName = normalized.ToDictionary(kv => kv.Key.name, kv => kv.Value.name);
        description.human = description.human.Select(h =>
        {
            // Humanoid bones not normalized (possible in AllSkinnedBones mode) keep pointing at the original.
            if (originalToNormalizedName.TryGetValue(h.boneName, out var name)) h.boneName = name;
            return h;
        }).ToArray();
        description.skeleton = model.GetComponentsInChildren<Transform>(true).Select(t => new SkeletonBone
        {
            name = t.name,
            position = t.localPosition,
            rotation = t.localRotation,
            scale = t.localScale,
        }).ToArray();

        var avatar = AvatarBuilder.BuildHumanAvatar(model, description);
        if (avatar == null || !avatar.isValid || !avatar.isHuman) return null;
        avatar.name = sourceAvatar.name + " (Normalized)";

        EnsureFolder(Path.GetDirectoryName(path).Replace('\\', '/'));
        AssetDatabase.CreateAsset(avatar, path);
        AssetDatabase.SaveAssets();
        return avatar;
    }

    // The normalized parent is the normalized bone of the closest normalized ancestor.
    static Transform FindNormalizedParent(Transform original, Transform root, Dictionary<Transform, Transform> normalized)
    {
        for (var p = original.parent; p != null && p != root; p = p.parent)
        {
            if (normalized.TryGetValue(p, out var n)) return n;
        }
        return null;
    }

    static int Depth(Transform t)
    {
        int depth = 0;
        for (; t.parent != null; t = t.parent) depth++;
        return depth;
    }

    // Avatar bones are matched by name, so every new transform needs a name unique in the hierarchy.
    static string UniqueName(string name, Dictionary<string, Transform> existing)
    {
        string unique = name;
        for (int i = 1; existing.ContainsKey(unique); i++) unique = $"{name}_{i}";
        existing[unique] = null;
        return unique;
    }

    /// <summary>Creates the project folder and any missing parents.</summary>
    public static void EnsureFolder(string folder)
    {
        if (AssetDatabase.IsValidFolder(folder)) return;
        string parent = Path.GetDirectoryName(folder).Replace('\\', '/');
        EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, Path.GetFileName(folder));
    }
}
