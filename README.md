# VRChat Billiards (New Networking Patch)

This is the repository for improving the underlying netcode of VRCBilliards. It may be behind Xieve's main project, or ahead of it, depending on the progress in the following TODOs.

This codebase is provided as-is, and is recommended for developers only.

TODOS:

1. Refactor VRCBilliards to have a more usable and maintable code style and structure.
2. Address current stability concerns (as they arise)
3. Port VRCBilliards to the new Udon Reliable Sync functionality (when it becomes available).

![](https://i.imgur.com/WsYGyHY.png)

## For World Creators
Follow the Collisions layers steps first before importing package!
This project can be downloaded from [The releases page](https://github.com/Xiexe/VRCBilliards/releases)
or by downloading the source, and extracting the zip into your project.

### Dependencies / Setup
- Install VRCSDK 3
- Install [Udon Sharp](https://github.com/MerlinVR/UdonSharp)
- Import the package OR unzip the Source Code into your project into it's own folder.

#### Collision layers
There are some objects that need to be set to only collide on a seperate layer. This is very important!

Recommended steps:
- Edit > Project Settings > Tags and Layers
- Set User Layer 23 to: `ht8b`
- Edit > Project Settings > Physics
- In the collision matrix deselect all apart from itself for the new layer as so:

![](https://i.imgur.com/jhku3V2.png)

### Quest / PC Toggles
The project includes some small scripts to change / toggle stuff between quest/pc versions

It has to be manually changed


On the top of the prefab there is one:

![](https://i.imgur.com/HPtMBiH.png)

And in the scene `__MAIN__` also has one of these scripts

### Caveats
- This project is currently not designed / tested with more than one instance of a table in a world and is currently unsupported
