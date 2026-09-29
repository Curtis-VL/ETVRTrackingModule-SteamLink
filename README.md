# ETVR VRCFT Tracking Module 

ETVR Tracking mode is a VRCFT addon dedicated to EyetrackVR Project.
It acts as middle ground for translating OSC messages sent by EyetrackVR to a format understandable by VRCFT project. 

This specific fork of the module integrates the gaze data from Steam Link OSC.
This can be used to smooth out the gaze tracking from the Steam Frame whilst using EyeTrackVR for other features such as lid position.

With it:
- ETVR can work with whichever game VRCFT supports. 
- Users don't have to worry about setting up a different set of params than those required by VRCFT
- ETVR get's to be forever(*) compatible

## How to use this: 

### Module installation

First, make sure the original EyeTrackVR module is **not** installed in VRCFaceTracking, this will conflict with this plugin.

(Check 'Module Registry' in VRCFaceTracking to see if it is installed)

Next, download the module DLL file [from releases pages](https://github.com/Curtis-VL/ETVRTrackingModule-SteamLink/releases)

Paste this into your Start search bar and open the folder it suggests: `%appdata%\VRCFaceTracking\CustomLibs`

If the directory doesn't exist, feel free to create it.
Don't forget to replace the `{your_user}` part with your pc's name. 

    # For example:
    
    from:
    
    `C:\Users\{your_user}\AppData\Roaming\VRCFaceTracking\CustomLibs\`
    
    to:
    
    `C:\Users\lorow\AppData\Roaming\VRCFaceTracking\CustomLibs\`

### SteamVR setup

In the SteamVR Settings on your desktop...

Steam Link > Enable OSC > On
Steam Link > Share face tracking data to other apps on this PC via OSC > On
Steam Link > OSC Output Port > 9015 (ALT)

### EyeTrackVR app setup

You'll need to change the `Port` in `Settings` from `9000` to `8889`.

Settings will save automatically, but for them to take effect, you'll need to restart the app.

### You're done!

Start VRCFaceTracking!


## Want more info about the EyeTrackVR VRCFaceTracking module?

See the original repo here: [ETVRTrackingModule](https://github.com/lorow/ETVRTrackingModule/).

