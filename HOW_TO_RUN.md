# How to Run — Beyond: VR Experience

> **Important:** this repository is a portfolio showcase. It does **not** contain the full Unity project or a playable build. The full project (about 7.5 GB, including licensed third-party assets) is kept in the team's private repository. The steps below describe how that project is set up and run, based on its settings.

## Hardware

- **Meta Quest headset** (the project targets Android with OpenXR **Meta Quest Support**)
- **Touch controllers** (OpenXR Oculus Touch controller profile; hand interaction poses are also enabled)
- A **microphone** (the headset's built-in mic): the phone call and the suspect interrogations are answered by voice
- A Windows PC able to run Unity 6 for development

## Software

| Item | Version |
|---|---|
| Unity Editor | **6000.3.19f1** with the Android Build Support module |
| Universal Render Pipeline | 17.3.0 |
| OpenXR Plugin | 1.16.1 |
| XR Interaction Toolkit | 3.3.2 |
| XR Hands | 1.9.0 |
| Input System | 1.19.0 |
| Meta XR Voice SDK | 85.0.1 |
| Convai SDK for Unity | 4.5.0 |
| Timeline | 1.8.12 |

All of these are declared in `Packages/manifest.json` and resolve automatically when the project is opened.

## Setup

1. Get access to the full project from the team repository and clone it with **Git LFS** installed. Large assets are stored in LFS.
2. Open the folder in **Unity Hub** with Unity **6000.3.19f1**.
3. **Convai**: enter a Convai API key in the Convai settings so the suspect characters can respond.
4. **OpenAI report (optional)**: the end-of-investigation report reads the key from the `OPENAI_API_KEY` environment variable. Without it, the report step logs a warning and is skipped.
5. **Meta Voice / Wit.ai**: the phone call uses a Wit client token. In the published `scripts/PhoneCallController.cs` it is replaced with `YOUR_WIT_CLIENT_TOKEN`.
6. On Quest, allow the **microphone permission** when prompted. The project adds the Android `RECORD_AUDIO` permission at build time.

## Scenes (Build Settings order)

1. `Final Scenes/01_Opening`
2. `our_Scenes/CLAUDE_UI`: main menu
3. `our_Scenes/exportedscene/Basic Sample`: interrogation
4. `our_Scenes/RinvestigationScene`
5. `our_Scenes/H`: crime scene (storm, evidence, phone call)
6. `our_Scenes/TestScene1`: funeral and murder cutscenes

## Build & Run on Quest

1. **File → Build Profiles** → switch to **Android**.
2. Connect the Quest by USB with developer mode enabled.
3. **Build And Run**.

You can also test in the Editor with the XR Interaction Toolkit **Device Simulator**, which is included in the project.

## Controls

| Action | Quest controller | Keyboard (Editor) |
|---|---|---|
| Grab / pick up evidence | Grip (XR Interaction Toolkit default) | — |
| Confirm a case file / start interrogation | Trigger (either hand) | `I` |
| Answer the phone (push-to-talk) | **B** (right secondary button) | `B` |
| Talk to the Loan Shark (recorder) | **A** (hold) | `R` (hold) |
| Talk to Maya | **A** (hold) | `Space` (hold) |

Walking is switched on and off by the story flow. For example, it's locked while you are seated in the interrogation room.

## Voice Interaction

- **Phone call**: after the 2-minute timer, the phone rings. Pick it up, hold **B**, and say the suspect's name ("Maya" or "Carlos" / "the loan shark"). On-screen Maya / Carlos buttons are also available.
- **Interrogations**: hold the push-to-talk input and ask questions out loud. Maya's interview ends after three questions.

## Troubleshooting

- **The voice answer isn't recognised**: check that the microphone permission was granted on the headset and that a valid Wit token is set.
- **Suspects don't reply**: check the Convai API key and the internet connection.
- **No investigation report**: set `OPENAI_API_KEY`.

## Known Limitations

- No playable build is included in this repository.
- Convai, Wit.ai, and OpenAI all need an internet connection and valid keys.
- Speech recognition is English-only.
