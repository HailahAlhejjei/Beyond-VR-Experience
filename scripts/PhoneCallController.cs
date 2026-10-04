using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Meta.WitAi.Configuration;
using Meta.WitAi.Data.Configuration;
using Meta.WitAi.Data.Info;
using Oculus.Voice;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.XR;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

#if UNITY_ANDROID && !UNITY_EDITOR
using UnityEngine.Android;
#endif

/// <summary>
/// Drives the old-phone sequence: timer finished -> ringing -> pickup -> question ->
/// microphone answer -> Maya or loan-shark response.
/// </summary>
[DisallowMultipleComponent]
public sealed class PhoneCallController : MonoBehaviour
{
    private const string AudioEmitterName = "Phone Audio Emitter";
    private const string VoiceInputName = "Phone Meta Voice Input";
    // This public demo token ships with Meta Voice SDK's Built-In Timer sample.
    // It gives this prototype immediate English transcription without training.
    private const string BuiltInWitClientToken = "YOUR_WIT_CLIENT_TOKEN";

    private static readonly string[] MayaPronunciations =
    {
        "maya", "maia", "maiya", "mayah", "mya", "mia", "miya", "miah",
        "my uh", "my ah", "mai uh", "may uh", "ma ya", "may a", "my a",
        "maya's", "miyas", "mayor", "mayer", "myer", "may i", "meya",
        "meyah", "meia", "maja", "miyah", "my yuh", "mai ya", "may ya",
        "my ya", "mey uh", "meya's", "mya's", "myers", "my yeah"
    };

    private static readonly string[] CarlosPronunciations =
    {
        "carlos", "car los", "carlo", "carl os", "carloss", "carlose",
        "kar los", "karlos", "karloz", "car loss", "car lows", "car loaf",
        "carlos's", "carlo's", "carl owes", "carlus", "carlis", "carlous",
        "karlo", "karlo's", "carlosz", "carlosh", "car less", "car lass",
        "car laws", "carlows", "carl loss", "carl us", "carl is", "carl",
        "car loads", "car load", "car loans", "car loan", "car loose",
        "car louse", "car lows", "car lowes", "carloes", "carl owes",
        "carol's", "carols", "carlisle", "carlos ah", "call us", "callos"
    };

    private static readonly string[] LoanSharkPronunciations =
    {
        "loan shark", "loan-shark", "loanshark", "lone shark", "lone-shark",
        "loneshark", "the loan shark", "the lone shark", "loan sharks",
        "lone sharks", "loan sharc", "lone sharc", "loan charge", "lone charge",
        "loan shock", "lone shock", "loan sharp", "lone sharp", "long shark",
        "lowen shark", "lohn shark", "low shark", "loan shork", "lone shork",
        "loan shark's", "lone shark's", "loan shart", "lone shart", "loan chart",
        "lone chart", "long sharc", "lawn shark", "lawn shork", "the loanshark",
        "loan shot", "lone shot", "lawn shot", "long shot", "loan shop",
        "lone shop", "lawn shop", "loan chalk", "lone chalk", "loan shock",
        "lone shock", "low and shark", "lo and shark", "alone shark",
        "loan sherk", "lone sherk", "loan shuck", "lone shuck", "shark"
    };

    private enum CallState
    {
        WaitingForTimer,
        Ringing,
        Question,
        Listening,
        Responding,
        Complete
    }

    [Header("Scene references")]
    [SerializeField] private DigitalClockCountdown timer;
    [SerializeField] private XRGrabInteractable phoneInteractable;
    [SerializeField] private AudioSource phoneAudio;
    [SerializeField] private AppVoiceExperience voiceExperience;

    [Header("Phone sounds")]
    [SerializeField] private AudioClip ringingClip;
    [SerializeField] private AudioClip mansQuestionClip;
    [SerializeField] private AudioClip phoneLineClip;
    [SerializeField] private AudioClip mayaResponseClip;
    [SerializeField] private AudioClip loanSharkResponseClip;

    [Header("Recognition")]
    [SerializeField, Min(1f)] private float answerTimeoutSeconds = 45f;
    [SerializeField] private bool acceptCarlosAsLoanShark = true;

    private CallState state;
    private Coroutine sequence;
    private Coroutine recognitionRetry;
    private float listeningStartedAt;
    private bool timerWasRunning;
    private bool voiceEventsSubscribed;
    private bool pushToTalkWasActive;
    private string latestPartialTranscript = string.Empty;
    private InputAction pushToTalkAction;

    public bool IsListening => state == CallState.Listening && pushToTalkWasActive;

    private void Awake()
    {
        EnsureReferences();
        EnsurePushToTalkInput();
        state = CallState.WaitingForTimer;
        timerWasRunning = timer != null && timer.IsRunning;
        PreloadPhoneClips();
        StopPhoneAudio();
    }

    private void OnEnable()
    {
        EnsurePushToTalkInput();
        if (!pushToTalkAction.enabled)
            pushToTalkAction.Enable();

        if (phoneInteractable != null)
        {
            phoneInteractable.selectEntered.AddListener(OnPhonePickedUp);
            phoneInteractable.selectExited.AddListener(OnPhoneReleased);
        }
        SubscribeVoiceEvents();
    }

    private void OnDisable()
    {
        if (phoneInteractable != null)
        {
            phoneInteractable.selectEntered.RemoveListener(OnPhonePickedUp);
            phoneInteractable.selectExited.RemoveListener(OnPhoneReleased);
        }
        pushToTalkWasActive = false;
        if (pushToTalkAction != null && pushToTalkAction.enabled)
            pushToTalkAction.Disable();
        UnsubscribeVoiceEvents();
        voiceExperience?.DeactivateAndAbortRequest();
    }

    private void OnDestroy()
    {
        pushToTalkAction?.Dispose();
        pushToTalkAction = null;
    }

    private void Update()
    {
        UpdatePushToTalk();

        if (state == CallState.WaitingForTimer)
        {
            var running = timer != null && timer.IsRunning;
            // Also handle a controller that is installed/enabled after the timer
            // has already reached zero.
            if (!running && timer != null && timer.RemainingSeconds <= 0.01f)
                BeginRinging();
            timerWasRunning = running;
        }
        else if (state == CallState.Listening && Time.unscaledTime - listeningStartedAt >= answerTimeoutSeconds)
        {
            pushToTalkWasActive = false;
            CancelRecognitionRetry();
            voiceExperience?.DeactivateAndAbortRequest();
            StopPhoneAudio();
            state = CallState.Complete;
            Debug.LogWarning("Phone call timed out waiting for a Maya/Carlos answer.", this);
        }
    }

    public void ResetCall()
    {
        pushToTalkWasActive = false;
        latestPartialTranscript = string.Empty;
        voiceExperience?.DeactivateAndAbortRequest();
        CancelRecognitionRetry();
        if (sequence != null)
            StopCoroutine(sequence);
        EnsureReferences();
        state = CallState.WaitingForTimer;
        timerWasRunning = timer != null && timer.IsRunning;
        StopPhoneAudio();
    }

    private void EnsureReferences()
    {
        if (timer == null)
        {
            var timerObject = GameObject.Find("timer-h");
            if (timerObject != null)
                timer = timerObject.GetComponentInChildren<DigitalClockCountdown>(true);
            if (timer == null)
                timer = FindFirstObjectByType<DigitalClockCountdown>();
        }

        if (phoneInteractable == null)
            phoneInteractable = GetComponentInChildren<XRGrabInteractable>(true);

        EnsureMetaVoiceExperience();

        EnsurePhoneAudioEmitter();
        phoneAudio.playOnAwake = false;
        // Full 3D placement makes every call sound originate from the visible
        // handset, while the band-pass filters give it a speakerphone character.
        phoneAudio.spatialBlend = 1f;
        phoneAudio.loop = false;
        phoneAudio.spatialize = false;
        phoneAudio.rolloffMode = AudioRolloffMode.Linear;
        phoneAudio.minDistance = 0.25f;
        phoneAudio.maxDistance = 12f;
        phoneAudio.dopplerLevel = 0f;
        phoneAudio.spread = 25f;
        phoneAudio.bypassEffects = false;
        phoneAudio.bypassListenerEffects = false;
        phoneAudio.bypassReverbZones = true;
        phoneAudio.volume = 1f;
        phoneAudio.mute = false;
    }

    private void EnsureMetaVoiceExperience()
    {
        if (voiceExperience == null)
            voiceExperience = GetComponentInChildren<AppVoiceExperience>(true);

        if (voiceExperience == null)
        {
            var voiceObject = new GameObject(VoiceInputName);
            voiceObject.SetActive(false);
            voiceObject.transform.SetParent(transform, false);
            voiceExperience = voiceObject.AddComponent<AppVoiceExperience>();
            ConfigureMetaVoice(voiceExperience);
            voiceObject.SetActive(true);
        }
        else
        {
            ConfigureMetaVoice(voiceExperience);
        }
    }

    private static void ConfigureMetaVoice(AppVoiceExperience voice)
    {
        var runtime = voice.RuntimeConfiguration ?? new WitRuntimeConfiguration();
        if (runtime.witConfiguration == null)
        {
            var configuration = ScriptableObject.CreateInstance<WitConfiguration>();
            configuration.name = "Phone Built-In Wit Configuration";
            configuration.SetClientAccessToken(BuiltInWitClientToken);
            configuration.SetApplicationInfo(new WitAppInfo
            {
                name = "Built-in Models",
                id = "voiceSDK_en",
                lang = "en",
                isPrivate = false
            });
            runtime.witConfiguration = configuration;
        }

        runtime.sendAudioToWit = true;
        // Meta's public built-in English app uses the normal HTTP speech
        // endpoint. It still returns partial and full raw transcriptions.
        runtime.transcribeOnly = false;
        runtime.alwaysRecord = false;
        runtime.soundWakeThreshold = 0.0001f;
        runtime.minKeepAliveVolume = 0.0001f;
        runtime.minKeepAliveTimeInSeconds = 2.5f;
        runtime.minTranscriptionKeepAliveTimeInSeconds = 1.5f;
        runtime.maxRecordingTime = 8f;
        voice.RuntimeConfiguration = runtime;
        voice.UsePlatformIntegrations = false;
    }

    private void SubscribeVoiceEvents()
    {
        if (voiceEventsSubscribed || voiceExperience == null)
            return;

        voiceExperience.VoiceEvents.OnPartialTranscription.AddListener(OnPartialSpeechResult);
        voiceExperience.VoiceEvents.OnFullTranscription.AddListener(OnFullSpeechResult);
        voiceExperience.VoiceEvents.OnError.AddListener(OnMetaVoiceError);
        voiceExperience.VoiceEvents.OnStoppedListening.AddListener(OnMetaStoppedListening);
        voiceEventsSubscribed = true;
    }

    private void UnsubscribeVoiceEvents()
    {
        if (!voiceEventsSubscribed || voiceExperience == null)
            return;

        voiceExperience.VoiceEvents.OnPartialTranscription.RemoveListener(OnPartialSpeechResult);
        voiceExperience.VoiceEvents.OnFullTranscription.RemoveListener(OnFullSpeechResult);
        voiceExperience.VoiceEvents.OnError.RemoveListener(OnMetaVoiceError);
        voiceExperience.VoiceEvents.OnStoppedListening.RemoveListener(OnMetaStoppedListening);
        voiceEventsSubscribed = false;
    }

    private void EnsurePhoneAudioEmitter()
    {
        var emitter = transform.Find(AudioEmitterName);
        if (emitter == null)
        {
            var emitterObject = new GameObject(AudioEmitterName);
            emitter = emitterObject.transform;
            emitter.SetParent(transform, false);
        }

        PositionEmitterAtVisiblePhone(emitter);

        var emitterAudio = emitter.GetComponent<AudioSource>();
        if (emitterAudio == null)
            emitterAudio = emitter.gameObject.AddComponent<AudioSource>();

        if (phoneAudio != null && phoneAudio != emitterAudio)
        {
            phoneAudio.Stop();
            phoneAudio.enabled = false;
        }
        phoneAudio = emitterAudio;

        var highPass = emitter.GetComponent<AudioHighPassFilter>();
        if (highPass == null)
            highPass = emitter.gameObject.AddComponent<AudioHighPassFilter>();
        highPass.cutoffFrequency = 300f;
        highPass.highpassResonanceQ = 1f;

        var lowPass = emitter.GetComponent<AudioLowPassFilter>();
        if (lowPass == null)
            lowPass = emitter.gameObject.AddComponent<AudioLowPassFilter>();
        lowPass.cutoffFrequency = 3400f;
        lowPass.lowpassResonanceQ = 1f;

        var distortion = emitter.GetComponent<AudioDistortionFilter>();
        if (distortion == null)
            distortion = emitter.gameObject.AddComponent<AudioDistortionFilter>();
        distortion.distortionLevel = 0.04f;
    }

    private void PositionEmitterAtVisiblePhone(Transform emitter)
    {
        var renderers = GetComponentsInChildren<Renderer>(true);
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
    }

    private void BeginRinging()
    {
        if (state != CallState.WaitingForTimer || ringingClip == null)
            return;

        state = CallState.Ringing;
        phoneAudio.clip = ringingClip;
        phoneAudio.loop = true;
        phoneAudio.Play();
    }

    /// <summary>
    /// External one-shot request (e.g. the bracelet finale) to begin ringing NOW. It ONLY delegates to the
    /// existing private <see cref="BeginRinging"/> — no ringing logic is duplicated and nothing else here changes.
    /// BeginRinging()'s own <c>state == WaitingForTimer</c> guard makes this a safe no-op if a call has already
    /// started (timer ring in Flow 1 or early pickup in Flow 3), so it can never double-ring or disturb an
    /// in-progress call/answer. The normal 2-minute timer, B input, Wit/voice, and Maya/Carlos flow are untouched.
    /// </summary>
    public void StartRingingFromExternalTrigger()
    {
        BeginRinging();
    }

    // ======================================================================================
    // CLAUDE additive hooks for the final-investigation flow adapter (CLAUDE_FinalInvestigationFlow).
    // These add NOTHING to the existing behaviour: they only expose read-only call state and delegate to
    // the SAME private PlayResponse the Voice SDK already calls, so the Final Assessment UI can reuse the
    // exact existing Maya/Carlos outcomes (audio + state transitions) without duplicating any dialogue.
    // The 2-minute timer, B push-to-talk, Wit/Voice SDK, and Maya/Carlos matching are all untouched.
    // ======================================================================================
    public bool CLAUDE_IsRinging => state == CallState.Ringing;
    public bool CLAUDE_IsAwaitingSpeech => state == CallState.Listening;   // "So who did it?" finished, mic open
    public bool CLAUDE_IsResolved => state == CallState.Responding || state == CallState.Complete;
    public bool CLAUDE_IsCallComplete => state == CallState.Complete;      // response finished playing

    /// <summary>Fires when ANY answer (voice OR the CLAUDE UI) is accepted, with the same label PlayResponse uses.</summary>
    public event System.Action<string> CLAUDE_OnAnswerAccepted;

    /// <summary>
    /// UI "Maya" button → the exact existing Maya response. Returns the clip length. Works the
    /// instant it's clicked (waiting for timer, ringing, mid-question, or already listening) since
    /// the CLAUDE Final Assessment UI now appears immediately on pickup, before the "So who did it?"
    /// clip has necessarily finished - only a no-op once a response has already started/finished.
    /// </summary>
    public float CLAUDE_TriggerMaya()
    {
        if (state == CallState.Responding || state == CallState.Complete) return 0f;
        PlayResponse(mayaResponseClip, "Maya (CLAUDE UI)");
        return mayaResponseClip != null ? mayaResponseClip.length : 0f;
    }

    /// <summary>UI "Carlos" button → the exact existing Carlos/loan-shark response. Returns the clip length.
    /// Same immediate-click behaviour as <see cref="CLAUDE_TriggerMaya"/>.</summary>
    public float CLAUDE_TriggerCarlos()
    {
        if (state == CallState.Responding || state == CallState.Complete) return 0f;
        PlayResponse(loanSharkResponseClip, "Carlos (CLAUDE UI)");
        return loanSharkResponseClip != null ? loanSharkResponseClip.length : 0f;
    }

    /// <summary>Lock the call so no further voice/UI answer resolves (Mother/Suicide, and post-outcome safety).</summary>
    public void CLAUDE_LockCall()
    {
        CancelRecognitionRetry();
        pushToTalkWasActive = false;
        latestPartialTranscript = string.Empty;
        voiceExperience?.DeactivateAndAbortRequest();
        if (state == CallState.Listening) state = CallState.Complete;
    }

    private void OnPhonePickedUp(UnityEngine.XR.Interaction.Toolkit.SelectEnterEventArgs args)
    {
        // Picking up early acts as the player placing the call. The timer is
        // still the only thing that starts the incoming ringing sound.
        if (state != CallState.WaitingForTimer && state != CallState.Ringing)
            return;

        StopPhoneAudio();
        if (sequence != null)
            StopCoroutine(sequence);
        sequence = StartCoroutine(QuestionThenListen());
    }

    private void OnPhoneReleased(UnityEngine.XR.Interaction.Toolkit.SelectExitEventArgs args)
    {
        if (state != CallState.Listening)
            return;

        pushToTalkWasActive = false;
        CancelRecognitionRetry();
        voiceExperience?.DeactivateAndAbortRequest();
        Debug.Log("Phone released: push-to-talk microphone closed.", this);
    }

    private IEnumerator QuestionThenListen()
    {
        state = CallState.Question;
        if (mansQuestionClip == null)
        {
            Debug.LogError("PhoneCallController is missing the man's question clip.", this);
            yield break;
        }

        phoneAudio.PlayOneShot(mansQuestionClip);
        Debug.Log($"Phone playing man's question: {mansQuestionClip.name}", this);
        yield return new WaitForSecondsRealtime(mansQuestionClip.length);

        // The CLAUDE UI can now answer (CLAUDE_TriggerMaya/Carlos -> PlayResponse) at any point,
        // including while this wait was running. If that already moved state past Question, don't
        // clobber it back to Listening or start the phone-line loop over the response.
        if (state != CallState.Question)
            yield break;

        state = CallState.Listening;
        listeningStartedAt = Time.unscaledTime;
        pushToTalkWasActive = false;
        latestPartialTranscript = string.Empty;
        if (phoneLineClip != null)
        {
            phoneAudio.clip = phoneLineClip;
            phoneAudio.loop = true;
            phoneAudio.Play();
        }

        Debug.Log("Phone is ready. Keep holding the phone and hold B on the right controller to speak.", this);
    }

    private void OnPartialSpeechResult(string transcript)
    {
        ProcessSpeechResult(transcript, false);
    }

    private void OnFullSpeechResult(string transcript)
    {
        ProcessSpeechResult(transcript, true);
    }

    private void ProcessSpeechResult(string transcript, bool isFinal)
    {
        if (state != CallState.Listening || string.IsNullOrWhiteSpace(transcript))
            return;

        var normalized = NormalizeSpeech(transcript);
        var combined = isFinal && !string.IsNullOrEmpty(latestPartialTranscript)
            ? $"{latestPartialTranscript} {normalized}"
            : normalized;
        Debug.Log($"Phone microphone heard {(isFinal ? "final" : "partial")}: {transcript}", this);

        if (MatchesMaya(normalized) || MatchesMaya(combined))
        {
            PlayResponse(mayaResponseClip, "Maya");
        }
        else if (acceptCarlosAsLoanShark
            && (MatchesCarlosOrLoanShark(normalized)
                || MatchesCarlosOrLoanShark(combined)))
        {
            PlayResponse(loanSharkResponseClip, "Carlos/loan shark");
        }
        else if (isFinal)
        {
            latestPartialTranscript = string.Empty;
            QueueRecognitionRetry(0.25f);
        }
        else
        {
            // Keep the newest partial result so multi-word answers can be
            // matched when Meta returns the final transcription.
            latestPartialTranscript = normalized;
        }
    }

    private static bool MatchesMaya(string normalizedValue)
    {
        return ContainsAnyPronunciation(normalizedValue, MayaPronunciations)
            || ContainsFuzzyName(normalizedValue, "maya", "maia", "miya", "meya");
    }

    private static bool MatchesCarlosOrLoanShark(string normalizedValue)
    {
        return ContainsAnyPronunciation(normalizedValue, CarlosPronunciations)
            || ContainsFuzzyName(normalizedValue, "carlos", "karlos", "carloss")
            || ContainsFuzzyCompactPhrase(normalizedValue, 3, "carlos", "karlos")
            || ContainsAnyPronunciation(normalizedValue, LoanSharkPronunciations)
            || ContainsFuzzyLoanShark(normalizedValue)
            || ContainsFuzzyCompactPhrase(
                normalizedValue, 3, "loanshark", "loneshark", "lawnshark");
    }

    private static bool ContainsAnyPronunciation(string normalizedValue, params string[] candidates)
    {
        var compactValue = normalizedValue.Replace(" ", string.Empty);
        foreach (var candidate in candidates)
        {
            var normalizedCandidate = NormalizeSpeech(candidate);
            var compactCandidate = normalizedCandidate.Replace(" ", string.Empty);
            if (normalizedValue.Contains(normalizedCandidate)
                || compactValue.Contains(compactCandidate))
                return true;
        }
        return false;
    }

    private static bool ContainsFuzzyName(string normalizedValue, params string[] spellings)
    {
        var words = normalizedValue.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var word in words)
        {
            foreach (var spelling in spellings)
            {
                var allowedDistance = spelling.Length >= 6 ? 2 : 1;
                if (Math.Abs(word.Length - spelling.Length) <= allowedDistance
                    && EditDistance(word, spelling) <= allowedDistance)
                    return true;
            }
        }
        return false;
    }

    private static bool ContainsFuzzyLoanShark(string normalizedValue)
    {
        var words = normalizedValue.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < words.Length - 1; i++)
        {
            var soundsLikeLoan = IsWithinDistance(words[i], 1, "loan", "lone", "lohn", "lawn", "long");
            var soundsLikeShark = IsWithinDistance(words[i + 1], 1, "shark", "sharc", "shork", "sharp");
            if (soundsLikeLoan && soundsLikeShark)
                return true;
        }
        return false;
    }

    private static bool ContainsFuzzyCompactPhrase(
        string normalizedValue, int maximumDistance, params string[] candidates)
    {
        var words = normalizedValue.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        for (var start = 0; start < words.Length; start++)
        {
            var compactWindow = string.Empty;
            for (var end = start; end < words.Length && end < start + 3; end++)
            {
                compactWindow += words[end];
                foreach (var candidate in candidates)
                {
                    var compactCandidate = NormalizeSpeech(candidate).Replace(" ", string.Empty);
                    if (Math.Abs(compactWindow.Length - compactCandidate.Length) <= maximumDistance
                        && EditDistance(compactWindow, compactCandidate) <= maximumDistance)
                        return true;
                }
            }
        }
        return false;
    }

    private static bool IsWithinDistance(string value, int maximumDistance, params string[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (Math.Abs(value.Length - candidate.Length) <= maximumDistance
                && EditDistance(value, candidate) <= maximumDistance)
                return true;
        }
        return false;
    }

    private static int EditDistance(string first, string second)
    {
        var previous = new int[second.Length + 1];
        var current = new int[second.Length + 1];
        for (var column = 0; column <= second.Length; column++)
            previous[column] = column;

        for (var row = 1; row <= first.Length; row++)
        {
            current[0] = row;
            for (var column = 1; column <= second.Length; column++)
            {
                var substitutionCost = first[row - 1] == second[column - 1] ? 0 : 1;
                current[column] = Math.Min(
                    Math.Min(current[column - 1] + 1, previous[column] + 1),
                    previous[column - 1] + substitutionCost);
            }

            var swap = previous;
            previous = current;
            current = swap;
        }
        return previous[second.Length];
    }

    private static string NormalizeSpeech(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var builder = new StringBuilder(value.Length);
        var previousWasSpace = false;
        foreach (var character in value.ToLower(CultureInfo.InvariantCulture))
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(character);
                previousWasSpace = false;
            }
            else if (!previousWasSpace && builder.Length > 0)
            {
                builder.Append(' ');
                previousWasSpace = true;
            }
        }
        return builder.ToString().Trim();
    }

    private void OnMetaVoiceError(string error, string message)
    {
        if (state == CallState.Listening && pushToTalkWasActive)
        {
            Debug.LogWarning($"Meta Voice SDK error {error}: {message}", this);
            QueueRecognitionRetry(1f);
        }
    }

    private void OnMetaStoppedListening()
    {
        if (state == CallState.Listening && pushToTalkWasActive)
            QueueRecognitionRetry(0.2f);
    }

    private void UpdatePushToTalk()
    {
        var shouldBeActive = state == CallState.Listening
            && phoneInteractable != null
            && phoneInteractable.isSelected
            && IsRightControllerBPressed();

        if (shouldBeActive && !pushToTalkWasActive)
        {
            pushToTalkWasActive = true;
            CancelRecognitionRetry();
            StartMetaListening();
            Debug.Log("Phone push-to-talk opened: phone held and B pressed.", this);
        }
        else if (!shouldBeActive && pushToTalkWasActive)
        {
            pushToTalkWasActive = false;
            CancelRecognitionRetry();
            // Stop recording gracefully so Meta can finish and return the full
            // transcript. Aborting here loses slower results such as Carlos or
            // the two-word answer "loan shark".
            voiceExperience?.Deactivate();
            Debug.Log("Phone push-to-talk closed; finishing transcription.", this);
        }
    }

    private void EnsurePushToTalkInput()
    {
        if (pushToTalkAction != null)
            return;

        pushToTalkAction = new InputAction("Phone Push To Talk", InputActionType.Button);
        pushToTalkAction.AddBinding("<XRController>{RightHand}/secondaryButton");
        pushToTalkAction.AddBinding("<OculusTouchController>{RightHand}/secondaryButton");
        pushToTalkAction.AddBinding("<MetaQuestTouchProController>{RightHand}/secondaryButton");
        pushToTalkAction.AddBinding("<Gamepad>/buttonEast");
        pushToTalkAction.AddBinding("<Keyboard>/b");
    }

    private bool IsRightControllerBPressed()
    {
        // The Input System bindings cover OpenXR, Oculus Touch, Quest Touch Pro,
        // the XR Device Simulator, and desktop testing. Keep the legacy XR
        // feature check as a final fallback for older Quest controller layouts.
        if (pushToTalkAction != null && pushToTalkAction.IsPressed())
            return true;

        var rightController = InputDevices.GetDeviceAtXRNode(XRNode.RightHand);
        return rightController.isValid
            && rightController.TryGetFeatureValue(UnityEngine.XR.CommonUsages.secondaryButton, out var isPressed)
            && isPressed;
    }

    private void StartMetaListening()
    {
        if (state != CallState.Listening
            || !pushToTalkWasActive
            || phoneInteractable == null
            || !phoneInteractable.isSelected
            || !IsRightControllerBPressed()
            || voiceExperience == null)
            return;
        if (voiceExperience.Active || voiceExperience.IsRequestActive)
            return;

        var configuration = voiceExperience.RuntimeConfiguration?.witConfiguration;
        if (configuration == null || string.IsNullOrWhiteSpace(configuration.GetClientAccessToken()))
        {
            Debug.LogError("Meta Voice SDK needs a Wit configuration with a Client Access Token.", this);
            QueueRecognitionRetry(2f);
            return;
        }

        latestPartialTranscript = string.Empty;
        voiceExperience.ActivateImmediately();
        Debug.Log("Phone microphone activated through Meta Voice SDK.", this);
        StartCoroutine(VerifyMetaListeningAfterDelay());
    }

    private IEnumerator VerifyMetaListeningAfterDelay()
    {
        yield return new WaitForSecondsRealtime(1f);
        if (state == CallState.Listening
            && voiceExperience != null
            && !voiceExperience.Active
            && !voiceExperience.IsRequestActive)
        {
            Debug.LogWarning($"Meta Voice SDK did not remain active: {voiceExperience.GetActivateAudioError()}", this);
            QueueRecognitionRetry(1f);
        }
    }

    private void PlayResponse(AudioClip response, string label)
    {
        // Voice recognition only ever calls this while already Listening, so this guard change
        // only widens what the CLAUDE UI path (CLAUDE_TriggerMaya/Carlos) may do: answer any time
        // before a response has already started/finished, not just once Listening is reached.
        if (state == CallState.Responding || state == CallState.Complete || response == null)
            return;

        CancelRecognitionRetry();
        pushToTalkWasActive = false;
        latestPartialTranscript = string.Empty;
        state = CallState.Responding;
        voiceExperience?.DeactivateAndAbortRequest();
        StopPhoneAudio();
        phoneAudio.PlayOneShot(response);
        StartCoroutine(FinishAfter(response.length));
        Debug.Log($"Phone answer recognized as {label}: playing response.", this);
        CLAUDE_OnAnswerAccepted?.Invoke(label);   // CLAUDE: notify the final-flow adapter (voice OR UI path)
    }

    private IEnumerator FinishAfter(float seconds)
    {
        yield return new WaitForSecondsRealtime(seconds);
        if (state == CallState.Responding)
            state = CallState.Complete;
    }

    private void QueueRecognitionRetry(float delay)
    {
        if (state != CallState.Listening || recognitionRetry != null)
            return;
        recognitionRetry = StartCoroutine(RestartRecognitionAfter(delay));
    }

    private IEnumerator RestartRecognitionAfter(float delay)
    {
        yield return new WaitForSecondsRealtime(delay);
        recognitionRetry = null;
        if (state == CallState.Listening)
            StartMetaListening();
    }

    private void CancelRecognitionRetry()
    {
        if (recognitionRetry == null)
            return;
        StopCoroutine(recognitionRetry);
        recognitionRetry = null;
    }

    private void StopPhoneAudio()
    {
        if (phoneAudio == null)
            return;
        phoneAudio.Stop();
        phoneAudio.clip = null;
        phoneAudio.loop = false;
    }

    private void PreloadPhoneClips()
    {
        LoadClip(ringingClip);
        LoadClip(mansQuestionClip);
        LoadClip(phoneLineClip);
        LoadClip(mayaResponseClip);
        LoadClip(loanSharkResponseClip);
    }

    private static void LoadClip(AudioClip clip)
    {
        if (clip != null && clip.loadState == AudioDataLoadState.Unloaded)
            clip.LoadAudioData();
    }

    private sealed class QuestSpeechInput : IDisposable
    {
        private readonly Action<string> resultCallback;
        private readonly Action<string> errorCallback;
        private readonly ConcurrentQueue<string> pendingResults = new ConcurrentQueue<string>();
        private readonly ConcurrentQueue<string> pendingErrors = new ConcurrentQueue<string>();
#if UNITY_ANDROID && !UNITY_EDITOR
        private AndroidJavaObject recognizer;
        private AndroidJavaObject activity;
        private RecognitionListenerProxy listener;
#endif

        public QuestSpeechInput(Action<string> resultCallback, Action<string> errorCallback)
        {
            this.resultCallback = resultCallback;
            this.errorCallback = errorCallback;
        }

        public void Prepare()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (!Permission.HasUserAuthorizedPermission(Permission.Microphone))
                Permission.RequestUserPermission(Permission.Microphone);
#endif
        }

        public void Poll()
        {
            while (pendingErrors.TryDequeue(out var error))
                errorCallback?.Invoke(error);
            while (pendingResults.TryDequeue(out var result))
                resultCallback?.Invoke(result);
        }

        public void StartListening()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (!Permission.HasUserAuthorizedPermission(Permission.Microphone))
            {
                Permission.RequestUserPermission(Permission.Microphone);
                errorCallback?.Invoke("Microphone permission requested; answer again after allowing it.");
                return;
            }

            try
            {
                activity = new AndroidJavaClass("com.unity3d.player.UnityPlayer")
                    .GetStatic<AndroidJavaObject>("currentActivity");
                activity.Call("runOnUiThread", new AndroidJavaRunnable(() =>
                {
                    try
                    {
                        using var speechRecognizerClass = new AndroidJavaClass("android.speech.SpeechRecognizer");
                        if (recognizer == null)
                        {
                            var defaultRecognizerAvailable = false;
                            try
                            {
                                defaultRecognizerAvailable = speechRecognizerClass.CallStatic<bool>(
                                    "isRecognitionAvailable", activity);
                            }
                            catch { }

                            var useOnDeviceRecognizer = false;
                            using (var versionClass = new AndroidJavaClass("android.os.Build$VERSION"))
                            {
                                var sdkVersion = versionClass.GetStatic<int>("SDK_INT");
                                if (sdkVersion >= 31)
                                {
                                    try
                                    {
                                        useOnDeviceRecognizer = speechRecognizerClass.CallStatic<bool>(
                                            "isOnDeviceRecognitionAvailable", activity);
                                    }
                                    catch { }
                                }
                            }

                            if (!useOnDeviceRecognizer && !defaultRecognizerAvailable)
                            {
                                pendingErrors.Enqueue("Neither Horizon on-device nor Android default speech recognition is available.");
                                return;
                            }

                            recognizer = useOnDeviceRecognizer
                                ? speechRecognizerClass.CallStatic<AndroidJavaObject>("createOnDeviceSpeechRecognizer", activity)
                                : speechRecognizerClass.CallStatic<AndroidJavaObject>("createSpeechRecognizer", activity);
                            listener = new RecognitionListenerProxy(pendingResults.Enqueue, pendingErrors.Enqueue);
                            recognizer.Call("setRecognitionListener", listener);
                        }

                        using var intent = new AndroidJavaObject("android.content.Intent", "android.speech.action.RECOGNIZE_SPEECH");
                        intent.Call<AndroidJavaObject>("putExtra", "android.speech.extra.LANGUAGE_MODEL", "free_form");
                        intent.Call<AndroidJavaObject>("putExtra", "android.speech.extra.LANGUAGE", "en-US");
                        intent.Call<AndroidJavaObject>("putExtra", "android.speech.extra.MAX_RESULTS", 5);
                        intent.Call<AndroidJavaObject>("putExtra", "android.speech.extra.PARTIAL_RESULTS", true);
                        intent.Call<AndroidJavaObject>("putExtra", "android.speech.extra.PREFER_OFFLINE", true);
                        recognizer.Call("startListening", intent);
                    }
                    catch (Exception exception)
                    {
                        pendingErrors.Enqueue(exception.Message);
                    }
                }));
            }
            catch (Exception exception)
            {
                pendingErrors.Enqueue(exception.Message);
            }
#else
            errorCallback?.Invoke("Quest microphone recognition is only active in an Android/Quest build.");
#endif
        }

        public void StopListening()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (recognizer != null)
            {
                try
                {
                    activity?.Call("runOnUiThread", new AndroidJavaRunnable(() =>
                    {
                        try { recognizer?.Call("cancel"); } catch { }
                    }));
                }
                catch { }
            }
#endif
        }

        public void Dispose()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (recognizer != null)
            {
                var recognizerToDispose = recognizer;
                recognizer = null;
                try
                {
                    activity?.Call("runOnUiThread", new AndroidJavaRunnable(() =>
                    {
                        try { recognizerToDispose.Call("destroy"); } catch { }
                        recognizerToDispose.Dispose();
                    }));
                }
                catch
                {
                    recognizerToDispose.Dispose();
                }
            }
            listener = null;
            activity = null;
#endif
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        private sealed class RecognitionListenerProxy : AndroidJavaProxy
        {
            private readonly Action<string> resultCallback;
            private readonly Action<string> errorCallback;

            public RecognitionListenerProxy(Action<string> resultCallback, Action<string> errorCallback)
                : base("android.speech.RecognitionListener")
            {
                this.resultCallback = resultCallback;
                this.errorCallback = errorCallback;
            }

            public void onReadyForSpeech(AndroidJavaObject parameters) { }
            public void onBeginningOfSpeech() { }
            public void onRmsChanged(float rmsdB) { }
            public void onBufferReceived(byte[] buffer) { }
            public void onEndOfSpeech() { }
            public void onError(int error) => errorCallback?.Invoke($"Android speech error {error}.");
            public void onEvent(int eventType, AndroidJavaObject parameters) { }
            public void onPartialResults(AndroidJavaObject partialResults) => ProcessResults(partialResults);

            public void onResults(AndroidJavaObject results) => ProcessResults(results);

            private void ProcessResults(AndroidJavaObject results)
            {
                if (results == null)
                    return;

                var matches = results.Call<AndroidJavaObject>(
                    "getStringArrayList", "results_recognition");
                if (matches == null)
                    return;

                var count = matches.Call<int>("size");
                for (var i = 0; i < count; i++)
                {
                    var text = matches.Call<string>("get", i);
                    if (!string.IsNullOrWhiteSpace(text))
                        resultCallback?.Invoke(text);
                }
            }
        }
#endif
    }
}
