#if UNITY_ANDROID
using System.IO;
using System.Xml;
using UnityEditor.Android;

internal sealed class QuestMicrophoneManifestPostprocessor : IPostGenerateGradleAndroidProject
{
    private const string AndroidNamespace = "http://schemas.android.com/apk/res/android";
    private const string RecordAudioPermission = "android.permission.RECORD_AUDIO";
    private const string InternetPermission = "android.permission.INTERNET";

    public int callbackOrder => 100;

    public void OnPostGenerateGradleAndroidProject(string path)
    {
        var manifestPath = Path.Combine(path, "src", "main", "AndroidManifest.xml");
        if (!File.Exists(manifestPath))
            return;

        var document = new XmlDocument();
        document.Load(manifestPath);
        var manifest = document.DocumentElement;
        if (manifest == null)
            return;

        EnsurePermission(document, manifest, RecordAudioPermission);
        EnsurePermission(document, manifest, InternetPermission);
        document.Save(manifestPath);
    }

    private static void EnsurePermission(XmlDocument document, XmlElement manifest, string permissionName)
    {
        var permissions = manifest.SelectNodes("uses-permission");
        if (permissions != null)
        {
            foreach (XmlNode permission in permissions)
            {
                if (permission.Attributes?["name", AndroidNamespace]?.Value == permissionName)
                    return;
            }
        }

        var permissionElement = document.CreateElement("uses-permission");
        permissionElement.SetAttribute("name", AndroidNamespace, permissionName);
        manifest.PrependChild(permissionElement);
    }
}
#endif
