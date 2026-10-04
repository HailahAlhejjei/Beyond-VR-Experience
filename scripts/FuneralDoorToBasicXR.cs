using System.Collections;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>
/// Door handoff from the funeral scene to the exact Basic Sample scene.
/// Attach this to a separate root-level empty GameObject, never to the XR Origin.
/// </summary>
public sealed class FuneralDoorToBasicXR : MonoBehaviour
{
    [Header("Required references")]
    [SerializeField] private Transform door;
    [SerializeField] private XROrigin xrOrigin;

    [Header("Door trigger")]
    [SerializeField, Range(0.5f, 1f)] private float triggerDistance = 0.9f;

    [Header("Transition")]
    [SerializeField] private string destinationScene =
        "Assets/our_Scenes/exportedscene/Basic Sample.unity";
    [SerializeField, Min(0f)] private float sceneHandoffDelay = 4f;
    [SerializeField, Min(0.05f)] private float fadeDuration = 0.6f;

    private Camera xrCamera;
    private CanvasGroup fadeGroup;
    private bool triggered;

    private void Awake()
    {
        DisableOlderDoorHandlers();

        if (xrOrigin == null)
            xrOrigin = FindFirstObjectByType<XROrigin>(FindObjectsInactive.Exclude);

        if (door == null || xrOrigin == null)
        {
            Debug.LogError("[FuneralDoorToBasicXR] Assign the Door and source XR Origin.", this);
            enabled = false;
            return;
        }

        if (transform == xrOrigin.transform || transform.IsChildOf(xrOrigin.transform))
        {
            Debug.LogError(
                "[FuneralDoorToBasicXR] This must be on a separate root-level empty GameObject.",
                this);
            enabled = false;
            return;
        }

        if (!Application.CanStreamedLevelBeLoaded(destinationScene))
        {
            Debug.LogError(
                "[FuneralDoorToBasicXR] Scene is missing from Build Settings: " + destinationScene,
                this);
            enabled = false;
            return;
        }

        xrCamera = xrOrigin.Camera;
        if (xrCamera == null)
        {
            Debug.LogError("[FuneralDoorToBasicXR] The source XR Origin has no Camera.", this);
            enabled = false;
            return;
        }

        DontDestroyOnLoad(gameObject);
        BuildFadeCanvas();
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
        FreezeSourceXR();

        float elapsed = 0f;
        while (elapsed < fadeDuration)
        {
            elapsed += Time.unscaledDeltaTime;
            fadeGroup.alpha = Mathf.Clamp01(elapsed / fadeDuration);
            yield return null;
        }
        fadeGroup.alpha = 1f;

        float remaining = sceneHandoffDelay - (Time.realtimeSinceStartup - triggeredAt);
        if (remaining > 0f)
            yield return new WaitForSecondsRealtime(remaining);

        // This coroutine remains alive because this component is not on the XR rig.
        xrOrigin.gameObject.SetActive(false);

        AsyncOperation load = SceneManager.LoadSceneAsync(destinationScene, LoadSceneMode.Single);
        if (load == null)
        {
            Debug.LogError("[FuneralDoorToBasicXR] Scene loading could not start.", this);
            yield break;
        }

        while (!load.isDone)
            yield return null;

        ActivateDestinationXR();
        Destroy(gameObject);
    }

    private void ActivateDestinationXR()
    {
        Scene destination = SceneManager.GetSceneByPath(destinationScene);
        if (!destination.IsValid() || !destination.isLoaded)
            destination = SceneManager.GetActiveScene();

        XROrigin destinationOrigin = null;
        foreach (GameObject root in destination.GetRootGameObjects())
        {
            destinationOrigin = root.GetComponentInChildren<XROrigin>(true);
            if (destinationOrigin != null)
                break;
        }

        if (destinationOrigin == null)
        {
            Debug.LogError(
                "[FuneralDoorToBasicXR] Basic Sample loaded, but it contains no XR Origin.",
                this);
            return;
        }

        // Preserve the intended spawn transform saved inside Basic Sample.
        destinationOrigin.gameObject.SetActive(true);
        destinationOrigin.enabled = true;
    }

    private void FreezeSourceXR()
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

    private void DisableOlderDoorHandlers()
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

    private void BuildFadeCanvas()
    {
        GameObject canvasObject = new GameObject(
            "Funeral Door Fade Canvas",
            typeof(RectTransform),
            typeof(Canvas),
            typeof(CanvasGroup));

        RectTransform canvasRect = (RectTransform)canvasObject.transform;
        canvasRect.SetParent(xrCamera.transform, false);
        canvasRect.localPosition = new Vector3(
            0f, 0f, Mathf.Max(xrCamera.nearClipPlane + 0.05f, 0.15f));
        canvasRect.localRotation = Quaternion.identity;
        canvasRect.localScale = Vector3.one;
        canvasRect.sizeDelta = new Vector2(4f, 4f);

        Canvas canvas = canvasObject.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.worldCamera = xrCamera;
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
    }
}
