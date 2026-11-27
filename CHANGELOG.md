# Changelog

## 3.3.0

### New

- **Overskin for Mac**: the same app on macOS 12 and later, for Apple silicon and Intel Macs.
  Captures, the editor (live text, auto-hide, cut out, beautify), recording with auto-zoom, a
  smooth pointer, sound and the webcam, GIFs and the GIF editor, the video editor with
  subtitles, pins, the library, steps, workflows and hotkeys, laid out and coloured as on
  Windows. Download `Overskin-mac-AppleSilicon.dmg` or `Overskin-mac-Intel.dmg` from the release.
- **Cut out the subject**: *Cut out* in the editor (and the wand in the viewer) keeps the person,
  pet or thing in front and makes the background see-through, with clean edges. Worked out on
  your PC. See-through pictures show on a checkerboard in the editor, copy with their see-through
  parts, and always save as PNG.
- **Search by what's in the picture**: the Library finds "cat", "chart" or "minecraft" in
  captures with no such words in them. Each capture is looked at once, in the background, on
  your PC.
- **Favourites and tags**: star captures and tag them (#work) in the Library, then search by tag.
- **Find duplicates** in the Library, and **send to your phone**: a QR code opens the capture
  on your phone over your home Wi-Fi, with *Share* and *Save to Photos* buttons.
- **Instant replay**: Overskin can keep recording the main screen in the background and save the
  last 15 seconds to 2 minutes with a hotkey (Ctrl+Alt+F10), sound included.
- **Before recording**: an optional 3-2-1 countdown (off unless you turn it on: the area darkens
  except a clear circle with the number and a ring running down), *Stop after* a set time, and
  *Dim around the pointer* so viewers' eyes follow it.
- **Spotlight** in the editor (S): drag boxes round what matters and everything else darkens.
- **Social sizes** in Beautify: the picture laid out for an X post, Instagram square or story,
  YouTube thumbnail and more.
- **Clickable text**: links, email addresses and phone numbers in a screenshot open with a click
  in the editor and the viewer. Double-click one to edit its text instead.
- **Hide faces and chat names**: Auto-hide can also find faces and Discord-style names.
- **Auto-tight box**: press T while choosing an area and the box shrinks to fit what's inside,
  solid shapes (buttons, coloured panels) included.
- **Captions**: word-by-word captions (each word lighting up, or building up) and four looks
  (Classic, Bold, Clean, Boxed) in the video editor; a speech-bubble style for GIFs; and *Make a
  GIF* from a stretch of a video, subtitles included. (*Caption a GIF* on the Recording page is gone.)
- **Command search** (Ctrl+Alt+J) finds any action or setting by typing, and the **ring menu**
  (Ctrl+Alt+Space) puts the capture choices round the pointer. Settings has a search box too.
- **Windows' look**: *Use Windows' accent colour*, and a *See-through window* with the blurred
  background of Windows' own apps, on Windows 11 and Windows 10 alike (on Windows 10 the window
  keeps its own rounded corners), tuned so light themes stay clean rather than grey.
- **Text from screen: just copy it**: a switch (Capture, After capturing) that copies the text
  without opening a window. The reader also fixes letters and numbers it mixes up, from what's
  round them ("HELL0" becomes HELLO, "2O26" becomes 2026), leaving codes and addresses alone.
- **Fine-tuning typed letters**: typing into a screenshot's text shows a bar to change the font,
  size and weight when the matched style is a little off. It restyles the selected text (or the
  word the cursor is in) as well as what's typed next.
- **Editing a screenshot's text is cleaner**: taking out or moving words no longer leaves thin
  lines or the tails of letters behind, more of the fonts on your PC (and their real weights) are
  tried when matching, and links open with one click. Inter and Roboto, which many apps and
  websites use, now come with Overskin (about 400 KB), so text in them is matched exactly.
- **The Library header** no longer overlaps: the count and progress sit on their own line under
  the search, and the Duplicates view stays until they're dealt with.

- **Overskin on the Microsoft Store**: a Store version alongside the GitHub installer, with the
  same features. It's signed by Microsoft (no SmartScreen warning), updated by the Store, and
  makes no network requests. Start with Windows uses the Store's startup task, listed in Task
  Manager → Startup apps. The Store and GitHub copies keep separate settings.
- **Subtitles, made for you**: *Make subtitles* in the video editor listens to what's said and
  writes it out as timed subtitle lines, on your PC with nothing sent anywhere. It works out the
  language by itself (about 100 of them). Each line lines up with the voice, so nothing shows
  during pauses. Fix any word by typing over it, remove a line, or drag the subtitles on the
  video to move them all. Saving puts them into the video, and also writes an `.srt` file beside
  it for YouTube or VLC, with times that follow your trims and cuts. It uses OpenAI's Whisper
  speech model, which comes inside Overskin (the installer is about 58 MB bigger for it).
- **Swipe the preview card**: after a capture, drag the card left to open the capture in the
  viewer, or right to send it away, like Windows' notifications. Let go early and it springs
  back. The thumbnail still drags the picture out into other apps.
- **Choose how drawings fade while recording**: *Drawings fade away* turns fading on or off,
  and *Fade after* sets how long each stays (half a second to 15 seconds). With fading off,
  what you draw stays until you stop drawing, then fades all together.

- **Fonts for captions and text**: the GIF editor and the picture editor's text tool have a font
  picker, each font shown in its own look. Six free fonts come with Overskin (Anton, Bebas Neue,
  Oswald, Bangers, Permanent Marker, Comic Neue), alongside Impact, Segoe UI and others from
  Windows. *Add a font…* loads any font file you have, and it stays for next time.
- **The Discord caption look**: white-bar GIF captions now use Anton, the closest free match to
  the Futura Condensed Extra Bold those memes use. If Futura is installed, it's used instead.
- **More GIF editor options**: the caption bar can be white, black or yellow and go above or
  below the GIF; top-and-bottom text can be white, black or yellow. New speeds (¼× and 3×), play
  forever, once or three times, colour looks (grey, sepia, vivid), mirror, and *Fewer frames* for
  about half the file size.
- **Taking screenshots settings**: a new card on the Capture page for the magnifier (on by
  default; the capture bar's button changes it too) and crosshair lines across the screen.
- **Smoother GIFs**: a new GIF card on the Recording page sets the frame rate before you record:
  15, 30 (now the default, up from 15) or 50 fps. 50 is the smoothest a GIF can play; Discord
  and browsers slow anything faster down. Big areas at 50 fps can record for less than 30
  seconds, and the record bar shows the real limit.

### Improved

- **Copying is instant**: the picture goes on the clipboard the moment it's taken, before it's
  saved and before Overskin's window comes back, so Ctrl+V works straight away. The saved file
  joins it a moment later, so pasting into a chat or a folder still keeps its name.
- **Fewer extra lines of text**: the short descriptions under headings and buttons (such as
  "Whole page", "How-to guide", "Drop, paste or snip" and the notes in the video and GIF
  editors) are gone, and hotkey names are shorter.
- **Cropping works like a photo app**: Crop in the GIF editor starts with the whole picture
  framed, with thick corner and edge handles to pull in. Drag inside to move it, Enter or Done
  keeps it, Esc or Cancel puts it back. The video editor's crop starts framed the same way.
- **The preview card swipes smoothly**: it moves once per screen refresh, measured on the screen,
  and fades all the way out as it opens the viewer.

- **Snapping to pictures finds far more of them.** Pictures whose corners aren't all the same
  (a chat bubble's tail, a card with only its top corners rounded, gallery tiles), iOS-style
  corners, oval or cut corners, very round corners, a corner covered by hover buttons, dark
  photos in a dark chat, pictures on frosted or noisy backgrounds, a picture right under a
  header, image-search grids with 2 px gaps, and screenshots of apps or web pages posted in a
  chat (with the pictures inside them found too).
- **Fewer wrong snaps**: buttons, key labels, coloured calendar events and spreadsheet cells
  are no longer taken for pictures, and a picture with a horizon or a caption strip across it
  is snapped to whole, not in halves.

### Changed

- **Trace has moved to its own project.** The Trace tab (turning a picture into mouse strokes
  and drawing it in Paint, Krita, skribbl.io or Gartic Phone) is no longer part of Overskin.
  The tag `v3.2.1` is the last version that includes it. Pins as tracing paper (fade, click-through
  and the drawing grid) stay in Overskin.
- Ctrl+Alt+D and Ctrl+Alt+P now only draw on the screen and pause while a video records. The
  *Stop drawing* shortcut (Esc or Ctrl+Alt+X) is gone with Trace.
- The after-capture card, the editor, the Library and pinned pictures no longer have a Trace
  button. The card shows Edit, Pin, Copy and Show in folder.
- Old settings files load as before; the Trace settings in them are ignored.

## 3.2.1

### New

- **Full screen in the viewer**: the new button next to Fit, or F or F11, fills the whole monitor
  (taskbar too) with just the picture or video on black. A double-click on a video does the same.
  Esc, or the button in the corner, comes back to where the viewer was. The button and the
  pointer fade away when the mouse rests.
- **The viewer opens beside Overskin** on the side with more room, instead of in the middle of
  the screen on top of it. With no room either side it opens in the middle, above Overskin.

### Fixed

- The 3.2.0 download on GitHub was the 3.1.0 installer by mistake, so the fixes in 3.2.0 (like
  the even gaps on the Capture page) weren't in it. This release has them all.

## 3.2.0

Overskin updates itself, crops videos and GIFs, and snaps to round pictures.

### New

- **Updates from inside the app**: when a newer Overskin is on GitHub, a banner under the tabs
  says so, with *What's new* and *Download and install*. The installer is downloaded from
  Overskin's own GitHub release, checked (its size and version), and installed without a window
  in a few seconds; then Overskin opens again. Your settings, pins, captures, shortcuts and
  start-with-Windows choice stay as they are. It checks when Overskin starts and twice a day;
  turn that off, or *Check now*, in Settings → About. Nothing installs until you click.
- **Crop in the video editor and the GIF editor**: *Draw the crop*, then drag a box on the
  picture; drag inside it to move it, or its edges and corners to resize it. Hold it to 16:9,
  9:16 (TikTok, Shorts, Reels), 1:1 or 4:3. Outside the box is dimmed. In videos, blur boxes,
  text and arrows stay where you put them; in GIFs, the caption fits the cropped picture.
- **Snapping to pictures finds round ones**: profile pictures and other circles, pill shapes,
  and pictures with big round corners are outlined to their shape. Square-cornered pictures on
  gradient backgrounds are found too.

### Fixed

- **The Capture, Settings and Trace pages had a thinner gap on the right than on the left**
  when they were short enough not to scroll. Both sides now match.
- **Typing into a screenshot's text** could leave the bottom halves of letters behind, or put new
  letters over old ones, when another piece of text sat on the same row (a name and its time,
  or a second column). The whole height of the line now moves.
- **Stills, blurring and saving from videos whose width isn't a multiple of 16** came out
  scrambled into slanted stripes: Windows' video decoder pads each row, and that's now read
  from the frame itself.

## 3.1.0

A tidier app: shorter pages, hotkeys grouped by what they do, smoother area picking and a
livelier preview card.

### New

- **Tray menu quick actions**: right-click the tray icon and Capture a region, Capture a window,
  Capture the full screen, Record a video and Library are right there, with their hotkeys.
  While a video records, Stop and Pause (or Resume) are at the top instead; while a GIF records,
  Stop. Everything else is still in the groups below.
- **Clear out old captures** (Settings → General): captures older than 30 days, 90 days or a year
  go to the Recycle Bin by themselves. Only captures in your screenshots folder, never ones
  pinned on screen; turning it on asks first and says how many and how much space.
- **The Library shows how much space it uses**: "12 captures · 48 MB" next to the filters, and
  Settings shows the total for all your captures.

### Changed

- **The Capture page is much shorter**: After capturing, Watermark, Video, Sound, Webcam,
  Pointer and Auto-zoom fold away to their headings, each with a short summary of what's set
  ("Copy · Save PNG · Preview", "30 fps · Sharper"). Click a heading to open it; Overskin
  remembers which you left open.
- **Hotkeys are grouped**: Capture, Recording, Pins and Trace, each with its icon, in tighter
  rows. Pause and Draw now have their own rows under Recording (they share their keys with
  Trace, as before), so it's clear they work while recording.
- **Smoother area picking**: the box, magnifier and size readout follow the pointer frame by
  frame, read just before each screen refresh, instead of a frame or two behind it.
- **The preview card pops up**: it rises out of the corner, grows into place with a small
  bounce, and sinks away when it goes.
- The Video card on the Recording page shows its hotkey (Ctrl+Alt+R) like the others, and a line
  under the recording buttons says which keys pause and draw while recording.
- **Caption a GIF** moved from Screen tools to beside the recording buttons.
- Trace's seven styles fit on one row ("Gartic Phone" is now "Gartic", with the full name on hover).

## 3.0.1

- Fixed: file names with {app} could say "Medal" (or another overlay app) instead of the app you captured. The window in front is used when the capture is over it, and the see-through layers Medal, Discord, NVIDIA, Steam and Game Bar keep over games are passed over.

## 3.0.0

Recording grows up: pause, draw on the screen as you record, edit the auto-zooms afterwards,
blur private details in videos and grab stills. Plus QR codes, file names you choose, effects in
the editor, and screenshots that reach the clipboard much sooner.

### New

- **Pause a recording** with the pause button on the recording bar or Ctrl+Alt+P. The video and
  its sound carry on from where they stopped, with no gap; the red frame turns amber while paused.
- **Draw on the screen while recording**: the pencil on the recording bar, or Ctrl+Alt+D. Pen,
  arrow, box or highlighter in five colours; what you draw is recorded and fades after a few
  seconds. The pointer is hidden from the video while you draw, and those clicks don't zoom or ring.
  Right-click puts the pen away; the recording bar always stays on top, so Pause and Stop work.
- **QR codes**: *Text from screen* now reads QR codes and barcodes too. Drag over one and what it
  says is copied, with *Open link* when it's a web address. Offline.
- **Blur in videos**: the video editor has a Blur card. *Add a blur box* and drag over a name or
  an address; *Find private details* reads the frame showing now and blurs every email, phone
  number, card number, address and key on it. Each box has From and To, and can be dragged.
- **Grab a still**: the camera button next to the video's time copies the frame showing and opens
  it in the editor, at the video's full size.
- **File names you choose**: a pattern on the Capture page, such as `{app} {date} {time}`, with
  buttons for the date, time, app, window title, type and a counter, and an example underneath.
  A `\` makes folders (`{app}\{date} {time}` keeps each app's captures together).
- **Videos in the viewer play like on a video site**: a bar along the bottom with play and
  pause, the time, a scrubber and the volume (it tucks away while the video plays and the mouse
  rests), a play or pause sign that pops up in the middle on each click, and zoom like a picture,
  with the percentage shown. Space or K plays and pauses, J and L skip 5 seconds, Up and Down
  change the volume, M mutes, and a double-click fills the screen.
- **Effects** in the editor: a border in any of six colours, rounded corners, torn edges (top,
  bottom, sides or all round), a shadow that follows the shape, and grayscale, sepia or invert.
- **Auto-zoom you can edit afterwards**: recordings with auto-zoom keep an unzoomed copy and
  where the pointer went, and the zooms show as bands under the video in the video editor. Make
  one zoom in more or less, move its start and end, remove it, or add a new one, then Save.
  (*Editable afterwards* on the Recording page; not with a webcam, backdrop, shortcut keys or
  watermark, which still zoom as they record.)
- **Keep files under a size**: every saved screenshot fits in the size you set (say 8 MB).
  A JPG loses a little quality first, then any picture gets a little smaller.
- **JPG quality** slider on the Capture page.
- **Video: save a small copy** under a size you set (Discord's free limit is 10 MB), with your
  cuts, labels and zooms.
- **Viewer**: a film strip of thumbnails along the bottom, and a search box that finds captures
  by the words in them (Ctrl+F). It looks like the editor now (tool strip, stage) and keeps the
  picture centred when the window changes size.
- **Mockup frames** in Beautify: a browser window, a laptop or a phone, or *your own* frame
  picture (a PNG with a see-through screen, such as the device bezels makers publish for
  designers); the screenshot is fitted into the screen.

### Faster

- **After a capture**: the clipboard gets the picture about ten times quicker (straight
  through Windows rather than WPF's clipboard), the file is written off the main thread, the
  Library catches up a moment later instead of holding things up, and the app is compiled ahead
  of time so the first capture of a session isn't slow either.

### Changed and fixed

- Recent captures (Ctrl+Alt+H) open in the viewer, like the Library.
- Fixed: a pale halo round the rounded corners of the notifications and the recording bar.
- Fixed: in the viewer, a paused video stayed "paused" on the next video, so the first click did nothing.

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
