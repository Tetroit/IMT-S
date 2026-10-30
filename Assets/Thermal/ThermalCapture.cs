using System.Globalization;
using System.IO;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;


namespace IMT.Thermal
{
    /// <summary>
    /// Skeleton capture path: render to a single-channel float target, read it back, optionally
    /// write raw frames to disk. No physics here - this only proves the plumbing preserves values.
    ///
    /// Frames are written as little-endian float32, row-major, origin bottom-left, with a sidecar
    /// .json naming the dimensions. Raw rather than PNG or EXR because the consumer is numpy, and
    /// because any image encoder is another place values could be silently transformed.
    /// Each frame also gets its own frame_N.json: the camera's pose and projection, and the inputs the atmosphere's
    /// table in use was built for (step 4), so a capture can be predicted pixel by pixel. They are taken when the
    /// readback is requested, before that frame renders: the pixels are the frame before's, which in a still scene
    /// is the same.
    /// </summary>
    [RequireComponent(typeof(Camera))]
    [AddComponentMenu("Thermal/Thermal Capture")]
    public class ThermalCapture : MonoBehaviour
    {
        [Header("Target")]
        [SerializeField] int m_Width = 640;
        [SerializeField] int m_Height = 512;

        [Header("Capture")]
        [SerializeField] bool m_WriteFrames;
        [SerializeField] string m_OutputDirectory = "Captures";

        [Header("Verification")]
        [Tooltip("Log the centre pixel each frame. Use this to confirm a known constant survives.")]
        [SerializeField] bool m_LogCentrePixel = true;

        Camera m_Camera;
        ThermalEnvironment m_Environment;
        RenderTexture m_Target;
        float[] m_Pixels;
        bool m_ReadbackPending;
        int m_FrameIndex;

        /// <summary>The float target the camera renders into.</summary>
        public RenderTexture target { get { return m_Target; } }

        void OnEnable()
        {
            m_Camera = GetComponent<Camera>();
            m_Environment = FindAnyObjectByType<ThermalEnvironment>();

            m_Target = new RenderTexture(m_Width, m_Height, 24, RenderTextureFormat.RFloat,
                                         RenderTextureReadWrite.Linear)
            {
                name = "ThermalTarget",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
            };
            m_Target.Create();

            m_Camera.targetTexture = m_Target;

            // Post-processing would tonemap and colour-convert the values. There is no display
            // here; the buffer is a measurement.
            var data = m_Camera.GetUniversalAdditionalCameraData();
            if (data != null)
                data.renderPostProcessing = false;

            m_FrameIndex = 0;
        }

        void OnDisable()
        {
            if (m_Camera != null)
                m_Camera.targetTexture = null;

            if (m_Target != null)
            {
                m_Target.Release();
                if (Application.isPlaying) Destroy(m_Target); else DestroyImmediate(m_Target);
                m_Target = null;
            }
        }

        void LateUpdate()
        {
            if (m_Target == null || m_ReadbackPending || !SystemInfo.supportsAsyncGPUReadback)
                return;

            m_ReadbackPending = true;
            int index = m_FrameIndex++;
            string sidecar = m_WriteFrames ? Sidecar() : null;

            AsyncGPUReadback.Request(m_Target, 0, TextureFormat.RFloat, request =>
            {
                m_ReadbackPending = false;
                if (request.hasError)
                {
                    Debug.LogError("[Thermal] readback failed");
                    return;
                }

                NativeArray<float> data = request.GetData<float>();
                if (m_Pixels == null || m_Pixels.Length != data.Length)
                    m_Pixels = new float[data.Length];
                data.CopyTo(m_Pixels);

                OnFrame(index, sidecar);
            });
        }

        void OnFrame(int index, string sidecar)
        {
            if (m_LogCentrePixel)
            {
                int centre = (m_Height / 2) * m_Width + (m_Width / 2);
                Debug.Log(string.Format("[Thermal] frame {0}  centre = {1:R}", index, m_Pixels[centre]));
            }
            
            if (m_WriteFrames)
                WriteFrame(index, sidecar);
        }

        void WriteFrame(int index, string sidecar)
        {
            string dir = Path.Combine(Application.dataPath, "..", m_OutputDirectory);
            Directory.CreateDirectory(dir);

            if (index == 0)
            {
                File.WriteAllText(Path.Combine(dir, "meta.json"),
                    string.Format("{{\"width\":{0},\"height\":{1},\"dtype\":\"float32\",\"origin\":\"bottom-left\"}}",
                                  m_Width, m_Height));
            }

            var bytes = new byte[m_Pixels.Length * sizeof(float)];
            System.Buffer.BlockCopy(m_Pixels, 0, bytes, 0, bytes.Length);
            File.WriteAllBytes(Path.Combine(dir, string.Format("frame_{0:D6}.raw", index)), bytes);
            if (sidecar != null)
                File.WriteAllText(Path.Combine(dir, string.Format("frame_{0:D6}.json", index)), sidecar);
        }

        string Sidecar()
        {
            Transform t = m_Camera.transform;
            Vector3 p = t.position;
            Quaternion q = t.rotation;
            // The closing braces are added outside the format: "{10:R}}}" reads the escaped brace into the specifier.
            string camera = string.Format(CultureInfo.InvariantCulture,
                "\"camera\":{{\"position\":[{0:R},{1:R},{2:R}],\"rotation\":[{3:R},{4:R},{5:R},{6:R}],\"fov\":{7:R}," +
                "\"aspect\":{8:R},\"near\":{9:R},\"far\":{10:R}", p.x, p.y, p.z, q.x, q.y, q.z, q.w,
                m_Camera.fieldOfView, m_Camera.aspect, m_Camera.nearClipPlane, m_Camera.farClipPlane) + "}";
            ThermalAtmosphereTable table = m_Environment != null ? m_Environment.AtmosphereTable : null;
            string atmosphere = "null";
            if (table != null && table.HasTable)
            {
                ThermalAtmosphereTable.Inputs i = table.BuiltInputs;
                atmosphere = string.Format(CultureInfo.InvariantCulture,
                    "{{\"air_K\":{0:R},\"dew_point_K\":{1:R},\"altitude_m\":{2:R},\"ozone\":{3:R}," +
                    "\"camera_height_m\":{4:R},\"far_m\":{5:R}", i.air.airTemperatureK, i.air.dewPointK,
                    i.air.altitudeM, i.air.ozone, i.cameraHeightM, i.farM) + "}";
            }
            return "{" + camera + ",\"atmosphere_table\":" + atmosphere + "}";
        }
    }
}
