Avatar Light Edit
=====================
(한국어: README.txt · 日本語: README.ja.txt)

A Windows program for previewing and editing the look of a VRChat avatar (materials, colors, blendshapes, outfits, poses) without opening Unity.
It is free and open source (GPLv3). It is not made or endorsed by VRChat or Unity.


Requirements
------------
- Windows 10/11 64-bit, a graphics card that supports DirectX 11
- You don't need to install .NET or Unity to run and view (.NET is included)
- Optional, for more features
  - Unity 2022.3 (the version VRChat specifies): uploading to VRChat, exporting to a Unity project, building shader packs from your own shaders
  - Microsoft Visual C++ Redistributable (x64): webcam face tracking. If it's missing, you'll be guided to install it when you turn webcam tracking on
  - SteamVR: viewing in VR


Install and first run
---------------------
1. Extract the zip as it is, wherever you like. A place your account can write to, such as Documents, is best.
   In Program Files the updater can't copy files. If the folder path is too long the program won't start; move it somewhere shorter when prompted.
2. Run ALEdit.exe.
   The program is not code-signed, so a "Windows protected your PC" window may appear.
   Click "More info" -> "Run anyway". Please check first that you downloaded it from the official release page.
3. Press Ctrl+I to import an avatar .unitypackage or a prefab from a Unity project.
   The screen language can be changed in Settings (Ctrl+,): 한국어 / English / 日本語.

To uninstall, delete the folder. Settings and imported files stay in the folders below; delete them too if you don't need them.
  %APPDATA%\ALEdit       settings, crash logs
  %LOCALAPPDATA%\ALEdit  imported avatars, caches, upload projects


Updates
-------
When a new version is out, "Update" appears at the bottom left. Click it to download, replace the program folder and restart.
Your settings and work files stay as they are. You can turn off automatic checks in Settings > Update.


FAQ
---
Q. Are my avatar's original files changed?
A. No. Originals are only read. Your edits are saved separately in the session file and in %LOCALAPPDATA%\ALEdit.

Q. Some materials show as stripes.
A. That shader isn't included in the program. Its values are kept as they are. If you have Unity, you can build a shader pack to see it.
   lilToon, Poiyomi (free version) and MToon are included by default.

Q. Does it look exactly the same as in VRChat?
A. It is close, but not identical. PhysBone is an approximate simulation based on the VRChat documentation,
   and animators / FX layers are not reproduced. Please do the final check in VRChat before uploading.

Q. Do I enter my VRChat account into this program when uploading?
A. No. You log in yourself in the VRChat SDK window in Unity. The SDK is not included in this program either;
   it is downloaded to your PC from VRChat's official repository when you upload. Please follow the VRChat Terms of Service and your avatar's license yourself.

Q. It doesn't start, or it closes suddenly.
A. See "Reporting problems" below.


Reporting problems
------------------
Settings (Ctrl+,) > About > "Report a problem" opens the folder with crash logs (crash-date.txt).
Please attach the latest log file to an issue on the release page's Issues tab, along with what you were doing.
Logs may contain avatar names and PC paths (including your user name), so read them before posting and hide anything you want.
There is no feature that sends logs automatically.


Privacy
-------
This program does not send usage data or avatar files anywhere. It only uses the internet for:
- Update check: asks GitHub for the latest version at startup (can be turned off in Settings)
- Google Translate: only if you enter your own API key and turn on "Google Translate" on the BlendShape page; shape key names are then sent to Google Cloud.
  Fees and terms follow your own Google account
- Preparing a VRChat upload: downloads the VRChat SDK and the gimmicks you chose from their official repositories
Webcam and microphone input (face tracking, lip sync) is processed only on this PC and is never saved or sent.
If you connect an external program that sends face tracking over VMC / OSC, that program's policy applies.


License
-------
Free software distributed under the GNU GPL version 3 (GPLv3). A plain explanation and the additional permissions are in LICENSE.txt,
the full GPLv3 text is in COPYING.txt, and notices for bundled open source are in THIRD_PARTY_NOTICES.txt.
The source code is available from the GitHub repository you downloaded this program from, under the same version tag.
