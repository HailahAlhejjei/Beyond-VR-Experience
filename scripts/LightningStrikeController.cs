using System.Collections;
using UnityEngine;
using UnityEngine.Rendering;

public sealed class LightningStrikeController : MonoBehaviour
{
    [Header("Dedicated Lightning Objects")]
    [SerializeField] private Volume lightningVolume;
    [SerializeField] private Light lightningLight;
    [SerializeField] private LineRenderer lightningBolt;
    [SerializeField] private LineRenderer secondaryLightningBolt;
    [SerializeField] private Transform boltStart;
    [SerializeField] private Transform boltEnd;
    [SerializeField] private Transform secondaryBoltStart;
    [SerializeField] private Transform secondaryBoltEnd;
    [SerializeField] private Transform lightningAimTarget;
    [SerializeField] private AudioSource thunderSource;
    [SerializeField] private AudioClip[] thunderClips;

    [Header("Outdoor Strike Zone")]
    [SerializeField] private Vector2 strikeXRange = new Vector2(10.5f, 12f);
    [SerializeField] private Vector2 strikeZRange = new Vector2(-2.8f, -1.2f);
    [SerializeField] private Vector2 secondaryStrikeXRange = new Vector2(12.5f, 14.5f);
    [SerializeField] private Vector2 secondaryStrikeZRange = new Vector2(2.2f, 3.8f);
    [SerializeField] private Vector2 boltTopYRange = new Vector2(10.5f, 14f);
    [SerializeField] private Vector2 boltBottomYRange = new Vector2(-1.2f, 0.4f);

    [Header("Flash Variation")]
    [SerializeField] private Vector2 flashDurationRange = new Vector2(0.76f, 0.88f);
    [SerializeField] private Vector2 flashWeightRange = new Vector2(0.78f, 1f);
    [SerializeField] private Vector2 lightIntensityRange = new Vector2(7f, 12f);
    [SerializeField] private Vector2 thunderDelayRange = new Vector2(0.35f, 1.05f);
    [SerializeField] private int boltSegments = 15;
    [SerializeField] private float boltJitter = 0.42f;

    private static readonly float[] StrikeWaitPattern = { 10f, 30f, 45f, 30f };
    private Coroutine patternRoutine;
    private LineRenderer activeBolt;
    private bool useSecondaryStrikeZone;
    private readonly float[] recordedStrikeTimes = new float[16];
    private readonly Vector3[] recordedStrikePositions = new Vector3[16];

    public int StrikeCount { get; private set; }
    public Vector3 LastStrikeWorldPosition { get; private set; }
    public float LastThunderDelay { get; private set; }
    public float LastFlashDuration { get; private set; }
    public int LastStrikeZone { get; private set; }

    public float GetRecordedStrikeTime(int strikeIndex)
    {
        return strikeIndex >= 0 && strikeIndex < StrikeCount && strikeIndex < recordedStrikeTimes.Length
            ? recordedStrikeTimes[strikeIndex]
            : -1f;
    }

    public Vector3 GetRecordedStrikePosition(int strikeIndex)
    {
        return strikeIndex >= 0 && strikeIndex < StrikeCount && strikeIndex < recordedStrikePositions.Length
            ? recordedStrikePositions[strikeIndex]
            : Vector3.zero;
    }

    private void Awake()
    {
        ResetFlashObjects();
    }

    private void OnEnable()
    {
        useSecondaryStrikeZone = false;
        ResetFlashObjects();
        patternRoutine = StartCoroutine(StrikePattern());
    }

    private void OnDisable()
    {
        if (patternRoutine != null)
        {
            StopCoroutine(patternRoutine);
            patternRoutine = null;
        }

        StopAllCoroutines();
        ResetFlashObjects();
    }

    private IEnumerator StrikePattern()
    {
        int patternIndex = 0;

        while (enabled)
        {
            yield return new WaitForSecondsRealtime(StrikeWaitPattern[patternIndex]);
            TriggerStrike();
            yield return new WaitForSecondsRealtime(LastFlashDuration);
            patternIndex = (patternIndex + 1) % StrikeWaitPattern.Length;
        }
    }

    private void TriggerStrike()
    {
        if (StrikeCount < recordedStrikeTimes.Length)
        {
            recordedStrikeTimes[StrikeCount] = Time.unscaledTime;
        }
        StrikeCount++;

        float duration = Random.Range(flashDurationRange.x, flashDurationRange.y);
        LastFlashDuration = duration;
        float peakWeight = Random.Range(flashWeightRange.x, flashWeightRange.y);
        float peakLight = Random.Range(lightIntensityRange.x, lightIntensityRange.y);

        Vector3 strikeOrigin = BuildBolt();
        LineRenderer strikeBolt = activeBolt;
        LastStrikeWorldPosition = strikeOrigin;
        int currentStrikeIndex = StrikeCount - 1;
        if (currentStrikeIndex >= 0 && currentStrikeIndex < recordedStrikePositions.Length)
        {
            recordedStrikePositions[currentStrikeIndex] = strikeOrigin;
        }

        Vector3 lightPosition = new Vector3(strikeOrigin.x - 0.75f, 6f, strikeOrigin.z);
        lightningLight.transform.position = lightPosition;
        if (lightningAimTarget != null)
        {
            lightningLight.transform.LookAt(lightningAimTarget.position);
        }

        StartCoroutine(Flash(duration, peakWeight, peakLight, strikeBolt));

        if (thunderSource != null && thunderClips != null && thunderClips.Length > 0)
        {
            Camera listenerCamera = Camera.main;
            float distance = listenerCamera != null
                ? Vector3.Distance(listenerCamera.transform.position, strikeOrigin)
                : 18f;
            float distanceFactor = Mathf.InverseLerp(10f, 28f, distance);
            float delay = Mathf.Lerp(thunderDelayRange.x, thunderDelayRange.y, distanceFactor)
                + Random.Range(-0.08f, 0.08f);
            delay = Mathf.Clamp(delay, thunderDelayRange.x, thunderDelayRange.y);
            LastThunderDelay = delay;
            AudioClip clip = thunderClips[Random.Range(0, thunderClips.Length)];
            StartCoroutine(PlayThunderAfterDelay(clip, delay, strikeOrigin, peakWeight));
        }
    }

    private IEnumerator Flash(
        float duration,
        float peakWeight,
        float peakLight,
        LineRenderer strikeBolt)
    {
        float boltWidth = Random.Range(0.055f, 0.11f);
        strikeBolt.enabled = true;
        lightningLight.enabled = true;

        float elapsed = 0f;
        while (elapsed < duration)
        {
            elapsed += Time.unscaledDeltaTime;
            float normalized = Mathf.Clamp01(elapsed / duration);
            float flashShape = Mathf.Sin(normalized * Mathf.PI);
            flashShape = Mathf.SmoothStep(0f, 1f, flashShape);

            lightningVolume.weight = peakWeight * flashShape;
            lightningLight.intensity = peakLight * flashShape;
            strikeBolt.widthMultiplier = boltWidth * Mathf.Lerp(0.55f, 1f, flashShape);
            strikeBolt.startColor = new Color(0.86f, 0.94f, 1f, flashShape);
            strikeBolt.endColor = new Color(0.62f, 0.82f, 1f, flashShape * 0.9f);

            yield return null;
        }

        lightningVolume.weight = 0f;
        lightningLight.intensity = 0f;
        lightningLight.enabled = false;
        strikeBolt.enabled = false;
    }

    private IEnumerator PlayThunderAfterDelay(
        AudioClip clip,
        float delay,
        Vector3 strikeOrigin,
        float strikeStrength)
    {
        yield return new WaitForSecondsRealtime(delay);
        if (clip != null && thunderSource != null)
        {
            thunderSource.transform.position = strikeOrigin;
            thunderSource.enabled = true;
            thunderSource.pitch = Random.Range(0.94f, 1.02f);
            thunderSource.volume = Mathf.Lerp(0.55f, 0.72f, strikeStrength)
                * Random.Range(0.94f, 1f);
            thunderSource.PlayOneShot(clip);
        }
    }

    private Vector3 BuildBolt()
    {
        bool useSecond = useSecondaryStrikeZone
            && secondaryLightningBolt != null
            && secondaryBoltStart != null
            && secondaryBoltEnd != null;
        useSecondaryStrikeZone = !useSecondaryStrikeZone;

        LineRenderer strikeBolt = useSecond ? secondaryLightningBolt : lightningBolt;
        Transform strikeStart = useSecond ? secondaryBoltStart : boltStart;
        Transform strikeEnd = useSecond ? secondaryBoltEnd : boltEnd;
        Vector2 activeXRange = useSecond ? secondaryStrikeXRange : strikeXRange;
        Vector2 activeZRange = useSecond ? secondaryStrikeZRange : strikeZRange;
        activeBolt = strikeBolt;
        LastStrikeZone = useSecond ? 2 : 1;

        int count = Mathf.Max(4, boltSegments);
        strikeBolt.positionCount = count;
        strikeBolt.useWorldSpace = true;

        float strikeX = Random.Range(activeXRange.x, activeXRange.y);
        float strikeZ = Random.Range(activeZRange.x, activeZRange.y);
        Vector3 start = new Vector3(
            strikeX,
            Random.Range(boltTopYRange.x, boltTopYRange.y),
            strikeZ);
        Vector3 end = new Vector3(
            Mathf.Max(9.75f, strikeX + Random.Range(-0.45f, 0.45f)),
            Random.Range(boltBottomYRange.x, boltBottomYRange.y),
            Mathf.Clamp(strikeZ + Random.Range(-0.45f, 0.45f), -3.2f, 4.2f));

        strikeStart.position = start;
        strikeEnd.position = end;

        Vector3 direction = end - start;
        Vector3 side = Vector3.Cross(direction.normalized, Vector3.up);
        if (side.sqrMagnitude < 0.01f)
        {
            side = Vector3.right;
        }
        side.Normalize();
        Vector3 depth = Vector3.Cross(direction.normalized, side).normalized;

        for (int i = 0; i < count; i++)
        {
            float t = i / (float)(count - 1);
            float taper = Mathf.Sin(t * Mathf.PI);
            Vector3 jitter = (side * Random.Range(-boltJitter, boltJitter)
                + depth * Random.Range(-boltJitter, boltJitter)) * taper;
            strikeBolt.SetPosition(i, start + direction * t + jitter);
        }

        return start;
    }

    private void ResetFlashObjects()
    {
        if (lightningVolume != null)
        {
            lightningVolume.weight = 0f;
        }

        if (lightningLight != null)
        {
            lightningLight.intensity = 0f;
            lightningLight.enabled = false;
        }

        if (lightningBolt != null)
        {
            lightningBolt.enabled = false;
        }

        if (secondaryLightningBolt != null)
        {
            secondaryLightningBolt.enabled = false;
        }
    }
}
