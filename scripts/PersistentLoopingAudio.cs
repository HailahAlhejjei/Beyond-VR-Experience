using System.Collections;
using UnityEngine;

[DisallowMultipleComponent]
[RequireComponent(typeof(AudioSource))]
public sealed class PersistentLoopingAudio : MonoBehaviour
{
    [SerializeField, Min(0f)] private float startupDelay = 0.25f;
    [SerializeField, Min(0.1f)] private float retryDelay = 1f;

    private AudioSource audioSource;
    private Coroutine startRoutine;
    private float nextRetryTime;
    private bool applicationPaused;
    private bool applicationQuitting;

    private void Awake()
    {
        audioSource = GetComponent<AudioSource>();
        audioSource.enabled = true;
        audioSource.loop = true;
    }

    private void OnEnable()
    {
        QueueStart(startupDelay);
    }

    private void OnDisable()
    {
        if (startRoutine != null)
        {
            StopCoroutine(startRoutine);
            startRoutine = null;
        }
    }

    private void Update()
    {
        if (applicationPaused || applicationQuitting || audioSource == null)
        {
            return;
        }

        if (!audioSource.isPlaying && Time.unscaledTime >= nextRetryTime)
        {
            QueueStart(0f);
            nextRetryTime = Time.unscaledTime + retryDelay;
        }
    }

    private void OnApplicationPause(bool pauseStatus)
    {
        applicationPaused = pauseStatus;
        if (!pauseStatus)
        {
            QueueStart(0.1f);
        }
    }

    private void OnApplicationFocus(bool hasFocus)
    {
        if (hasFocus && !applicationPaused)
        {
            QueueStart(0.1f);
        }
    }

    private void OnApplicationQuit()
    {
        applicationQuitting = true;
    }

    private void QueueStart(float delay)
    {
        if (!isActiveAndEnabled || applicationQuitting || startRoutine != null)
        {
            return;
        }

        startRoutine = StartCoroutine(StartWhenReady(delay));
    }

    private IEnumerator StartWhenReady(float delay)
    {
        if (delay > 0f)
        {
            yield return new WaitForSecondsRealtime(delay);
        }

        AudioClip clip = audioSource.clip;
        if (clip == null)
        {
            startRoutine = null;
            yield break;
        }

        if (clip.loadState == AudioDataLoadState.Unloaded)
        {
            clip.LoadAudioData();
        }

        while (clip.loadState == AudioDataLoadState.Loading)
        {
            yield return null;
        }

        if (clip.loadState == AudioDataLoadState.Loaded
            && !audioSource.isPlaying
            && !applicationPaused)
        {
            audioSource.Play();
        }

        nextRetryTime = Time.unscaledTime + retryDelay;
        startRoutine = null;
    }
}
