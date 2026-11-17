# AI Engineer

Give a locomotive an engineer, as in Railroader: send it to a spot on the
map and it finds the way, sets the switches, and drives there, while you
run another train. Queue more stops, couplings, and set-outs behind it.

Diesel, electric, and steam locos (DE2, DE6, DH4, BE2, S060, S282, and
similar modded ones); the DM3's manual gearbox isn't driven yet. On a steam
loco the engineer works the regulator, the cutoff (full gear to start,
notched up with speed), and the cylinder cocks (open while the cylinders
are cold or have water in them), and waits for steam before it moves. With Realistic Firing installed, its automatic fireman is put to
work (Shunting for yard work, Road for a run, Easing off ahead of a stop),
and his mode is put back as you left it when the engineer lets go: this needs a
Realistic Firing newer than 1.23.0. With 1.23.0 or older the engineer
still drives, but you keep the fire yourself (or with F7), and it tells you
so when it takes the loco. On stock steam the engineer fires it itself:
coal into the firebox as the work asks, the injector for the water, the
blower while it stands.

## Giving orders

- **The engineer's tablet.** A tablet you hold, or fit to a large gadget
  mount in a cab, like Realistic Firing's firing tablet. You get one in
  your starting kit (once per save), it is sold beside the clinometer, and
  **F9** puts one in front of you if you have none. Hold it and press its
  screen (mouse mode while held on desktop; your hand in VR). Three pages
  behind its tab bar, and a plate for each of your engineers beside the
  tabs: press one to give it orders.
  - **Map:** the game's own station booklet, page by page. Map shows the
    route map; press a station's badge, or its tab down the left (nine at
    a time, ▲ and ▼ for the rest), for its overview; press a yard's
    capsule, or its letter on the right, for the yard. The lit letter,
    pressed again, goes back up. + and – zoom; the arrows move the page;
    Show opens your loco's page. The cars are drawn on the tracks in their jobs' colors,
    your locos light, the chosen loco's route in yellow, its orders
    numbered, and each switch's setting as a green bar. Press a track, a
    cut of cars, or a loco: its orders come up at the bottom (Go to, cars
    first or loco first, or Stop here; Couple on either end; Work a job its cars belong
    to; Back or Ahead by car lengths), Now or After its orders.
  - **Engineers:** what each is doing and its orders, each with ✕; Give
    orders, Stop, Hand back; **Fuel** sends it to the nearest service
    station selling what it runs on (diesel, coal or water, charge), and
    **Service** to the nearest that repairs, loco end first, stopped over
    the station's spot.
  - **Jobs:** the jobs you hold (their booklets; Railroad Contracts'
    waybills become these); tick one or several and hand them over, Do it
    now or Add to the queue. If your loco is coupled to cars that aren't
    the jobs', it uncouples from them first, where they stand. Then it
    fetches each job's cars, wherever they are: from several tracks for
    one job, and buried behind other cuts, which it couples on through and
    takes along. It takes only the job's cars from a cut: those beyond them
    it leaves where they stand, hand brakes on; those between it takes along
    and puts back after. It sets each job's cars out on its track, cars
    first, as far in as the track goes, hand brakes on, coupled to the
    job's cars already there. If cars by the entry leave no room, it
    couples to them (and the next cut, if need be) and shoves them further
    in. A shunting job it works step by step: it brings the cars to the
    warehouse machine's track and waits there while you work the machine,
    then takes them on. Turn the booklets in after.
  - When an order needs the loco turned round, it runs round a wye or a
    loop. If there's none it can reach, it does the move anyway and tells
    you what it couldn't manage.
  - Under every page, the last order you gave, with **Undo**.
  - **With the Comms Tablet installed,** these pages are its **Engineers**
    app instead, on the Comms Tablet's home screen, and the engineer's
    tablet is not given, sold, or put in front of you by F9; one you saved
    loads as a Comms Tablet. In multiplayer only the host has the app.
- **A helper, by the whistle (doubleheading).** A steam loco coupled in
  the train you drive can be your helper: press **Helper** on its card on
  the Engineers page (it shows when you are in the lead loco or its
  tender), or **Be my helper** on it on the map. Then from your cab:
  - **Two long:** it releases its brake and works the way your reverser is
    set, matching your regulator and cutoff, and shuts off when you do.
  - **One short:** it shuts off and applies its brake.
  - **Three short, standing:** it sets back and works the other way. Your
    reverser must be set that way too, or it stays shut off.
  - It listens to your whistle's **sound**, so you can quill it and you
    needn't let the cord go between blasts: ease it until the whistle
    drops to well under half (a sigh is fine) and pull again. A dip shorter
    than a quarter second, or shallower, is the same blast.
  - A **long** is a pull of a second and a half or more; anything
    shorter is a **short**; a blip under 0.4 s isn't counted, nor is the
    cord springing back after you let it go. The signal ends 2 s after
    your last blast.
  - It answers each signal it takes with two short. Your brake handle
    works the whole train; its own brake valve is cut out while it helps.
    **Helping** on its card (or uncoupling it) stops it, brake on.
- **Comms Radio, AI ENGINEER mode** (also in VR). Point at a loco and click
  to take it. Then point at track; the radio's arrow buttons choose the
  order (GO HERE, ADD STOP, GO COUPLE, ADD COUPLE, ADD LEAVE 1/2/3/5, HAND
  BACK, BACK), and a click gives it.

## Orders

- **Stop:** the leading end stops at the waypoint.
- **Couple:** runs on up to ten car lengths past the waypoint to the cars
  standing there, couples at walking pace, and releases their hand brakes.
- **Leave N:** stops at the waypoint and cuts off the N cars at the end of
  the train away from the loco, setting their hand brakes.

It keeps to the posted speed limits (and a cap on yard tracks), never
throws a switch with a car on it, never plans a way through standing cars
or past cars left too near a switch (about 35 m from the points), and backs
a train through a switchback when that's the way in. If its wheels slip it
eases off and sands.

**Stopping it.** **Stop** (Engineers page) and **Stop here** (a loco's
orders on the map) end whatever it is doing, a job run included, and hold
the train; **Hand back** also gives the controls back. Taking a lever yourself, aboard
the train or with a remote paired to the loco, hands it straight to you. A
train that stands five minutes on one order tells you what it is waiting
on. If the end
of a cut you asked for can't be reached without going through the cars, it
couples on the other end and tells you.

## With Realistic Brakes

The engineer works the air as a crew would: it won't start until every
car's brakes are off and the train line is charged (it says so while it
waits), it sets the train brake for a stop and steps it up rather than
easing it (American brakes release all the way), and it plans its stops
longer: 0.12 m/s², a setting. Its own cars' hand brakes come off before it
starts; cars it leaves get theirs on.

## Settings

In the mod's settings (Ctrl+F10), and on the Comms Tablet's Settings app
when it is installed:

- **Driving:** top speed on runs, speed for yard moves, the speed cap on
  yard tracks (read when a save loads), how far short of standing cars it
  stops, and whether it starts a shut-down loco.
- **Braking:** how hard it plans to brake with cars, running light, and
  with cars on Realistic Brakes.
- **Display:** notifications.
- **Keys:** the engineer's tablet key (F9; not shown with the Comms
  Tablet, whose Engineers app takes the tablet's place).

An engineer takes the speeds, gap, and braking when it is put on a loco.
Hover a setting's ? in the mod manager's window for more.

## Works with

- **Realistic Brakes** 1.2 or newer: the air as above.
- **Realistic Firing** newer than 1.23.0: its fireman, as above.
- **The Comms Tablet:** the pages become its Engineers app.
- **Skin Manager:** the tablet is kept in saves and comes from the shops.
- **Railroad Contracts:** its waybills are jobs on the Jobs page.

## Limits

- **VR:** the tablet's plates can be pressed, but not the tracks and cars
  on its map; use the Comms Radio mode to point at track.
- **Multiplayer:** only the host gives orders and drives; a client has no
  tablet pages. Every player needs the mod. Not yet tried in a
  multiplayer session.
- The DM3 isn't driven (its manual gearbox).
- Orders aren't saved with the game: after loading a save, give your
  engineers their orders again.
- A job whose cars stand in two lots on one track, with other cars between
  them, may be set out with the second lot left uncoupled; couple it
  yourself to turn the job in.
- Removing the mod: a saved engineer's tablet is dropped from the save
  when it loads.
