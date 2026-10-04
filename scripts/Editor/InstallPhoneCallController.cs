using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

[InitializeOnLoad]
internal static class InstallPhoneCallController
{
    private const string SoundsRoot = "Assets/h-sounds/";
    private const string AudioEmitterName = "Phone Audio Emitter";
    private static bool installQueued;
    private static bool installing;

    static InstallPhoneCallController()
    {
        QueueInstall();
        EditorSceneManager.sceneOpened += OnSceneOpened;
    }

    [DidReloadScripts]
    private static void AfterScriptsReloaded() => QueueInstall();

    [MenuItem("Tools/Final Project/Install Phone Call")]
    private static void InstallFromMenu() => Install();

    private static void OnSceneOpened(Scene scene, OpenSceneMode mode) => QueueInstall();

    private static void QueueInstall()
    {
        if (installQueued || installing || EditorApplication.isPlayingOrWillChangePlaymode)
            return;
        installQueued = true;
        EditorApplication.delayCall += Install;
    }

    private static void Install()
    {
        installQueued = false;
        if (installing || EditorApplication.isPlayingOrWillChangePlaymode)
            return;
        installing = true;

        try
        {
            InstallInternal();
        }
        finally
        {
            installing = false;
        }
    }

    private static void InstallInternal()
    {

        // These three files are valid WAV data that were saved without extensions.
        // Renaming through AssetDatabase lets Unity switch them to AudioImporter safely.
        var renamedAudio = RenameToWav("mans-q");
        renamedAudio |= RenameToWav("maya-h");
        renamedAudio |= RenameToWav("phone-h");
        if (renamedAudio)
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

        var question = LoadClip("mans-q.wav", "mans-q");
        var maya = LoadClip("maya-h.wav", "maya-h");
        var phoneLine = LoadClip("phone-h.wav", "phone-h");
        var ringing = LoadClip("ringing-h.wav");
        var loanShark = LoadClip("loanshark-h.mp3");

        var changedScenes = new HashSet<Scene>();
        var configuredPhones = new HashSet<GameObject>();
        for (var sceneIndex = 0; sceneIndex < SceneManager.sceneCount; sceneIndex++)
        {
            var scene = SceneManager.GetSceneAt(sceneIndex);
            if (!scene.isLoaded)
                continue;

            foreach (var root in scene.GetRootGameObjects())
            {
                foreach (var transform in root.GetComponentsInChildren<Transform>(true))
                {
                    if (!IsPhoneName(transform.name))
                        continue;

                    var phoneRoot = PrefabUtility.GetOutermostPrefabInstanceRoot(transform.gameObject);
                    if (phoneRoot == null)
                        phoneRoot = transform.gameObject;
                    if (!configuredPhones.Add(phoneRoot))
                        continue;

                    GameObjectUtility.SetStaticEditorFlags(phoneRoot, 0);

                    var controller = phoneRoot.GetComponent<PhoneCallController>();
                    if (controller == null)
                        controller = Undo.AddComponent<PhoneCallController>(phoneRoot);

                    var interactable = phoneRoot.GetComponent<XRGrabInteractable>();
                    if (interactable == null)
                        interactable = Undo.AddComponent<XRGrabInteractable>(phoneRoot);
                    interactable.useDynamicAttach = true;
                    interactable.matchAttachPosition = true;
                    interactable.matchAttachRotation = true;
                    interactable.movementType = UnityEngine.XR.Interaction.Toolkit.Interactables.XRBaseInteractable.MovementType.Instantaneous;
                    interactable.predictedVisualsTransform = null;

                    var body = phoneRoot.GetComponent<Rigidbody>();
                    if (body == null)
                        body = Undo.AddComponent<Rigidbody>(phoneRoot);
                    body.mass = 0.5f;
                    body.useGravity = true;
                    body.isKinematic = false;
                    body.interpolation = RigidbodyInterpolation.Interpolate;
                    body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;

                    EnsureGrabCollider(phoneRoot.transform);

                    var source = EnsurePhoneAudioEmitter(phoneRoot.transform);

                    SerializedObject serialized = new SerializedObject(controller);
                    serialized.FindProperty("timer").objectReferenceValue = FindTimer();
                    serialized.FindProperty("phoneInteractable").objectReferenceValue = interactable;
                    serialized.FindProperty("phoneAudio").objectReferenceValue = source;
                    serialized.FindProperty("ringingClip").objectReferenceValue = ringing;
                    serialized.FindProperty("mansQuestionClip").objectReferenceValue = question;
                    serialized.FindProperty("phoneLineClip").objectReferenceValue = phoneLine;
                    serialized.FindProperty("mayaResponseClip").objectReferenceValue = maya;
                    serialized.FindProperty("loanSharkResponseClip").objectReferenceValue = loanShark;
                    serialized.ApplyModifiedPropertiesWithoutUndo();

                    source.playOnAwake = false;
                    source.loop = false;
                    source.spatialBlend = 1f;
                    source.spatialize = false;
                    source.rolloffMode = AudioRolloffMode.Linear;
                    source.minDistance = 0.25f;
                    source.maxDistance = 12f;
                    source.dopplerLevel = 0f;
                    source.spread = 25f;
                    source.bypassEffects = false;
                    source.bypassListenerEffects = false;
                    source.bypassReverbZones = true;
                    source.volume = 1f;
                    source.mute = false;
                    EditorUtility.SetDirty(controller);
                    EditorUtility.SetDirty(source);
                    EditorUtility.SetDirty(body);
                    EditorUtility.SetDirty(interactable);
                    changedScenes.Add(scene);
                }
            }
        }

        AssetDatabase.SaveAssets();
        foreach (var changedScene in changedScenes)
        {
            EditorSceneManager.MarkSceneDirty(changedScene);
            if (!string.IsNullOrEmpty(changedScene.path))
                EditorSceneManager.SaveScene(changedScene);
        }

        if (question == null || maya == null || phoneLine == null || ringing == null || loanShark == null)
            Debug.LogWarning("PhoneCallController installed, but one or more phone clips could not be imported as AudioClips yet. Refresh Unity after the file renames finish.");
        else if (configuredPhones.Count > 0)
            Debug.Log($"PhoneCallController installed on {configuredPhones.Count} phone object(s) with grab physics, timer, microphone, and response routing.");
    }

    private static bool IsPhoneName(string objectName)
    {
        if (string.IsNullOrEmpty(objectName))
            return false;

        var normalized = objectName
            .Replace("_", string.Empty)
            .Replace("-", string.Empty)
            .Replace(" ", string.Empty)
            .ToLowerInvariant();
        return normalized.Contains("cellphone");
    }

    private static void EnsureGrabCollider(Transform phoneRoot)
    {
        var box = phoneRoot.GetComponent<BoxCollider>();
        if (box == null)
            box = Undo.AddComponent<BoxCollider>(phoneRoot.gameObject);

        var renderers = phoneRoot.GetComponentsInChildren<Renderer>(true);
        if (renderers.Length == 0)
            return;

        var initialized = false;
        var minimum = Vector3.zero;
        var maximum = Vector3.zero;
        foreach (var renderer in renderers)
        {
            var bounds = renderer.bounds;
            for (var x = -1; x <= 1; x += 2)
            for (var y = -1; y <= 1; y += 2)
            for (var z = -1; z <= 1; z += 2)
            {
                var worldCorner = bounds.center + Vector3.Scale(bounds.extents, new Vector3(x, y, z));
                var localCorner = phoneRoot.InverseTransformPoint(worldCorner);
                if (!initialized)
                {
                    minimum = maximum = localCorner;
                    initialized = true;
                }
                else
                {
                    minimum = Vector3.Min(minimum, localCorner);
                    maximum = Vector3.Max(maximum, localCorner);
                }
            }
        }

        if (!initialized)
            return;
        box.center = (minimum + maximum) * 0.5f;
        box.size = maximum - minimum;
        EditorUtility.SetDirty(box);
    }

    private static AudioSource EnsurePhoneAudioEmitter(Transform phoneRoot)
    {
        var emitter = phoneRoot.Find(AudioEmitterName);
        if (emitter == null)
        {
            var emitterObject = new GameObject(AudioEmitterName);
            Undo.RegisterCreatedObjectUndo(emitterObject, "Create phone audio emitter");
            emitter = emitterObject.transform;
            Undo.SetTransformParent(emitter, phoneRoot, "Parent phone audio emitter");
        }

        var renderers = phoneRoot.GetComponentsInChildren<Renderer>(true);
        var foundRenderer = false;
        var visibleBounds = new Bounds();
        foreach (var phoneRenderer in renderers)
        {
            if (phoneRenderer.transform.IsChildOf(emitter))
                continue;
            if (!foundRenderer)
            {
                visibleBounds = phoneRenderer.bounds;
                foundRenderer = true;
            }
            else
            {
                visibleBounds.Encapsulate(phoneRenderer.bounds);
            }
        }

        if (foundRenderer)
            emitter.position = visibleBounds.center;
        else
            emitter.localPosition = Vector3.zero;
        emitter.localRotation = Quaternion.identity;
        emitter.localScale = Vector3.one;

        var source = emitter.GetComponent<AudioSource>();
        if (source == null)
            source = Undo.AddComponent<AudioSource>(emitter.gameObject);

        var highPass = emitter.GetComponent<AudioHighPassFilter>();
        if (highPass == null)
            highPass = Undo.AddComponent<AudioHighPassFilter>(emitter.gameObject);
        highPass.cutoffFrequency = 300f;
        highPass.highpassResonanceQ = 1f;

        var lowPass = emitter.GetComponent<AudioLowPassFilter>();
        if (lowPass == null)
            lowPass = Undo.AddComponent<AudioLowPassFilter>(emitter.gameObject);
        lowPass.cutoffFrequency = 3400f;
        lowPass.lowpassResonanceQ = 1f;

        var distortion = emitter.GetComponent<AudioDistortionFilter>();
        if (distortion == null)
            distortion = Undo.AddComponent<AudioDistortionFilter>(emitter.gameObject);
        distortion.distortionLevel = 0.04f;

        EditorUtility.SetDirty(emitter);
        EditorUtility.SetDirty(highPass);
        EditorUtility.SetDirty(lowPass);
        EditorUtility.SetDirty(distortion);
        return source;
    }

    private static bool RenameToWav(string baseName)
    {
        var source = SoundsRoot + baseName;
        var destination = SoundsRoot + baseName + ".wav";
        if (AssetDatabase.LoadMainAssetAtPath(destination) != null)
            return false;
        if (AssetDatabase.LoadMainAssetAtPath(source) == null)
            return false;

        var error = AssetDatabase.MoveAsset(source, destination);
        if (!string.IsNullOrEmpty(error))
        {
            Debug.LogWarning($"Could not rename {source}: {error}");
            return false;
        }
        return true;
    }

    private static AudioClip LoadClip(string fileName, string extensionlessFallback = null)
    {
        var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(SoundsRoot + fileName);
        if (clip != null)
            return clip;
        return extensionlessFallback == null
            ? null
            : AssetDatabase.LoadAssetAtPath<AudioClip>(SoundsRoot + extensionlessFallback);
    }

    private static DigitalClockCountdown FindTimer()
    {
        var timerObject = GameObject.Find("timer-h");
        if (timerObject != null)
        {
            var timer = timerObject.GetComponentInChildren<DigitalClockCountdown>(true);
            if (timer != null)
                return timer;
        }
        return UnityEngine.Object.FindFirstObjectByType<DigitalClockCountdown>();
    }
}
