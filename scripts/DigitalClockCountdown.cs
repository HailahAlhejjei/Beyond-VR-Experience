using UnityEngine;
using UnityEngine.Experimental.Rendering;

[DisallowMultipleComponent]
[RequireComponent(typeof(MeshRenderer))]
public sealed class DigitalClockCountdown : MonoBehaviour
{
    [SerializeField, Min(1f)] private float durationSeconds = 60f;
    [SerializeField] private bool playOnAwake = true;
    [SerializeField] private bool loop;
    [SerializeField] private bool useUnscaledTime = true;

    private static readonly Color32 SegmentColor = new Color32(5, 188, 0, 255);
    private static readonly Color32 BackgroundColor = new Color32(1, 27, 0, 255);

    private const float ReferenceSize = 4096f;
    private const float PatchLeft = 1440f;
    private const float PatchTop = 1650f;
    private const float PatchRight = 1718f;
    private const float PatchBottom = 3680f;

    private static readonly float[] DigitCenters = { 1872f, 2332f, 3007f, 3460f };

    private MeshRenderer targetRenderer;
    private Material runtimeMaterial;
    private Texture originalBaseMap;
    private RenderTexture runtimeAtlas;
    private Texture2D displayPatch;
    private Color32[] patchPixels;
    private float scaleX;
    private float scaleY;
    private float remainingSeconds;
    private int displayedSeconds = -1;
    private bool isRunning;

    public float RemainingSeconds => remainingSeconds;
    public bool IsRunning => isRunning;

    private void Awake()
    {
        targetRenderer = GetComponent<MeshRenderer>();
        CreateRuntimeDisplay();
        RestartTimer();
        isRunning = playOnAwake;
    }

    private void Update()
    {
        if (!isRunning)
            return;

        var deltaTime = useUnscaledTime ? Time.unscaledDeltaTime : Time.deltaTime;
        remainingSeconds = Mathf.Max(0f, remainingSeconds - deltaTime);
        var wholeSeconds = Mathf.CeilToInt(remainingSeconds);

        if (wholeSeconds != displayedSeconds)
            RenderTime(wholeSeconds);

        if (remainingSeconds > 0f)
            return;

        if (loop)
            RestartTimer();
        else
            isRunning = false;
    }

    public void RestartTimer()
    {
        remainingSeconds = Mathf.Max(1f, durationSeconds);
        displayedSeconds = -1;
        RenderTime(Mathf.CeilToInt(remainingSeconds));
        isRunning = true;
    }

    public void PauseTimer()
    {
        isRunning = false;
    }

    public void ResumeTimer()
    {
        if (remainingSeconds > 0f)
            isRunning = true;
    }

    private void CreateRuntimeDisplay()
    {
        runtimeMaterial = targetRenderer.material;
        originalBaseMap = runtimeMaterial.GetTexture("_BaseMap");
        if (originalBaseMap == null)
            originalBaseMap = runtimeMaterial.mainTexture;

        if (originalBaseMap == null)
        {
            Debug.LogError("DigitalClockCountdown needs a base texture on the clock material.", this);
            enabled = false;
            return;
        }

        var atlasWidth = originalBaseMap.width;
        var atlasHeight = originalBaseMap.height;
        var descriptor = new RenderTextureDescriptor(atlasWidth, atlasHeight)
        {
            depthBufferBits = 0,
            graphicsFormat = GraphicsFormat.R8G8B8A8_SRGB,
            msaaSamples = 1,
            useMipMap = false,
            autoGenerateMips = false
        };

        runtimeAtlas = new RenderTexture(descriptor)
        {
            name = "Digital Clock Runtime Atlas",
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp
        };
        runtimeAtlas.Create();
        Graphics.Blit(originalBaseMap, runtimeAtlas);

        runtimeMaterial.SetTexture("_BaseMap", runtimeAtlas);
        runtimeMaterial.SetTexture("_MainTex", runtimeAtlas);

        scaleX = atlasWidth / ReferenceSize;
        scaleY = atlasHeight / ReferenceSize;
        var patchWidth = Mathf.Max(1, Mathf.RoundToInt((PatchRight - PatchLeft) * scaleX));
        var patchHeight = Mathf.Max(1, Mathf.RoundToInt((PatchBottom - PatchTop) * scaleY));

        displayPatch = new Texture2D(
            patchWidth,
            patchHeight,
            GraphicsFormat.R8G8B8A8_SRGB,
            TextureCreationFlags.None)
        {
            name = "Digital Clock Countdown Patch",
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp
        };
        patchPixels = new Color32[patchWidth * patchHeight];
    }

    private void RenderTime(int totalSeconds)
    {
        if (displayPatch == null || runtimeAtlas == null)
            return;

        displayedSeconds = Mathf.Max(0, totalSeconds);
        for (var i = 0; i < patchPixels.Length; i++)
            patchPixels[i] = BackgroundColor;

        var minutes = Mathf.Clamp(displayedSeconds / 60, 0, 99);
        var seconds = displayedSeconds % 60;
        DrawDigit(0, minutes / 10);
        DrawDigit(1, minutes % 10);
        DrawColon();
        DrawDigit(2, seconds / 10);
        DrawDigit(3, seconds % 10);

        displayPatch.SetPixels32(patchPixels);
        displayPatch.Apply(false, false);

        var destinationX = Mathf.RoundToInt(PatchLeft * scaleX);
        var destinationY = runtimeAtlas.height - Mathf.RoundToInt(PatchBottom * scaleY);
        Graphics.CopyTexture(
            displayPatch, 0, 0, 0, 0, displayPatch.width, displayPatch.height,
            runtimeAtlas, 0, 0, destinationX, destinationY);
    }

    private void DrawDigit(int slot, int digit)
    {
        digit = Mathf.Clamp(digit, 0, 9);
        var center = DigitCenters[slot];

        var a = digit != 1 && digit != 4;
        var b = digit != 5 && digit != 6;
        var c = digit != 2;
        var d = digit != 1 && digit != 4 && digit != 7;
        var e = digit == 0 || digit == 2 || digit == 6 || digit == 8;
        var f = digit != 1 && digit != 2 && digit != 3 && digit != 7;
        var g = digit != 0 && digit != 1 && digit != 7;

        if (digit == 1)
        {
            DrawBeveledRect(1584f, center - 41f, 1670f, center + 41f, false);
            DrawBeveledRect(1488f, center - 41f, 1574f, center + 41f, false);
            return;
        }

        if (a) DrawBeveledRect(1662f, center - 124f, 1688f, center + 124f, true);
        if (g) DrawBeveledRect(1566f, center - 124f, 1592f, center + 124f, true);
        if (d) DrawBeveledRect(1470f, center - 124f, 1496f, center + 124f, true);
        if (f) DrawBeveledRect(1584f, center - 178f, 1670f, center - 98f, false);
        if (b) DrawBeveledRect(1584f, center + 98f, 1670f, center + 178f, false);
        if (e) DrawBeveledRect(1488f, center - 178f, 1574f, center - 98f, false);
        if (c) DrawBeveledRect(1488f, center + 98f, 1574f, center + 178f, false);
    }

    private void DrawColon()
    {
        DrawDiamond(1533f, 2656f, 20f, 52f);
        DrawDiamond(1620f, 2656f, 20f, 52f);
    }

    private void DrawBeveledRect(float left, float top, float right, float bottom, bool pointsAlongY)
    {
        var pixelLeft = Mathf.Clamp(Mathf.RoundToInt((left - PatchLeft) * scaleX), 0, displayPatch.width - 1);
        var pixelRight = Mathf.Clamp(Mathf.RoundToInt((right - PatchLeft) * scaleX), pixelLeft + 1, displayPatch.width);
        var pixelTop = Mathf.Clamp(Mathf.RoundToInt((top - PatchTop) * scaleY), 0, displayPatch.height - 1);
        var pixelBottom = Mathf.Clamp(Mathf.RoundToInt((bottom - PatchTop) * scaleY), pixelTop + 1, displayPatch.height);
        var bevel = Mathf.Max(1, Mathf.RoundToInt(10f * Mathf.Min(scaleX, scaleY)));

        for (var yFromTop = pixelTop; yFromTop < pixelBottom; yFromTop++)
        {
            for (var x = pixelLeft; x < pixelRight; x++)
            {
                var inside = true;
                if (pointsAlongY)
                {
                    var distanceToEnd = Mathf.Min(yFromTop - pixelTop, pixelBottom - 1 - yFromTop);
                    var inset = Mathf.Max(0, bevel - distanceToEnd);
                    inside = x >= pixelLeft + inset && x < pixelRight - inset;
                }
                else
                {
                    var distanceToEnd = Mathf.Min(x - pixelLeft, pixelRight - 1 - x);
                    var inset = Mathf.Max(0, bevel - distanceToEnd);
                    inside = yFromTop >= pixelTop + inset && yFromTop < pixelBottom - inset;
                }

                if (inside)
                    SetPatchPixel(x, yFromTop, SegmentColor);
            }
        }
    }

    private void DrawDiamond(float centerX, float centerY, float radiusX, float radiusY)
    {
        var left = Mathf.RoundToInt((centerX - radiusX - PatchLeft) * scaleX);
        var right = Mathf.RoundToInt((centerX + radiusX - PatchLeft) * scaleX);
        var top = Mathf.RoundToInt((centerY - radiusY - PatchTop) * scaleY);
        var bottom = Mathf.RoundToInt((centerY + radiusY - PatchTop) * scaleY);
        var pixelCenterX = (left + right) * 0.5f;
        var pixelCenterY = (top + bottom) * 0.5f;
        var pixelRadiusX = Mathf.Max(1f, (right - left) * 0.5f);
        var pixelRadiusY = Mathf.Max(1f, (bottom - top) * 0.5f);

        for (var yFromTop = Mathf.Max(0, top); yFromTop < Mathf.Min(displayPatch.height, bottom); yFromTop++)
        {
            for (var x = Mathf.Max(0, left); x < Mathf.Min(displayPatch.width, right); x++)
            {
                var distance = Mathf.Abs(x - pixelCenterX) / pixelRadiusX
                    + Mathf.Abs(yFromTop - pixelCenterY) / pixelRadiusY;
                if (distance <= 1f)
                    SetPatchPixel(x, yFromTop, SegmentColor);
            }
        }
    }

    private void SetPatchPixel(int x, int yFromTop, Color32 color)
    {
        var yFromBottom = displayPatch.height - 1 - yFromTop;
        patchPixels[yFromBottom * displayPatch.width + x] = color;
    }

    private void OnDestroy()
    {
        if (runtimeAtlas != null)
        {
            runtimeAtlas.Release();
            Destroy(runtimeAtlas);
        }

        if (displayPatch != null)
            Destroy(displayPatch);

        if (runtimeMaterial != null)
            Destroy(runtimeMaterial);
    }
}
