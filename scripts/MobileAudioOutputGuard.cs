using System.Collections;
using UnityEngine;

[DisallowMultipleComponent]
[RequireComponent(typeof(AudioListener))]
public sealed class MobileAudioOutputGuard : MonoBehaviour
{
    [SerializeField, Min(0f)] private float resumeDelay = 0.2f;
    [SerializeField, Min(0.1f)] private float healthCheckInterval = 1f;

    private Coroutine resumeRoutine;
    private float nextHealthCheckTime;
    private bool applicationPaused;

    private void Awake()
    {
        RestoreAudioOutput();
    }

    private void OnEnable()
    {
        QueueRestore(0f);
    }

    private void OnDisable()
    {
        if (resumeRoutine != null)
        {
            StopCoroutine(resumeRoutine);
            resumeRoutine = null;
        }
    }

private void Update()
    {
        if (!Application.isMobilePlatform
            || applicationPaused
            || Time.unscaledTime < nextHealthCheckTime)
        {
            return;
        }

        nextHealthCheckTime = Time.unscaledTime + healthCheckInterval;

        if (!AudioSettings.Mobile.audioOutputStarted)
        {
            RestoreAudioOutput();
        }
    }

    private void OnApplicationPause(bool pauseStatus)
    {
        applicationPaused = pauseStatus;

        if (!pauseStatus)
        {
            QueueRestore(resumeDelay);
        }
    }

    private void OnApplicationFocus(bool hasFocus)
    {
        if (hasFocus && !applicationPaused)
        {
            QueueRestore(resumeDelay);
        }
    }

    private void QueueRestore(float delay)
    {
        if (!isActiveAndEnabled)
        {
            return;
        }

        if (resumeRoutine != null)
        {
            StopCoroutine(resumeRoutine);
        }

        resumeRoutine = StartCoroutine(RestoreAfterDelay(delay));
    }

    private IEnumerator RestoreAfterDelay(float delay)
    {
        if (delay > 0f)
        {
            yield return new WaitForSecondsRealtime(delay);
        }

        RestoreAudioOutput();
        resumeRoutine = null;
    }

private void RestoreAudioOutput()
    {
        AudioListener.pause = false;
        AudioListener.volume = 1f;

        if (!Application.isMobilePlatform)
        {
            return;
        }

        AudioSettings.Mobile.stopAudioOutputOnMute = true;

        if (!AudioSettings.Mobile.audioOutputStarted)
        {
            AudioSettings.Mobile.StartAudioOutput();
            Debug.Log("[MobileAudioOutputGuard] Restarted the mobile audio output thread.");
        }
    }
}
