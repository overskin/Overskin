# Changelog

## 2.7.0

### New

- **Viewer**: a click in the Library (or, if you like, every capture) opens in a viewer instead of the
  editor. Arrow keys go through all your captures, the wheel zooms smoothly towards the mouse,
  drag to look around, double-click for actual size, and pixels stay sharp up close. GIFs and
  videos play. Copy, rotate (saved to the file), pin, show in the folder, delete, or press E
  for the editor. Turn on *Open in the viewer* on the Capture page to see each capture there.
- **Hotkey for video**: Ctrl+Alt+R starts and stops a video recording (change it in Settings).
- **Text styles** in the editor: normal, bold or italic.

### Fixed

- **The capture screen no longer stutters on several monitors.** It was one window stretched
  across every monitor, and with monitors at different refresh rates (360, 144 and 60 Hz, say)
  Windows showed it at the slowest pace. Each monitor now has its own window.
- **Scrolling capture in chats**: when the page doesn't move down (a chat already at its newest
  message), it scrolls up through the history instead, and waits a moment when the app loads
  more. The "Scrolling…" message no longer shows up as a black box in the picture.
- Questions such as *Save your changes?* are in Overskin's own style instead of Windows' grey box.

- **Clicking or hovering a window on the capture screen** picked the whole screen when an
  app such as Medal, the NVIDIA overlay or Discord's overlay kept an invisible layer over
  everything. Those see-through layers are now skipped, so windows highlight and capture again.
- **The capture screen is smoother**: a fast mouse sends many moves per screen refresh; only
  the newest is drawn now, and the dimming isn't rebuilt on every move.
- **Snap to pictures works on real screens**: pictures placed half a pixel off (soft edges),
  memes and cartoons with flat colours, and pictures with things lined up right beside them
  are found, edge to edge.
- **Scrolling capture no longer stops at the first animation**: rows are compared closely
  rather than exactly, so a blinking cursor, a spinner, a floating button or a header that
  gains a shadow don't stop it, and it looks again when the page is still gliding.

### Editor

- **Move things in a screenshot** (Move tool, M): click a word or a picture, or drag a box
  around anything (even a single letter), and it lifts off as a piece you can drag anywhere or
  scale from a corner (Shift stretches freely). The tool stays picked, ready for the next. The gap fills
  in from what was around it. On a plain background the piece comes off without its
  background, soft edges and all. Right-click selected text for *Move it*.
- **Swap a picture**: right-click a picture inside a screenshot for *Replace with a picture
  file* or *Replace with the clipboard picture*. The new one fills the same box with the same
  rounded corners. *Move this picture* is there too.

### Video

- **Cut idle parts**: one click finds every stretch of 2 seconds or more where nothing on
  screen moves and cuts it out.
- **Cut anything out**: *Cut from here*, then *End the cut here* (or X). Cuts show in red
  under the video and are skipped while it plays.
- **Text and arrows**: type a label and *Add text*, or *Add an arrow* and drag on the video.
  Each shows for 3 seconds from where the video is; drag it into place and set *From* and *To*.

### Everything else

- Numbered steps are gone from the editor.
- **Watermark tiling** has its own settings: angle, spacing and staggered rows, and text can
  be an outline only.
- **Pins page**: the shortcut list sits right under your pins, and the subtitle no longer
  cuts off.
- **Recording page**: auto-zoom's fine-tuning (speed, follow, zoom-out time) is under *More*;
  the backdrop list shows each one as a swatch.
- **Settings**: hotkeys are grouped under Capture, Pins and windows, and Drawing.
- **Screen tools**: *Pin the clipboard* fills the empty spot.

## 2.6.1

- **Snap to pictures**: on the capture screen, hovering over a picture inside a window (a photo
  someone sent in Discord, an image on a web page, a thumbnail) outlines just that picture,
  rounded corners included, and a click captures it edge to edge. Hold Ctrl for the whole
  window instead. Pictures are found in the background as the capture screen opens, so it
  never slows it down.

## 2.6.0

### Video

- **Smooth cursor** (on by default): the pointer is recorded on its own, faster than the video,
  and drawn a fraction of a second later. It glides with no lag, eases in and out of every move,
  loses any shake, lands exactly on each click, and blurs along its path while it moves.
  *Smoothing* and *Motion blur* are adjustable.
- **Backdrop**: put the recording on a gradient with rounded corners and a soft shadow.
- **Keyboard shortcuts on screen**: shortcuts such as Ctrl + C or Alt + Tab appear near the
  bottom of the video. Ordinary typing is never shown.
- **Sound**: system sound, a microphone (pick which), or both.
- **Webcam**: you in a circle or rounded square in any corner, any size.
- **Faster capture**: the screen is read straight from the graphics card, about three times
  faster, with the old way as a fallback (an area across two screens, a rotated or HDR screen).
- **Trim**: click a video in the Library, set Start and End (or press I and O), and save the
  part in between as a new file, sound included.
- **Steps as a guide**: reword or drop steps, add a title, and save it as a picture, an HTML
  page, Markdown or a PDF.

### Editor

- **Live text by the letter**: drag to select single letters, double-click for a word, click
  for a text cursor. Backspace and Delete remove letters and the line closes up.
- **Type into screenshots**: put the cursor in a line and type. Overskin works out the font
  (Segoe UI, Arial, Calibri, Cambria, Georgia, Times New Roman, Verdana, Tahoma, Trebuchet MS,
  Consolas, Courier New), its size, colour and ClearType edges from the picture, so new letters
  blend in. Works on plain backgrounds.
- The editor opens with the Select tool, ready to click into text.
- **Watermark**: text or a picture on screenshots and videos, in a corner, the middle or tiled.

### Pins

- **Free Transform**: click a pin for a Photoshop-style box to move, scale, stretch and rotate
  it. Shift and Alt work as you'd expect, Enter commits, Esc puts it back.
- **Pin the clipboard** from anywhere with Ctrl+Alt+V, the Paste button, the tray or the
  taskbar jump list.
- **Compare**: two pictures on top of each other with a wipe or a fade between them.

### Trace

- **Gartic Phone style**: paints with the game's five brushes and ten opacity levels, layering
  colours for shades the palette doesn't have. Setup is a checklist that ticks itself off.
- **Draws like a person**: outlines first as continuous strokes, then colour up to the lines,
  then detail. Smooth curves instead of dots, each stroke speeding up and slowing down like a
  hand, with the pen lifting between strokes.
- Removed *Fill with bucket* and the *Instant* speed.

### Everything else

- **Workflows** (Settings): one hotkey for a whole routine: capture, hide private details,
  beautify, watermark, save, copy the picture or its text, pin, edit. New workflows are named
  after what they do ("Copy text from an area"), and that's what the tray menu shows.
- The tray menu's submenus all open the same way.
- Consistent capital letters on labels across the app.
- Fixed: clicking a window to capture it could do nothing when *Windows on a transparent
  background* was on.
- Fixed: picking a colour on the capture bar also took the screenshot.
- Fixed: trimming a GIF while it played made the preview jump.

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
