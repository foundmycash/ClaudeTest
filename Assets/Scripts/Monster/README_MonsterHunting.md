# Monster hunting AI

Detection and the chase are two separate systems. **MonsterSenses** decides *when* the monster starts hunting (sight,
proximity, sound; any one is enough). **MonsterHunter** then locks onto that one player and chases until they are safe
or dead; the senses are switched off for the whole chase, so losing sight or quiet never cancels it.

## Set up (one time)

1. Select the **Monster** object in the Forest scene.
2. **Tools > Monster > Set Up Hunting AI On Selected Monster**. Adds Monster Body, Senses, Hunter, Footsteps and Legs Link,
   and puts the NavMesh agent on the "Forest Monster" agent type (it was on Humanoid; the forest NavMesh is baked for
   "Forest Monster", so the monster could not path before).
3. Shared rooms only: **Tools > Monster > Make Monster Networked (Fusion)** (Network Object owned by the master client,
   Network Transform, Monster Network Link).
4. Safe zones: **GameObject > Monster > Safe Zone**, move/scale the box. Players whose chest is inside it are safe.
5. Save the scene. Assign your own footstep clips to **Monster Footsteps > Footstep Clips** (until then a generated
   placeholder stomp is used).

## Scripts (Assets/Scripts/Monster)

| Script | Job |
|---|---|
| `MonsterSenses` | Sight (raycast from the eyes to head/chest), proximity (distance), sound (microphone). Raises `Detected`. |
| `MonsterHunter` | State machine Idle / Chasing / Returning, target lock, speed ramp, NavMesh steering, catch, safe/dead end conditions. |
| `HuntTarget` | A player the monster may hunt (the Gorilla rig gets one by itself; remote avatars get one from the network link). `Kill()` / `Revive()`, `forceSafe`. |
| `SafeZone` | Volume that makes players inside it safe. No trigger events needed. |
| `MicrophoneNoiseDetector` + `NoiseAnalyzer` | Loudness only (dB). Nothing is recorded, stored or sent. |
| `MonsterFootsteps` | The only monster sound. Timing follows real speed. |
| `MonsterBody` | Eyes and which way the model faces (it faces world -Z). |
| `MonsterLegsLink` | Tells the Legs Animator the monster is walking and gives it the crawl gait (see below); reports each hand plant to the footsteps. |
| `MonsterNetworkLink` | Shares one monster in a Photon Fusion room. |

## Tuning (all in the Inspector)

- **Monster Senses**: Sight Distance (25 m), Field Of View, Vertical Field Of View, Obstacle Layers, Sight Interval, Proximity Radius, Hearing Range (0 = anywhere).
- **Microphone Noise Detector** (created automatically; add it to any object to change it): Threshold Db (default -28, raise = less sensitive), Min Above Floor Db, Min Loud Seconds, Cooldown, High Pass Hz.
- **Monster Hunter**: Base Chase Speed, Max Chase Speed, Speed Gain Per Second, Acceleration Delay, Acceleration Ease In, Return Speed, Catch Distance, Kill On Catch, Safe Confirm Time, Unavailable Grace, Turn Speed.
- **Monster Footsteps**: Footstep Clips, Stride Length, Min Step Interval, Volume, Pitch Variation, 3D distances. Stride Length is only used until the first hand plants and if the Crawl Gait is switched off; with the Crawl Gait on, each sound plays when a hand lands.
- **Monster Legs Link** (the walk): see the next section.

## Crawl gait (how the arms step)

The monster crawls on its two arms (the Legs Animator calls them legs; the hands are its "feet") and has no walk
animation. With the Legs Animator's default **Glue Mode = Automatic** that made it scoot: while walking it switches to
"Moving" glue, which expects a walk clip to lift the feet and only fixes them to the ground, so the hands slid along the
ground in tiny, flat re-plants (about 2 cm of lift). **Monster Legs Link > Crawl Gait** (on by default) switches the
Legs Animator to "Idle" glue, which does real steps: the hand lifts off behind the body, swings forward in an arc, lands
ahead, and the other arm only starts once it has landed, so the arms alternate. It also does two things a fixed setting
cannot, because the monster runs anywhere from 3.5 to 8 m/s:

- the swing time shrinks with speed (**Swing Time Times Speed** / **Swing Time Range**), so the stride stays about the same size instead of growing until the arms are stretched out straight behind;
- the hands land a bit ahead of the body (**Reach Per Speed**, capped by **Max Reach**), so the arms work around the shoulders instead of only trailing behind.

It also starts each hand's ground check higher (**Ground Check Height**, 1.6), because on a hillside the ground beside the
body can be higher than the hips and a hand then found no ground and hung in the air for a moment.

Tuning, all on the Monster Legs Link: **Min / Max Hand Raise** (how high the hands lift), **Swing Time Times Speed**
(bigger = slower, heavier arm movement; smaller = faster flapping), **Reach Per Speed / Max Reach**, **Ground Check
Height**. Untick **Crawl Gait** (also while playing) to see the original behaviour. Nothing is written into the scene's
Legs Animator component: the values are applied when the game starts and undone when you untick the option.

Measured in a scratch copy at a fixed 72 fps, straight running at 3.5 / 5.5 / 8 m/s: before, the hands lifted about 2 cm
and both were released (sliding) 65-70 % of the time; now they lift about 35 cm, exactly one hand is released 96-98 % of
the time (both never), each hand plants 1.5 / 2.3 / 3.3 times a second, and the arms work between about 0.7 m behind and
0.8 m ahead of the body centre at every speed (never fully straight). Not seen on a Quest or in a headset.

## Connecting your own death / game-over logic

There is no death system in the project yet. Either call `HuntTarget.Kill()` yourself, tick **Kill On Catch** on the
Hunter, or hook your logic to **On Target Caught** (raised once when the monster reaches the player).

## Microphone on Quest

The manifest now declares `android.permission.RECORD_AUDIO`; the player is asked at runtime (Quest shows a system
dialog the first time). If it is denied or there is no microphone the detector says so once and sight and proximity keep
working. Game audio coming out of the headset speakers can reach the microphone: raise Threshold Db if the game's own
sounds ever set the monster off.
