# Web Crawler

A desktop pet for Windows. A spider made of nodes and thin lines walks around on top of your windows, glitches the screen where its feet land, spins webs, hunts flies, and slowly learns whether it can trust you. Its body colour shows how it feels about you: red when it's wary, shifting through orange and yellow to green as it gets comfortable.

It's click-through everywhere except the spider's own body and its web threads, so you can keep working underneath it.

## Run

Build it once (see [Build](#build)), then double-click `publish\WebCrawler.exe`. It needs the .NET 8 Desktop Runtime.

Only one copy runs at a time. Launching it again while it's already running does nothing; use the tray icon instead.

### Start with Windows

Press **Win + R**, type `shell:startup`, press Enter, and put a shortcut to `publish\WebCrawler.exe` in the folder that opens. Delete the shortcut to stop it starting.

## Controls

| Action | What it does |
| --- | --- |
| Hold **Esc** for 1 second | Removes all spiders and quits |
| Hover over the spider | The cursor turns into a hand; it can be grabbed |
| Quick click on the spider | Startles it: it hops back and runs |
| Press and drag the spider | Picks it up; it dangles from the cursor on a thread |
| Let go mid-swipe | Throws it |
| Box-select it on the desktop or in File Explorer | Sends it to sleep in a web |
| Click a web's threads | Tears them; 4 clicks clears the web |
| Right-click the tray icon | Opens the menu below |
| Double-click the tray icon | Adds another spider |
| Hover the tray icon | Shows the first spider's comfort |

### Tray menu

- **Add spider / Remove spider**: up to 6 spiders.
- **Affection (follow, tap, rest)**: lets a spider that trusts you come over to tap, follow or rest near the cursor.
- **Flies**: turns flies on or off.
- **Release a fly**: sends a fly onto the desktop straight away.
- **Clear webs**: removes every web.
- **Glitch intensity**: Low, Medium or High.
- **Pause** and **Quit**.

## Behaviour

### Walking

Each leg has three jointed segments (femur, tibia and metatarsus) solved in 3D, so knees arch up above the body and feet lift in an arc as they step. Legs step in the alternating ripple real spiders use: a leg waits while the legs beside it and its mirror on the other side are in the air. Feet aim for where the body is about to be, including turns. Fast walking takes quick, low steps; slow creeping takes long, high ones. When it stops, stray feet shuffle back into place one at a time. The body bobs as legs lift and breathes gently when still, with a soft shadow underneath.

### Glitches

Where its feet land, and occasionally around its body while it walks, the real screen underneath glitches for a moment:

- colour channels pulled apart
- horizontal slices shifted sideways
- shuffled tiles
- coloured highlight bars
- outline boxes with small code-style labels such as `href`, `doi` or `<a>`, tied back to the spider by a thread
- small enlarged copies of the text floating above
- inverted patches

It also leaves a faint dragline trail behind it. **Glitch intensity** in the tray menu controls how often this happens.

### Mood in how it moves

- **Wary** (below 50% comfort, see below): it moves in quick, low dashes, freezing between them.
- **Comfortable** (above 60%): it strolls at an even, unhurried pace and often stops to look around.

### Getting around your desktop

- **Wandering**: it roams to random spots, sometimes pausing.
- **Window edges**: about half the time it walks along the title bar or sides of one of your front windows, and may tuck into the corner at the end.
- **Hiding**: tucked in a corner, it crouches with its legs pulled in and faces out toward the open screen.
- **Grooming**: when it stops, it sometimes raises its front legs and rubs them together.

### Idle fidgets

Whenever it's standing still (pausing, hiding, resting, sitting in a web, peeking), it fidgets every few seconds:

- **Tapping a leg**: lifts a middle leg and taps the ground.
- **Cleaning a leg**: draws one front leg through its mouth.
- **Freezing**: goes completely still, holding its breath, then twitches. Wary spiders freeze more often.
- **Looking around**: turns its body to one side and back.

### Comfort and mood

Every spider has a comfort level from 0% (red) to 100% (green). A new spider starts at 0%. Comfort is saved every 30 seconds and when you quit, so the spider keeps its colour between runs. It's stored in `%LOCALAPPDATA%\WebCrawler\state.json`.

**It's a relationship you keep up.** Once a full day has passed since you last did something it liked, its comfort drains by 10% per day, measured in real days, whether or not the PC is on. Fully green to wary takes about six days of neglect. Anything that raises comfort resets the clock.

| Raises comfort | Lowers comfort |
| --- | --- |
| Eating a fly: +5% (+6% if it stores it in its web) | Throwing it hard: −6% |
| A calm, still cursor resting near it while you're at the PC: about +0.4% a second | Clicking to startle it: −3% |
| Being set down gently: +1% | Tearing its web while it's in it: −3% |
| | Pressing it awake: −2% |
| | Rushing the cursor at it so it flees: −2% |

Each time it warms to you, a ring in its new colour briefly swells out from its body.

**Wary (below 50%)**: the redder it is, the stronger all of this gets.

- It goes to places as far from the cursor as it can find, mostly screen and window corners, and hides there for longer.
- When the cursor comes within range, it stops what it's doing, crouches and turns to face it. If the cursor is very close, it slowly backs away while still watching.
- If the cursor moves suddenly while it's watching, it jumps back with a red **!** and runs off to hide.
- It never chases the cursor and won't hunt a fly that's near it.
- Holding the cursor still near a wary spider slowly earns its trust.

**Comfortable (50% and above)**: it roams freely, and the greener it is, the faster a cursor rush has to be before it flees.

**Affection (75% and above)**, if **Affection** is ticked in the tray menu, now and then it comes over to:

- **Tap**: walks up to the cursor and pats it a few times with a front leg.
- **Follow**: trails the cursor at a polite distance, stopping when it stops and turning to face it.
- **Rest**: settles down a short way from the cursor for a while, keeping an eye on it.

### Fleeing and startling

- Whipping the cursor quickly straight at it makes it bolt.
- A quick click on it makes it hop backward and scurry off.

### Picking it up and throwing it

Press and drag it and it hangs from the cursor on a thread, legs kicking, body swinging to trail behind. Let go slowly and it's set down gently. Let go mid-swipe and it flies: it skids, bounces off screen edges, lands, wobbles dizzily with small nodes circling its head, then runs away.

### Webs

- Every few minutes it picks a spot on **open desktop** where the whole web fits (a screen corner if one is clear, otherwise a clear patch of wallpaper), never over a window.
- It turns on the spot while building: spokes first, then the spiral from the outside in. Then it sits in the middle for a while.
- Webs are drawn in the same node style, last about 10 minutes and then fade. There can be up to 4 at a time.
- Moving a window over a web hides it until the desktop shows again.
- Clicking a web's threads snaps the threads near the click, cuts any spoke it hits and makes the web shudder. Wrapped flies near the click drop out. The fourth click clears what's left.
- Disturbing a web it's sitting in sends it running.
- **Repairs**: about 15 seconds after a web is torn (but not cleared), the spider comes back, turns on the hub and mends it thread by thread until it's whole again. Tearing it while it works sends it running. Four quick clicks still clear a web for good.

### Flies

- Flies appear over open desktop every 20–50 seconds (at most 2 at a time) and only ever fly over desktop. One that drifts over a window vanishes; if no desktop is visible, no flies come.
- They buzz around, land now and then, dodge the cursor, and leave after about a minute.
- A fly that flies through a finished web can get **stuck**. It shudders and the web trembles. Any spider within reach drops what it's doing and sprints over, even if it has just eaten. A fly that isn't collected tears free after about 25 seconds.
- The spider stalks a nearby fly, then pounces. It wraps the catch in silk with its front legs. If one of its finished webs is nearby, the wrapped fly is stored on the web; otherwise it eats it and the screen glitches as it goes down.

### Sleep

- Drag a selection box over it on the desktop or in File Explorer. Either hold the box over it for a moment or let go with it inside.
- It doesn't flee from the box. It walks sleepily, with Z's already drifting up, to the nearest open patch of desktop with room for a web, quickly spins one, and falls asleep in the middle.
- If no desktop is visible anywhere, it curls up and sleeps where it is.
- While asleep it ignores everything else. A window moved over its web hides it along with the web.
- Pressing it, or clicking its web, wakes it with a jolt: a red **!** appears, it hops back and runs off.

### Noticing what you're doing

- **Typing**: when you've been typing for a bit and it's at least 50% comfortable, it comes over to the window you're typing in, sits on its top edge with its front legs hooked over, and watches where you're typing (in apps that report their text cursor; otherwise it just looks down into the window). It stays while you keep typing.
- **Peeking**: every minute or so, if it's at least 30% comfortable, it may wander over and peek over the top edge of the window you're using for a few seconds. It loses interest if you move or switch windows.
- **New and moved windows**: when a window opens or moves nearby, it gives a little startled hop, then walks over to the window's edge, looks around and faces it. A very wary spider runs off to hide instead.
- **When you're away**: after a minute without input it ranges much further across your screens. If you come back after more than two minutes and it hasn't gone far, you'll find it somewhere new.

These only use whether there has been keyboard or mouse input and where windows are. It never reads what you type.

### Fullscreen

If a screen has something fullscreen on it (a video, a game, a browser in F11), the spiders, flies and webs keep off it. A spider already there moves to another screen; if every screen is fullscreen, the spiders disappear until one is free again. Maximised windows don't count as fullscreen.

## Build

```bash
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
```

Needs the .NET 8 SDK. The app is written in C# with Windows Forms and draws everything itself with GDI+ into layered, always-on-top windows.
