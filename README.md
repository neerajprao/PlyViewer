# PLY Viewer

Open a Gaussian-splat `.ply` file in Unity and fly through it with W A S D.

Built with Unity 6000.6.6f1 and [UnitySplats](https://github.com/arloopa/UnitySplats) (MIT), which renders the splats on the GPU (Metal on a Mac).

## Open a file

**From Terminal**

```bash
cd ~/Desktop/CGVR/PlyViewer
./view-ply /path/to/scene.ply
```

Unity opens this project, loads the file and presses Play for you. If Unity is already open, the new file replaces the current one. The first start imports packages and takes a few minutes; after that it's quick.

**From inside Unity**

Menu bar: **PLY Viewer → Open .ply File...**, then choose the file. Play starts by itself.

## Controls

Click inside the **Game** view first, so the keys go to the viewer.

| Key | Action |
| --- | --- |
| W A S D | move |
| Mouse | look around |
| Space / Ctrl | up / down (also E / Q) |
| hold Shift | faster |
| Scroll wheel | change speed |
| R | back to the start view |
| P | save the current view as this file's start view |
| F | flip upside down (if the scene loads upside down) |
| T | tilt 90° (for files whose "up" is a different axis) |
| H | hide or show the help box |
| Esc | release the mouse |

Press ▶ again to stop. For a full-screen view, set the Game tab's **Play Focused** option to **Play Maximized**.

## Good to know

- **Start view.** The viewer looks for an open spot inside the scene facing the most splats. A `.ply` doesn't record where the original camera stood, so for a demo, fly to the best spot once and press **P**. That file then always opens there, and **R** returns there.
- **Orientation.** Files from Brush include the scene's "up" direction and load upright. Other files use the usual 3DGS convention. If one comes out upside down or sideways, press **F** or **T**; the choice is saved along with **P**.
- **Detail.** Sharpness comes from the `.ply` itself: how good the capture and training were. Splats look best near where the camera moved during capture.
- **GPU.** Rendering is capped at 60 fps. Quit Unity (Cmd+Q) when you're done, because splats keep the GPU busy while Play is running.

## Project layout

```
view-ply                                   open a file from Terminal
Assets/PlyViewer/Scripts/PlyViewer.cs      loads the .ply, places the camera, on-screen help
Assets/PlyViewer/Scripts/PlyInfo.cs        reads the PLY header and a sample of splat positions
Assets/PlyViewer/Scripts/FlyCamera.cs      W A S D / mouse fly camera, no physics
Assets/PlyViewer/Editor/PlyViewerEditor.cs PLY Viewer menu; picks up files sent by view-ply
Assets/Scenes/PlyViewer.unity              the viewer scene
Packages/manifest.json                     UnitySplats (pinned), Input System
```
