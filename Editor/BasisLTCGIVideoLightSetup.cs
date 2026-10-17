using pi.LTCGI;
using UnityEditor;
using UnityEngine;

namespace Basis.Integration.LTCGI.Editor
{
    /// <summary>
    /// One menu item that turns a selected Basis media player into an LTCGI video light: adds an
    /// LTCGI Controller if the scene has none, makes the player's screen a dynamic LTCGI screen,
    /// adds the video adapter, and bakes.
    /// </summary>
    internal static class BasisLTCGIVideoLightSetup
    {
        private const string MenuPath = "Basis/Tools/LTCGI/Set Up Video Light On Selected Player";
        private const string ControllerPrefab = "Packages/at.pimaker.ltcgi/LTCGI Controller.prefab";

        [MenuItem(MenuPath, true)]
        private static bool CanSetUp() => Selection.activeGameObject != null
            && Selection.activeGameObject.GetComponentInParent<BasisMediaPlayer>() != null;

        [MenuItem(MenuPath, false, 520)]
        private static void SetUp()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                Debug.LogError("LTCGI video light: exit Play mode first. LTCGI only bakes in edit mode.");
                return;
            }
            var selected = Selection.activeGameObject;
            var player = selected.GetComponentInParent<BasisMediaPlayer>();

            var screenRenderer = FindScreenRenderer(selected, player);
            if (screenRenderer == null)
            {
                Debug.LogWarning($"LTCGI video light: '{player.name}' has no screen renderer to light from.", player);
                return;
            }
            // LTCGI_Controller.UpdateMaterials throws on anything else, after the screen is already added.
            if (!screenRenderer.TryGetComponent(out MeshFilter filter) || filter.sharedMesh == null
                || (filter.sharedMesh.vertexCount != 3 && filter.sharedMesh.vertexCount != 4))
            {
                Debug.LogError($"LTCGI video light: '{screenRenderer.name}' needs a MeshFilter with a quad or triangle mesh to be an LTCGI screen.", screenRenderer);
                return;
            }

            // Outside desktop builds LTCGI allows one screen, and adding a second one throws partway through this setup.
            foreach (var other in Object.FindObjectsByType<LTCGI_Screen>(FindObjectsInactive.Exclude))
            {
                if (!other.enabled || other.gameObject == screenRenderer.gameObject) continue;
#if UNITY_STANDALONE
                Debug.LogWarning($"LTCGI video light: '{other.name}' is already an LTCGI screen. LTCGI allows one screen outside desktop builds, so this scene won't bake for Quest.", other);
                break;
#else
                Debug.LogError($"LTCGI video light: '{other.name}' is already an LTCGI screen, and LTCGI allows only one on this platform.", other);
                return;
#endif
            }

            var controller = Object.FindFirstObjectByType<LTCGI_Controller>();
            GameObject prefab = null;
            if (controller == null)
            {
                prefab = AssetDatabase.LoadAssetAtPath<GameObject>(ControllerPrefab);
                if (prefab == null || !prefab.TryGetComponent(out LTCGI_Controller _))
                {
                    Debug.LogError($"LTCGI video light: no LTCGI Controller prefab at '{ControllerPrefab}'. Add an LTCGI Controller to the scene, then run this again.");
                    return;
                }
            }

            Undo.SetCurrentGroupName("Set Up LTCGI Video Light");
            int group = Undo.GetCurrentGroup();

            if (controller == null)
            {
                var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, player.gameObject.scene);
                Undo.RegisterCreatedObjectUndo(instance, "Add LTCGI Controller");
                instance.TryGetComponent(out controller);
            }

            if (!screenRenderer.TryGetComponent(out LTCGI_Screen screen))
                screen = Undo.AddComponent<LTCGI_Screen>(screenRenderer.gameObject);
            Undo.RecordObject(screen, "Configure LTCGI Screen");
            screen.Dynamic = true;
            screen.ColorMode = ColorMode.Texture;
            screen.TextureIndex = 0; // [Live Video]

            if (!player.TryGetComponent(out BasisLTCGIVideoAdapter adapter))
                adapter = Undo.AddComponent<BasisLTCGIVideoAdapter>(player.gameObject);
            Undo.RecordObject(adapter, "Configure LTCGI Video Adapter");
            adapter.Player = player;

            if (LTCGI_Controller.Singleton == null) LTCGI_Controller.Singleton = controller;
            LTCGI_Controller.Singleton.UpdateMaterials();
            // The bake adds LTCGI_UdonAdapter to the controller, so it can only be wired up now.
            if (LTCGI_Controller.Singleton.TryGetComponent(out LTCGI_UdonAdapter udonAdapter))
            {
                Undo.RecordObject(adapter, "Configure LTCGI Video Adapter");
                adapter.Target = udonAdapter;
            }
            Undo.CollapseUndoOperations(group);
            Debug.Log($"LTCGI video light set up on '{player.name}'. Surfaces need an LTCGI shader, such as LTCGI/Lit URP, to receive it.", player);
        }

        // The screen can sit on the player or on a child, so look at the selection first, then
        // the player and its children.
        private static Renderer FindScreenRenderer(GameObject selected, BasisMediaPlayer player)
        {
            if (!selected.TryGetComponent(out BasisVideoMaterialOutput output))
                output = player.GetComponentInChildren<BasisVideoMaterialOutput>(true);
            if (output != null && output.TargetRenderer != null) return output.TargetRenderer;
            if (selected.TryGetComponent(out Renderer renderer) || player.TryGetComponent(out renderer)) return renderer;
            return null;
        }
    }
}
