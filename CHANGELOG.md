# Changelog

## 2.5.0

- **Live text in the editor**: with the Select tool (V), drag over the words in a screenshot to
  select them like in a PDF. Ctrl+C copies them, Delete erases them (smart erase fills in from
  around them), and right-click can also hide them. Ctrl+A selects all the text.
- **Copy text** button in the editor (Ctrl+Shift+C): every word in the picture onto the
  clipboard, with a message saying so.
- **Opacity** slider next to size in the editor, for every mark (hidden areas stay solid).
- **Capture toolbar colours**: the full palette, a colour wheel with brightness, a colour code
  box, and Pick, which takes a colour from anywhere on the frozen screen.
- **Step recorder**: each click shows a pointer in its circle, and in apps and games that don't
  name their buttons the step reads the words where you clicked ("Click “Shop” in Fortnite").
- **Auto-hide** finds more and misses less: card numbers that the text reader garbles
  (1s read as l or I, or a line it skips), the card's expiry date and security code when
  the number is there too, IPv6 addresses, folder paths, street addresses, cities, states,
  postcodes and countries. Right-click the Auto-hide button (or its setting) to choose
  which of them it hides.
- Overskin is Windows-only for now; Linux and macOS versions are planned.

## 2.4.0

- `OverskinSetup.exe`: a per-user installer (no administrator rights) with Start
  menu and desktop shortcuts, start with Windows, and an uninstaller in Settings → Apps.
  The installer is the only download; settings now live in `%AppData%\Overskin`.
- Removed sound effects.
- Removed uploading to catbox.moe and Litterbox, which had become unreliable.
- **Video**: record an area, window or screen to MP4. **Auto-zoom** glides in on clicks,
  follows the pointer, and draws a bigger, smoothed pointer with click rings.
- **Scrolling capture**: scrolls a window for you and stitches the whole page into one picture.
- **Combine** pictures side by side, stacked or as a grid collage, from the Library or the
  tray, and open the result in the editor.
- The tray menu has every feature, in groups; the taskbar jump list has the main captures.
- Library: Videos filter.
- Capture is split into **Screenshots** and **Recording**. Recording has the video settings:
  frame rate, quality, pointer look, size and smoothing, click rings, and auto-zoom amount,
  speed, follow smoothing and zoom-out delay.
- Recorded pointers are drawn from vector shapes, so they stay sharp at any size and zoom.
- **Light mode** and a **theme** list: Graphite, Carbon, Ink, Black, Light, Paper and editor
  palettes (Visual Studio, Dracula, Nord, Gruvbox, Monokai, One Dark, Tokyo Night, Catppuccin,
  Solarized, GitHub, Serika).
- Editor: **Hide** has blur, pixelate and mosaic, sized with the slider; **Auto-hide** finds
  and hides emails, phone numbers, card numbers, IP addresses and keys (also available
  automatically for every screenshot); **Smart erase** fills in what you paint over;
  **Beautify** sets the picture on a backdrop with padding, corners and a shadow.
- Library: **Convert to** PNG, JPG, BMP, TIFF, GIF or MP4, from any picture, GIF or video.
- Fixed: Overskin crashed when started hidden, which is how *Start with Windows* starts it.
- **Recent captures** strip (Ctrl+Alt+H) and dragging from the after-capture card, straight
  into other apps.
- **Steps** recorder: a numbered how-to picture, one screenshot per click.
- **Search** the Library by the words inside captures.
- Capture bar: keep the area to a shape or an exact size; windows can be captured on their
  own with a transparent background and shadow.
- **Capture bar** on the capture screen: area, window, screen or all screens; draw with a pen,
  arrows, boxes or a highlighter before capturing; pointer and magnifier on or off.
- Overskin's own messages, recording bar and recording outline never appear in screenshots or
  recordings (they're now drawn as solid windows, which Windows can hide from captures).
- Theme list is a plain dropdown; tabs no longer slide outside the window.
- Editors have the same rounded shape as the main window; the editor toolbar wraps onto a
  second row in a narrow window instead of running under Undo and Redo.
- Editor: rotate, flip and resize the picture.
- Library: **Copy** puts the real file on the clipboard (same name; GIFs and videos paste as
  themselves); **Rename** and **Select** several to copy, combine, convert, rename (with a
  pattern) or delete together; sort by date, name or size; copy a file's path.
- Capture: arrow keys move the pointer one pixel (Shift: ten) for exact selections, and Enter
  captures the window under the pointer.
- Library: filter by All, Pictures or GIFs.
- Trace: **Fill with bucket** for the Outline and Colour styles. Big areas are outlined and
  filled with one click of the game's bucket, thin areas are drawn as one stroke down the
  middle, and soft-edge slivers are cleaned up first. About 3x faster on a typical cartoon.
- Trace: colour switches wait a few screen refreshes instead of a fixed 140 ms (Careful keeps
  the old timing).
- Cleaner interface: removed explanatory captions (descriptions moved to tooltips), consistent
  section headers, button sizes and shortcut labels.
- Crash reports: if Overskin ever crashes it saves a short, anonymous report to
  `%LocalAppData%\Overskin\logs`. Open the folder from Settings → About.
- Editor: swatches have an outline, so black is visible on the dark toolbar.

## 2.3.0

- Editor: zoom (Ctrl+wheel, Ctrl+0/1/+/-), resize handles, right-click menu, double-click
  to edit, filled shapes, colour wheel, upload.
- Pinned pictures: double-click to transform with handles.
- Set area shows the original picture instead of a laggy trace preview.
- First sound effects.

## 2.2.0

- GIF recording with the mouse pointer, Discord-style captions and a GIF editor.
- Uploads to catbox.moe or Litterbox, with the link copied.

## 2.1.0

- Trace catalog, speed presets (Optimized follows your monitor's refresh rate).
- Tab and window animations.

## 2.0.0

- Renamed to Overskin and rebuilt around capturing: region, window and full screen
  captures, an editor, a library, text from screen, colour picker and ruler.
