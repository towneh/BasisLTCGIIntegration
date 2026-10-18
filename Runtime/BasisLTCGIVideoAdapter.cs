using Basis.EventDriver;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;

namespace Basis.Integration.LTCGI
{
    /// <summary>
    /// Feeds a <see cref="BasisMediaPlayer"/>'s picture into LTCGI's video texture so a
    /// dynamic LTCGI screen casts light that matches what the player shows.
    /// </summary>
    /// <remarks>
    /// The player hands out <see cref="BasisMediaPlayer.OutputTexture"/>, whose identity
    /// changes on load, resize and reconnect and whose row order depends on the platform.
    /// LTCGI samples one shared texture with mip levels standing in for blur, so this
    /// owns a mipmapped RenderTexture with a stable handle, copies each presented frame
    /// into it with the flips and projection of the screen's
    /// <see cref="BasisVideoMaterialOutput"/>, and binds it to LTCGI. Without LTCGI's blur
    /// chain (Fast Sampling, which LTCGI forces outside desktop builds) the texture is bound
    /// to the global LTCGI reads directly.
    /// </remarks>
    [AddComponentMenu("Basis/Basis LTCGI Video Adapter")]
    [DisallowMultipleComponent]
    [AutoStaticsCleanup]
    public sealed partial class BasisLTCGIVideoAdapter : MonoBehaviour
    {
        [Tooltip("Player to read frames from. If unassigned, GetComponentInParent<BasisMediaPlayer>() is used.")]
        public BasisMediaPlayer Player;

        [Tooltip("LTCGI runtime adapter to feed. If unassigned, the first one in the scene is used.")]
        public LTCGI_UdonAdapter Target;

        [Tooltip("The screen output the light matches: its flips, projection and placeholder. If unassigned, the first BasisVideoMaterialOutput under the player is used.")]
        public BasisVideoMaterialOutput Output;

        [Tooltip("Longest side of the texture handed to LTCGI, in pixels. Larger sources are scaled down. Area light and glossy reflections rarely need more.")]
        [Min(16)] public int MaxResolution = 1024;

        [Tooltip("Parts of the picture to hand to LTCGI, in the UVs the screen shows it with, side by side in this order. One part of (0, 0, 1, 1) is the whole frame. With n parts, part i fills u from i/n to (i+1)/n of the texture LTCGI reads, so the LTCGI screen for that part takes those UVs.")]
        public Rect[] SourceRects = { new Rect(0f, 0f, 1f, 1f) };

        private BasisMediaPlayer subscribed;
        private BasisVideoMaterialOutput output;
        // LTCGI has one live video input, so the first adapter enabled takes it and any other
        // disables itself.
        private static BasisLTCGIVideoAdapter active;
        private RenderTexture rt;
        // One part's column, copied into rt when there's more than one part.
        private RenderTexture partRT;
        private Texture source;
        private long blittedFrame = -1;
        private Rect[] blittedRects;
        private Texture bound;
        private bool ended;

        private static readonly int IdMainTex = Shader.PropertyToID("_MainTex");
        private static readonly int IdLod0 = Shader.PropertyToID("_Udon_LTCGI_Texture_LOD0");
        private static readonly int[] LodGlobalIds =
        {
            Shader.PropertyToID("_Udon_LTCGI_Texture_LOD1"),
            Shader.PropertyToID("_Udon_LTCGI_Texture_LOD2"),
            Shader.PropertyToID("_Udon_LTCGI_Texture_LOD3"),
        };

        private bool HasBlurChain => Target != null && Target.BlurCRTInput != null;

        private void Reset()
        {
            if (Player == null) Player = GetComponentInParent<BasisMediaPlayer>();
        }

        private void OnEnable()
        {
            if (active != null && active != this)
            {
                BasisDebug.LogWarning($"BasisLTCGIVideoAdapter on '{name}': '{active.name}' already feeds LTCGI's video input. Disable it first to light from this player.", BasisDebug.LogTag.Video);
                enabled = false;
                return;
            }
            if (Player == null) Player = GetComponentInParent<BasisMediaPlayer>();
            if (Player == null)
            {
                BasisDebug.LogWarning("BasisLTCGIVideoAdapter: no BasisMediaPlayer found; nothing to feed LTCGI.", BasisDebug.LogTag.Video);
                enabled = false;
                return;
            }
            if (Target == null) Target = FindFirstObjectByType<LTCGI_UdonAdapter>();
            if (Target == null)
            {
                BasisDebug.LogWarning("BasisLTCGIVideoAdapter: no LTCGI_UdonAdapter in the scene. Bake an LTCGI controller with at least one dynamic screen.", BasisDebug.LogTag.Video);
                enabled = false;
                return;
            }

            output = Output != null ? Output : Player.GetComponentInChildren<BasisVideoMaterialOutput>(true);
            if (output == null)
                BasisDebug.LogWarning($"BasisLTCGIVideoAdapter on '{name}': no BasisVideoMaterialOutput under the player. Using Mono projection, no flips and black when there's no picture.", BasisDebug.LogTag.Video);

            active = this;
            subscribed = Player;
            subscribed.OnOutputTextureChanged += HandleTextureChanged;
            subscribed.OnEnded += HandleEnded;
            BasisEventDriver.OnLateUpdate += SimulateLateUpdate;
            HandleTextureChanged(subscribed.OutputTexture);
        }

        private void OnDisable()
        {
            if (active == this) active = null;
            BasisEventDriver.OnLateUpdate -= SimulateLateUpdate;
            if (subscribed != null)
            {
                subscribed.OnOutputTextureChanged -= HandleTextureChanged;
                subscribed.OnEnded -= HandleEnded;
            }
            subscribed = null;
            output = null;
            source = null;
            blittedFrame = -1;
            ended = false;
            // Hand LTCGI back its own texture before releasing ours, so nothing samples a
            // destroyed RenderTexture.
            if (bound != null) Bind(ControllerVideoTexture);
            bound = null;
            ReleaseRT();
        }

        // The video texture the LTCGI controller was baked with, handed back when the adapter stops.
        private Texture ControllerVideoTexture =>
            Target != null && Target._LTCGI_LODs != null && Target._LTCGI_LODs.Length > 0 ? Target._LTCGI_LODs[0] : null;

        private void HandleTextureChanged(Texture texture)
        {
            source = texture;
            ended = false;
            if (texture == null)
            {
                ShowNoPicture();
                return;
            }
            EnsureRT(texture.width, texture.height);
            if (rt == null) return;
            Blit();
            Bind(rt);
        }

        // As the screen output does: a looping player starts again on the same texture, so the
        // last frame holds; otherwise the light goes to what the screen shows with no picture.
        private void HandleEnded()
        {
            if (subscribed == null || subscribed.Loop) return;
            ended = true;
            ShowNoPicture();
        }

        // The screen output's placeholder, or black where it has none, as the screen shows
        // before the first frame and after the end.
        private void ShowNoPicture()
        {
            Texture placeholder = output != null ? output.PlaceholderTexture : null;
            if (placeholder == null)
            {
                Bind(Texture2D.blackTexture);
                return;
            }
            EnsureRT(placeholder.width, placeholder.height);
            if (rt == null) return;
            CopyParts(placeholder, Vector2.one, Vector2.zero);
            Bind(rt);
        }

        private void SimulateLateUpdate()
        {
            if (source != null && !ended && PartsChanged)
            {
                HandleTextureChanged(source);
                return;
            }
            if (ended && PartsChanged) ShowNoPicture();
            // Cover art and paused video present no new frames; there is nothing to copy. After
            // the end, only a frame presented once the player leaves Ended brings the video back,
            // at the video's size rather than the placeholder's. The poll that reports the end
            // can also count the last frames before it, so frames counted while still Ended are
            // taken as seen.
            if (ended && subscribed.State == BmState.Ended)
                blittedFrame = subscribed.PresentedFrameCount;
            else if (source != null && subscribed.PresentedFrameCount != blittedFrame)
            {
                if (ended) EnsureRT(source.width, source.height);
                if (rt != null)
                {
                    ended = false;
                    Blit();
                    if (bound != rt) Bind(rt);
                }
            }
            EnsureBound();
        }

        private void Bind(Texture texture)
        {
            if (Target == null) return;
            bound = texture;
            if (HasBlurChain)
                Target._SetVideoTexture(texture);
            else
                Shader.SetGlobalTexture(IdLod0, texture);
        }

        // LTCGI's start-up overwrites LOD0 with the baked video texture, and an external reset
        // (asset unload, render pipeline rebuild, a Desktop/VR switch) can clear the bindings
        // with nothing to put them back. Check them each frame and re-assert what was dropped.
        // Blur-chain shaders sample LOD0 directly too.
        private void EnsureBound()
        {
            if (Target == null || bound == null) return;
            bool blurChain = HasBlurChain;
            bool dropped = Shader.GetGlobalTexture(IdLod0) != bound
                || (blurChain && Target.BlurCRTInput.material != null && Target.BlurCRTInput.material.GetTexture(IdMainTex) != bound);
            var lods = Target._LTCGI_LODs;
            if (blurChain && lods != null)
                for (int j = 1; j < lods.Length && j - 1 < LodGlobalIds.Length; j++)
                    if (lods[j] != null && Shader.GetGlobalTexture(LodGlobalIds[j - 1]) != lods[j])
                    {
                        Shader.SetGlobalTexture(LodGlobalIds[j - 1], lods[j]);
                        dropped = true;
                    }
            if (dropped) Bind(bound);
        }

        private void Blit()
        {
            // LTCGI maps the light with the screen mesh's UVs, so use the screen output's flips
            // and projection.
            bool hasOutput = output != null;
            BasisVideoOutputMath.GetProjectionUv(
                hasOutput ? output.ProjectionMode : BasisVideoProjectionMode.Mono,
                hasOutput ? output.StereoEye : BasisVideoStereoEye.Left,
                out Vector2 scale, out Vector2 offset, out _);
            bool flipH = hasOutput && output.FlipHorizontally;
            // The frame's row order depends on the platform present path, so fold that
            // per-client correction into the authored flip, as BasisVideoMaterialOutput does.
            bool flipV = (hasOutput && output.FlipVertically) ^ subscribed.OutputFrameIsTopLeftOrigin;
            if (flipV && flipH) BasisVideoOutputMath.ApplyBothFlip(ref scale, ref offset);
            else if (flipV) BasisVideoOutputMath.ApplyVerticalFlip(ref scale, ref offset);
            else if (flipH) BasisVideoOutputMath.ApplyHorizontalFlip(ref scale, ref offset);
            CopyParts(source, scale, offset);
            blittedFrame = subscribed.PresentedFrameCount;
        }

        // The parts are in the UVs the screen shows, so each goes on after the projection and flips.
        private void CopyParts(Texture from, Vector2 scale, Vector2 offset)
        {
            Rect[] parts = Parts;
            if (parts.Length == 1)
            {
                Graphics.Blit(from, rt, Vector2.Scale(scale, parts[0].size), offset + Vector2.Scale(scale, parts[0].min));
            }
            else
            {
                // CopyTexture needs CopyTextureSupport.Basic. Every graphics API Basis builds for
                // (D3D11, D3D12, Vulkan) has it; a GLES-only target without it would need a shader path.
                for (int i = 0; i < parts.Length; i++)
                {
                    Graphics.Blit(from, partRT, Vector2.Scale(scale, parts[i].size), offset + Vector2.Scale(scale, parts[i].min));
                    Graphics.CopyTexture(partRT, 0, 0, 0, 0, partRT.width, partRT.height, rt, 0, 0, i * partRT.width, 0);
                }
                rt.GenerateMips();
            }
            blittedRects = SourceRects != null ? (Rect[])SourceRects.Clone() : null;
        }

        private bool PartsChanged
        {
            get
            {
                if (SourceRects == null || blittedRects == null) return SourceRects != blittedRects;
                if (SourceRects.Length != blittedRects.Length) return true;
                for (int i = 0; i < SourceRects.Length; i++)
                    if (SourceRects[i] != blittedRects[i]) return true;
                return false;
            }
        }

        // SourceRects clamped to the frame; an empty or degenerate part is the whole frame.
        private Rect[] Parts
        {
            get
            {
                if (SourceRects == null || SourceRects.Length == 0) return new[] { new Rect(0f, 0f, 1f, 1f) };
                var parts = new Rect[SourceRects.Length];
                for (int i = 0; i < parts.Length; i++)
                {
                    Rect s = SourceRects[i];
                    Rect r = Rect.MinMaxRect(Mathf.Clamp01(s.xMin), Mathf.Clamp01(s.yMin), Mathf.Clamp01(s.xMax), Mathf.Clamp01(s.yMax));
                    parts[i] = r.width > 0f && r.height > 0f ? r : new Rect(0f, 0f, 1f, 1f);
                }
                return parts;
            }
        }

        private void EnsureRT(int frameWidth, int frameHeight)
        {
            Rect[] parts = Parts;
            int partWidth = 0, partHeight = 0;
            foreach (Rect r in parts)
            {
                partWidth = Mathf.Max(partWidth, Mathf.RoundToInt(frameWidth * r.width));
                partHeight = Mathf.Max(partHeight, Mathf.RoundToInt(frameHeight * r.height));
            }
            if (partWidth <= 0 || partHeight <= 0) return;
            float fit = Mathf.Min(1f, (float)MaxResolution / Mathf.Max(partWidth * parts.Length, partHeight));
            int pw = Mathf.Max(1, Mathf.RoundToInt(partWidth * fit));
            int h = Mathf.Max(1, Mathf.RoundToInt(partHeight * fit));
            int w = pw * parts.Length;
            bool packed = parts.Length > 1;
            if (rt != null && rt.width == w && rt.height == h && (partRT != null) == packed) return;
            ReleaseRT();
            // LTCGI samples this at mip levels in place of blur, so it needs a full chain. Packed parts are copied in
            // after rendering, so their mips are generated once all are in.
            rt = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB)
            {
                name = "BasisLTCGIVideo",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Trilinear,
                useMipMap = true,
                autoGenerateMips = !packed,
            };
            rt.Create();
            if (packed)
            {
                partRT = new RenderTexture(pw, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB)
                {
                    name = "BasisLTCGIVideoPart",
                    wrapMode = TextureWrapMode.Clamp,
                };
                partRT.Create();
            }
        }

        private void ReleaseRT()
        {
            if (partRT != null)
            {
                partRT.Release();
                if (Application.isPlaying) Destroy(partRT); else DestroyImmediate(partRT);
                partRT = null;
            }
            if (rt == null) return;
            rt.Release();
            if (Application.isPlaying) Destroy(rt); else DestroyImmediate(rt);
            rt = null;
        }
    }
}
