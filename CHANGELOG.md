# AI Engineer: changelog

## 0.16.2 (2026-11-23)

- Fixed: with Realistic Brakes, while you rode in a diesel's cab, the
  engineer left the train brake slightly on, so the train was slow to start
  and ran with its brakes dragging.

## 0.16.1 (2026-11-21)

- Fixed: with Persistent Jobs, an engineer sent to pick up cars at a station
  far from you could find nothing there.
- Fixed: working jobs, the engineer set your caboose aside. It now keeps a
  caboose coupled next to the loco with the train.

## 0.16.0 (2026-11-21)

- New: the conductor, for the train you drive yourself. Aboard it, press a
  track on the map and choose Conductor: route here. The Conductor tab shows
  the route, and nothing changes until you press Execute. Then you drive
  while the conductor lines the switches ahead and tells you where to stop
  and reverse. It never touches the controls, and leaves any switch you
  throw as you set it. Stand down ends it.
- You can fire a steam loco while the engineer drives: Fire: You on its card
  on the Engineers page. The engineer leaves the fire and the water to you,
  tells you when the fire goes out or steam or water runs low, and waits for
  steam if it runs out. A setting chooses what new engineers start with.
- Gentler on the throttle: it opens about a notch at a time, eases off as
  the traction motors heat up, and feels its way back up after a wheel slip.
  It uses less sand on dry rail.
- If the traction motor fuse is off, it holds the train and tells you. Turn
  the fuse back on in the cab and it carries on.
- Starting uphill, it takes up power against the brakes before letting them
  off, so the train doesn't roll back.
- When it can't get a train moving, or keep it moving, it tells you which
  loco and why, drops its orders, and holds the train on its brakes. Give it
  another order to try again.
- With cars behind it, it slows and stops on the train brake and keeps the
  independent light, putting it on fully only to hold the train once it has
  stopped.
- Setting out cars on a track that already has some, it couples to them and
  shoves them in when that's much shorter than going round.
- The Engineers page lists the loco you're on and those coupled to it, with
  a button to give it orders.
- With no steam at all, it stops and waits on any kind of move.
- Fixed: with Realistic Brakes, stepping into the cab of a steam loco the
  engineer was driving handed you the controls. When you do take a lever,
  the message now says which one.
- Fixed: with Realistic Brakes, a diesel could stand at full throttle with
  no power, or blow its traction motor fuse when the power came back.
- Performance improvements and bug fixes.
- The Comms Tablet's Engineers app has these too.

## 0.15.3 (2026-11-18)

- Your engineer keeps the loco in front on the road. A train with its cars
  ahead of the loco used to shove them all the way to the next station. Now
  it turns on a wye or loop if one is within reach and runs loco first,
  backing up only at the end, where a track needs the cars first. Short
  shoves in a yard work as before.
- Sent to pick up cars, a loco no longer goes the long way round just to
  arrive facing away from them. If turning would add more than about 1 km,
  it couples on facing the cars.
- A loco with only its tender or a second unit now counts as running light.
- Fixed: a steam loco with a tender could wait forever at "Releasing the
  brakes".
- Fixed: with Double Track, the tablet's route map page couldn't find its
  switches, so it could miss which way they were set, your engineer's route,
  and trains out on the line.
- The Comms Tablet's Engineers app has all of these too.

## 0.15.2 (2026-11-17)

- Fixed: a loco that isn't yours (a company loco, or one you spawned), picked
  on the tablet's map with "Give orders to it", now stays picked and gets its
  plate, so its first order gives it an engineer. Before, the tablet dropped
  it within a second and the order went to one of your other engineers. The
  Comms Tablet's Engineers app has the same fix.
- The tablet's top bar always shows the plate of the loco you're giving
  orders to, even when you have more engineers than fit.
- The map has a clean, flat background, as the Comms Tablet's own map has,
  instead of the booklet's worn paper and the route map's stained grid, and
  its title is darker so it reads on it. On the Comms Tablet's Engineers app
  too.

## 0.15.1 (2026-11-17)

First public release.

- Give a locomotive an engineer: send it to a track, a cut of cars, or a
  spot on the map, and it finds the route, sets the switches, and drives
  there while you run another train. Queue more orders behind it.
- Orders: go to a track (cars first or loco first), couple to either end
  of a cut, set out cars, back or ahead by car lengths, stop here, hand
  back. Fuel and Service send it to the nearest service station.
- Hand over jobs, one or several at once. It fetches each job's cars, from
  several tracks or from behind other cuts, sets them out on their tracks,
  and plans the order with the least running. Shunting jobs are worked step
  by step, waiting at the warehouse machine for you.
- The engineer's tablet: held, or fitted to a large gadget mount. It comes
  in your starting kit once per save, is sold beside the clinometer, and F9
  puts one in front of you. Its pages are a map (the game's station
  booklet), your engineers, and your jobs. With the Comms Tablet
  installed, they are its Engineers app instead.
- The Comms Radio's AI ENGINEER mode, in VR too.
- Diesel, electric, and steam. On steam it works the regulator, cutoff, and
  cylinder cocks. With Realistic Firing (newer than 1.23.0) it puts the
  automatic fireman to work; on stock steam it fires the loco itself.
- A steam helper in your own train, worked by your whistle: two long to go,
  one short to stop, three short to back.
- With Realistic Brakes it waits for the air and sets the train brake as a
  crew would.
- It keeps to the speed limits, never throws a switch under a car, couples
  at walking pace, and eases off when its wheels slip.
- Works with Skin Manager: the tablet is kept in saves and comes from the
  shops.
