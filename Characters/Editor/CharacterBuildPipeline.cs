using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace BonBonStudio.SDK.Characters
{
    // Runs on a copy of the character just before it's bundled, like VRChat's IVRCSDKPreprocessAvatarCallback, so tools can
    // rewrite what gets exported without touching the scene. Lowest callbackOrder runs first; return false to cancel the export.
    public interface IPreprocessCharacterCallback : IOrderedCallback
    {
        bool OnPreprocessCharacter(GameObject character);
    }

    // Thrown when a callback cancels the export or fails; the message says which one
    public class CharacterPreprocessException : Exception
    {
        public CharacterPreprocessException(string message, Exception inner = null) : base(message, inner) { }
    }

    public static class CharacterBuildPipeline
    {
        // The copy lives in a preview scene, which closing destroys along with anything the callbacks added to it
        public sealed class PreparedCharacter : IDisposable
        {
            public GameObject Character { get; }
            readonly Scene scene;
            readonly List<Action> cleanups;

            internal PreparedCharacter(GameObject character, Scene scene, List<Action> cleanups)
            {
                Character = character;
                this.scene = scene;
                this.cleanups = cleanups;
            }

            // Call after the bundle is built: cleanups delete assets the bundle was made from
            public void Dispose()
            {
                if (scene.IsValid()) EditorSceneManager.ClosePreviewScene(scene);
                foreach (var cleanup in cleanups)
                {
                    try { cleanup(); }
                    catch (Exception e) { Debug.LogException(e); }
                }
                cleanups.Clear();
            }
        }

        // Callbacks ask for cleanup work here, such as deleting generated assets, which can only happen after the build
        static List<Action> pendingCleanups;
        public static void AfterBuild(Action cleanup)
        {
            if (pendingCleanups == null) throw new InvalidOperationException("AfterBuild only works inside OnPreprocessCharacter.");
            pendingCleanups.Add(cleanup);
        }

        public static PreparedCharacter Prepare(GameObject character)
        {
            var scene = EditorSceneManager.NewPreviewScene();
            var cleanups = new List<Action>();
            var prepared = new PreparedCharacter(null, scene, cleanups);
            try
            {
                var copy = Object.Instantiate(character);
                copy.name = character.name;
                SceneManager.MoveGameObjectToScene(copy, scene);
                prepared = new PreparedCharacter(copy, scene, cleanups);

                pendingCleanups = cleanups;
                foreach (var callback in Callbacks())
                {
                    var name = callback.GetType().Name;
                    bool keepGoing;
                    try { keepGoing = callback.OnPreprocessCharacter(copy); }
                    catch (Exception e) { throw new CharacterPreprocessException($"{name} failed while preparing {character.name}: {e.Message}", e); }
                    if (!keepGoing) throw new CharacterPreprocessException($"{name} cancelled the export of {character.name}.");
                    if (!copy) throw new CharacterPreprocessException($"{name} destroyed {character.name}'s copy.");
                }
                return prepared;
            }
            catch
            {
                prepared.Dispose();
                throw;
            }
            finally
            {
                pendingCleanups = null;
            }
        }

        static IEnumerable<IPreprocessCharacterCallback> Callbacks() =>
            TypeCache.GetTypesDerivedFrom<IPreprocessCharacterCallback>()
                .Where(type => !type.IsAbstract && !type.IsGenericTypeDefinition && type.GetConstructor(Type.EmptyTypes) != null)
                .Select(type => (IPreprocessCharacterCallback)Activator.CreateInstance(type))
                .OrderBy(callback => callback.callbackOrder)
                .ToList();
    }

    // Modular Avatar and other NDMF plugins, applied the way VRChat's build would apply them. Found by reflection so
    // the SDK neither needs NDMF installed nor breaks when it isn't
    internal class NdmfCharacterCallback : IPreprocessCharacterCallback
    {
        // NDMF's own VRChat hook runs at -11000, before VRCFury at -10000
        public int callbackOrder => -11000;

        static readonly Type Processor = Type.GetType("nadena.dev.ndmf.AvatarProcessor, nadena.dev.ndmf");
        static readonly Type AvatarRoot = Type.GetType("nadena.dev.ndmf.runtime.components.NDMFAvatarRoot, nadena.dev.ndmf.runtime");

        public bool OnPreprocessCharacter(GameObject character)
        {
            if (Processor == null) return true;
            var canProcess = Processor.GetMethod("CanProcessObject", new[] { typeof(GameObject) });
            var process = Processor.GetMethod("ProcessAvatar", new[] { typeof(GameObject) });
            var clean = Processor.GetMethod("CleanTemporaryAssets", Type.EmptyTypes);
            if (process == null || clean == null)
            {
                Debug.LogWarning("This NDMF version doesn't have AvatarProcessor.ProcessAvatar; exporting without NDMF processing.");
                return true;
            }

            // Without the VRChat SDK, NDMF only treats objects with NDMFAvatarRoot as avatars; it's editor-only, so it isn't exported
            if (canProcess != null && !(bool)canProcess.Invoke(null, new object[] { character }) && AvatarRoot != null)
                character.AddComponent(AvatarRoot);

            // NDMF writes the merged animators, meshes and materials as temporary assets, which the bundle needs until it's built
            CharacterBuildPipeline.AfterBuild(() => clean.Invoke(null, null));
            try { process.Invoke(null, new object[] { character }); }
            catch (TargetInvocationException e) { throw e.InnerException ?? e; }
            return true;
        }
    }

    // Poiyomi's own lock-on-upload hook only compiles with the VRChat SDK, so lock its materials here instead. Found by
    // reflection because Poiyomi lives in Assets, where a package assembly can't reference it
    internal class PoiyomiLockCallback : IPreprocessCharacterCallback
    {
        // Where Poiyomi's LockMaterialsOnUpload runs, after NDMF has made any material copies
        public int callbackOrder => 100;

        static readonly Type Optimizer = Type.GetType("Thry.ThryEditor.ShaderOptimizer, ThryAssemblyDefinition");

        public bool OnPreprocessCharacter(GameObject character)
        {
            var progressBar = Optimizer?.GetNestedType("ProgressBar");
            var lockMaterials = progressBar == null ? null : Optimizer.GetMethod("LockMaterials", new[] { typeof(IEnumerable<Material>), progressBar });
            if (lockMaterials == null) return true;

            // Materials an animation swaps in are locked too, as Poiyomi's hook does for the avatar's animators
            var materials = character.GetComponentsInChildren<Renderer>(true).SelectMany(r => r.sharedMaterials)
                .Concat(character.GetComponentsInChildren<Animator>(true)
                    .Where(a => a.runtimeAnimatorController)
                    .SelectMany(a => a.runtimeAnimatorController.animationClips)
                    .Distinct()
                    .SelectMany(clip => AnimationUtility.GetObjectReferenceCurveBindings(clip)
                        .Where(b => typeof(Renderer).IsAssignableFrom(b.type) && b.propertyName.StartsWith("m_Materials"))
                        .SelectMany(b => AnimationUtility.GetObjectReferenceCurve(clip, b))
                        .Select(key => key.value as Material)))
                .Where(m => m)
                .Distinct()
                .ToList();
            if (materials.Count == 0) return true;

            object result;
            try { result = lockMaterials.Invoke(null, new[] { materials, Enum.Parse(progressBar, "Uncancellable") }); }
            catch (TargetInvocationException e) { throw e.InnerException ?? e; }
            if (result is false) throw new CharacterPreprocessException($"Poiyomi couldn't lock {character.name}'s materials. See the console for details.");
            return true;
        }
    }

    // Unity strips EditorOnly objects from scenes in player builds but not from bundled prefabs
    internal class StripEditorOnlyCallback : IPreprocessCharacterCallback
    {
        // Where VRChat's RemoveAvatarEditorOnly runs
        public int callbackOrder => -1024;

        public bool OnPreprocessCharacter(GameObject character)
        {
            if (character.CompareTag("EditorOnly")) throw new CharacterPreprocessException($"{character.name} is tagged EditorOnly, so nothing would be exported.");
            foreach (var t in character.GetComponentsInChildren<Transform>(true))
            {
                if (t && t.CompareTag("EditorOnly")) Object.DestroyImmediate(t.gameObject);
            }
            return true;
        }
    }
}
