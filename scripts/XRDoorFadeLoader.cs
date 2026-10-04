using System.Collections;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>
/// Attach this component to a separate, root-level empty GameObject.
/// When the XR player's head enters the door radius, movement stops and the view
/// fades to black, holds, then the old XR Origin GameObject is disabled and the
/// destination scene is loaded. The black overlay survives the scene switch (it's
/// parented to this DontDestroyOnLoad object, not to the old scene's camera) and
/// head-locks to whatever Camera.main is each frame, so it can fade back OUT again
/// once the destination scene's own camera comes up — no jarring instant reveal.
/// </summary>
public sealed class XRDoorFadeLoader : MonoBehaviour
{
    [Header("Required references")]
    [SerializeField] private Transform door;
    [SerializeField] private XROrigin xrOrigin;

    [Header("Door trigger")]
    [SerializeField, Range(0.5f, 1f)] private float triggerDistance = 0.9f;

    [Header("Transition")]
    [Tooltip("Use the full Build Settings path so Unity cannot select another scene with the same name.")]
    [SerializeField] private string destinationScene =
        "Assets/our_Scenes/exportedscene/Basic Sample.unity";
    [SerializeField, Min(0f)] private float sceneHandoffDelay = 3f;
    [SerializeField, Min(0.05f)] private float fadeDuration = 0.6f;
    [Tooltip("Fade-back-in duration once the destination scene has finished loading.")]
    [SerializeField, Min(0.05f)] private float fadeInDuration = 0.85f;
    [Tooltip("Distance in front of whatever camera is active that the overlay sits at.")]
    [SerializeField] private float headLockDistance = 0.15f;

    [Header("Hint")]
    [Tooltip("Shown via the shared CLAUDE_InvestigatorSubtitle hint line, and cleared the moment " +
             "the door triggers. Leave blank to show no hint.")]
    [SerializeField] private string hintText = "Walk to the door.";
    [Tooltip("Seconds after the scene starts before the hint appears.")]
    [SerializeField, Min(0f)] private float hintDelay = 4f;

    private Camera xrCamera;
    private Canvas canvas;
    private RectTransform canvasRect;
    private CanvasGroup fadeGroup;
    private bool triggered;

    private void Awake()
    {
        DisableOlderDoorTransitionHandlers();

        if (xrOrigin == null)
            xrOrigin = FindFirstObjectByType<XROrigin>(FindObjectsInactive.Exclude);

        if (door == null || xrOrigin == null)
        {
            Debug.LogError("[XRDoorFadeLoader] Assign the Door and XR Origin references.", this);
            enabled = false;
            return;
        }

        if (transform == xrOrigin.transform || transform.IsChildOf(xrOrigin.transform))
        {
            Debug.LogError(
                "[XRDoorFadeLoader] Put this script on a separate root-level empty GameObject, " +
                "not on or under the XR Origin.", this);
            enabled = false;
            return;
        }

        if (!Application.CanStreamedLevelBeLoaded(destinationScene))
        {
            Debug.LogError(
                "[XRDoorFadeLoader] Scene '" + destinationScene +
                "' is not available. Add it to Build Settings.", this);
            enabled = false;
            return;
        }

        xrCamera = xrOrigin.Camera;
        if (xrCamera == null)
        {
            Debug.LogError("[XRDoorFadeLoader] The XR Origin has no Camera assigned.", this);
            enabled = false;
            return;
        }

        DontDestroyOnLoad(gameObject);
        BuildFadeCanvas();
    }

    private Coroutine hintRoutine;

    private void Start()
    {
        if (!string.IsNullOrEmpty(hintText))
            hintRoutine = StartCoroutine(ShowHintAfterDelay());
    }

    private IEnumerator ShowHintAfterDelay()
    {
        if (hintDelay > 0f)
            yield return new WaitForSeconds(hintDelay);

        if (triggered) yield break; // already reached the door before the delay elapsed

        // Start's own Awake pass has long since run by now, so Instance is set.
        if (CLAUDE.Investigation.CLAUDE_InvestigatorSubtitle.Instance != null)
            CLAUDE.Investigation.CLAUDE_InvestigatorSubtitle.Instance.ShowHint(hintText);
    }

    private void DisableOlderDoorTransitionHandlers()
    {
        foreach (MonoBehaviour behaviour in
                 FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
        {
            if (behaviour == null || behaviour == this)
                continue;

            string typeName = behaviour.GetType().Name;
            if (typeName == "CLAUDE_FuneralFreeRoamTransition" ||
                typeName == "CLAUDE_FuneralExitTransition")
            {
                behaviour.enabled = false;
            }
        }
    }

    private void Update()
    {
        if (triggered || xrCamera == null || door == null)
            return;

        Vector3 playerPosition = xrCamera.transform.position;
        Vector3 doorPosition = door.position;
        playerPosition.y = 0f;
        doorPosition.y = 0f;

        if ((playerPosition - doorPosition).sqrMagnitude <= triggerDistance * triggerDistance)
        {
            triggered = true;
            StartCoroutine(FadeDisableAndLoad());
        }
    }

    private IEnumerator FadeDisableAndLoad()
    {
        float triggeredAt = Time.realtimeSinceStartup;

        // Stop movement immediately, but leave the camera GameObject active so the
        // player can see the fade before the entire old rig is switched off.
        FreezeXRLocomotion();

        if (hintRoutine != null) StopCoroutine(hintRoutine);
        if (CLAUDE.Investigation.CLAUDE_InvestigatorSubtitle.Instance != null)
            CLAUDE.Investigation.CLAUDE_InvestigatorSubtitle.Instance.HideHint();

        float elapsed = 0f;
        while (elapsed < fadeDuration)
        {
            elapsed += Time.unscaledDeltaTime;
            fadeGroup.alpha = Mathf.Clamp01(elapsed / fadeDuration);
            yield return null;
        }
        fadeGroup.alpha = 1f;

        float remainingDelay = sceneHandoffDelay - (Time.realtimeSinceStartup - triggeredAt);
        if (remainingDelay > 0f)
            yield return new WaitForSecondsRealtime(remainingDelay);

        // This component lives on a separate DontDestroyOnLoad object, so its
        // coroutine continues after the complete XR rig is deactivated.
        xrOrigin.gameObject.SetActive(false);

        // Single mode unloads the funeral scene. The newly loaded Basic Sample scene
        // therefore uses its own serialized XR Origin position and rotation.
        AsyncOperation load = SceneManager.LoadSceneAsync(destinationScene, LoadSceneMode.Single);
        if (load == null)
        {
            Debug.LogError("[XRDoorFadeLoader] Could not start loading '" + destinationScene + "'.", this);
            yield break;
        }

        while (!load.isDone)
            yield return null;

        yield return null; // let the destination scene's own Main Camera become Camera.main first
        HeadLock(); // snap onto it immediately so the reveal doesn't start off-target for a frame

        float fadeInElapsed = 0f;
        while (fadeInElapsed < fadeInDuration)
        {
            fadeInElapsed += Time.unscaledDeltaTime;
            fadeGroup.alpha = 1f - Mathf.Clamp01(fadeInElapsed / fadeInDuration);
            yield return null;
        }
        fadeGroup.alpha = 0f;

        Destroy(gameObject);
    }

    private void FreezeXRLocomotion()
    {
        xrOrigin.enabled = false;

        foreach (MonoBehaviour behaviour in xrOrigin.GetComponentsInChildren<MonoBehaviour>(true))
        {
            if (behaviour == null)
                continue;

            System.Type type = behaviour.GetType();
            string typeNamespace = type.Namespace ?? string.Empty;
            if (typeNamespace.StartsWith("UnityEngine.XR.Interaction.Toolkit.Locomotion") ||
                type.Name == "DynamicMoveProvider")
            {
                behaviour.enabled = false;
            }
        }

        CharacterController characterController = xrOrigin.GetComponent<CharacterController>();
        if (characterController != null)
            characterController.enabled = false;
    }

    private void BuildFadeCanvas()
    {
        GameObject canvasObject = new GameObject(
            "XR Door Fade Canvas",
            typeof(RectTransform),
            typeof(Canvas),
            typeof(CanvasGroup));

        // Parented to THIS (DontDestroyOnLoad) object, not the old scene's camera, so it
        // survives the scene switch instead of being destroyed along with the old XR rig.
        canvasRect = (RectTransform)canvasObject.transform;
        canvasRect.SetParent(transform, false);
        canvasRect.localScale = Vector3.one;
        canvasRect.sizeDelta = new Vector2(4f, 4f);

        canvas = canvasObject.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.overrideSorting = true;
        canvas.sortingOrder = short.MaxValue;

        fadeGroup = canvasObject.GetComponent<CanvasGroup>();
        fadeGroup.alpha = 0f;
        fadeGroup.interactable = false;
        fadeGroup.blocksRaycasts = false;

        GameObject imageObject = new GameObject("Black", typeof(RectTransform), typeof(Image));
        RectTransform imageRect = (RectTransform)imageObject.transform;
        imageRect.SetParent(canvasRect, false);
        imageRect.anchorMin = Vector2.zero;
        imageRect.anchorMax = Vector2.one;
        imageRect.offsetMin = Vector2.zero;
        imageRect.offsetMax = Vector2.zero;

        Image image = imageObject.GetComponent<Image>();
        image.color = Color.black;
        image.raycastTarget = false;

        HeadLock(); // snap into place immediately, don't wait a frame for LateUpdate
    }

    private void LateUpdate() { if (canvasRect != null) HeadLock(); }

    // Follows whatever Camera.main is each frame - the old scene's camera before the load,
    // the destination scene's own camera after it - so no reference to either is needed.
    private void HeadLock()
    {
        Camera cam = Camera.main;
        if (cam == null) return;

        canvasRect.position = cam.transform.position + cam.transform.forward * headLockDistance;
        canvasRect.rotation = cam.transform.rotation;
        if (canvas.worldCamera != cam) canvas.worldCamera = cam;
    }

#if UNITY_EDITOR
    private void OnValidate()
    {
        triggerDistance = Mathf.Clamp(triggerDistance, 0.5f, 1f);
        sceneHandoffDelay = Mathf.Max(0f, sceneHandoffDelay);
        fadeDuration = Mathf.Max(0.05f, fadeDuration);
        fadeInDuration = Mathf.Max(0.05f, fadeInDuration);
        headLockDistance = Mathf.Max(0.01f, headLockDistance);
        hintDelay = Mathf.Max(0f, hintDelay);
    }
#endif
}
