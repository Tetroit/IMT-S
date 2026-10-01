using UnityEngine;
using UnityEngine.Rendering;

namespace IMT.Thermal
{
    /// <summary>
    /// Debug view of the radiance target: a fixed linear stretch to grey, drawn in the Game view.
    ///
    /// Display only. It reads <see cref="ThermalCapture.target"/> and writes to its own texture, so
    /// the float buffer stays a measurement. Limits are typed, not derived from the frame - an
    /// auto-stretch would map every frame to fill the range, which makes two frames with different
    /// content look identical and hides exactly the changes this is here to show.
    /// </summary>
    [ExecuteAlways]
    [AddComponentMenu("Thermal/Thermal Preview")]
    public class ThermalPreview : MonoBehaviour
    {
        [Header("Source")]
        [Tooltip("Leave empty to use the ThermalCapture on this object, or the first one in the scene.")]
        [SerializeField] ThermalCapture m_Capture;

        [Header("Mapping")]
        [Tooltip("Radiance shown as black. Sky at 220 K is about 15.")]
        [SerializeField] float m_Low = 10.0f;
        [Tooltip("Radiance shown as white. A sunlit high-emissivity face is about 61.")]
        [SerializeField] float m_High = 65.0f;
        [Tooltip("Perceptual only. Off is honest, on is easier to read.")]
        [SerializeField] bool m_DisplayGamma = true;

        [Header("Display")]
        [SerializeField] bool m_Show = true;
        [SerializeField] Layout m_Layout = Layout.Fill;
        [Tooltip("Corner layout only.")]
        [Range(0.1f, 2.0f)]
        [SerializeField] float m_Scale = 0.75f;
        [SerializeField] bool m_ShowLimits = true;

        public enum Layout
        {
            /// <summary>Whole Game view, aspect preserved. Letterboxes unless the Game view is 5:4.</summary>
            Fill,
            /// <summary>Small overlay in the top-left, leaving the rest of the Game view visible.</summary>
            Corner,
        }

        [Header("Fit")]
        [Tooltip("Percentile clipped at each end by 'Fit Limits To Frame'. 0 gives exact min and max.")]
        [Range(0.0f, 10.0f)]
        [SerializeField] float m_FitPercentile = 1.0f;

        Material m_Material;
        RenderTexture m_Preview;

        static readonly int k_Low = Shader.PropertyToID("_Low");
        static readonly int k_High = Shader.PropertyToID("_High");
        static readonly int k_Gamma = Shader.PropertyToID("_Gamma");

        /// <summary>The grey preview texture, if anything else wants to display it.</summary>
        public RenderTexture preview { get { return m_Preview; } }

        void OnEnable()
        {
            if (m_Capture == null)
                m_Capture = GetComponent<ThermalCapture>();
            if (m_Capture == null)
                m_Capture = FindFirstObjectByType<ThermalCapture>();

            Shader shader = Shader.Find("Thermal/Preview");
            if (shader == null)
            {
                Debug.LogError("[Thermal] shader 'Thermal/Preview' not found");
                enabled = false;
                return;
            }

            m_Material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
        }

        void OnDisable()
        {
            Release();
        }

        void Release()
        {
            if (m_Preview != null)
            {
                m_Preview.Release();
                DestroyImmediate(m_Preview);
                m_Preview = null;
            }

            if (m_Material != null)
            {
                DestroyImmediate(m_Material);
                m_Material = null;
            }
        }

        void LateUpdate()
        {
            RenderTexture src = m_Capture != null ? m_Capture.target : null;
            if (src == null || m_Material == null)
                return;

            if (m_Preview == null || m_Preview.width != src.width || m_Preview.height != src.height)
            {
                if (m_Preview != null)
                {
                    m_Preview.Release();
                    DestroyImmediate(m_Preview);
                }

                // Linear, so what the shader writes is what gets drawn. The gamma toggle above is
                // the only thing that should ever change the look.
                m_Preview = new RenderTexture(src.width, src.height, 0, RenderTextureFormat.ARGB32,
                                              RenderTextureReadWrite.Linear)
                {
                    name = "ThermalPreview",
                    filterMode = FilterMode.Point,
                    wrapMode = TextureWrapMode.Clamp,
                };
                m_Preview.Create();
            }

            m_Material.SetFloat(k_Low, m_Low);
            m_Material.SetFloat(k_High, m_High);
            m_Material.SetFloat(k_Gamma, m_DisplayGamma ? 1.0f : 0.0f);

            Graphics.Blit(src, m_Preview, m_Material);
        }

        GUIStyle m_LabelStyle;

        void OnGUI()
        {
            if (!m_Show || m_Preview == null)
                return;

            Rect area;
            if (m_Layout == Layout.Fill)
            {
                area = new Rect(0.0f, 0.0f, Screen.width, Screen.height);

                // Nothing renders to the screen - the camera has a targetTexture - so without this
                // the letterbox bars show whatever was last in the buffer.
                GUI.DrawTexture(area, Texture2D.blackTexture, ScaleMode.StretchToFill, false);
            }
            else
            {
                area = new Rect(8.0f, 8.0f, m_Preview.width * m_Scale, m_Preview.height * m_Scale);
            }

            // ScaleToFit, never StretchToFill: a distorted aspect ratio would misrepresent every
            // spatial pattern this view exists to show.
            GUI.DrawTexture(area, m_Preview, ScaleMode.ScaleToFit, false);

            if (!m_ShowLimits)
                return;

            if (m_LabelStyle == null)
            {
                m_LabelStyle = new GUIStyle(GUI.skin.label) { fontSize = 11 };
                m_LabelStyle.normal.textColor = Color.white;
            }

            string text = string.Format("black {0:0.###}   white {1:0.###}   W m-2 sr-1{2}",
                                        m_Low, m_High, m_DisplayGamma ? "   (gamma)" : "");
            Rect label = m_Layout == Layout.Fill
                ? new Rect(10.0f, Screen.height - 22.0f, 400.0f, 20.0f)
                : new Rect(12.0f, area.yMax + 2.0f, 400.0f, 20.0f);

            GUI.Label(label, text, m_LabelStyle);
        }

        /// <summary>
        /// Set the limits from the frame currently on the GPU. A one-off convenience for finding
        /// sensible numbers - it is not, and should not become, a per-frame auto-stretch.
        /// </summary>
        [ContextMenu("Fit Limits To Frame")]
        void FitLimitsToFrame()
        {
            RenderTexture src = m_Capture != null ? m_Capture.target : null;
            if (src == null)
            {
                Debug.LogWarning("[Thermal] no capture target to fit against");
                return;
            }

            var request = AsyncGPUReadback.Request(src, 0, TextureFormat.RFloat);
            request.WaitForCompletion();
            if (request.hasError)
            {
                Debug.LogError("[Thermal] preview readback failed");
                return;
            }

            var data = request.GetData<float>();
            var values = new float[data.Length];
            data.CopyTo(values);
            System.Array.Sort(values);

            // A single hot pixel would otherwise set the whole white point, so clip a percentile by
            // default. The same reason analyse_frames.py stretches 1-99 rather than min-max.
            int drop = Mathf.Clamp(Mathf.RoundToInt(values.Length * m_FitPercentile / 100.0f),
                                   0, values.Length / 2 - 1);
            m_Low = values[drop];
            m_High = values[values.Length - 1 - drop];

            Debug.Log(string.Format("[Thermal] preview limits {0:R} .. {1:R}  " +
                                    "(clipping {2:0.##}% each end; frame min {3:R}, max {4:R})",
                                    m_Low, m_High, m_FitPercentile,
                                    values[0], values[values.Length - 1]));
        }
    }
}
