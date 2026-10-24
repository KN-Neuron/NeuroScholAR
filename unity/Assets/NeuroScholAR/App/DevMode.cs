using UnityEngine;

namespace NeuroScholAR.App
{
    /// <summary>
    /// One switch for every debugging aid (setup screen checkbox, remembered across launches): AR planes drawn by
    /// classification, gaze and fingertip markers, and the head-locked stats and log overlay (<see cref="UI.DevOverlay"/>).
    /// Components read <see cref="Enabled"/> themselves, so turning it on needs no restart.
    /// </summary>
    public static class DevMode
    {
        private const string Key = "dev_mode";
        private static bool? _enabled;

        public static bool Enabled
        {
            get => _enabled ??= PlayerPrefs.GetInt(Key, 0) == 1;
            set
            {
                _enabled = value;
                PlayerPrefs.SetInt(Key, value ? 1 : 0);
                PlayerPrefs.Save();
            }
        }
    }

    /// <summary>See-through materials for debug helpers (same blend setup as the hand meshes).</summary>
    public static class DevMaterials
    {
        public static Material Transparent(Color color)
        {
            var material = new Material(Shader.Find("NeuroScholAR/Simple")) { color = color };
            material.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
            material.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            material.SetFloat("_ZWrite", 0);
            material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
            return material;
        }

        /// <summary>Small sphere marker without a collider (it must never catch gaze rays or fingertips).</summary>
        public static Transform Marker(string name, Color color, float size)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.name = name;
            Object.Destroy(go.GetComponent<Collider>());
            go.GetComponent<Renderer>().sharedMaterial = Transparent(color);
            go.transform.localScale = Vector3.one * size;
            go.SetActive(false);
            return go.transform;
        }
    }
}
