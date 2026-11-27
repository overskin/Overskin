# Overskin on macOS: what's ported

The Mac version is the Avalonia app in this folder (shared with Linux), with a Mac layer under it
(`Platform/Mac.cs`, `Platform/MacText.cs`). Built on Windows with `build-mac.ps1`; nothing Mac-only
counts as working until it has been tried on a real Mac.

Key: **built** = written and compiles, waiting for a test on a Mac · **tested** = confirmed on a Mac ·
**partial** = some of it · **not yet** = still to port.

## Round 1 (this build): the foundation

| Feature | Mac status | How it works on a Mac |
| --- | --- | --- |
| App starts, Dock icon, menu bar icon and menu | built | Avalonia's macOS backend |
| One copy at a time; `overskin --capture` hands over | built | shared with Linux |
| Settings and log in the Mac's usual places | built | ~/Library/Application Support/Overskin, ~/Library/Logs/Overskin |
| Global hotkeys, changeable in Settings | built | Carbon hotkeys (no permission needed): ⌃⌥S area, ⌃⌥F screen, ⌃⌥R video, ⌃⌥G GIF, ⌃⌥H recent, ⌃⌥L scrolling |
| Start at login | built | a LaunchAgent |
| Screenshot: area, window (click one), whole screen | built | `screencapture` of the display under the pointer, at Retina sharpness; overlay above the menu bar |
| Drawing on the screen before capturing | built | shared |
| Screen Recording permission: asked for, explained | built | CGPreflight/RequestScreenCaptureAccess |
| Copy picture / files / text | built | NSPasteboard (PNG and TIFF) |
| Notifications | built | AppleScript notification |
| Show in Finder, move to the Trash | built | `open -R`, NSFileManager |
| Editor: marks, pixelate, blur, smart erase, crop, turn, flip, resize, beautify | built | shared |
| Reading text (copy the text, auto-hide private details) | built | Apple's Vision text reader through osascript; Tesseract if installed |
| Pins: on top, zoom, see-through, click-through | built | NSWindow ignoresMouseEvents |
| Library: browse, sort, rename, convert, combine | built | shared (converting needs ffmpeg from Homebrew) |
| Recording a video | built | `screencapture -v`, then `avconvert` (built in) or ffmpeg makes the MP4 |
| Recording a GIF, GIF editor | built | needs ffmpeg (`brew install ffmpeg`) |
| Auto-zoom on clicks | built | CoreGraphics pointer and button state; needs ffmpeg |
| Scrolling capture | built | CoreGraphics scroll events; needs Accessibility permission, macOS 13+ |
| Step recorder (how-to guide) | built | sees clicks through CoreGraphics |
| Recent captures strip | built | shared |
| Mac self-test (`overskin --selftest`) | built | checks displays, permissions, screenshot, windows, clipboard, Vision, hotkeys, Trash |

## Round 2 (also in this build)

| Feature | Mac status | How it works on a Mac |
| --- | --- | --- |
| Text or QR code from screen, with "just copy" and letter fixing | built | Vision reads text; ZXing reads codes; the Windows letter fixer, shared |
| Snip and pin (the pin opens exactly where the snip was) | built | placed in points on the display it came from |
| Pin the clipboard picture (also a copied picture file) | built | NSPasteboard: PNG, TIFF or a Finder file |
| Capture the active window | built | the front window from CoreGraphics' window list |
| Repeat the last area | built | remembered in settings |
| Colour picker (loupe; Shift+click for several) | built | over a still of the screen |
| Ruler (drag to measure) | built | over a still of the screen |
| Hotkeys for all of these | built | ⌃⌥W window, ⌃⌥A snip, ⌃⌥O text, ⌃⌥K colour, ⌃⌥V pin clipboard |

## Round 3 (also in this build)

| Feature | Mac status | How it works |
| --- | --- | --- |
| Spotlight tool (S); smart erase moved to X, as on Windows | built | one dimming layer for all spotlights |
| Effects: border, corners, torn edge, shadow, grey/sepia/invert | built | the Windows algorithms, for premultiplied pixels |
| Watermark: text or logo, corner, middle or tiled, size, opacity, outline | built | stamped as each screenshot is saved |
| Social sizes in Beautify (X, Instagram, Story, YouTube, LinkedIn) | built | exact pixel sizes |
| Cut out the subject (background see-through, clean edges) | built | the same IS-Net model, ONNX Runtime; checkerboard behind see-through pictures |
| Search the Library by what's in the pictures | built | the same CLIP models and threshold as Windows |

The AI parts are checked on Windows by the self-test (same results as the Windows app). The Apple
silicon build uses ONNX Runtime 1.32; the Intel build uses 1.20.1, the last release with Intel Macs.

## Round 4 (also in this build)

| Feature | Mac status | How it works |
| --- | --- | --- |
| Favourites (★) and tags (#work), with a filter and #tag search | built | kept in library-marks.json beside the settings |
| Find duplicates (older copies selected, ready for the trash) | built | the Windows fingerprint, small grey copy and shape test |
| Compare two pictures (drag the line) | built | |
| Send to phone (QR code, Share and Save to Photos on the phone) | built | the Windows web server, shared |
| Viewer (← →, wheel zoom, drag, E edit, P pin), and "Open the viewer" after a capture | built | |
| Filter: everything, pictures, GIFs and videos, favourites, duplicates | built | |

## Round 5 (also in this build)

| Feature | Mac status | How it works |
| --- | --- | --- |
| Countdown before recording (off by default; dims all but a circle with 3, 2, 1) | built | |
| Stop a recording by itself after 15 s to 5 min | built | the record bar |
| Dim around the pointer (videos) | built | the Windows dimming code, applied after recording from the pointer's path; needs ffmpeg |
| Hide faces and chat names (auto-hide) | built | faces: Apple's Vision face finder (Mac only); names: the Windows rules |
| Search everything (⌃⌥J) | built | the Windows scoring |
| Ring menu (⌃⌥Space) | built | eight capture choices round the pointer |

## Round 6 (also in this build)

| Feature | Mac status | How it works |
| --- | --- | --- |
| Magnifier and crosshair lines while choosing (each a setting, as on Windows) | built | |
| Auto-tight box: T while choosing, or the setting | built | the Windows code, shared (and fixed: solid shapes were never tightened) |
| Preview card after a capture: Edit, Pin, Copy, View, Show in folder; swipe right to dismiss, left to view | built | |
| Settings search | built | filters cards and rows |
| Move old captures to the trash (a week to a year; favourites kept) | built | at start and when changed |

## Round 7 (this build): opening on a Mac, and the last gaps

| Feature | Mac status | How it works on a Mac |
|---|---|---|
| The app opens at all (Apple silicon) | built | The program is signed ad hoc during the build (Apple silicon refuses unsigned code: "damaged"). Unsigned libraries are signed the same way. Every piece of code is checked before zipping |
| The program stays runnable after unzipping | built | The zips are marked as made on Unix; before, Windows marked them as DOS and the Mac dropped the "runnable" permission |
| A crash at start leaves its reason | built | `~/Library/Logs/Overskin/overskin.log`; each start-up step (tray, hotkeys, pins, update checks) fails on its own without stopping the window |
| "Match the Mac" can't freeze the app | built | the dark-mode and accent checks run off the UI thread, with a 3-second limit |
| GIF editor speech bubble, as on Windows | built | the top of the GIF cut out as a see-through bubble (tail left, middle or right; text size sets its depth); kept see-through in the GIF, white in an MP4 |
| How-to guides: web page, Markdown, PDF, tall picture | built | self-tested |
| Recording: pause and the pen | built | |
| Accent colour, match the Mac, see-through window | built | |

## Round 8: after the first test on a Mac (it opened)

| What | Mac status | How |
|---|---|---|
| Screenshot colours true (greens were coming out blue) | built | A Mac loads pictures red-first; every pixel read is now turned to the blue-first order the rest of the app uses. Self-tested with a red-first picture |
| Colours in the display's own space turned to ordinary sRGB | built | `sips --matchTo` sRGB after each screenshot |
| The window comes back after a capture | built | it reappears when the editor (or video editor, viewer) the capture opened is closed; clicking Overskin in the Dock always opens it; pictures opened with Overskin from Finder go to the editor |
| The look matches the Windows app | built | same layout (top bar with Capture, Library, Pins, Settings; Screenshots/Recording; tiles; folding cards with summaries; status line), the same colours worked out the same way, the same Lucide icons (generated from the Windows icon file), the Mac's own font, and the traffic lights in the top bar |
| Editor laid out as on Windows | built | tools along the top, Pin / Copy / Save as / Save along the bottom |
| Pins page | built | pin an image (paste, snip, browse), pin a window live, the list with click-through, and the shortcuts |
| Edit the clipboard | built | opens the copied picture in the editor |

## Still to port from the Windows app

Nothing known. Captures cover the display the pointer is on (the Windows overlay spans every
monitor at once); everything else on Windows has a Mac counterpart. What's left is testing on a
real Mac.

## Opening it on a Mac

1. Unzip the zip for your Mac (Apple silicon: M1 and later; Intel: older Macs) and drag Overskin to Applications.
2. Once, in Terminal: `xattr -cr /Applications/Overskin.app` (or open it, then System Settings › Privacy & Security › Open Anyway).
3. The first capture asks for Screen Recording; switch Overskin on, then quit and reopen it. A new build is a new signature, so macOS may ask again after an update.
