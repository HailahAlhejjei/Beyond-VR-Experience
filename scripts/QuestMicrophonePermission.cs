using UnityEngine;

#if UNITY_ANDROID && !UNITY_EDITOR
using UnityEngine.Android;
#endif

namespace MyStuff
{
    /// <summary>Requests the Quest runtime microphone permission before push-to-talk opens.</summary>
    public static class QuestMicrophonePermission
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        private static bool requestInFlight;
#endif

        public static bool CanRecord()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (Permission.HasUserAuthorizedPermission(Permission.Microphone))
            {
                requestInFlight = false;
                return true;
            }

            if (!requestInFlight)
            {
                requestInFlight = true;
                Permission.RequestUserPermission(Permission.Microphone);
            }

            return false;
#else
            return true;
#endif
        }
    }
}
